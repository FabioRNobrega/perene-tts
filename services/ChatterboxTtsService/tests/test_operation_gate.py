import threading

import pytest

from app.operation_gate import BusyError, OperationGate


def test_second_interactive_operation_is_busy():
    gate = OperationGate()
    gate.reserve_interactive()
    with pytest.raises(BusyError):
        gate.reserve_interactive()
    gate.release_interactive()
    gate.reserve_interactive()
    gate.release_interactive()


def test_interactive_waits_for_current_chunk_then_goes_first():
    gate = OperationGate()
    order, waited = [], threading.Event()
    chunk_started, finish_chunk = threading.Event(), threading.Event()

    def batch():
        with gate.batch_step():
            order.append("chunk-1")
            chunk_started.set()
            assert finish_chunk.wait(5)
        with gate.batch_step():
            order.append("chunk-2")

    def interactive():
        try:
            with gate.interactive(waited.set):
                order.append("interactive")
        finally:
            gate.release_interactive()

    batch_thread = threading.Thread(target=batch)
    batch_thread.start()
    assert chunk_started.wait(5)
    gate.reserve_interactive()
    interactive_thread = threading.Thread(target=interactive)
    interactive_thread.start()
    assert waited.wait(5)
    finish_chunk.set()
    for thread in (batch_thread, interactive_thread):
        thread.join(5)
        assert not thread.is_alive()
    assert order == ["chunk-1", "interactive", "chunk-2"]


def test_batch_step_waits_while_an_interactive_operation_is_reserved():
    gate = OperationGate()
    gate.reserve_interactive()
    entered = threading.Event()

    def batch():
        with gate.batch_step():
            entered.set()

    thread = threading.Thread(target=batch)
    thread.start()
    assert not entered.wait(0.2)
    gate.release_interactive()
    assert entered.wait(5)
    thread.join(5)


def test_interactive_without_contention_does_not_report_waiting():
    gate = OperationGate()
    gate.reserve_interactive()
    waited = []
    with gate.interactive(lambda: waited.append(True)):
        pass
    gate.release_interactive()
    assert waited == []


def test_conditioning_owner_is_tracked():
    gate = OperationGate()
    assert gate.conditioning_owner is None
    gate.conditioning_owner = "voice-a"
    assert gate.conditioning_owner == "voice-a"
