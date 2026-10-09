from array import array
import random
import wave

import pytest

from app.chunking import chunk_text
from app.wav_assembly import assemble_chunks

RATE = 24000


class SeededEngine:
    """Mirrors ChatterboxEngine.synthesize: one seeded waveform per chunk plus inter-chunk silences."""

    def synthesize(self, chunks, output, language, sentence_ms, paragraph_ms, seed, progress=None, *, seed_offset=0):
        samples = array("h")
        for index, chunk in enumerate(chunks):
            generator = random.Random(seed + seed_offset + index)
            samples.extend(generator.randint(-8000, 8000) for _ in range(200 + len(chunk.text)))
            if index < len(chunks) - 1:
                silence = paragraph_ms if chunk.paragraph_break_after else sentence_ms
                samples.extend([0] * int(RATE * silence / 1000))
        with wave.open(str(output), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(RATE)
            audio.writeframes(samples.tobytes())


def frames(path):
    with wave.open(str(path), "rb") as audio:
        return audio.getparams()[:3], audio.readframes(audio.getnframes())


def test_resumable_chunks_match_one_uninterrupted_call(tmp_path):
    text = "First sentence here. Second one follows. " * 12 + "\n\nA new paragraph. " * 3 + "\n\nThe end."
    chunks = chunk_text(text, 280)
    assert len(chunks) > 3 and any(chunk.paragraph_break_after for chunk in chunks)
    engine = SeededEngine()
    whole = tmp_path / "whole.wav"
    engine.synthesize(chunks, whole, "en", 180, 420, 1234)
    parts = []
    for index, chunk in enumerate(chunks):
        part = tmp_path / f"chunk-{index:05d}.wav"
        engine.synthesize([chunk], part, "en", 180, 420, 1234, seed_offset=index)
        parts.append(part)
    assembled = tmp_path / "assembled.wav"
    assemble_chunks(parts, chunks, assembled, 180, 420)
    assert frames(assembled) == frames(whole)


def test_no_trailing_silence_and_format_mismatch_rejected(tmp_path):
    chunks = chunk_text("Only one.", 280)
    engine = SeededEngine()
    single = tmp_path / "single.wav"
    engine.synthesize(chunks, single, "en", 180, 420, 1)
    assembled = tmp_path / "out.wav"
    assemble_chunks([single], chunks, assembled, 180, 420)
    assert frames(assembled) == frames(single)
    other = tmp_path / "other.wav"
    with wave.open(str(other), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16000)
        audio.writeframes(b"\x01\x00" * 10)
    with pytest.raises(ValueError):
        assemble_chunks([single, other], chunk_text("One. Two.", 5), tmp_path / "bad.wav", 180, 420)
