#!/bin/sh
set -eu
if [ "$(uname -s)" != "Darwin" ]; then
    printf '%s\n' 'Build the AppKit foundation on macOS.' >&2
    exit 1
fi
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
bundle="$root/artifacts/macos/Tabrilo.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp "$root/apps/macos/Info.plist" "$bundle/Contents/Info.plist"
cp "$root/shared/contracts/commands.json" "$root/shared/contracts/formats.json" "$bundle/Contents/Resources/"
xcrun swiftc -framework AppKit "$root/apps/macos/Sources/main.swift" -o "$bundle/Contents/MacOS/Tabrilo"
printf '%s\n' "$bundle"
