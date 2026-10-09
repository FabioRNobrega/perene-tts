from __future__ import annotations

from contextlib import asynccontextmanager
import io
import logging
import os
import asyncio
from pathlib import Path
import tempfile
import threading
from urllib.parse import quote
import zipfile

from fastapi import FastAPI, File, Form, HTTPException, Response, UploadFile
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse
from pydantic import BaseModel, Field

from .batch_manifest import BatchManifestError
from .batch_project import MAX_FILES, MAX_TOTAL_BYTES, BatchTooLargeError, check_file_size, sanitize_display_name
from .batch_runner import BatchRunner
from .batch_service import BatchService, InvalidTransition
from .engine import ChatterboxEngine, SynthesisError
from .studio import BusyError, MAX_UPLOAD, MODEL_REVISION, REFERENCE_FORMATS, StudioService, encode_mp3
from .voice_store import VoiceStoreError

# Older creations carry these keys as null so the /creations wire shape stays uniform.
BATCH_CREATION_FIELDS = ("batch_id", "batch_name", "track_number", "track_count", "source_name")


def select_device(requested: str) -> str:
    import torch
    if requested == "auto":
        if torch.cuda.is_available():
            return "cuda"
        if torch.backends.mps.is_available():
            return "mps"
        return "cpu"
    if requested not in ("cpu", "mps", "cuda"):
        raise ValueError("TTS_DEVICE must be auto, cpu, mps, or cuda")
    if requested == "mps" and not torch.backends.mps.is_available():
        raise ValueError("Metal/MPS is unavailable; use native macOS or TTS_DEVICE=cpu")
    if requested == "cuda" and not torch.cuda.is_available():
        raise ValueError("CUDA is unavailable")
    return requested


class SpeechRequest(BaseModel):
    voice_id: str = Field(min_length=36, max_length=36)
    text: str = Field(min_length=1, max_length=5000)


def load_model_with_retry(engine, stop: threading.Event):
    delay = 15
    while not stop.is_set():
        engine.load()
        if engine.ready:
            return
        logging.warning("Model loading failed; retrying in %s seconds", delay)
        if stop.wait(delay):
            return
        delay = min(delay * 2, 60)


