"""Single background thread that converts queued batches one chunk at a time with durable checkpoints."""
from __future__ import annotations

import json
import logging
import os
from pathlib import Path
import threading
import time
from typing import Callable

from .audio_validation import pcm_wav_metrics, validate_output
from .batch_manifest import BatchManifest, BatchTrack
from .batch_service import BatchService
from .chunking import chunk_text
from .engine import SynthesisEngine
from .operation_gate import OperationGate
from .studio import CHUNK_CHARS, PARAGRAPH_SILENCE_MS, SENTENCE_SILENCE_MS
from .voice_store import sha256_file
from .wav_assembly import assemble_chunks

LOGGER = logging.getLogger(__name__)
GENERIC_FAILURE = "Audio generation failed. Check worker logs and retry."
CHECKPOINT_NAME = "checkpoint.json"
# Long tracks can be ~45 minutes of PCM; leave the MP3 encoder ample time.
ENCODE_TIMEOUT_SECONDS = 1800


class TrackError(ValueError):
    """Concise, user-readable track failure. Other errors stay in logs only."""


class BatchRunner:
    def __init__(
        self,
        service: BatchService,
        engine: SynthesisEngine,
        gate: OperationGate,
        select_voice: Callable[[str], None],
        encode_mp3: Callable[..., None],
        poll_seconds: float = 5.0,
    ) -> None:
        self.service = service
        self.engine = engine
        self.gate = gate
        self.select_voice = select_voice
        self.encode_mp3 = encode_mp3
        self.poll_seconds = poll_seconds
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        if self._thread is None:
            self._thread = threading.Thread(target=self._loop, name="batch-runner", daemon=True)
            self._thread.start()

    def stop(self, timeout: float = 1.0) -> None:
        self._stop.set()
        self.service.wake.set()
        if self._thread is not None:
            self._thread.join(timeout)

    def _loop(self) -> None:
        while not self._stop.is_set():
            self.service.wake.clear()
            try:
                worked = self.run_once()
            except Exception:
                LOGGER.exception("Batch runner iteration failed")
                worked = False
            if not worked:
                self.service.wake.wait(self.poll_seconds)

    def run_once(self) -> bool:
        """Process the oldest queued batch until it pauses, stops, or finishes. False when idle."""
        manifest = self.service.claim_next(self.engine.ready)
        if manifest is None:
            return False
        batch_id = manifest.batch_id
        LOGGER.info("Batch %s running", batch_id)
        try:
            while True:
                manifest = self.service.load(batch_id)
                if manifest.state != "running":
                    break
                track = next((item for item in manifest.tracks if item.state == "pending"), None)
                if track is None:
                    self.service.finish(batch_id)
                    break
                if self._convert(manifest, track) != "running":
                    break
        except KeyError:
            LOGGER.info("Batch %s disappeared while running", batch_id)
        LOGGER.info("Batch %s left the runner", batch_id)
        return True

    def _convert(self, manifest: BatchManifest, track: BatchTrack) -> str:
        batch_id, number = manifest.batch_id, track.number
        try:
            return self._track(manifest, track)
        except KeyError:
            raise
        except Exception as error:
            LOGGER.exception("Batch %s track %03d failed", batch_id, number)
            self.service.clear_work(batch_id, number)
            message = str(error) if isinstance(error, TrackError) else GENERIC_FAILURE
            return self.service.fail_track(batch_id, number, message)

    def _track(self, manifest: BatchManifest, track: BatchTrack) -> str:
        repository = self.service.repository
        batch_id, number = manifest.batch_id, track.number
        source = repository.source_path(batch_id, number)
        if not source.is_file() or sha256_file(source) != track.source_sha256:
            raise TrackError("Source text changed after the batch was created. Delete the batch and upload it again.")
        chunks = chunk_text(source.read_text(encoding="utf-8"), CHUNK_CHARS)
        if len(chunks) != track.chunk_count:
            raise TrackError("Source text no longer matches the batch. Delete the batch and upload it again.")
        work = repository.work_dir(batch_id, number)
        done = self._prepare_checkpoints(manifest, track, work)
        state = self.service.begin_track(batch_id, number, done)
        if state != "running":
            return state
        if done:
            LOGGER.info("Batch %s track %03d resumes at chunk %s/%s", batch_id, number, done + 1, len(chunks))
        for index in range(done, len(chunks)):
            started = time.monotonic()
            temporary = work / f".chunk-{index:05d}.wav"
            with self.gate.batch_step():
                # An interactive job may have loaded another voice since the last chunk.
                if self.gate.conditioning_owner != manifest.voice_id:
                    self.select_voice(manifest.voice_id)
                self.engine.synthesize([chunks[index]], temporary, manifest.language_id, SENTENCE_SILENCE_MS,
                                       PARAGRAPH_SILENCE_MS, manifest.synthesis_seed, seed_offset=index)
            validate_output(temporary)
            os.replace(temporary, work / f"chunk-{index:05d}.wav")
            LOGGER.info("Batch %s track %03d chunk %s/%s", batch_id, number, index + 1, len(chunks))
            state = self.service.chunk_done(batch_id, number, index + 1, time.monotonic() - started)
            if state != "running":
                return state
        wav = work / "track.wav"
        assemble_chunks([work / f"chunk-{index:05d}.wav" for index in range(len(chunks))], chunks, wav,
                        SENTENCE_SILENCE_MS, PARAGRAPH_SILENCE_MS)
        duration = validate_output(wav)
        output = repository.track_path(batch_id, number)
        output.parent.mkdir(exist_ok=True)
        temporary_mp3 = output.with_name(f".{output.stem}.tmp.mp3")
        try:
            self.encode_mp3(wav, temporary_mp3, timeout=ENCODE_TIMEOUT_SECONDS)
            digest = sha256_file(temporary_mp3)
            os.replace(temporary_mp3, output)
        finally:
            temporary_mp3.unlink(missing_ok=True)
        state = self.service.complete_track(batch_id, number, digest, duration)
        self.service.clear_work(batch_id, number)
        LOGGER.info("Batch %s track %03d completed", batch_id, number)
        return state

    def _prepare_checkpoints(self, manifest: BatchManifest, track: BatchTrack, work: Path) -> int:
        """Keep checkpoints only for the identical synthesis identity; return contiguous valid chunks."""
        identity = {
            "voice_id": manifest.voice_id, "language_id": manifest.language_id,
            "synthesis_seed": manifest.synthesis_seed, "model_revision": manifest.model_revision,
            "source_revision": manifest.source_revision,
            "conditioning_format_version": manifest.conditioning_format_version,
            "source_sha256": track.source_sha256, "chunk_count": track.chunk_count,
        }
        marker = work / CHECKPOINT_NAME
        try:
            compatible = json.loads(marker.read_text(encoding="utf-8")) == identity
        except (OSError, ValueError):
            compatible = False
        if not compatible:
            if work.exists():
                LOGGER.info("Batch %s track %03d discards incompatible checkpoints", manifest.batch_id, track.number)
            self.service.clear_work(manifest.batch_id, track.number)
            work.mkdir(parents=True)
            temporary = work / f".{CHECKPOINT_NAME}"
            temporary.write_text(json.dumps(identity, sort_keys=True), encoding="utf-8")
            os.replace(temporary, marker)
            return 0
        done = 0
        while done < track.chunk_count:
            try:
                pcm_wav_metrics(work / f"chunk-{done:05d}.wav")
            except Exception:
                break
            done += 1
        return done
