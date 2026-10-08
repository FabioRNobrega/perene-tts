from app.main import load_model_with_retry


class StopSignal:
    def __init__(self, stop_after=None):
        self.delays = []
        self.stop_after = stop_after

    def is_set(self):
        return False

    def wait(self, delay):
        self.delays.append(delay)
        return self.stop_after is not None and len(self.delays) >= self.stop_after


class LoadingEngine:
    def __init__(self, succeed_after=None):
        self.ready = False
        self.attempts = 0
        self.succeed_after = succeed_after

    def load(self):
        self.attempts += 1
        self.ready = self.succeed_after is not None and self.attempts >= self.succeed_after


def test_transient_model_failure_retries_then_stops_when_ready():
    engine = LoadingEngine(succeed_after=3)
    stop = StopSignal()
    load_model_with_retry(engine, stop)
    assert engine.ready and engine.attempts == 3
    assert stop.delays == [15, 30]


def test_repeated_failure_backs_off_and_shutdown_interrupts_retry():
    engine = LoadingEngine()
    stop = StopSignal(stop_after=4)
    load_model_with_retry(engine, stop)
    assert engine.attempts == 4
    assert stop.delays == [15, 30, 60, 60]