class RequestSizeLimit:
    """Bound multipart parsing, including requests without Content-Length."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http" or scope["method"] != "POST":
            return await self.app(scope, receive, send)
        limit = {"/voices": MAX_UPLOAD + 64 * 1024, "/batches": MAX_TOTAL_BYTES + 256 * 1024}.get(
            scope["path"], 64 * 1024)
        headers = dict(scope.get("headers", []))
        try:
            size = int(headers.get(b"content-length", b"0"))
        except ValueError:
            size = limit + 1
        if size > limit:
            return await JSONResponse(status_code=413, content={"detail": "Request is too large"})(scope, receive, send)
        received = 0

        async def bounded_receive():
            nonlocal received
            message = await receive()
            if message["type"] == "http.request":
                received += len(message.get("body", b""))
                if received > limit:
                    raise HTTPException(413, "Request is too large")
            return message

        await self.app(scope, bounded_receive, send)


class _ZipSink(io.RawIOBase):
    """Non-seekable sink: zipfile writes data descriptors, and the response streams each block."""

    def __init__(self):
        self._buffer = bytearray()

    def writable(self):
        return True

    def write(self, data):
        self._buffer.extend(data)
        return len(data)

    def take(self) -> bytes:
        data = bytes(self._buffer)
        self._buffer.clear()
        return data


def stream_zip(entries: list[tuple[str, Path]]):
    sink = _ZipSink()
    # MP3 is already compressed, so entries are stored; files are read in blocks, never whole.
    with zipfile.ZipFile(sink, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, path in entries:
            with path.open("rb") as source, archive.open(name, "w", force_zip64=True) as target:
                while block := source.read(1024 * 1024):
                    target.write(block)
                    yield sink.take()
            yield sink.take()
    yield sink.take()


def attachment(filename: str) -> str:
    return f"attachment; filename*=utf-8''{quote(filename)}"


def batch_enabled() -> bool:
    return os.getenv("TTS_BATCH_ENABLED", "true").strip().lower() not in ("0", "false", "no", "off")


def create_app(studio: StudioService | None = None, batches: BatchService | None = None) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app):
        stop = threading.Event()
        loader = None
        runner = None
        if studio is None:
            device = select_device(os.getenv("TTS_DEVICE", "auto"))
            engine = ChatterboxEngine(MODEL_REVISION, device=device)
            app.state.studio = StudioService(Path(os.getenv("TTS_DATA_DIR", "/data")), engine)
            app.state.batches = BatchService(app.state.studio, enabled=batch_enabled())
            app.state.batches.recover()
            if app.state.batches.enabled:
                runner = BatchRunner(app.state.batches, engine, app.state.studio.gate,
                                     app.state.studio.select_voice, encode_mp3)
                runner.start()

            def load_then_wake():
                load_model_with_retry(engine, stop)
                app.state.batches.wake.set()

            loader = threading.Thread(target=load_then_wake, daemon=True)
            loader.start()
        try:
            yield
        finally:
            stop.set()
            if runner is not None:
                await asyncio.to_thread(runner.stop, 1)
            if loader is not None:
                await asyncio.to_thread(loader.join, 1)

    app = FastAPI(title="Perene TTS Worker", lifespan=lifespan)
    app.add_middleware(RequestSizeLimit)
    if studio is not None:
        app.state.studio = studio
        app.state.batches = batches or BatchService(studio)

    @app.exception_handler(ValueError)
    async def invalid(request, error):
        return JSONResponse(status_code=422, content={"detail": str(error)})

    @app.exception_handler(KeyError)
    async def missing(request, error):
        return JSONResponse(status_code=404, content={"detail": error.args[0]})

    @app.exception_handler(BusyError)
    async def busy(request, error):
        return JSONResponse(status_code=409, content={"detail": str(error)})

    @app.exception_handler(SynthesisError)
    async def unavailable(request, error):
        return JSONResponse(status_code=503, content={"detail": str(error)})

    @app.exception_handler(VoiceStoreError)
    async def storage(request, error):
        logging.exception("Voice storage failed", exc_info=error)
        return JSONResponse(status_code=500, content={"detail": "Voice storage is unavailable. Check worker logs."})

    @app.exception_handler(InvalidTransition)
    async def conflict(request, error):
        return JSONResponse(status_code=409, content={"detail": str(error)})

    @app.exception_handler(BatchTooLargeError)
    async def too_large(request, error):
        return JSONResponse(status_code=413, content={"detail": str(error)})

    @app.exception_handler(BatchManifestError)
    async def batch_storage(request, error):
        logging.exception("Batch storage failed", exc_info=error)
        return JSONResponse(status_code=500, content={"detail": "Batch storage is unavailable. Check worker logs."})

    @app.get("/health")
    def health():
        return {**app.state.studio.health(), "batch_enabled": app.state.batches.enabled}

    @app.get("/voices")
    def voices():
        return app.state.studio.voices()

    @app.get("/creations")
    def creations():
        entries = [{**dict.fromkeys(BATCH_CREATION_FIELDS), **entry} for entry in app.state.studio.creations()]
        entries.extend(app.state.batches.creations())
        return sorted(entries, key=lambda entry: entry["created_at"], reverse=True)

    @app.post("/voices", status_code=202)
    async def create_voice(name: str = Form(...), language: str = Form(...), audio: UploadFile = File(...)):
        suffix = Path(audio.filename or "").suffix.lower()
        if suffix not in REFERENCE_FORMATS:
            await audio.close()
            raise HTTPException(422, "Please upload an MP3 or WAV recording")
        descriptor, filename = tempfile.mkstemp(suffix=suffix, dir=app.state.studio.root / "uploads")
        path = Path(filename)
        accepted = False
        try:
            total = 0
            with os.fdopen(descriptor, "wb") as stream:
                while block := await audio.read(64 * 1024):
                    total += len(block)
                    if total > MAX_UPLOAD:
                        raise HTTPException(413, "Reference recording exceeds 20 MiB")
                    stream.write(block)
            if not total:
                raise HTTPException(422, "Reference recording is empty")
            result = app.state.studio.create_voice(path, name, language)
            accepted = True
            return result
        finally:
            await audio.close()
            if not accepted:
                path.unlink(missing_ok=True)

    @app.post("/speech", status_code=202)
    def speech(body: SpeechRequest):
        return app.state.studio.speech(body.voice_id, body.text)

    @app.get("/jobs/{job_id}")
    def job(job_id: str):
        return app.state.studio.job(job_id)

    @app.get("/audio/{job_id}")
    def audio(job_id: str):
        return FileResponse(app.state.studio.audio_path(job_id), media_type="audio/mpeg", filename="perene-speech.mp3")

    @app.get("/batches")
    def batches():
        return app.state.batches.list()

    @app.post("/batches", status_code=202)
    async def create_batch(name: str = Form(...), voice_id: str = Form(...), files: list[UploadFile] = File(...)):
        service: BatchService = app.state.batches
        try:
            if not service.enabled:
                raise SynthesisError("Batch audio is disabled on this worker.")
            if len(files) > MAX_FILES:
                raise HTTPException(422, f"A batch may contain at most {MAX_FILES} files")
            with tempfile.TemporaryDirectory(dir=app.state.studio.root / "uploads") as temporary:
                uploads, total = [], 0
                for number, upload in enumerate(files, start=1):
                    path = Path(temporary) / f"{number:03d}.txt"
                    size = 0
                    with path.open("wb") as stream:
                        while block := await upload.read(64 * 1024):
                            size += len(block)
                            total += len(block)
                            check_file_size(sanitize_display_name(upload.filename, number), size, total)
                            stream.write(block)
                    uploads.append((upload.filename, path))
                return await asyncio.to_thread(service.create, name, voice_id, uploads)
        finally:
            for upload in files:
                await upload.close()

    @app.get("/batches/{batch_id}")
    def batch(batch_id: str):
        return app.state.batches.detail(batch_id)

    @app.post("/batches/{batch_id}/pause")
    def pause_batch(batch_id: str):
        return app.state.batches.pause(batch_id)

    @app.post("/batches/{batch_id}/resume")
    def resume_batch(batch_id: str):
        return app.state.batches.resume(batch_id)

    @app.post("/batches/{batch_id}/stop")
    def stop_batch(batch_id: str):
        return app.state.batches.stop(batch_id)

    @app.post("/batches/{batch_id}/retry")
    def retry_batch(batch_id: str):
        return app.state.batches.retry(batch_id)

    @app.post("/batches/{batch_id}/tracks/{number}/retry")
    def retry_track(batch_id: str, number: int):
        return app.state.batches.retry_track(batch_id, number)

    @app.delete("/batches/{batch_id}", status_code=204)
    def delete_batch(batch_id: str):
        app.state.batches.delete(batch_id)
        return Response(status_code=204)

    @app.get("/batches/{batch_id}/tracks/{number}/audio")
    def track_audio(batch_id: str, number: int):
        path, filename = app.state.batches.track_audio(batch_id, number)
        return FileResponse(path, media_type="audio/mpeg", filename=filename)

    @app.get("/batches/{batch_id}/archive")
    def archive(batch_id: str):
        filename, entries = app.state.batches.archive(batch_id)
        return StreamingResponse(stream_zip(entries), media_type="application/zip",
                                 headers={"Content-Disposition": attachment(filename), "Cache-Control": "no-store"})

    return app


app = create_app()
