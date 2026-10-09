"""Concatenate per-chunk PCM checkpoints with the same silences as ChatterboxEngine.synthesize."""
from __future__ import annotations

from pathlib import Path
import wave

from .chunking import TextChunk

_BLOCK_FRAMES = 65536


def assemble_chunks(
    chunk_paths: list[Path],
    chunks: list[TextChunk],
    output_path: Path,
    sentence_silence_ms: int,
    paragraph_silence_ms: int,
) -> None:
    if not chunk_paths or len(chunk_paths) != len(chunks):
        raise ValueError("Every chunk needs exactly one checkpoint")
    parameters = None
    with wave.open(str(output_path), "wb") as output:
        for index, (path, chunk) in enumerate(zip(chunk_paths, chunks)):
            with wave.open(str(path), "rb") as source:
                current = (source.getnchannels(), source.getsampwidth(), source.getframerate())
                if parameters is None:
                    parameters = current
                    output.setnchannels(current[0])
                    output.setsampwidth(current[1])
                    output.setframerate(current[2])
                elif current != parameters:
                    raise ValueError("Chunk checkpoints use different audio formats")
                while frames := source.readframes(_BLOCK_FRAMES):
                    output.writeframes(frames)
            if index < len(chunks) - 1:
                silence_ms = paragraph_silence_ms if chunk.paragraph_break_after else sentence_silence_ms
                channels, width, rate = parameters
                # Same sample count as the engine: int(sr * ms / 1000), no silence after the last chunk.
                output.writeframes(bytes(int(rate * silence_ms / 1000) * channels * width))
