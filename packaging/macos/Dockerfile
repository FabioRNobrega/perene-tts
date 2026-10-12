# Builder for the macOS desktop app (make desktop-mac). It holds every build tool, so nothing is installed on the Mac;
# packaging/macos/build.sh runs inside it and writes "dist/Perene TTS.app" and its zip to the mounted repository.
# Works on any Docker host (Apple silicon or x86-64): everything produced targets macOS arm64.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG ZIG_VERSION=0.14.1
ARG ZIG_SHA256_X86_64=24aeeec8af16c381934a6cd7d95c807a8cb2cf7df9fa40d359aa884195c4716c
ARG ZIG_SHA256_AARCH64=f7a654acc967864f7a050ddacfaa778c7504a0eca8d2b678839c21eea47c992b
ARG RCODESIGN_VERSION=0.29.0
ARG RCODESIGN_SHA256_X86_64=dbe85cedd8ee4217b64e9a0e4c2aef92ab8bcaaa41f20bde99781ff02e600002
ARG RCODESIGN_SHA256_AARCH64=4af92c87ddf52f5f2d1258a3b4e56c7dcb8f1b2468df744976c5f139e031961f

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PYTHONDONTWRITEBYTECODE=1

# Python only runs the build (pip downloads macOS wheels; it never builds native code). llvm reads Mach-O files;
# gcc/make build FFmpeg's host-side helpers; zip packs the result.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv curl xz-utils make gcc libc6-dev llvm zip \
    && rm -rf /var/lib/apt/lists/*

# zig cross-compiles FFmpeg and LAME for macOS arm64 without an Apple SDK; rcodesign ad-hoc signs Mach-O files on Linux.
RUN set -eu; \
    case "$(uname -m)" in \
        x86_64) arch=x86_64; zig_sha="$ZIG_SHA256_X86_64"; sign_sha="$RCODESIGN_SHA256_X86_64" ;; \
        aarch64) arch=aarch64; zig_sha="$ZIG_SHA256_AARCH64"; sign_sha="$RCODESIGN_SHA256_AARCH64" ;; \
        *) echo "Unsupported build host $(uname -m)" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/zig.tar.xz "https://ziglang.org/download/$ZIG_VERSION/zig-$arch-linux-$ZIG_VERSION.tar.xz"; \
    echo "$zig_sha  /tmp/zig.tar.xz" | sha256sum -c -; \
    mkdir -p /opt/zig && tar -xJf /tmp/zig.tar.xz -C /opt/zig --strip-components=1; \
    ln -s /opt/zig/zig /usr/local/bin/zig; \
    curl -fsSL -o /tmp/rcodesign.tar.gz "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F$RCODESIGN_VERSION/apple-codesign-$RCODESIGN_VERSION-$arch-unknown-linux-musl.tar.gz"; \
    echo "$sign_sha  /tmp/rcodesign.tar.gz" | sha256sum -c -; \
    tar -xzf /tmp/rcodesign.tar.gz -C /tmp; \
    install -m 0755 /tmp/apple-codesign-*/rcodesign /usr/local/bin/rcodesign; \
    rm -rf /tmp/zig.tar.xz /tmp/rcodesign.tar.gz /tmp/apple-codesign-*; \
    printf '#!/bin/sh\nexec zig ar "$@"\n' > /usr/local/bin/macos-ar; \
    printf '#!/bin/sh\nexec zig ranlib "$@"\n' > /usr/local/bin/macos-ranlib; \
    chmod +x /usr/local/bin/macos-ar /usr/local/bin/macos-ranlib
# Copied with its executable bit (no --chmod, which the legacy builder lacks).
COPY macos-cc /usr/local/bin/macos-cc

# Build-side Python tools: the model download (same huggingface_hub as the worker lock) and the icon writer.
RUN python3 -m venv /opt/build-venv \
    && /opt/build-venv/bin/pip install --no-cache-dir pip==25.2 wheel==0.45.1 huggingface_hub==1.27.0 hf-xet==1.6.0 pillow==11.3.0
ENV PATH="/opt/build-venv/bin:$PATH"

WORKDIR /src
CMD ["bash", "packaging/macos/build.sh"]
