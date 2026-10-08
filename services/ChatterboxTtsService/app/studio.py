"""Local studio orchestration. One worker process owns all mutable model state."""
from __future__ import annotations

from dataclasses import asdict, dataclass, replace
from datetime import datetime, timezone
import json
import logging
import os
from pathlib import Path
import subprocess
import tempfile
import threading
from uuid import UUID, uuid4

from .audio_validation import validate_reference, validate_output, pcm_wav_metrics
from .chunking import chunk_text
from .engine import SynthesisEngine, SynthesisError
from .voice_store import LocalVoiceStore, VoiceCompatibility

MODEL_REVISION = "5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18"
SOURCE_REVISION = "5de7a54aa4e5e2baadb0182dde554908b48b85c2"
MAX_UPLOAD = 20 * 1024 * 1024
# Upload extension -> explicit FFmpeg demuxer; never let FFmpeg probe client input.
REFERENCE_FORMATS = {".mp3": "mp3", ".wav": "wav"}
MAX_TEXT = 5000
PREVIEWS = {
    "en": "Hello! Welcome to PereneTTS. This is a quick voice test to hear how natural and clear my voice sounds.",
    "pt": "Olá! Bem-vindo ao PereneTTS. Este é um teste rápido de voz para ouvir como minha voz soa natural e clara.",
    "sv": "Hej! Välkommen till PereneTTS. Det här är ett snabbt rösttest för att höra hur naturlig och tydlig min röst låter.",
}
LOGGER = logging.getLogger(__name__)


class BusyError(RuntimeError):
    pass


@dataclass(frozen=True)
class Job:
    job_id: str
    status: str = "running"
    message: str = "Preparing audio"
    percent: int = 0
    voice_id: str | None = None
    error: str | None = None
    audio_url: str | None = None


def canonical_id(value: str) -> str:
    try:
        parsed = str(UUID(value))
    except (ValueError, AttributeError) as error:
        raise ValueError("Invalid ID") from error
    if value != parsed:
        raise ValueError("Invalid ID")
    return parsed


def ffmpeg(*arguments: str) -> None:
    try:
        subprocess.run(
            ["ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-y", *arguments],
            check=True, capture_output=True, timeout=120,
        )
    except (subprocess.SubprocessError, OSError) as error:
        raise ValueError("Audio conversion failed. Use a readable MP3 or WAV recording.") from error


