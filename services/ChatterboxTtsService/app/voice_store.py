from __future__ import annotations

from dataclasses import asdict, dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
from uuid import UUID, uuid4


class VoiceStoreError(RuntimeError):
    pass


class VoiceSelectionError(VoiceStoreError):
    pass


@dataclass(frozen=True)
class VoiceCompatibility:
    source_revision: str
    model_revision: str
    model_name: str
    format_version: int


@dataclass(frozen=True)
class VoiceMetadata:
    voice_id: str
    language_id: str
    reference_sha256: str
    conditioning_sha256: str
    source_revision: str
    model_revision: str
    model_name: str
    format_version: int
    created_at_utc: str
    updated_at_utc: str

    @classmethod
    def from_path(cls, path: Path) -> "VoiceMetadata":
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(raw, dict) or set(raw) != set(cls.__dataclass_fields__):
                raise ValueError("Unexpected metadata fields")
            metadata = cls(**raw)
            UUID(metadata.voice_id)
            string_fields = (
                metadata.voice_id,
                metadata.language_id,
                metadata.reference_sha256,
                metadata.conditioning_sha256,
                metadata.source_revision,
                metadata.model_revision,
                metadata.model_name,
                metadata.created_at_utc,
                metadata.updated_at_utc,
            )
            if not all(isinstance(value, str) for value in string_fields):
                raise ValueError("Metadata fields have invalid types")
            if metadata.language_id not in ("en", "pt", "sv"):
                raise ValueError("Metadata language is unsupported")
            for digest, expected_length in (
                (metadata.reference_sha256, 64),
                (metadata.conditioning_sha256, 64),
                (metadata.source_revision, 40),
                (metadata.model_revision, 40),
            ):
                if len(digest) != expected_length or any(
                    character not in "0123456789abcdef" for character in digest
                ):
                    raise ValueError("Metadata checksum or revision is invalid")
            if (
                not isinstance(metadata.format_version, int)
                or isinstance(metadata.format_version, bool)
                or metadata.format_version < 1
            ):
                raise ValueError("Metadata format version is invalid")
        except (OSError, json.JSONDecodeError, TypeError, ValueError) as error:
            raise VoiceStoreError("Stored voice metadata is invalid") from error
        return metadata


@dataclass(frozen=True)
class ModelConditioningMetadata:
    voice_id: str
    conditioning_sha256: str
    source_revision: str
    model_revision: str
    model_name: str
    format_version: int
    created_at_utc: str
    updated_at_utc: str

    @classmethod
    def from_path(cls, path: Path) -> "ModelConditioningMetadata":
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(raw, dict) or set(raw) != set(cls.__dataclass_fields__):
                raise ValueError("Unexpected model metadata fields")
            metadata = cls(**raw)
            UUID(metadata.voice_id)
            if metadata.model_name != "nano":
                raise ValueError("Unsupported secondary model")
            for digest, expected_length in (
                (metadata.conditioning_sha256, 64),
                (metadata.source_revision, 40),
                (metadata.model_revision, 40),
            ):
                if len(digest) != expected_length or any(
                    character not in "0123456789abcdef" for character in digest
                ):
                    raise ValueError("Model metadata checksum or revision is invalid")
            if (
                not isinstance(metadata.format_version, int)
                or isinstance(metadata.format_version, bool)
                or metadata.format_version < 1
            ):
                raise ValueError("Model metadata format version is invalid")
            if not all(
                isinstance(value, str)
                for value in (
                    metadata.created_at_utc,
                    metadata.updated_at_utc,
                )
            ):
                raise ValueError("Model metadata timestamps are invalid")
        except (OSError, json.JSONDecodeError, TypeError, ValueError) as error:
            raise VoiceStoreError("Stored model conditioning metadata is invalid") from error
        return metadata


@dataclass(frozen=True)
class VoiceDecision:
    voice_id: str
    language_id: str
    reference_sha256: str
    status: str
    metadata: VoiceMetadata | None
    conditioning_metadata: ModelConditioningMetadata | None
    conditioning_path: Path
    reference_path: Path
    archive_reference: bool


