import pytest

from app.batch_project import (MAX_FILE_BYTES, MAX_FILES, MAX_TOTAL_BYTES, BatchInputError, BatchTooLargeError,
                               build_source, check_file_count, check_file_size, output_name,
                               sanitize_display_name, validate_batch_name)
from app.chunking import chunk_text


@pytest.mark.parametrize("name", ["Lumky", "  In Milton Lumky Territory  ", "x" * 80, "Livro ção"])
def test_valid_batch_names_are_trimmed(name):
    assert validate_batch_name(name) == name.strip()


@pytest.mark.parametrize("name", ["", "   ", "x" * 81, "a/b", "a\\b", "tab\tname", "nul\x00", "del\x7f"])
def test_invalid_batch_names_are_rejected(name):
    with pytest.raises(BatchInputError):
        validate_batch_name(name)


def test_file_count_and_size_bounds():
    check_file_count(1)
    check_file_count(MAX_FILES)
    for count in (0, MAX_FILES + 1):
        with pytest.raises(BatchInputError):
            check_file_count(count)
    check_file_size("a.txt", MAX_FILE_BYTES, MAX_FILE_BYTES)
    with pytest.raises(BatchTooLargeError, match="a.txt: file exceeds 1 MiB"):
        check_file_size("a.txt", MAX_FILE_BYTES + 1, MAX_FILE_BYTES + 1)
    with pytest.raises(BatchTooLargeError, match="10 MiB"):
        check_file_size("a.txt", 10, MAX_TOTAL_BYTES + 1)


def test_utf8_with_bom_is_accepted_and_normalized():
    source = build_source("Intro.txt", "\ufeff  Olá, mundo!\n".encode("utf-8"))
    assert source.text == "Olá, mundo!"
    assert source.content == "Olá, mundo!".encode("utf-8")
    assert len(source.sha256) == 64
    assert source.chunk_count == 1


@pytest.mark.parametrize("data,reason", [
    (b"\xff\xfe\x00bad", "not valid UTF-8"),
    (b" \n\t \n", "empty"),
    (b"x" * 200_001, "200,000 characters"),
])
def test_invalid_text_names_the_file(data, reason):
    with pytest.raises(BatchInputError, match=f"chapter-002.txt: .*{reason}"):
        build_source("chapter-002.txt", data)


def test_oversized_and_non_text_files_are_rejected():
    with pytest.raises(BatchTooLargeError):
        build_source("big.txt", b"x" * (MAX_FILE_BYTES + 1))
    with pytest.raises(BatchInputError, match="only .txt"):
        build_source("cover.jpg", b"text")
    assert build_source("limit.txt", b"  " + b"x" * 200_000 + b"  ").chunk_count >= 1


@pytest.mark.parametrize("raw,expected", [
    ("chapter-001.txt", "chapter-001.txt"),
    ("../../etc/passwd.txt", "passwd.txt"),
    ("C:\\Users\\me\\Intro.txt", "Intro.txt"),
    ("bad\x00\x1fname.txt", "badname.txt"),
    ("=?utf-8?B?SW50cm9kdcOnw6NvLnR4dA==?=", "Introdução.txt"),
    ("..", "file 004.txt"),
    ("", "file 004.txt"),
    (None, "file 004.txt"),
])
def test_display_names_are_sanitized_labels(raw, expected):
    assert sanitize_display_name(raw, 4) == expected


def test_output_names_are_zero_padded():
    assert output_name("Lumky", 7) == "Lumky 007.mp3"
    assert output_name("Lumky", 120) == "Lumky 120.mp3"


def test_chunk_count_matches_chunker():
    text = ("A sentence that is reasonably long. " * 40 + "\n\n" + "Another paragraph. " * 20).strip()
    assert build_source("long.txt", text.encode()).chunk_count == len(chunk_text(text, 280))
