#!/usr/bin/env bash
# Builds "dist/Perene TTS.app" and dist/Perene-TTS-macos-arm64.zip for Apple-silicon Macs.
# Runs inside the builder image (packaging/macos/Dockerfile) through `make desktop-mac`; nothing is installed on the Mac.
# Everything is downloaded ready-made for macOS arm64 (standalone Python, PyPI wheels, .NET runtime pack) or
# cross-compiled (FFmpeg with zig), then ad-hoc signed with rcodesign. Downloads are cached in build/macos/.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/packaging/macos"
SERVICE="$ROOT/services/ChatterboxTtsService"
BUILD="$ROOT/build/macos"
DIST="$ROOT/dist"
APP="$DIST/Perene TTS.app"
ZIP="$DIST/Perene-TTS-macos-arm64.zip"
VERSION="$(tr -d '[:space:]' < "$HERE/VERSION")"
LLVM_BIN="$(llvm-config --bindir 2>/dev/null || true)"

PYTHON_RELEASE=20261009
PYTHON_ARCHIVE="cpython-3.11.17+$PYTHON_RELEASE-aarch64-apple-darwin-install_only_stripped.tar.gz"
PYTHON_SHA256=f58d38af4680fbf9f89c329e732b7cca1bd06ff7a93884798b27d541bee138f6
FFMPEG_VERSION=7.1.1
FFMPEG_SHA256=733984395e0dbbe5c046abda2dc49a5544e7e0e1e2366bba849222ae9e3a03b1
LAME_VERSION=3.100
LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e
# pip resolves wheels for the Mac, not for this Linux container.
MAC_WHEELS=(--platform macosx_14_0_arm64 --python-version 3.11 --implementation cp --abi cp311 --only-binary=:all:)
export HF_HOME="$BUILD/hf-cache" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nError: %s\n' "$*" >&2; exit 1; }

download() {  # url sha256 destination
    if [[ ! -f "$3" ]]; then curl -fL --retry 3 -o "$3.part" "$1" && mv "$3.part" "$3"; fi
    echo "$2  $3" | sha256sum -c - >/dev/null || fail "Checksum mismatch for $3; delete it and rebuild."
}

preflight() {
    step "Checking the builder"
    local tool
    for tool in dotnet zig rcodesign macos-cc python3 zip "$LLVM_BIN/llvm-objdump"; do
        command -v "$tool" >/dev/null || fail "$tool is missing. Run this through make desktop-mac (it uses the builder image)."
    done
    mkdir -p "$BUILD/downloads" "$BUILD/licenses" "$DIST"
}

python_runtime() {
    step "Standalone Python 3.11 for macOS arm64"
    download "https://github.com/astral-sh/python-build-standalone/releases/download/$PYTHON_RELEASE/${PYTHON_ARCHIVE//+/%2B}" \
        "$PYTHON_SHA256" "$BUILD/downloads/$PYTHON_ARCHIVE"
}

packages() {
    step "Worker packages (macOS arm64 wheels from PyPI)"
    local site="$BUILD/site-packages" wheels="$BUILD/wheels" stamp
    stamp="$(cat "$SERVICE/requirements.lock.txt" "$SERVICE/requirements.txt" | sha256sum | cut -d' ' -f1)"
    if [[ -f "$site/.stamp" && "$(cat "$site/.stamp")" == "$stamp" ]]; then return; fi
    rm -rf "${site:?}" "${wheels:?}"
    mkdir -p "$wheels"
    # Two requirements ship only as source; both are pure Python, so the wheels built here run on any platform.
    pip wheel --quiet --no-deps --wheel-dir "$wheels" \
        "$(grep -E '^antlr4-python3-runtime==' "$SERVICE/requirements.lock.txt")" \
        "$(grep -E '^chatterbox-tts @' "$SERVICE/requirements.txt")"
    { grep -vE '^\s*(#|$)' "$SERVICE/requirements.lock.txt"
      grep -vE '^\s*(#|$)|^chatterbox-tts @' "$SERVICE/requirements.txt"
      ls "$wheels"/chatterbox_tts-*.whl; } > "$BUILD/requirements-macos.txt"
    # Same pinned set as scripts/mac-worker.sh setup; PyPI's macOS arm64 torch wheel includes MPS.
    pip install --quiet --no-deps --target "$site" --find-links "$wheels" "${MAC_WHEELS[@]}" -r "$BUILD/requirements-macos.txt"
    echo "$stamp" > "$site/.stamp"
}

