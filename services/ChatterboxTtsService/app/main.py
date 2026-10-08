from __future__ import annotations

from contextlib import asynccontextmanager
import logging
import os
import asyncio
from pathlib import Path
import tempfile
import threading

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel, Field

from .engine import ChatterboxEngine, SynthesisError
from .studio import BusyError, MAX_UPLOAD, MODEL_REVISION, REFERENCE_FORMATS, StudioService
from .voice_store import VoiceStoreError


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
        limit = MAX_UPLOAD + 64 * 1024 if scope["path"] == "/voices" else 64 * 1024
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


def create_app(studio: StudioService | None = None) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app):
        stop = threading.Event()
        loader = None
        if studio is None:
            device = select_device(os.getenv("TTS_DEVICE", "auto"))
            engine = ChatterboxEngine(MODEL_REVISION, device=device)
            app.state.studio = StudioService(Path(os.getenv("TTS_DATA_DIR", "/data")), engine)
            loader = threading.Thread(target=load_model_with_retry, args=(engine, stop), daemon=True)
            loader.start()
        try:
            yield
        finally:
            stop.set()
            if loader is not None:
                await asyncio.to_thread(loader.join, 1)

    app = FastAPI(title="Perene TTS Worker", lifespan=lifespan)
    app.add_middleware(RequestSizeLimit)
    if studio is not None:
        app.state.studio = studio

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

    @app.get("/health")
    def health():
        return app.state.studio.health()

    @app.get("/voices")
    def voices():
        return app.state.studio.voices()

    @app.get("/creations")
    def creations():
        return app.state.studio.creations()

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

    return app


app = create_app()
