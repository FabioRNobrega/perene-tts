"""Validate uploaded batch text files. Client file names are display labels only, never paths."""
from __future__ import annotations

from dataclasses import dataclass
from email.header import decode_header, make_header
import hashlib
import re

from .chunking import chunk_text
from .studio import CHUNK_CHARS

MAX_FILES = 200
MAX_FILE_BYTES = 1024 * 1024
MAX_TOTAL_BYTES = 10 * 1024 * 1024
MAX_FILE_CHARACTERS = 200_000
MAX_NAME = 80
MAX_DISPLAY_NAME = 255
_CONTROL = re.compile(r"[\x00-\x1f\x7f]")


class BatchInputError(ValueError):
    """User-readable batch validation error (mapped to 422)."""


class BatchTooLargeError(BatchInputError):
    """Upload exceeds a byte limit (mapped to 413)."""


@dataclass(frozen=True)
class BatchSource:
    display_name: str
    text: str
    sha256: str
    chunk_count: int

    @property
    def content(self) -> bytes:
        return self.text.encode("utf-8")


def validate_batch_name(name: str) -> str:
    name = name.strip() if isinstance(name, str) else ""
    if not name or len(name) > MAX_NAME:
        raise BatchInputError("Batch name must contain 1–80 characters")
    if "/" in name or "\\" in name or _CONTROL.search(name):
        raise BatchInputError("Batch name cannot contain /, \\, or control characters")
    return name


def sanitize_display_name(name: str | None, number: int) -> str:
    """Keep only the last path component, without control characters, as a label."""
    name = name or ""
    if name.startswith("=?") and name.endswith("?="):
        # .NET MultipartFormDataContent sends non-ASCII file names as RFC 2047 encoded-words.
        try:
            name = str(make_header(decode_header(name)))
        except (ValueError, LookupError):
            pass
    label = re.split(r"[\\/]", name)[-1]
    label = _CONTROL.sub("", label).strip()[:MAX_DISPLAY_NAME]
    return label if label not in ("", ".", "..") else f"file {number:03d}.txt"


def output_name(batch_name: str, number: int) -> str:
    return f"{batch_name} {number:03d}.mp3"


def check_file_count(count: int) -> None:
    if count < 1:
        raise BatchInputError("Choose at least one .txt file")
    if count > MAX_FILES:
        raise BatchInputError(f"A batch may contain at most {MAX_FILES} files")


def check_file_size(display_name: str, size: int, total: int) -> None:
    if size > MAX_FILE_BYTES:
        raise BatchTooLargeError(f"{display_name}: file exceeds 1 MiB")
    if total > MAX_TOTAL_BYTES:
        raise BatchTooLargeError("The batch exceeds 10 MiB in total")


def build_source(display_name: str, data: bytes) -> BatchSource:
    if not display_name.lower().endswith(".txt"):
        raise BatchInputError(f"{display_name}: only .txt files are accepted")
    if len(data) > MAX_FILE_BYTES:
        raise BatchTooLargeError(f"{display_name}: file exceeds 1 MiB")
    try:
        text = data.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise BatchInputError(f"{display_name}: file is not valid UTF-8 text") from error
    text = text.strip()
    if not text:
        raise BatchInputError(f"{display_name}: file is empty")
    if len(text) > MAX_FILE_CHARACTERS:
        raise BatchInputError(f"{display_name}: file exceeds 200,000 characters")
    try:
        chunk_count = len(chunk_text(text, CHUNK_CHARS))
    except ValueError as error:
        raise BatchInputError(f"{display_name}: file has no readable text") from error
    content = text.encode("utf-8")
    return BatchSource(display_name, text, hashlib.sha256(content).hexdigest(), chunk_count)
