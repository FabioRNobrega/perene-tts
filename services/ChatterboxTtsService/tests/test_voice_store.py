from __future__ import annotations

from dataclasses import replace
import hashlib
import json
import os
from pathlib import Path
from uuid import UUID, uuid4

import pytest

from app.voice_store import (
    LocalVoiceStore,
    VoiceCompatibility,
    VoiceStoreError,
    sha256_file,
)
from conftest import write_wav


@pytest.fixture
def compatibility() -> VoiceCompatibility:
    return VoiceCompatibility(
        source_revision="5de7a54aa4e5e2baadb0182dde554908b48b85c2",
        model_revision="5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18",
        model_name="multilingual-v3",
        format_version=1,
    )


@pytest.fixture
def store(tmp_path: Path) -> LocalVoiceStore:
    return LocalVoiceStore(tmp_path / "voices", tmp_path / "outputs")


def publish_fake_conditioning(
    store: LocalVoiceStore,
    decision,
    compatibility: VoiceCompatibility,
    content: bytes = b"fake-conditioning:valid",
):
    temporary = store.temporary_conditioning_path(
        decision.voice_id, compatibility.model_name
    )
    temporary.write_bytes(content)
    return store.publish(decision, temporary, compatibility)


def test_first_resolution_generates_uuid_and_publish_writes_metadata(
    store, compatibility, tmp_path: Path
) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)

    decision = store.resolve("en", reference, compatibility)
    metadata = publish_fake_conditioning(store, decision, compatibility)

    assert decision.status == "created"
    assert str(UUID(decision.voice_id)) == decision.voice_id
    assert store.conditioning_path(decision.voice_id).is_file()
    assert store.metadata_path(decision.voice_id).is_file()
    assert metadata.reference_sha256 == sha256_file(reference)
    assert metadata.conditioning_sha256 == sha256_file(
        store.conditioning_path(decision.voice_id)
    )
    assert metadata.source_revision == compatibility.source_revision
    assert metadata.model_revision == compatibility.model_revision
    assert metadata.format_version == compatibility.format_version


def test_compatible_resolution_reuses_stable_voice(store, compatibility, tmp_path) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)
    created = store.resolve("en", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility)

    loaded = store.resolve("en", reference, compatibility)

    assert loaded.voice_id == created.voice_id
    assert loaded.status == "loaded"


def test_legacy_voice_backfills_reference_without_changing_uuid(
    store, compatibility, tmp_path
) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)
    created = store.resolve("pt", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility)
    store.reference_path(created.voice_id).unlink()

    decision = store.resolve("pt", reference, compatibility)
    publish_fake_conditioning(store, decision, compatibility)

    assert decision.voice_id == created.voice_id
    assert decision.status == "regenerated"
    assert store.reference_path(created.voice_id).read_bytes() == reference.read_bytes()


def test_startup_backfill_archives_only_an_exact_legacy_match(
    store, compatibility, tmp_path
) -> None:
    reference = tmp_path / "pt-reference.wav"
    write_wav(reference, sample=1000)
    created = store.resolve("pt", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility)
    store.reference_path(created.voice_id).unlink()

    migrated = store.backfill_legacy_reference("pt", reference)
    write_wav(reference, sample=2000)
    no_match = store.backfill_legacy_reference("pt", reference)

    assert migrated == created.voice_id
    assert no_match is None
    assert sha256_file(store.reference_path(created.voice_id)) == created.reference_sha256


@pytest.mark.parametrize(
    "changed",
    [
        {"source_revision": "a" * 40},
        {"model_revision": "b" * 40},
        {"format_version": 2},
    ],
)
def test_compatibility_change_regenerates_same_voice(
    store, compatibility, tmp_path, changed
) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)
    created = store.resolve("en", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility)

    decision = store.resolve("en", reference, replace(compatibility, **changed))

    assert decision.voice_id == created.voice_id
    assert decision.status == "regenerated"


def test_nano_conditioning_reuses_voice_without_replacing_multilingual_artifacts(
    store, compatibility, tmp_path
) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference, duration_seconds=6.1)
    created = store.resolve("en", reference, compatibility)
    publish_fake_conditioning(
        store, created, compatibility, b"fake-conditioning:multilingual"
    )
    legacy_conditioning = store.conditioning_path(created.voice_id).read_bytes()
    legacy_metadata = store.metadata_path(created.voice_id).read_bytes()
    nano_compatibility = replace(
        compatibility,
        model_name="nano",
        model_revision="7" * 40,
    )

    nano = store.resolve(
        "en", reference, nano_compatibility, voice_id=created.voice_id
    )
    publish_fake_conditioning(
        store, nano, nano_compatibility, b"fake-conditioning:nano"
    )
    loaded = store.resolve(
        "en", reference, nano_compatibility, voice_id=created.voice_id
    )

    assert nano.voice_id == created.voice_id
    assert nano.status == "created"
    assert loaded.status == "loaded"
    assert store.conditioning_path(created.voice_id).read_bytes() == legacy_conditioning
    assert store.metadata_path(created.voice_id).read_bytes() == legacy_metadata
    assert store.conditioning_path(created.voice_id, "nano").read_bytes() == b"fake-conditioning:nano"
    assert store.conditioning_metadata_path(created.voice_id, "nano").is_file()
    assert store.output_path(created.voice_id, "en", "nano").name == "preview-en-nano.wav"


