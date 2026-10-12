# Third-party notices — Perene TTS for macOS

Perene TTS bundles the components below. Their license texts are in this folder (`licenses/`), next to this file.

| Component | Version | License | Notes |
| --- | --- | --- | --- |
| FFmpeg | 7.1.1 | LGPL-2.1-or-later | Built from the unmodified source at <https://ffmpeg.org/releases/ffmpeg-7.1.1.tar.xz> with `--disable-gpl` and only the MP3/WAV components Perene TTS uses, cross-compiled with zig; run as a separate program (`Contents/MacOS/ffmpeg`). The exact configure line is printed by `ffmpeg -buildconf` and is in `packaging/macos/build.sh`. `ffmpeg/` |
| LAME | 3.100 | LGPL-2.0-or-later | Source: <https://lame.sourceforge.io/>, statically linked into FFmpeg. `lame/` |
| Python | 3.11.17, python-build-standalone 20261009 | PSF License; bundled libraries (OpenSSL, SQLite, XZ, zlib, libffi and others) under their own licenses | `python/PYTHON-LICENSE.txt`; <https://github.com/astral-sh/python-build-standalone> |
| Python packages (PyTorch, torchaudio, NumPy, librosa, transformers, FastAPI, Uvicorn and others) | see `requirements.lock.txt` | their own licenses (mostly BSD, MIT, Apache-2.0) | `python/packages/<name>/` |
| Chatterbox (Resemble AI) | source revision 5de7a54aa4e5e2baadb0182dde554908b48b85c2 | MIT | <https://github.com/resemble-ai/chatterbox> |
| Chatterbox model weights `ResembleAI/chatterbox` (multilingual-v3) | revision 5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18 | MIT (model card at this revision) | <https://huggingface.co/ResembleAI/chatterbox> |
| .NET runtime and ASP.NET Core | 10 | MIT | `dotnet/` |
| Photino.NET / Photino.Native | 4.0.16 / 4.0.22 | Apache-2.0 | <https://github.com/tryphotino/photino.NET> |
| Montserrat, Zilla Slab fonts | — | SIL Open Font License 1.1 | `ui/` |
| Bootstrap Icons | 1.13.1 | MIT | `ui/` (also used for the app icon) |
