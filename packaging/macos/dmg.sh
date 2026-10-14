#!/usr/bin/env bash
# Packs the signed "dist/Perene TTS.app" into dist/Perene-TTS-<VERSION>-macos-arm64.dmg (make desktop-dmg, on the Mac).
# Opening the image shows one 640 x 400 window on flat Perene paper: the PereneTTS logo above the app, a gold arrow and
# an Applications shortcut, so installing is one drag. Uses only tools built into macOS: hdiutil, ditto, tiffutil, osascript (Finder lays out the
# window) and codesign. The writable working image lives in the temp folder; only the finished .dmg goes to dist/.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/packaging/macos"
APP="$ROOT/dist/Perene TTS.app"
VERSION="$(tr -d '[:space:]' < "$HERE/VERSION")"
DMG="$ROOT/dist/Perene-TTS-$VERSION-macos-arm64.dmg"
# Rendered from dmg-background.svg by make desktop-icon (640 x 400 and 1280 x 800); the window below matches them.
BACKGROUND="$HERE/dmg-background.png"
BACKGROUND_2X="$HERE/dmg-background@2x.png"
VOLUME="Perene TTS"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/perene-dmg.XXXXXX")"
MOUNT=""

step() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nError: %s\n' "$*" >&2; exit 1; }

cleanup() {
    if [[ -n "$MOUNT" ]]; then hdiutil detach "$MOUNT" -quiet -force || true; fi
    rm -rf "${WORK:?}"
}
trap cleanup EXIT

attach() {  # image extra-args...; prints the mount point (the third tab-separated column of hdiutil's output)
    hdiutil attach "$@" -noautoopen | awk -F'\t' '$3 ~ /[^[:space:]]/ {point = $3} END {sub(/[[:space:]]+$/, "", point); print point}'
}

preflight() {
    step "Checking the app"
    [[ "$(uname -s)" == Darwin ]] || fail "make desktop-dmg uses hdiutil and Finder, which only exist on macOS."
    [[ -d "$APP" ]] || fail "dist/Perene TTS.app is missing; run make desktop-mac first."
    [[ -f "$BACKGROUND" && -f "$BACKGROUND_2X" ]] || fail "The dmg-background PNGs are missing; run make desktop-icon."
    [[ ! -e "/Volumes/$VOLUME" ]] || fail "A disk named \"$VOLUME\" is mounted; eject it in Finder and rerun."
    codesign --verify --deep --strict "$APP" || fail "dist/Perene TTS.app fails codesign --verify; rebuild it with make desktop-mac."
}

fill() {
    step "Writable image with the app and an Applications shortcut"
    local megabytes
    megabytes=$(( $(du -sm "$APP" | cut -f1) * 11 / 10 + 300 ))  # room for HFS+ metadata
    hdiutil create -quiet -size "${megabytes}m" -fs HFS+ -volname "$VOLUME" "$WORK/rw.dmg"
    MOUNT="$(attach "$WORK/rw.dmg" -readwrite -noverify)"
    [[ "$MOUNT" == "/Volumes/$VOLUME" ]] || fail "The image mounted at $MOUNT instead of /Volumes/$VOLUME."
    # The signature is inside the bundle; extended attributes (OneDrive's, Docker's) are left behind.
    ditto --norsrc --noextattr --noacl "$APP" "$MOUNT/Perene TTS.app"
    ln -s /Applications "$MOUNT/Applications"
    mkdir "$MOUNT/.background"
    # One TIFF with both sizes, so Retina screens get the sharp 2x picture.
    tiffutil -cathidpicheck "$BACKGROUND" "$BACKGROUND_2X" -out "$MOUNT/.background/background.tiff" >/dev/null
}

arrange() {
    step "Arranging the window in Finder"
    # The first run asks to let Terminal (or the editor) control Finder. Declined or timed out, the image still works,
    # it just opens as a plain window.
    if ! osascript - "$VOLUME" >/dev/null 2>"$WORK/finder.log" <<'APPLESCRIPT'
on run argv
    set volumeName to item 1 of argv
    with timeout of 60 seconds
        tell application "Finder"
            tell disk volumeName
                open
                set current view of container window to icon view
                set toolbar visible of container window to false
                set statusbar visible of container window to false
                set bounds of container window to {200, 120, 840, 520}
                set viewOptions to the icon view options of container window
                set arrangement of viewOptions to not arranged
                set icon size of viewOptions to 128
                set text size of viewOptions to 13
                set background picture of viewOptions to file ".background:background.tiff"
                -- Either side of the arrow (x 320), at equal distances. Finder draws each icon's visual centre
                -- about 44 pt below its position, which is where the arrow sits (y 234).
                set position of item "Perene TTS.app" of container window to {160, 190}
                set position of item "Applications" of container window to {480, 190}
                -- Out of view for people whose Finder shows hidden files (only reachable when it does).
                try
                    set position of item ".background" of container window to {900, 900}
                end try
                -- Closing and reopening makes Finder store the window size and layout.
                close
                open
                set bounds of container window to {200, 120, 840, 520}
                update without registering applications
                delay 3
                close
            end tell
        end tell
    end timeout
end run
APPLESCRIPT
    then
        echo "  Finder could not arrange the window, so the image opens as a plain window:" >&2
        sed 's/^/    /' "$WORK/finder.log" >&2
        echo "  To allow it: System Settings > Privacy & Security > Automation > (Terminal or your editor) > Finder." >&2
        return 0
    fi
    # Finder saves the layout to .DS_Store on its own schedule: wait until it exists and stops changing.
    local waited=0 last="" now
    while ((waited < 30)); do
        now="$(stat -f %m "$MOUNT/.DS_Store" 2>/dev/null || true)"
        [[ -n "$now" && "$now" == "$last" ]] && break
        last="$now"; sleep 2; waited=$((waited + 2))
    done
    [[ -f "$MOUNT/.DS_Store" ]] || echo "  Finder did not save the layout; the image opens as a plain window." >&2
}

compress() {
    step "Compressing to ${DMG#"$ROOT/"}"
    # macOS's file-event log; it would show as a folder for people whose Finder shows hidden files.
    rm -rf "$MOUNT/.fseventsd" || true
    sync
    hdiutil detach "$MOUNT" -quiet
    MOUNT=""
    rm -f "$DMG"
    # LZFSE (ULFO) opens on every macOS the app supports (14.0 or later).
    hdiutil convert "$WORK/rw.dmg" -quiet -format ULFO -o "$DMG"
    rm -f "$WORK/rw.dmg"
    codesign --force --sign - "$DMG"
}

verify() {
    step "Verifying the disk image and the app inside it"
    hdiutil verify -quiet "$DMG"
    mkdir -p "$WORK/check"
    MOUNT="$(attach "$DMG" -readonly -nobrowse -mountpoint "$WORK/check")"
    codesign --verify --deep --strict --verbose=1 "$MOUNT/Perene TTS.app"
    hdiutil detach "$MOUNT" -quiet
    MOUNT=""
    du -sh "$DMG" | sed "s|$ROOT/||"
}

preflight
fill
arrange
compress
verify
printf '\nDone: %s\n' "${DMG#"$ROOT/"}"
