#!/bin/bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$PROJECT_ROOT"

# Use ad hoc signing by default; opt into a specific signing identity explicitly.
signing_identity="${CODE_SIGN_IDENTITY:--}"
build_args=(-c release --arch arm64 --arch x86_64)
swift build "${build_args[@]}" -Xswiftc -file-prefix-map -Xswiftc "$PROJECT_ROOT=."
BIN_DIR="$(swift build "${build_args[@]}" --show-bin-path)"
APP_PATH="$PROJECT_ROOT/dist/Translated.app"

mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"
cp "$BIN_DIR/Translated" "$APP_PATH/Contents/MacOS/Translated"
cp "$PROJECT_ROOT/Resources/Info.plist" "$APP_PATH/Contents/Info.plist"
# Debug symbol tables can embed the builder's absolute source and object paths.
strip -S "$APP_PATH/Contents/MacOS/Translated"
swift "$PROJECT_ROOT/scripts/make-icon.swift" "$PROJECT_ROOT/dist/AppIcon.iconset"
iconutil -c icns "$PROJECT_ROOT/dist/AppIcon.iconset" -o "$APP_PATH/Contents/Resources/AppIcon.icns"
codesign --force --sign "$signing_identity" --identifier app.translated.macos "$APP_PATH"
codesign --verify --strict "$APP_PATH"
echo "Built: $APP_PATH"
