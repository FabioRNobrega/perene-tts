from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
import logging
from pathlib import Path
import random
import threading
from typing import Callable, Protocol
import warnings

from .chunking import TextChunk


LOGGER = logging.getLogger(__name__)
MODEL_REPOSITORY = "ResembleAI/chatterbox"
NANO_MODEL_REPOSITORY = "ResembleAI/chatterbox-nano"
MODEL_FILES = [
    "ve.pt",
    "t3_mtl23ls_v3.safetensors",
    "s3gen.pt",
    "grapheme_mtl_merged_expanded_v1.json",
    "conds.pt",
    "Cangjie5_TC.json",
]
EXAGGERATION = 0.5
CFG_WEIGHT = 0.5
TEMPERATURE = 0.8
REPETITION_PENALTY = 1.2
MIN_P = 0.05
TOP_P = 1.0


class SynthesisError(RuntimeError):
    pass


@dataclass(frozen=True)
class SynthesisResult:
    sample_rate: int
    duration_seconds: float
    chunk_count: int


class SynthesisEngine(Protocol):
    device: str
    model_name: str
    model_revision: str

    @property
    def ready(self) -> bool: ...

    @property
    def load_error(self) -> str | None: ...

    def load(self) -> None: ...

    def prepare_conditioning(self, reference_path: Path) -> None: ...

    def save_conditioning(self, path: Path) -> None: ...

    def load_conditioning(self, path: Path) -> None: ...

    def synthesize(
        self,
        chunks: list[TextChunk],
        output_path: Path,
        language_id: str,
        sentence_silence_ms: int,
        paragraph_silence_ms: int,
        synthesis_seed: int,
        progress_callback: Callable[[int, int], None] | None = None,
        *,
        seed_offset: int = 0,
    ) -> SynthesisResult: ...


