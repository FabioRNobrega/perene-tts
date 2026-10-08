from __future__ import annotations

from array import array
from pathlib import Path
import sys
import wave


class InvalidReferenceError(ValueError):
    pass


class OutputWriteError(RuntimeError):
    pass


def validate_reference(
    path: Path,
    minimum_seconds: float = 3.0,
    minimum_pcm_peak: int = 32,
) -> None:
    if not path.is_file():
        raise InvalidReferenceError(f"Reference audio is missing: {path}")
    try:
        duration, peak = pcm_wav_metrics(path)
        if duration < minimum_seconds:
            raise InvalidReferenceError(
                f"Reference audio must be at least {minimum_seconds:g} seconds; "
                f"received {duration:.3f} seconds"
            )
        if peak < minimum_pcm_peak:
            raise InvalidReferenceError("Reference audio is silent or too quiet")
    except InvalidReferenceError:
        raise
    except (wave.Error, EOFError, OSError, ValueError) as error:
        raise InvalidReferenceError(
            "Reference audio is not a readable 16-bit PCM WAV"
        ) from error


def validate_output(path: Path, minimum_pcm_peak: int = 32) -> float:
    if not path.is_file() or path.stat().st_size <= 44:
        raise OutputWriteError("Synthesis did not produce a non-empty WAV file")
    try:
        duration, peak = pcm_wav_metrics(path)
        if duration <= 0:
            raise OutputWriteError("Synthesized WAV contains no audio samples")
        if peak < minimum_pcm_peak:
            raise OutputWriteError("Synthesized WAV is silent or too quiet")
        return duration
    except OutputWriteError:
        raise
    except (wave.Error, EOFError, OSError, ValueError) as error:
        raise OutputWriteError(
            "Synthesized output is not a readable 16-bit PCM WAV"
        ) from error


def pcm_wav_metrics(path: Path) -> tuple[float, int]:
    with wave.open(str(path), "rb") as audio:
        if audio.getnframes() <= 0 or audio.getframerate() <= 0:
            raise ValueError("WAV contains no audio samples")
        if audio.getsampwidth() != 2:
            raise ValueError("WAV must use 16-bit PCM samples")
        if audio.getnchannels() not in (1, 2):
            raise ValueError("WAV must be mono or stereo")
        peak = 0
        while frames := audio.readframes(8192):
            samples = array("h")
            samples.frombytes(frames)
            if sys.byteorder != "little":
                samples.byteswap()
            peak = max(peak, max((abs(sample) for sample in samples), default=0))
        duration = audio.getnframes() / audio.getframerate()
        return duration, peak