class StudioService:
    def __init__(self, root: Path, engine: SynthesisEngine):
        self.root = root.resolve()
        self.engine = engine
        self.store = LocalVoiceStore(self.root / "voices", self.root / "outputs")
        self.compatibility = VoiceCompatibility(SOURCE_REVISION, MODEL_REVISION, "multilingual-v3", 2)
        self._gate = threading.Lock()
        self._jobs_lock = threading.Lock()
        self._jobs: dict[str, Job] = {}
        for name in ("voices", "outputs", "uploads"):
            (self.root / name).mkdir(parents=True, exist_ok=True)

    def health(self) -> dict:
        return {"model_ready": self.engine.ready, "device": self.engine.device,
                "supported_languages": list(PREVIEWS),
                "load_error": "Model loading failed; retrying automatically. Check worker logs." if self.engine.load_error else None}

    def voices(self) -> list[dict]:
        result = []
        for metadata in self.store.list_metadata():
            sidecar = self.store.voice_dir(metadata.voice_id) / "name.json"
            name = json.loads(sidecar.read_text())["name"] if sidecar.exists() else metadata.voice_id
            result.append({"voice_id": metadata.voice_id, "language_id": metadata.language_id, "name": name})
        return result

    def creations(self) -> list[dict]:
        result = []
        for audio in (self.root / "outputs").glob("*.mp3"):
            try:
                canonical_id(audio.stem)
            except ValueError:
                continue
            entry = {"job_id": audio.stem, "kind": "audio", "voice_id": None,
                     "voice_name": "Earlier creation", "language_id": None, "text": "",
                     "created_at": datetime.fromtimestamp(audio.stat().st_mtime, timezone.utc).isoformat()}
            sidecar = audio.with_suffix(".json")
            try:
                metadata = json.loads(sidecar.read_text())
                if not isinstance(metadata, dict):
                    raise ValueError("Invalid creation metadata")
                # Only our public fields are exposed; legacy audio remains playable without metadata.
                entry.update({key: metadata[key] for key in entry if key in metadata and key != "job_id"})
            except (OSError, ValueError, TypeError):
                pass
            result.append(entry)
        return sorted(result, key=lambda entry: entry["created_at"], reverse=True)

    def job(self, job_id: str) -> dict:
        canonical_id(job_id)
        with self._jobs_lock:
            if job_id not in self._jobs:
                raise KeyError("Job not found. The worker may have restarted; retry the operation.")
            return asdict(self._jobs[job_id])

    def audio_path(self, job_id: str) -> Path:
        path = self.root / "outputs" / f"{canonical_id(job_id)}.mp3"
        if not path.is_file():
            raise KeyError("Audio not found")
        return path

    def create_voice(self, upload: Path, name: str, language: str) -> dict:
        try:
            name = name.strip()
            if not name or len(name) > 80:
                raise ValueError("Voice name must contain 1–80 characters")
            if language not in PREVIEWS:
                raise ValueError("Choose English, Portuguese, or Swedish")
            return self._start(lambda job: self._create(job, upload, name, language))
        except Exception:
            upload.unlink(missing_ok=True)
            raise

    def speech(self, voice_id: str, text: str) -> dict:
        canonical_id(voice_id)
        text = text.strip()
        if not text or len(text) > MAX_TEXT:
            raise ValueError("Text must contain 1–5,000 characters")
        metadata = next((voice for voice in self.store.list_metadata() if voice.voice_id == voice_id), None)
        if metadata is None:
            raise KeyError("Voice not found")
        return self._start(lambda job: self._synthesize(job, metadata.language_id, text, voice_id))

    def _start(self, action) -> dict:
        if not self.engine.ready:
            raise SynthesisError("Model is not ready. Wait for loading or check worker logs.")
        if not self._gate.acquire(blocking=False):
            raise BusyError("Another operation is running. Please try again when it finishes.")
        job = Job(str(uuid4()))
        with self._jobs_lock:
            # Keep memory bounded; audio and voice artifacts remain on disk.
            if len(self._jobs) >= 100:
                self._jobs.pop(next(iter(self._jobs)))
            self._jobs[job.job_id] = job
        try:
            threading.Thread(target=self._run, args=(job.job_id, action), daemon=True).start()
        except Exception:
            self._gate.release()
            raise
        return asdict(job)

    def _run(self, job_id, action):
        try:
            action(job_id)
            self._update(job_id, status="completed", percent=100, message="Your audio is ready",
                         audio_url=f"/audio/{job_id}")
        except Exception as error:
            LOGGER.exception("Job %s failed", job_id)
            # Validation errors are intentionally user-readable; inference/storage details stay in logs.
            message = str(error) if isinstance(error, ValueError) else "Audio generation failed. Check worker logs and retry."
            self._update(job_id, status="failed", error=message, message=message)
        finally:
            self._gate.release()

    def _update(self, job_id, **values):
        with self._jobs_lock:
            self._jobs[job_id] = replace(self._jobs[job_id], **values)

    def _create(self, job_id, upload, name, language):
        try:
            with tempfile.TemporaryDirectory(dir=self.root / "uploads") as temporary:
                reference = Path(temporary) / "reference.wav"
                # protocol whitelist blocks remote playlist fetches; duration cap bounds decoding work.
                ffmpeg("-protocol_whitelist", "file,pipe", "-f", REFERENCE_FORMATS[upload.suffix], "-i", str(upload),
                       "-t", "61", "-vn", "-ac", "1", "-ar", "24000", "-c:a", "pcm_s16le", str(reference))
                validate_reference(reference)
                duration, _ = pcm_wav_metrics(reference)
                if duration > 60:
                    raise ValueError("Reference audio must be between 3 and 60 seconds")
                self._update(job_id, message="Preparing your voice", percent=10)
                decision = self.store.resolve(language, reference, self.compatibility)
                self._load(decision)
                voice_id = decision.voice_id
                sidecar = self.store.voice_dir(voice_id) / "name.json"
                # Preserve original name when an identical reference is uploaded again.
                if not sidecar.exists():
                    temporary_name = sidecar.with_suffix(".tmp")
                    temporary_name.write_text(json.dumps({"name": name}, ensure_ascii=False))
                    os.replace(temporary_name, sidecar)
                self._update(job_id, voice_id=voice_id)
                self._render(job_id, language, PREVIEWS[language], voice_id, "preview")
        finally:
            upload.unlink(missing_ok=True)

    def _load(self, decision):
        validate_reference(decision.reference_path)
        if decision.status == "loaded":
            try:
                self.engine.load_conditioning(decision.conditioning_path)
                return
            except SynthesisError:
                decision = self.store.require_regeneration(decision)
        temporary = self.store.temporary_conditioning_path(decision.voice_id)
        try:
            self.engine.prepare_conditioning(decision.reference_path)
            self.engine.save_conditioning(temporary)
            self.engine.load_conditioning(temporary)
            self.store.publish(decision, temporary, self.compatibility)
        finally:
            temporary.unlink(missing_ok=True)

    def _synthesize(self, job_id, language, text, voice_id):
        decision = self.store.resolve(language, self.store.reference_path(voice_id), self.compatibility, voice_id)
        self._update(job_id, voice_id=voice_id, message="Loading saved voice", percent=10)
        self._load(decision)
        self._render(job_id, language, text, voice_id, "speech")

    def _render(self, job_id, language, text, voice_id, kind):
        with tempfile.TemporaryDirectory(dir=self.root / "outputs") as temporary:
            wav = Path(temporary) / "speech.wav"
            mp3 = Path(temporary) / "speech.mp3"
            self.engine.synthesize(chunk_text(text, 280), wav, language, 180, 420, 1234,
                                   lambda current, total: self._update(job_id,
                                   percent=25 + int(65 * current / total),
                                   message=f"Generating speech · {current}/{total}"))
            validate_output(wav)
            self._update(job_id, message="Encoding MP3", percent=95)
            ffmpeg("-i", str(wav), "-codec:a", "libmp3lame", "-b:a", "192k", str(mp3))
            if not mp3.is_file() or not mp3.stat().st_size:
                raise RuntimeError("MP3 encoder produced no audio")
            voice = next(voice for voice in self.voices() if voice["voice_id"] == voice_id)
            metadata = Path(temporary) / "creation.json"
            metadata.write_text(json.dumps({"job_id": job_id, "kind": kind,
                "voice_id": voice_id, "voice_name": voice["name"], "language_id": language,
                "text": text, "created_at": datetime.now(timezone.utc).isoformat()}, ensure_ascii=False))
            os.replace(metadata, self.root / "outputs" / f"{job_id}.json")
            os.replace(mp3, self.root / "outputs" / f"{job_id}.mp3")
