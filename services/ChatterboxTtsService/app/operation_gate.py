"""Model exclusivity shared by interactive studio jobs and the batch runner."""
from __future__ import annotations

from contextlib import contextmanager
import threading
from typing import Callable, Iterator


class BusyError(RuntimeError):
    pass


class OperationGate:
    """One model lock, at most one interactive operation, and interactive priority over batch chunks.

    An interactive operation reserves its slot in the HTTP request (so a second one gets 409 at once)
    and acquires the model in its job thread. While it is reserved, the batch runner does not start
    another chunk, so the interactive job runs as soon as the current chunk finishes.
    """

    def __init__(self) -> None:
        self._model = threading.Lock()
        self._condition = threading.Condition()
        self._interactive_reserved = False
        # Voice ID whose conditioning is currently loaded on the engine, or None when unknown.
        self.conditioning_owner: str | None = None

    @property
    def interactive_reserved(self) -> bool:
        with self._condition:
            return self._interactive_reserved

    def reserve_interactive(self) -> None:
        with self._condition:
            if self._interactive_reserved:
                raise BusyError("Another operation is running. Please try again when it finishes.")
            self._interactive_reserved = True

    def release_interactive(self) -> None:
        with self._condition:
            self._interactive_reserved = False
            self._condition.notify_all()

    @contextmanager
    def interactive(self, on_wait: Callable[[], None] | None = None) -> Iterator[None]:
        """Hold the model for a reserved interactive operation; on_wait runs if a batch chunk holds it."""
        if not self._model.acquire(blocking=False):
            if on_wait is not None:
                on_wait()
            self._model.acquire()
        try:
            yield
        finally:
            self._model.release()

    @contextmanager
    def batch_step(self) -> Iterator[None]:
        """Hold the model for exactly one batch chunk, yielding first to any reserved interactive operation."""
        while True:
            with self._condition:
                self._condition.wait_for(lambda: not self._interactive_reserved)
            self._model.acquire()
            with self._condition:
                if not self._interactive_reserved:
                    break
            # An interactive operation arrived between the check and the acquire; let it go first.
            self._model.release()
        try:
            yield
        finally:
            self._model.release()