class ChatterboxEngine:
    device = "cpu"

    def __init__(
        self,
        model_revision: str,
        model_name: str = "multilingual-v3",
        device: str = "cpu",
    ) -> None:
        if model_name not in ("multilingual-v3", "nano"):
            raise ValueError("Unsupported Chatterbox engine model")
        self.device = device
        self.model_revision = model_revision
        self.model_name = model_name
        self._model = None
        self._load_error: str | None = None
        self._load_lock = threading.Lock()

    @property
    def ready(self) -> bool:
        return self._model is not None

    @property
    def load_error(self) -> str | None:
        return self._load_error

    def load(self) -> None:
        if self.ready:
            return

        with self._load_lock:
            if self.ready:
                return
            try:
                with suppress_upstream_future_warnings():
                    from huggingface_hub import snapshot_download

                    LOGGER.info(
                        "Loading %s on %s from model revision %s",
                        self.model_name,
                        self.device,
                        self.model_revision,
                    )
                    if self.model_name == "nano":
                        from chatterbox.tts_turbo import ChatterboxTurboTTS

                        checkpoint_dir = snapshot_download(
                            repo_id=NANO_MODEL_REPOSITORY,
                            repo_type="model",
                            revision=self.model_revision,
                            allow_patterns=[
                                "*.safetensors",
                                "*.json",
                                "*.txt",
                                "*.pt",
                                "*.model",
                            ],
                        )
                        self._model = ChatterboxTurboTTS.from_local(
                            checkpoint_dir,
                            device=self.device,
                            nano=True,
                        )
                    else:
                        from chatterbox.mtl_tts import ChatterboxMultilingualTTS

                        checkpoint_dir = snapshot_download(
                            repo_id=MODEL_REPOSITORY,
                            repo_type="model",
                            revision=self.model_revision,
                            allow_patterns=MODEL_FILES,
                        )
                        self._model = ChatterboxMultilingualTTS.from_local(
                            checkpoint_dir,
                            device=self.device,
                            t3_model="v3",
                        )
                self._load_error = None
                LOGGER.info("Chatterbox model is ready")
            except Exception as error:
                self._load_error = _concise_error(error)
                LOGGER.exception("Chatterbox model failed to load: %s", self._load_error)

    def prepare_conditioning(self, reference_path: Path) -> None:
        self._require_ready()
        try:
            self._model.prepare_conditionals(str(reference_path))
        except Exception as error:
            raise SynthesisError(_concise_error(error)) from error

    def save_conditioning(self, path: Path) -> None:
        self._require_ready()
        try:
            if self._model.conds is None:
                raise RuntimeError("Voice conditioning has not been prepared")
            self._model.conds.save(path)
        except Exception as error:
            raise SynthesisError(_concise_error(error)) from error

    def load_conditioning(self, path: Path) -> None:
        self._require_ready()
        try:
            if self.model_name == "nano":
                from chatterbox.tts_turbo import Conditionals
            else:
                from chatterbox.mtl_tts import Conditionals

            self._model.conds = Conditionals.load(
                path, map_location=self.device
            ).to(self.device)
        except Exception as error:
            raise SynthesisError(_concise_error(error)) from error

    def synthesize(
        self,
        chunks: list[TextChunk],
        output_path: Path,
        language_id: str,
        sentence_silence_ms: int,
        paragraph_silence_ms: int,
        synthesis_seed: int,
        progress_callback: Callable[[int, int], None] | None = None,
        *,
        seed_offset: int = 0,
    ) -> SynthesisResult:
        self._require_ready()

        try:
            import torch
            import soundfile
            import numpy as np

            if self._model.conds is None:
                raise RuntimeError("Voice conditioning has not been selected")
            waveforms = []
            for index, chunk in enumerate(chunks):
                # seed_offset lets a single-chunk call reproduce chunk N of a whole-text call.
                chunk_seed = synthesis_seed + seed_offset + index
                torch.manual_seed(chunk_seed)
                random.seed(chunk_seed)
                np.random.seed(chunk_seed)
                with suppress_upstream_future_warnings():
                    if self.model_name == "nano":
                        if language_id != "en":
                            raise SynthesisError("Chatterbox Nano supports only English")
                        waveform = self._model.generate(
                            chunk.text,
                            audio_prompt_path=None,
                        )
                    else:
                        waveform = self._model.generate(
                            chunk.text,
                            language_id=language_id,
                            audio_prompt_path=None,
                            exaggeration=EXAGGERATION,
                            cfg_weight=CFG_WEIGHT,
                            temperature=TEMPERATURE,
                            repetition_penalty=REPETITION_PENALTY,
                            min_p=MIN_P,
                            top_p=TOP_P,
                        )
                waveforms.append(waveform.cpu())
                if progress_callback is not None:
                    progress_callback(index + 1, len(chunks))
                if index < len(chunks) - 1:
                    silence_ms = (
                        paragraph_silence_ms
                        if chunk.paragraph_break_after
                        else sentence_silence_ms
                    )
                    silence_samples = int(self._model.sr * silence_ms / 1000)
                    waveforms.append(torch.zeros((1, silence_samples)))

            combined = torch.cat(waveforms, dim=-1)
            soundfile.write(
                str(output_path),
                combined.squeeze(0).numpy(),
                self._model.sr,
                subtype="PCM_16",
            )
            return SynthesisResult(
                sample_rate=self._model.sr,
                duration_seconds=combined.shape[-1] / self._model.sr,
                chunk_count=len(chunks),
            )
        except SynthesisError:
            raise
        except Exception as error:
            raise SynthesisError(_concise_error(error)) from error

    def _require_ready(self) -> None:
        if not self.ready:
            raise SynthesisError(self.load_error or "Chatterbox model is still loading")


def _concise_error(error: Exception) -> str:
    message = " ".join(str(error).split())
    return message[:500] or error.__class__.__name__


@contextmanager
def suppress_upstream_future_warnings():
    with warnings.catch_warnings():
        warnings.filterwarnings(
            "ignore",
            message=r"`LoRACompatibleLinear` is deprecated.*",
            category=FutureWarning,
        )
        warnings.filterwarnings(
            "ignore",
            message=r"`torch\.backends\.cuda\.sdp_kernel\(\)` is deprecated.*",
            category=FutureWarning,
        )
        yield
