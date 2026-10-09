"""Batch lifecycle: creation, guarded transitions, recovery, listing, downloads, and My creations."""
from __future__ import annotations

from dataclasses import replace
from datetime import datetime, timezone
import logging
import os
from pathlib import Path
import shutil
import tempfile
import threading
from typing import Callable
from uuid import uuid4

from .batch_manifest import SCHEMA_VERSION, BatchManifest, BatchRepository, BatchTrack
from .batch_project import (BatchSource, build_source, check_file_count, output_name,
                            sanitize_display_name, validate_batch_name)
from .engine import SynthesisError
from .studio import MODEL_REVISION, SOURCE_REVISION, SYNTHESIS_SEED, StudioService, canonical_id
from .voice_store import sha256_file

LOGGER = logging.getLogger(__name__)
ACTIVE_STATES = ("running", "pausing", "stopping")
STOPPED_MESSAGE = "Stopped. Completed tracks were kept."


class InvalidTransition(RuntimeError):
    """Requested action does not apply to the batch's current state (mapped to 409)."""


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


class BatchService:
    def __init__(self, studio: StudioService, enabled: bool = True):
        self.studio = studio
        self.enabled = enabled
        self.repository = BatchRepository(studio.root / "batches")
        self.wake = threading.Event()
        self._lock = threading.RLock()

    # ----- HTTP-facing operations -----

    def create(self, name: str, voice_id: str, uploads: list[tuple[str | None, Path]]) -> dict:
        if not self.enabled:
            raise SynthesisError("Batch audio is disabled on this worker.")
        name = validate_batch_name(name)
        canonical_id(voice_id)
        voice = next((voice for voice in self.studio.voices() if voice["voice_id"] == voice_id), None)
        if voice is None:
            raise KeyError("Voice not found")
        check_file_count(len(uploads))
        sources: list[BatchSource] = []
        for number, (client_name, path) in enumerate(uploads, start=1):
            sources.append(build_source(sanitize_display_name(client_name, number), path.read_bytes()))
        now = utc_now()
        manifest = BatchManifest(
            schema_version=SCHEMA_VERSION, batch_id=str(uuid4()), name=name, voice_id=voice_id,
            voice_name=voice["name"], language_id=voice["language_id"], model_revision=MODEL_REVISION,
            source_revision=SOURCE_REVISION,
            conditioning_format_version=self.studio.compatibility.format_version,
            synthesis_seed=SYNTHESIS_SEED, state="queued", message="Waiting to start",
            created_at=now, updated_at=now, chunk_seconds_total=0.0, chunks_done_total=0,
            tracks=tuple(BatchTrack(number, source.display_name, source.sha256, source.chunk_count)
                         for number, source in enumerate(sources, start=1)))
        # Build in a hidden staging folder and rename, so a partial batch is never listed.
        staging = Path(tempfile.mkdtemp(prefix=".new-", dir=self.repository.root))
        try:
            (staging / "sources").mkdir()
            for number, source in enumerate(sources, start=1):
                (staging / "sources" / f"{number:03d}.txt").write_bytes(source.content)
            self.repository.save(manifest, staging)
            os.replace(staging, self.repository.batch_dir(manifest.batch_id))
        except BaseException:
            shutil.rmtree(staging, ignore_errors=True)
            raise
        LOGGER.info("Batch %s created with %s tracks", manifest.batch_id, len(sources))
        self.wake.set()
        return self.summary(manifest)

    def list(self) -> list[dict]:
        return [self.summary(manifest) for manifest in self.repository.list()]

    def detail(self, batch_id: str) -> dict:
        manifest = self.repository.load(canonical_id(batch_id))
        return {"batch": self.summary(manifest), "tracks": [
            {"number": track.number, "display_name": track.display_name,
             "output_name": output_name(manifest.name, track.number), "state": track.state,
             "chunk_count": track.chunk_count, "chunks_done": track.chunks_done, "error": track.error,
             "duration_seconds": track.duration_seconds, "completed_at": track.completed_at}
            for track in manifest.tracks]}

    def pause(self, batch_id: str) -> dict:
        def change(manifest):
            if manifest.state == "running":
                return replace(manifest, state="pausing", message="Pausing after the current chunk")
            if manifest.state == "queued":
                return replace(manifest, state="paused", message="Paused")
            raise InvalidTransition("Only queued or running batches can be paused")
        return self.summary(self._mutate(batch_id, change))

    def resume(self, batch_id: str) -> dict:
        def change(manifest):
            if manifest.state != "paused":
                raise InvalidTransition("Only paused batches can be resumed")
            return replace(manifest, state="queued", message="Waiting to start")
        result = self._mutate(batch_id, change)
        self.wake.set()
        return self.summary(result)

    def stop(self, batch_id: str) -> dict:
        def change(manifest):
            if manifest.state in ("running", "pausing"):
                return replace(manifest, state="stopping", message="Stopping after the current chunk")
            if manifest.state in ("queued", "paused"):
                return self._stopped(manifest)
            raise InvalidTransition("Only queued, running, or paused batches can be stopped")
        return self.summary(self._mutate(batch_id, change))

    def retry(self, batch_id: str) -> dict:
        def change(manifest):
            if manifest.state not in ("failed", "stopped"):
                raise InvalidTransition("Only failed or stopped batches can be retried")
            tracks = tuple(track if self._verified(manifest, track) or track.state == "pending"
                           else self._reset(manifest, track) for track in manifest.tracks)
            return replace(manifest, state="queued", message="Waiting to start", tracks=tracks)
        result = self._mutate(batch_id, change)
        self.wake.set()
        return self.summary(result)

    def retry_track(self, batch_id: str, number: int) -> dict:
        def change(manifest):
            track = self._track(manifest, number)
            if track.state != "failed":
                raise InvalidTransition("Only failed tracks can be retried")
            tracks = tuple(self._reset(manifest, item) if item.number == number else item
                           for item in manifest.tracks)
            if manifest.state in ("failed", "stopped", "completed"):
                return replace(manifest, state="queued", message="Waiting to start", tracks=tracks)
            return replace(manifest, tracks=tracks)
        result = self._mutate(batch_id, change)
        self.wake.set()
        return self.summary(result)

    def delete(self, batch_id: str) -> None:
        batch_id = canonical_id(batch_id)
        with self._lock:
            manifest = self.repository.load(batch_id)
            if manifest.state in ACTIVE_STATES:
                raise InvalidTransition("Pause or stop the batch before deleting it")
            self.repository.delete(batch_id)
        LOGGER.info("Batch %s deleted", batch_id)

    def track_audio(self, batch_id: str, number: int) -> tuple[Path, str]:
        manifest = self.repository.load(canonical_id(batch_id))
        track = self._track(manifest, number)
        path = self.repository.track_path(manifest.batch_id, number)
        if track.state != "completed" or not path.is_file():
            raise KeyError("Track audio not found")
        return path, output_name(manifest.name, number)

    def archive(self, batch_id: str) -> tuple[str, list[tuple[str, Path]]]:
        manifest = self.repository.load(canonical_id(batch_id))
        entries = [(output_name(manifest.name, track.number),
                    self.repository.track_path(manifest.batch_id, track.number))
                   for track in manifest.tracks if track.state == "completed"]
        entries = [(name, path) for name, path in entries if path.is_file()]
        if not entries:
            raise KeyError("No completed tracks to download yet")
        return f"{manifest.name}.zip", entries

    def creations(self) -> list[dict]:
        result = []
        for manifest in self.repository.list():
            for track in manifest.tracks:
                if track.state != "completed" or not self.repository.track_path(manifest.batch_id, track.number).is_file():
                    continue
                # Chapter text is never returned; it would bloat the 5-second poll.
                result.append({
                    "job_id": f"{manifest.batch_id}-{track.number:03d}", "kind": "batch",
                    "voice_id": manifest.voice_id, "voice_name": manifest.voice_name,
                    "language_id": manifest.language_id, "text": "", "created_at": track.completed_at,
                    "batch_id": manifest.batch_id, "batch_name": manifest.name, "track_number": track.number,
                    "track_count": len(manifest.tracks), "source_name": track.display_name})
        return result

    def recover(self) -> None:
        """Startup: interrupted work resumes automatically; stopping finishes as stopped."""
        with self._lock:
            for leftover in self.repository.root.glob(".new-*"):
                shutil.rmtree(leftover, ignore_errors=True)
            for manifest in self.repository.list():
                if manifest.state in ("queued", "running", "pausing"):
                    tracks = tuple(replace(track, state="pending") if track.state == "running" else track
                                   for track in manifest.tracks)
                    message = "Waiting to start" if manifest.state == "queued" else "Resuming after restart"
                    self._save(replace(manifest, state="queued", message=message, tracks=tracks))
                elif manifest.state == "stopping":
                    self._save(self._stopped(manifest))
        self.wake.set()

    # ----- Runner-facing operations; each re-reads state so user pause/stop always wins -----

    def claim_next(self, engine_ready: bool) -> BatchManifest | None:
        with self._lock:
            queued = sorted((manifest for manifest in self.repository.list() if manifest.state == "queued"),
                            key=lambda manifest: manifest.created_at)
            if not queued:
                return None
            manifest = queued[0]
            if not engine_ready:
                if manifest.message != "Waiting for the voice engine":
                    self._save(replace(manifest, message="Waiting for the voice engine"))
                return None
            return self._save(replace(manifest, state="running", message="Converting"))

    def load(self, batch_id: str) -> BatchManifest:
        return self.repository.load(batch_id)

    def begin_track(self, batch_id: str, number: int, chunks_done: int) -> str:
        def change(manifest):
            if manifest.state != "running":
                return self._settle(manifest)
            return self._with_track(replace(manifest, message=f"Converting track {number:03d}"), number,
                                    state="running", chunks_done=chunks_done, error=None)
        return self._mutate(batch_id, change).state

    def chunk_done(self, batch_id: str, number: int, chunks_done: int, seconds: float) -> str:
        def change(manifest):
            updated = self._with_track(manifest, number, chunks_done=chunks_done)
            updated = replace(updated, chunk_seconds_total=round(manifest.chunk_seconds_total + seconds, 3),
                              chunks_done_total=manifest.chunks_done_total + 1)
            return self._settle(updated)
        return self._mutate(batch_id, change).state

    def complete_track(self, batch_id: str, number: int, output_sha256: str, duration: float) -> str:
        def change(manifest):
            track = self._track(manifest, number)
            updated = self._with_track(manifest, number, state="completed", chunks_done=track.chunk_count,
                                       error=None, output_sha256=output_sha256,
                                       duration_seconds=round(duration, 3), completed_at=utc_now())
            return self._settle(updated)
        return self._mutate(batch_id, change).state

    def fail_track(self, batch_id: str, number: int, message: str) -> str:
        def change(manifest):
            return self._settle(self._with_track(manifest, number, state="failed", chunks_done=0, error=message))
        return self._mutate(batch_id, change).state

    def finish(self, batch_id: str) -> str:
        def change(manifest):
            if manifest.state != "running":
                return self._settle(manifest)
            failed = sum(track.state == "failed" for track in manifest.tracks)
            if failed:
                return replace(manifest, state="failed",
                               message=f"{failed} track{'s' if failed != 1 else ''} failed. Retry to convert them again.")
            return replace(manifest, state="completed", message="All tracks are ready")
        return self._mutate(batch_id, change).state

    def clear_work(self, batch_id: str, number: int) -> None:
        shutil.rmtree(self.repository.work_dir(batch_id, number), ignore_errors=True)

    # ----- helpers -----

    @staticmethod
    def summary(manifest: BatchManifest) -> dict:
        tracks = manifest.tracks
        current = next((track for track in tracks if track.state == "running"), None) or next(
            (track for track in tracks if track.state == "pending" and track.chunks_done > 0), None)
        return {
            "batch_id": manifest.batch_id, "name": manifest.name, "voice_id": manifest.voice_id,
            "voice_name": manifest.voice_name, "language_id": manifest.language_id, "state": manifest.state,
            "message": manifest.message, "created_at": manifest.created_at, "updated_at": manifest.updated_at,
            "track_count": len(tracks),
            "tracks_completed": sum(track.state == "completed" for track in tracks),
            "tracks_failed": sum(track.state == "failed" for track in tracks),
            "chunk_count": sum(track.chunk_count for track in tracks),
            "chunks_done": sum(track.chunks_done for track in tracks),
            "chunk_seconds_total": manifest.chunk_seconds_total,
            "chunks_done_total": manifest.chunks_done_total,
            "current_track_number": current.number if current else None,
            "current_track_name": current.display_name if current else None,
            "current_chunks_done": current.chunks_done if current else None,
            "current_chunk_count": current.chunk_count if current else None,
        }

    def _mutate(self, batch_id: str, change: Callable[[BatchManifest], BatchManifest]) -> BatchManifest:
        batch_id = canonical_id(batch_id)
        with self._lock:
            manifest = self.repository.load(batch_id)
            updated = change(manifest)
            return manifest if updated == manifest else self._save(updated)

    def _save(self, manifest: BatchManifest) -> BatchManifest:
        manifest = replace(manifest, updated_at=utc_now())
        self.repository.save(manifest)
        return manifest

    def _settle(self, manifest: BatchManifest) -> BatchManifest:
        """Apply a requested pause/stop once the runner reaches a safe point."""
        if manifest.state == "pausing":
            tracks = tuple(replace(track, state="pending") if track.state == "running" else track
                           for track in manifest.tracks)
            return replace(manifest, state="paused", message="Paused", tracks=tracks)
        if manifest.state == "stopping":
            return self._stopped(manifest)
        return manifest

    def _stopped(self, manifest: BatchManifest) -> BatchManifest:
        """Discard partial track progress; completed tracks and source files are kept."""
        tracks = []
        for track in manifest.tracks:
            if track.state == "running" or (track.state == "pending" and track.chunks_done):
                self.clear_work(manifest.batch_id, track.number)
                track = replace(track, state="pending", chunks_done=0)
            tracks.append(track)
        return replace(manifest, state="stopped", message=STOPPED_MESSAGE, tracks=tuple(tracks))

    def _reset(self, manifest: BatchManifest, track: BatchTrack) -> BatchTrack:
        self.clear_work(manifest.batch_id, track.number)
        self.repository.track_path(manifest.batch_id, track.number).unlink(missing_ok=True)
        return replace(track, state="pending", chunks_done=0, error=None, output_sha256=None,
                       duration_seconds=None, completed_at=None)

    def _verified(self, manifest: BatchManifest, track: BatchTrack) -> bool:
        path = self.repository.track_path(manifest.batch_id, track.number)
        return track.state == "completed" and path.is_file() and sha256_file(path) == track.output_sha256

    @staticmethod
    def _track(manifest: BatchManifest, number: int) -> BatchTrack:
        if not isinstance(number, int) or not 1 <= number <= len(manifest.tracks):
            raise KeyError("Track not found")
        return manifest.tracks[number - 1]

    def _with_track(self, manifest: BatchManifest, number: int, **values) -> BatchManifest:
        self._track(manifest, number)
        return replace(manifest, tracks=tuple(replace(track, **values) if track.number == number else track
                                              for track in manifest.tracks))
