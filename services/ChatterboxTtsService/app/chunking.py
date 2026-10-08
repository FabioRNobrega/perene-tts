from __future__ import annotations

from dataclasses import dataclass
import re


_SENTENCE_BOUNDARY = re.compile(r"(?<=[.!?])\s+")


@dataclass(frozen=True)
class TextChunk:
    text: str
    paragraph_break_after: bool


def chunk_text(text: str, max_chars: int) -> list[TextChunk]:
    paragraphs = [paragraph.strip() for paragraph in re.split(r"\n\s*\n", text) if paragraph.strip()]
    if not paragraphs:
        raise ValueError("Preview text cannot be empty")

    chunks: list[TextChunk] = []
    for paragraph_index, paragraph in enumerate(paragraphs):
        normalized = " ".join(paragraph.split())
        sentences = _SENTENCE_BOUNDARY.split(normalized)
        paragraph_chunks: list[str] = []
        current = ""

        for sentence in sentences:
            for segment in _split_oversized(sentence.strip(), max_chars):
                candidate = f"{current} {segment}".strip()
                if current and len(candidate) > max_chars:
                    paragraph_chunks.append(current)
                    current = segment
                else:
                    current = candidate

        if current:
            paragraph_chunks.append(current)

        for chunk_index, chunk in enumerate(paragraph_chunks):
            chunks.append(
                TextChunk(
                    text=chunk,
                    paragraph_break_after=(
                        paragraph_index < len(paragraphs) - 1
                        and chunk_index == len(paragraph_chunks) - 1
                    ),
                )
            )

    return chunks


def _split_oversized(text: str, max_chars: int) -> list[str]:
    if len(text) <= max_chars:
        return [text] if text else []

    words = text.split()
    segments: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if current and len(candidate) > max_chars:
            segments.append(current)
            current = word
        else:
            current = candidate
    if current:
        segments.append(current)
    return segments