model() {
    step "Model weights (multilingual-v3, pinned revision)"
    if [[ ! -f "$BUILD/model/model-manifest.json" ]]; then
        python3 - "$SERVICE" "$BUILD/model" <<'PY'
import ast, hashlib, json, shutil, sys
from pathlib import Path
from huggingface_hub import snapshot_download

def constant(path, name):  # read the pinned identity from the worker source without importing it
    for node in ast.parse(Path(path).read_text(encoding="utf-8")).body:
        if isinstance(node, ast.Assign) and any(getattr(t, "id", None) == name for t in node.targets):
            return ast.literal_eval(node.value)
    raise SystemExit(f"{name} not found in {path}")

service, target = Path(sys.argv[1]), Path(sys.argv[2])
files = constant(service / "app/engine.py", "MODEL_FILES")
repository = constant(service / "app/engine.py", "MODEL_REPOSITORY")
revision = constant(service / "app/studio.py", "MODEL_REVISION")
source = Path(snapshot_download(repo_id=repository, revision=revision, allow_patterns=files))
staging = target.with_name("model.tmp")
shutil.rmtree(staging, ignore_errors=True)
staging.mkdir(parents=True)
recorded = {}
for name in files:
    shutil.copyfile(source / name, staging / name)
    digest = hashlib.sha256()
    with (staging / name).open("rb") as stream:
        while block := stream.read(8 * 1024 * 1024):
            digest.update(block)
    recorded[name] = {"size": (staging / name).stat().st_size, "sha256": digest.hexdigest()}
manifest = {"repository": repository, "revision": revision, "files": recorded}
(staging / "model-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
staging.rename(target)
PY
    fi
    # The app only checks sizes at startup, so every digest is verified here.
    python3 - "$BUILD/model" <<'PY'
import hashlib, json, sys
from pathlib import Path
model = Path(sys.argv[1])
manifest = json.loads((model / "model-manifest.json").read_text(encoding="utf-8"))
for name, expected in manifest["files"].items():
    digest = hashlib.sha256()
    with (model / name).open("rb") as stream:
        while block := stream.read(8 * 1024 * 1024):
            digest.update(block)
    if digest.hexdigest() != expected["sha256"]:
        raise SystemExit(f"Model file {name} does not match model-manifest.json; delete build/macos/model and rebuild.")
print(f"  verified {len(manifest['files'])} files at revision {manifest['revision']}")
PY
}

ffmpeg_build() {
    step "Minimal LGPL FFmpeg $FFMPEG_VERSION + LAME $LAME_VERSION, cross-compiled for macOS arm64 (14.0)"
    local prefix="$BUILD/ffmpeg" sources="$BUILD/sources" jobs
    jobs="$(nproc)"
    if [[ ! -f "$prefix/bin/ffmpeg" ]]; then
        download "https://downloads.sourceforge.net/project/lame/lame/$LAME_VERSION/lame-$LAME_VERSION.tar.gz" "$LAME_SHA256" "$BUILD/downloads/lame-$LAME_VERSION.tar.gz"
        download "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz" "$FFMPEG_SHA256" "$BUILD/downloads/ffmpeg-$FFMPEG_VERSION.tar.xz"
        rm -rf "${prefix:?}" "${sources:?}"
        mkdir -p "$sources"
        tar --no-same-owner --no-same-permissions -xzf "$BUILD/downloads/lame-$LAME_VERSION.tar.gz" -C "$sources"
        tar --no-same-owner --no-same-permissions -xJf "$BUILD/downloads/ffmpeg-$FFMPEG_VERSION.tar.xz" -C "$sources"
        (
            cd "$sources/lame-$LAME_VERSION"
            sed -i '/lame_init_old/d' include/libmp3lame.sym  # the export list names a symbol that no longer exists
            ./configure --prefix="$prefix" --host=aarch64-apple-darwin --build="$(uname -m)-pc-linux-gnu" \
                CC=macos-cc AR=macos-ar RANLIB=macos-ranlib NM="$LLVM_BIN/llvm-nm" CFLAGS="-O2" \
                --disable-shared --enable-static --disable-frontend --disable-dependency-tracking >/dev/null
            make -j"$jobs" >/dev/null && make install >/dev/null
        )
        (
            cd "$sources/ffmpeg-$FFMPEG_VERSION"
            # Only what studio.py uses: MP3/WAV decode to 24 kHz mono PCM, and 192 kbps libmp3lame encode.
            # --disable-autodetect keeps AudioToolbox, VideoToolbox and every external library out.
            ./configure --prefix="$prefix" --enable-cross-compile --target-os=darwin --arch=aarch64 \
                --cc=macos-cc --ar=macos-ar --ranlib=macos-ranlib --nm="$LLVM_BIN/llvm-nm" --host-cc=gcc \
                --pkg-config=false --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib" \
                --disable-autodetect --disable-everything --disable-gpl --disable-nonfree --disable-network \
                --disable-asm --disable-doc --disable-debug --disable-stripping --disable-ffplay --disable-ffprobe \
                --disable-shared --enable-static --enable-pthreads --enable-libmp3lame \
                --enable-decoder='mp3*,pcm_*' --enable-encoder=libmp3lame,pcm_s16le \
                --enable-demuxer=mp3,wav --enable-muxer=mp3,wav --enable-parser=mpegaudio \
                --enable-filter=aresample,aformat,anull,abuffer,abuffersink --enable-protocol=file,pipe >/dev/null
            make -j"$jobs" >/dev/null && make install >/dev/null
        )
        mkdir -p "$BUILD/licenses/ffmpeg" "$BUILD/licenses/lame"
        cp "$sources/ffmpeg-$FFMPEG_VERSION/COPYING.LGPLv2.1" "$sources/ffmpeg-$FFMPEG_VERSION/LICENSE.md" "$BUILD/licenses/ffmpeg/"
        cp "$sources/lame-$LAME_VERSION/COPYING" "$BUILD/licenses/lame/"
    fi
    local config
    config="$(grep -a -o -- '--enable-libmp3lame\|--enable-gpl' "$prefix/bin/ffmpeg" | sort -u || true)"
    [[ "$config" == *--enable-libmp3lame* ]] || fail "FFmpeg was built without libmp3lame."
    [[ "$config" != *--enable-gpl* ]] || fail "FFmpeg must be LGPL-only."
}

webapp() {
    step ".NET self-contained publish for osx-arm64"
    # Publish from a copy, so the container never writes bin/ or obj/ into the source tree.
    local work="/tmp/perene-webapp"
    rm -rf "$work" "${BUILD:?}/webapp"
    mkdir -p "$work"
    tar -C "$ROOT" --exclude=bin --exclude=obj -cf - WebApp | tar --no-same-owner -C "$work" -xf -
    dotnet publish "$work/WebApp/WebApp.csproj" -c Release -r osx-arm64 --self-contained -o "$BUILD/webapp" -nologo -v quiet
}

licenses() {
    local out="$APP/Contents/Resources/licenses" python="$APP/Contents/Resources/python"
    mkdir -p "$out/dotnet" "$out/python" "$out/ui"
    cp "$HERE/THIRD-PARTY-NOTICES.md" "$out/"
    cp -R "$BUILD/licenses/." "$out/"
    if [[ -f "$python/lib/python3.11/LICENSE.txt" ]]; then cp "$python/lib/python3.11/LICENSE.txt" "$out/python/PYTHON-LICENSE.txt"; fi
    cp /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt "$out/dotnet/" 2>/dev/null || true
    cp "$ROOT/WebApp/wwwroot/vendor/"*.txt "$out/ui/"
    python3 - "$BUILD/site-packages" "$out/python/packages" <<'PY'
import importlib.metadata as metadata, shutil, sys
from pathlib import Path
out = Path(sys.argv[2])
for dist in metadata.distributions(path=[sys.argv[1]]):
    for file in dist.files or []:
        if any(word in file.name.upper() for word in ("LICENSE", "LICENCE", "COPYING", "NOTICE")):
            target = out / dist.metadata["Name"] / file.name
            target.parent.mkdir(parents=True, exist_ok=True)
            try:
                shutil.copyfile(file.locate(), target)
            except OSError:
                pass
PY
}

bundle() {
    step "Assembling Perene TTS.app"
    local resources="$APP/Contents/Resources" site
    rm -rf "$APP"
    mkdir -p "$APP/Contents/MacOS" "$resources"
    cp -a "$BUILD/webapp/." "$APP/Contents/MacOS/"
    cp "$BUILD/ffmpeg/bin/ffmpeg" "$APP/Contents/MacOS/ffmpeg"
    tar --no-same-owner -xzf "$BUILD/downloads/$PYTHON_ARCHIVE" -C "$resources"   # creates python/
    site="$resources/python/lib/python3.11/site-packages"
    cp -a "$BUILD/site-packages/." "$site/"
    rm -f "$site/.stamp"
    # The worker itself; runs as `python3.11 -I -m app.desktop`.
    tar -C "$SERVICE" --exclude=__pycache__ -cf - app | tar --no-same-owner -C "$site" -xf -
    cp -a "$BUILD/model" "$resources/model"
    sed "s/@VERSION@/$VERSION/g" "$HERE/Info.plist.in" > "$APP/Contents/Info.plist"
    python3 -c 'import sys; from PIL import Image; Image.open(sys.argv[1]).save(sys.argv[2], format="ICNS")' \
        "$HERE/AppIcon-1024.png" "$resources/AppIcon.icns"
    licenses
}

machos() {  # NUL-separated Mach-O files in the app, found by magic number
    python3 - "$APP" <<'PY'
import os, sys
magics = {b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca"}
for folder, _, names in os.walk(sys.argv[1]):
    for name in names:
        path = os.path.join(folder, name)
        if os.path.islink(path) or not os.path.isfile(path):
            continue
        with open(path, "rb") as stream:
            if stream.read(4) in magics:
                sys.stdout.write(path + "\0")
PY
}

check() {
    step "Checking every Mach-O file: arm64, macOS 14.0 or older, no libraries from outside the app"
    local file problems=0 count=0 headers minos id deps outside
    while IFS= read -r -d '' file; do
        count=$((count + 1))
        if ! headers="$("$LLVM_BIN/llvm-objdump" --macho --arch=arm64 --private-headers "$file" 2>/dev/null)"; then
            echo "  not arm64: ${file#"$DIST/"}"; problems=$((problems + 1)); continue
        fi
        minos="$(awk '$1=="minos" || ($1=="version" && prev ~ /LC_VERSION_MIN_MACOSX/) {print $2; exit} {if ($1=="cmd") prev=$2}' <<<"$headers")"
        if [[ -n "$minos" && "$(printf '%s\n14.0\n' "$minos" | sort -V | tail -1)" != 14.0 ]]; then
            echo "  needs macOS $minos: ${file#"$DIST/"}"; problems=$((problems + 1))
        fi
        # A library's own install name is not a dependency; everything else must be in the app or the OS.
        id="$("$LLVM_BIN/llvm-objdump" --macho --arch=arm64 --dylib-id "$file" 2>/dev/null | tail -n +2 | awk '{print $1}')"
        deps="$("$LLVM_BIN/llvm-objdump" --macho --arch=arm64 --dylibs-used "$file" 2>/dev/null | tail -n +2 | awk '{print $1}')"
        outside="$(grep -vxF "${id:-//none//}" <<<"$deps" | grep -vE '^$|^(@rpath|@loader_path|@executable_path)/|^/usr/lib/|^/System/' || true)"
        if [[ -n "$outside" ]]; then
            echo "  links outside the app: ${file#"$DIST/"} -> $outside"; problems=$((problems + 1))
        fi
    done < <(machos)
    ((problems == 0)) || fail "$problems compatibility problem(s) in $count Mach-O files."
    echo "  $count Mach-O files OK"
}

sign() {
    step "Ad-hoc signing (no Apple account, no hardened runtime)"
    # rcodesign signs nested Mach-O files first, then seals the bundle, like `codesign --sign -` on a Mac.
    rcodesign sign "$APP" >/dev/null
    # `rcodesign verify` does not handle ad-hoc signatures, so read each signature back; Apple's own
    # `codesign --verify --deep --strict` runs on the Mac in make desktop-smoke.
    local file info
    for file in "$APP/Contents/MacOS/WebApp" "$APP/Contents/MacOS/ffmpeg" "$APP/Contents/Resources/python/bin/python3.11"; do
        info="$(rcodesign print-signature-info "$file" 2>&1 || true)"
        [[ "$info" == *ADHOC* ]] || fail "No ad-hoc signature on ${file#"$DIST/"}"
    done
}

package() {
    step "Zip for sharing"
    rm -f "$ZIP"
    # -y keeps Python's symlinks; Finder's Archive Utility restores them and the permissions.
    (cd "$DIST" && zip -qry -X "$ZIP" "Perene TTS.app" && zip -qj -X "$ZIP" "$HERE/How to open.txt")
}

sizes() {
    step "Sizes"
    du -sh "$APP/Contents/MacOS" "$APP/Contents/Resources/python" "$APP/Contents/Resources/model" \
        "$APP/Contents/MacOS/ffmpeg" "$APP" "$ZIP" | sed "s|$ROOT/||"
}

preflight
if (($#)); then  # e.g. `build.sh ffmpeg_build` reruns one step
    for name in "$@"; do "$name"; done
    exit 0
fi
python_runtime
packages
model
ffmpeg_build
webapp
bundle
check
sign
package
sizes
printf '\nDone: dist/Perene TTS.app and dist/Perene-TTS-macos-arm64.zip\n'
printf 'On the Mac: make desktop-run (or double-click the app), and optionally make desktop-smoke.\n'