def test_reference_change_creates_new_voice_and_preserves_first(store, compatibility, tmp_path) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference, sample=1000)
    created = store.resolve("en", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility)
    first_conditioning = store.conditioning_path(created.voice_id).read_bytes()
    first_reference = store.reference_path(created.voice_id).read_bytes()
    write_wav(reference, sample=2000)

    changed = store.resolve("en", reference, compatibility)

    assert changed.voice_id != created.voice_id
    assert changed.status == "created"
    assert changed.reference_sha256 != created.reference_sha256
    assert store.conditioning_path(created.voice_id).read_bytes() == first_conditioning
    assert store.reference_path(created.voice_id).read_bytes() == first_reference


@pytest.mark.parametrize("mutation", ["missing", "corrupt"])
def test_missing_or_corrupt_conditioning_requires_regeneration(
    store, compatibility, tmp_path, mutation
) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)
    created = store.resolve("en", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility)
    conditioning = store.conditioning_path(created.voice_id)
    if mutation == "missing":
        conditioning.unlink()
    else:
        conditioning.write_bytes(b"tampered")

    decision = store.resolve("en", reference, compatibility)

    assert decision.voice_id == created.voice_id
    assert decision.status == "regenerated"


def test_atomic_publish_failure_restores_previous_pair(
    store, compatibility, tmp_path, monkeypatch
) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)
    created = store.resolve("en", reference, compatibility)
    publish_fake_conditioning(store, created, compatibility, b"fake-conditioning:old")
    conditioning_path = store.conditioning_path(created.voice_id)
    metadata_path = store.metadata_path(created.voice_id)
    old_conditioning = conditioning_path.read_bytes()
    old_metadata = metadata_path.read_bytes()
    decision = store.resolve(
        "en", reference, replace(compatibility, format_version=2)
    )
    temporary = store.temporary_conditioning_path(decision.voice_id)
    temporary.write_bytes(b"fake-conditioning:new")
    real_replace = os.replace

    def fail_metadata_publish(source, destination):
        source_name = Path(source).name
        if (
            source_name.startswith(".metadata-")
            and "backup" not in source_name
            and Path(destination).name == "metadata.json"
        ):
            raise OSError("simulated metadata failure")
        return real_replace(source, destination)

    monkeypatch.setattr("app.voice_store.os.replace", fail_metadata_publish)

    with pytest.raises(OSError, match="simulated metadata failure"):
        store.publish(
            decision,
            temporary,
            replace(compatibility, format_version=2),
        )

    assert conditioning_path.read_bytes() == old_conditioning
    assert metadata_path.read_bytes() == old_metadata
    assert not list(conditioning_path.parent.glob(".*backup*"))


def test_english_and_portuguese_are_isolated(store, compatibility, tmp_path) -> None:
    english_reference = tmp_path / "reference.wav"
    portuguese_reference = tmp_path / "pt-reference.wav"
    write_wav(english_reference, sample=1000)
    write_wav(portuguese_reference, sample=2000)
    english = store.resolve("en", english_reference, compatibility)
    portuguese = store.resolve("pt", portuguese_reference, compatibility)
    publish_fake_conditioning(store, english, compatibility, b"fake-conditioning:en")
    publish_fake_conditioning(store, portuguese, compatibility, b"fake-conditioning:pt")
    portuguese_checksum = sha256_file(store.conditioning_path(portuguese.voice_id))
    write_wav(english_reference, sample=3000)

    regenerated = store.resolve("en", english_reference, compatibility)

    assert english.voice_id != portuguese.voice_id
    assert regenerated.voice_id != english.voice_id
    assert regenerated.status == "created"
    assert sha256_file(store.conditioning_path(portuguese.voice_id)) == portuguese_checksum


def test_invalid_voice_id_and_output_language_cannot_escape_roots(store) -> None:
    with pytest.raises(VoiceStoreError, match="valid UUID"):
        store.voice_dir("../../outside")
    with pytest.raises(VoiceStoreError, match="Unsupported output language"):
        store.output_path(str(uuid4()), "../../outside")


def test_duplicate_profile_metadata_is_rejected(store, compatibility, tmp_path) -> None:
    reference = tmp_path / "reference.wav"
    write_wav(reference)
    first = store.resolve("en", reference, compatibility)
    first_metadata = publish_fake_conditioning(store, first, compatibility)
    second_id = str(uuid4())
    second_dir = store.voice_dir(second_id)
    second_dir.mkdir(parents=True)
    duplicate = {**first_metadata.__dict__, "voice_id": second_id}
    (second_dir / "metadata.json").write_text(json.dumps(duplicate), encoding="utf-8")
    (second_dir / "conditioning.pt").write_bytes(b"fake-conditioning:duplicate")

    with pytest.raises(VoiceStoreError, match="Multiple voices"):
        store.resolve("en", reference, compatibility)
