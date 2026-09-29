#!/usr/bin/env bash
set -euo pipefail

# Apple Silicon Mac 上で、ローカル検証用の Native AOT .app を作る。
repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "Apple Silicon 搭載の macOS で実行してください。" >&2
  exit 1
fi

version="$(dotnet msbuild src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true -getProperty:Version)"
if [[ ! "$version" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "Info.plist に使える数値版を取得できませんでした: $version" >&2
  exit 1
fi

build_dir="$repo_dir/local-macos-build/$(date -u +%Y%m%dT%H%M%SZ)-$version-arm64"
bundle="$build_dir/Fumilume.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

dotnet restore src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true -r osx-arm64 --locked-mode
dotnet publish src/Fumilume/Fumilume.csproj \
  -p:FumilumeTargetMac=true -r osx-arm64 -c Release --no-restore \
  -o "$bundle/Contents/MacOS"

sed "s/@VERSION@/$version/g" scripts/macos/Info.plist \
  > "$bundle/Contents/Info.plist"
plutil -lint "$bundle/Contents/Info.plist"

icon_tmp="$(mktemp -d)"
iconset="$icon_tmp/Fumilume.iconset"
mkdir -p "$iconset"
trap 'rm -rf -- "$icon_tmp"' EXIT
icon_source="$repo_dir/src/Fumilume/icon/app_icon_crystal_warm.png"
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" "$icon_source" \
    --out "$iconset/icon_${size}x${size}.png" >/dev/null
  doubled=$((size * 2))
  sips -s format png -z "$doubled" "$doubled" "$icon_source" \
    --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/Fumilume.icns"

chmod +x "$bundle/Contents/MacOS/Fumilume"
if [[ "$(lipo -archs "$bundle/Contents/MacOS/Fumilume")" != "arm64" ]]; then
  echo "発行された実行ファイルが Apple Silicon 専用ではありません。" >&2
  exit 1
fi
codesign --force --sign - "$bundle"
codesign --verify --strict "$bundle"
ditto -c -k --sequesterRsrc --keepParent "$bundle" "$build_dir/Fumilume-arm64.zip"

echo "ローカル検証用のアプリ: $bundle"
echo "ローカル検証用のZIP:  $build_dir/Fumilume-arm64.zip"
