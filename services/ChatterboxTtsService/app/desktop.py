"""Desktop sidecar entry point (the PyInstaller script for the macOS app).

The host launches this process, reads the single ready line from stdout, and talks to the worker over loopback
with a per-launch token. Closing stdin (or the host dying) shuts the worker down gracefully. Docker and mac-worker
modes keep running ``uvicorn app.main:app`` and never import this module.
"""
from __future__ import annotations

import logging
import os
import socket
import sys
import threading
from typing import BinaryIO, Callable, Protocol, TextIO

READY_PREFIX = "PERENE_WORKER_READY"
# Names only are logged; values (the token in particular) never are.
DESKTOP_SETTINGS = (
    "TTS_API_TOKEN", "TTS_DATA_DIR", "TTS_MODEL_DIR", "TTS_FFMPEG", "TTS_DEVICE", "TTS_BATCH_ENABLED",
    "PYTORCH_ENABLE_MPS_FALLBACK", "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HOME", "NUMBA_CACHE_DIR",
    "PKUSEG_HOME",
)
LOGGER = logging.getLogger("perene.desktop")


class Server(Protocol):
    should_exit: bool

    def run(self, sockets: list[socket.socket] | None = None) -> None: ...


def bind_loopback() -> socket.socket:
    # Port 0: the OS picks a free port. Never 0.0.0.0, so nothing outside this Mac can connect.
    return socket.create_server(("127.0.0.1", 0))


def watch_stdin(stdin: BinaryIO, server: Server) -> None:
    """The host keeps stdin open for the worker's lifetime; end-of-file means it closed the pipe or died."""
    try:
        while stdin.read(65536):
            pass
    except (OSError, ValueError):
        pass
    LOGGER.info("Host closed stdin; shutting down")
    server.should_exit = True


def desktop_main(stdin: BinaryIO, stdout: TextIO, server_factory: Callable[[], Server],
                 bind: Callable[[], socket.socket] = bind_loopback) -> int:
    sock = bind()
    port = sock.getsockname()[1]
    received = [name for name in DESKTOP_SETTINGS if os.getenv(name)]
    LOGGER.info("Desktop settings received: %s", ", ".join(received) or "none")
    # The listening socket already queues connections, so the host may connect before Uvicorn's loop starts.
    stdout.write(f"{READY_PREFIX} port={port}\n")
    stdout.flush()
    server = server_factory()
    threading.Thread(target=watch_stdin, args=(stdin, server), name="stdin-watch", daemon=True).start()
    server.run(sockets=[sock])
    return 0


def create_server() -> Server:
    import uvicorn

    from app.main import app

    # One server, one worker process: the shared model lock and single batch runner depend on it.
    # Access logs are off because the UI polls every few seconds.
    return uvicorn.Server(uvicorn.Config(app, workers=1, access_log=False, log_config=None))


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    raise SystemExit(desktop_main(sys.stdin.buffer, sys.stdout, create_server))