class LocalVoiceStore:
    def __init__(self, voices_dir: Path, outputs_dir: Path) -> None:
        self.voices_dir = voices_dir.resolve()
        self.outputs_dir = outputs_dir.resolve()

    def resolve(
        self,
        language_id: str,
        current_reference_path: Path,
        compatibility: VoiceCompatibility,
        voice_id: str | None = None,
    ) -> VoiceDecision:
        metadata_records = self.list_metadata(language_id)
        self._reject_duplicate_references(metadata_records)

        if voice_id is None and compatibility.model_name != "multilingual-v3":
            raise VoiceSelectionError(
                f"Model '{compatibility.model_name}' requires an existing voice ID"
            )

        if voice_id is not None:
            canonical_id = str(_parse_voice_id(voice_id))
            metadata = next(
                (item for item in metadata_records if item.voice_id == canonical_id),
                None,
            )
            if metadata is None:
                any_language = next(
                    (
                        item
                        for item in self.list_metadata()
                        if item.voice_id == canonical_id
                    ),
                    None,
                )
                if any_language is not None:
                    raise VoiceSelectionError(
                        f"Voice '{canonical_id}' belongs to language '{any_language.language_id}', not '{language_id}'"
                    )
                raise VoiceSelectionError(f"Voice '{canonical_id}' was not found")
            return self._decision_for_metadata(
                metadata, current_reference_path, compatibility
            )

        if not current_reference_path.is_file():
            raise VoiceStoreError(f"Reference audio is missing: {current_reference_path}")
        reference_sha256 = sha256_file(current_reference_path)
        matches = [
            item
            for item in metadata_records
            if item.reference_sha256 == reference_sha256
        ]
        if len(matches) > 1:
            raise VoiceStoreError(
                f"Multiple voices use the same '{language_id}' reference checksum"
            )
        if matches:
            return self._decision_for_metadata(
                matches[0], current_reference_path, compatibility
            )

        voice_id = str(uuid4())
        return VoiceDecision(
            voice_id=voice_id,
            language_id=language_id,
            reference_sha256=reference_sha256,
            status="created",
            metadata=None,
            conditioning_metadata=None,
            conditioning_path=self.conditioning_path(
                voice_id, compatibility.model_name
            ),
            reference_path=current_reference_path.resolve(),
            archive_reference=True,
        )

    def list_metadata(self, language_id: str | None = None) -> list[VoiceMetadata]:
        if language_id is not None and language_id not in ("en", "pt", "sv"):
            raise VoiceStoreError("Unsupported voice-list language")
        if not self.voices_dir.exists():
            return []
        records: list[VoiceMetadata] = []
        for candidate in self.voices_dir.iterdir():
            if not candidate.is_dir():
                continue
            try:
                voice_dir = self.voice_dir(candidate.name)
            except VoiceStoreError:
                continue
            metadata_path = voice_dir / "metadata.json"
            if not metadata_path.is_file():
                continue
            metadata = VoiceMetadata.from_path(metadata_path)
            if metadata.voice_id != candidate.name:
                raise VoiceStoreError("Stored voice ID does not match its directory")
            if language_id is None or metadata.language_id == language_id:
                records.append(metadata)
        return sorted(records, key=lambda item: (item.language_id, item.created_at_utc))

    def voice_summaries(self, language_id: str | None = None) -> list[dict[str, object]]:
        records = self.list_metadata(language_id)
        self._reject_duplicate_references(records)
        return [
            {
                "voice_id": item.voice_id,
                "language_id": item.language_id,
                "reference_sha256": item.reference_sha256,
                "model_name": item.model_name,
                "model_revision": item.model_revision,
                "format_version": item.format_version,
                "created_at_utc": item.created_at_utc,
                "updated_at_utc": item.updated_at_utc,
                "reference_archived": self.reference_path(item.voice_id).is_file(),
                "conditioning_ready": self.conditioning_path(item.voice_id).is_file(),
                "nano_conditioning_ready": self.conditioning_path(
                    item.voice_id, "nano"
                ).is_file(),
            }
            for item in records
        ]

    def backfill_legacy_reference(
        self, language_id: str, current_reference_path: Path
    ) -> str | None:
        """Archive a matching pre-v2 reference without altering conditioning metadata."""
        if not current_reference_path.is_file():
            return None
        reference_sha256 = sha256_file(current_reference_path)
        matches = [
            item
            for item in self.list_metadata(language_id)
            if item.reference_sha256 == reference_sha256
        ]
        if len(matches) > 1:
            raise VoiceStoreError(
                f"Multiple voices use the same '{language_id}' reference checksum"
            )
        if not matches:
            return None
        metadata = matches[0]
        destination = self.reference_path(metadata.voice_id)
        if destination.is_file():
            if sha256_file(destination) != reference_sha256:
                raise VoiceStoreError("Archived reference checksum does not match metadata")
            return None

        voice_dir = self.voice_dir(metadata.voice_id)
        descriptor, name = tempfile.mkstemp(
            prefix=".reference-backfill-", suffix=".wav", dir=voice_dir
        )
        os.close(descriptor)
        temporary = Path(name)
        try:
            shutil.copyfile(current_reference_path, temporary)
            if sha256_file(temporary) != reference_sha256:
                raise VoiceStoreError("Archived reference checksum validation failed")
            os.replace(temporary, destination)
        finally:
            temporary.unlink(missing_ok=True)
        return metadata.voice_id

    def require_regeneration(self, decision: VoiceDecision) -> VoiceDecision:
        return VoiceDecision(
            voice_id=decision.voice_id,
            language_id=decision.language_id,
            reference_sha256=decision.reference_sha256,
            status="regenerated",
            metadata=decision.metadata,
            conditioning_metadata=decision.conditioning_metadata,
            conditioning_path=decision.conditioning_path,
            reference_path=decision.reference_path,
            archive_reference=decision.archive_reference,
        )

    def temporary_conditioning_path(
        self, voice_id: str, model_name: str = "multilingual-v3"
    ) -> Path:
        voice_dir = self.voice_dir(voice_id)
        voice_dir.mkdir(parents=True, exist_ok=True)
        descriptor, name = tempfile.mkstemp(
            prefix=f".{self._conditioning_stem(model_name)}-",
            suffix=".pt",
            dir=voice_dir,
        )
        os.close(descriptor)
        path = Path(name)
        path.unlink()
        return path

    def publish(
        self,
        decision: VoiceDecision,
        temporary_conditioning_path: Path,
        compatibility: VoiceCompatibility,
    ) -> VoiceMetadata | ModelConditioningMetadata:
        if compatibility.model_name != "multilingual-v3":
            return self._publish_secondary_conditioning(
                decision, temporary_conditioning_path, compatibility
            )

        voice_dir = self.voice_dir(decision.voice_id)
        conditioning_temporary = temporary_conditioning_path.resolve()
        if conditioning_temporary.parent != voice_dir:
            raise VoiceStoreError("Temporary conditioning path escaped its voice directory")
        if (
            not conditioning_temporary.is_file()
            or conditioning_temporary.stat().st_size == 0
        ):
            raise VoiceStoreError("Conditioning save did not produce a non-empty artifact")

        reference_temporary: Path | None = None
        if decision.archive_reference:
            descriptor, reference_name = tempfile.mkstemp(
                prefix=".reference-", suffix=".wav", dir=voice_dir
            )
            os.close(descriptor)
            reference_temporary = Path(reference_name)
            try:
                shutil.copyfile(decision.reference_path, reference_temporary)
                if sha256_file(reference_temporary) != decision.reference_sha256:
                    raise VoiceStoreError("Archived reference checksum validation failed")
            except Exception:
                reference_temporary.unlink(missing_ok=True)
                raise

        now = _utc_now()
        metadata = VoiceMetadata(
            voice_id=decision.voice_id,
            language_id=decision.language_id,
            reference_sha256=decision.reference_sha256,
            conditioning_sha256=sha256_file(conditioning_temporary),
            source_revision=compatibility.source_revision,
            model_revision=compatibility.model_revision,
            model_name=compatibility.model_name,
            format_version=compatibility.format_version,
            created_at_utc=(
                decision.metadata.created_at_utc if decision.metadata is not None else now
            ),
            updated_at_utc=now,
        )
        metadata_path = self.metadata_path(decision.voice_id)
        descriptor, metadata_name = tempfile.mkstemp(
            prefix=".metadata-", suffix=".json", dir=voice_dir
        )
        metadata_temporary = Path(metadata_name)
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                json.dump(asdict(metadata), stream, indent=2, sort_keys=True)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            if VoiceMetadata.from_path(metadata_temporary) != metadata:
                raise VoiceStoreError("Conditioning metadata validation failed")
            replacements: list[tuple[Path, Path]] = []
            if reference_temporary is not None:
                replacements.append(
                    (reference_temporary, self.reference_path(decision.voice_id))
                )
            replacements.extend(
                [
                    (conditioning_temporary, decision.conditioning_path),
                    (metadata_temporary, metadata_path),
                ]
            )
            self._replace_artifacts(replacements)
            return metadata
        finally:
            conditioning_temporary.unlink(missing_ok=True)
            metadata_temporary.unlink(missing_ok=True)
            if reference_temporary is not None:
                reference_temporary.unlink(missing_ok=True)
            self._remove_empty_voice_dir(voice_dir)

    def conditioning_path(
        self, voice_id: str, model_name: str = "multilingual-v3"
    ) -> Path:
        return self.voice_dir(voice_id) / f"{self._conditioning_stem(model_name)}.pt"

    def conditioning_metadata_path(self, voice_id: str, model_name: str) -> Path:
        if model_name == "multilingual-v3":
            return self.metadata_path(voice_id)
        return self.voice_dir(voice_id) / f"{self._conditioning_stem(model_name)}.json"

    def metadata_path(self, voice_id: str) -> Path:
        return self.voice_dir(voice_id) / "metadata.json"

    def reference_path(self, voice_id: str) -> Path:
        return self.voice_dir(voice_id) / "reference.wav"

    def output_path(
        self,
        voice_id: str,
        language_id: str,
        model_name: str = "multilingual-v3",
    ) -> Path:
        if language_id not in ("en", "pt", "sv"):
            raise VoiceStoreError("Unsupported output language")
        parsed = _parse_voice_id(voice_id)
        directory = (self.outputs_dir / str(parsed)).resolve()
        if directory.parent != self.outputs_dir:
            raise VoiceStoreError("Output path escaped the output root")
        suffix = "" if model_name == "multilingual-v3" else f"-{model_name}"
        self._conditioning_stem(model_name)
        return directory / f"preview-{language_id}{suffix}.wav"

    def voice_dir(self, voice_id: str) -> Path:
        parsed = _parse_voice_id(voice_id)
        directory = (self.voices_dir / str(parsed)).resolve()
        if directory.parent != self.voices_dir:
            raise VoiceStoreError("Voice path escaped the voice root")
        return directory

    def _decision_for_metadata(
        self,
        metadata: VoiceMetadata,
        current_reference_path: Path,
        compatibility: VoiceCompatibility,
    ) -> VoiceDecision:
        archived_reference = self.reference_path(metadata.voice_id)
        archive_reference = False
        if archived_reference.is_file():
            if sha256_file(archived_reference) != metadata.reference_sha256:
                raise VoiceStoreError("Archived reference checksum does not match metadata")
            selected_reference = archived_reference
        elif (
            current_reference_path.is_file()
            and sha256_file(current_reference_path) == metadata.reference_sha256
        ):
            selected_reference = current_reference_path.resolve()
            archive_reference = True
        else:
            raise VoiceStoreError(
                f"Voice '{metadata.voice_id}' has no archived reference and the current reference does not match it"
            )

        conditioning_path = self.conditioning_path(
            metadata.voice_id, compatibility.model_name
        )
        conditioning_metadata: ModelConditioningMetadata | None = None
        if compatibility.model_name == "multilingual-v3":
            compatible = (
                metadata.source_revision == compatibility.source_revision
                and metadata.model_revision == compatibility.model_revision
                and metadata.model_name == compatibility.model_name
                and metadata.format_version == compatibility.format_version
                and conditioning_path.is_file()
                and not archive_reference
            )
            missing_status = "regenerated"
            expected_conditioning_sha256 = metadata.conditioning_sha256
        else:
            sidecar_path = self.conditioning_metadata_path(
                metadata.voice_id, compatibility.model_name
            )
            conditioning_metadata = (
                ModelConditioningMetadata.from_path(sidecar_path)
                if sidecar_path.is_file()
                else None
            )
            compatible = (
                conditioning_metadata is not None
                and conditioning_metadata.voice_id == metadata.voice_id
                and conditioning_metadata.source_revision == compatibility.source_revision
                and conditioning_metadata.model_revision == compatibility.model_revision
                and conditioning_metadata.model_name == compatibility.model_name
                and conditioning_metadata.format_version == compatibility.format_version
                and conditioning_path.is_file()
                and not archive_reference
            )
            missing_status = (
                "created"
                if conditioning_metadata is None and not conditioning_path.exists()
                else "regenerated"
            )
            expected_conditioning_sha256 = (
                conditioning_metadata.conditioning_sha256
                if conditioning_metadata is not None
                else ""
            )
        if compatible:
            try:
                compatible = (
                    sha256_file(conditioning_path) == expected_conditioning_sha256
                )
            except OSError:
                compatible = False

        return VoiceDecision(
            voice_id=metadata.voice_id,
            language_id=metadata.language_id,
            reference_sha256=metadata.reference_sha256,
            status="loaded" if compatible else missing_status,
            metadata=metadata,
            conditioning_metadata=conditioning_metadata,
            conditioning_path=conditioning_path,
            reference_path=selected_reference,
            archive_reference=archive_reference,
        )

    def _publish_secondary_conditioning(
        self,
        decision: VoiceDecision,
        temporary_conditioning_path: Path,
        compatibility: VoiceCompatibility,
    ) -> ModelConditioningMetadata:
        if compatibility.model_name != "nano" or decision.metadata is None:
            raise VoiceStoreError("Secondary conditioning requires an existing voice")
        voice_dir = self.voice_dir(decision.voice_id)
        conditioning_temporary = temporary_conditioning_path.resolve()
        if conditioning_temporary.parent != voice_dir:
            raise VoiceStoreError("Temporary conditioning path escaped its voice directory")
        if not conditioning_temporary.is_file() or conditioning_temporary.stat().st_size == 0:
            raise VoiceStoreError("Conditioning save did not produce a non-empty artifact")

        reference_temporary: Path | None = None
        if decision.archive_reference:
            descriptor, reference_name = tempfile.mkstemp(
                prefix=".reference-", suffix=".wav", dir=voice_dir
            )
            os.close(descriptor)
            reference_temporary = Path(reference_name)
            try:
                shutil.copyfile(decision.reference_path, reference_temporary)
                if sha256_file(reference_temporary) != decision.reference_sha256:
                    raise VoiceStoreError(
                        "Archived reference checksum validation failed"
                    )
            except Exception:
                reference_temporary.unlink(missing_ok=True)
                raise

        now = _utc_now()
        artifact_metadata = ModelConditioningMetadata(
            voice_id=decision.voice_id,
            conditioning_sha256=sha256_file(conditioning_temporary),
            source_revision=compatibility.source_revision,
            model_revision=compatibility.model_revision,
            model_name=compatibility.model_name,
            format_version=compatibility.format_version,
            created_at_utc=(
                decision.conditioning_metadata.created_at_utc
                if decision.conditioning_metadata is not None
                else now
            ),
            updated_at_utc=now,
        )
        metadata_path = self.conditioning_metadata_path(
            decision.voice_id, compatibility.model_name
        )
        descriptor, metadata_name = tempfile.mkstemp(
            prefix=f".{self._conditioning_stem(compatibility.model_name)}-metadata-",
            suffix=".json",
            dir=voice_dir,
        )
        metadata_temporary = Path(metadata_name)
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                json.dump(asdict(artifact_metadata), stream, indent=2, sort_keys=True)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            if ModelConditioningMetadata.from_path(metadata_temporary) != artifact_metadata:
                raise VoiceStoreError("Model conditioning metadata validation failed")
            replacements: list[tuple[Path, Path]] = []
            if reference_temporary is not None:
                replacements.append(
                    (reference_temporary, self.reference_path(decision.voice_id))
                )
            replacements.extend(
                [
                    (conditioning_temporary, decision.conditioning_path),
                    (metadata_temporary, metadata_path),
                ]
            )
            self._replace_artifacts(replacements)
            return artifact_metadata
        finally:
            conditioning_temporary.unlink(missing_ok=True)
            metadata_temporary.unlink(missing_ok=True)
            if reference_temporary is not None:
                reference_temporary.unlink(missing_ok=True)

    @staticmethod
    def _conditioning_stem(model_name: str) -> str:
        if model_name == "multilingual-v3":
            return "conditioning"
        if model_name == "nano":
            return "conditioning-nano"
        raise VoiceStoreError("Unsupported conditioning model")

    @staticmethod
    def _reject_duplicate_references(records: list[VoiceMetadata]) -> None:
        seen: set[tuple[str, str]] = set()
        for item in records:
            key = (item.language_id, item.reference_sha256)
            if key in seen:
                raise VoiceStoreError(
                    f"Multiple voices use the same '{item.language_id}' reference checksum"
                )
            seen.add(key)

    def _replace_artifacts(self, replacements: list[tuple[Path, Path]]) -> None:
        token = uuid4().hex
        backups: list[tuple[Path, Path]] = []
        published: list[Path] = []
        try:
            for _, destination in replacements:
                if destination.exists():
                    backup = destination.with_name(f".{destination.name}-backup-{token}")
                    os.replace(destination, backup)
                    backups.append((backup, destination))
            for source, destination in replacements:
                os.replace(source, destination)
                published.append(destination)
        except Exception:
            for destination in reversed(published):
                destination.unlink(missing_ok=True)
            for backup, destination in reversed(backups):
                os.replace(backup, destination)
            raise
        finally:
            for backup, _ in backups:
                backup.unlink(missing_ok=True)

    @staticmethod
    def _remove_empty_voice_dir(voice_dir: Path) -> None:
        try:
            voice_dir.rmdir()
        except OSError:
            pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _parse_voice_id(voice_id: str) -> UUID:
    try:
        parsed = UUID(voice_id)
    except (ValueError, AttributeError) as error:
        raise VoiceSelectionError("Voice ID must be a valid UUID") from error
    if str(parsed) != voice_id:
        raise VoiceSelectionError("Voice ID must use canonical lowercase UUID format")
    return parsed


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
