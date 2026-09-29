#!/usr/bin/env bash
set -euo pipefail
# Developer ID 署名・公証済みの Apple Silicon 配布物を作成する。
repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
  echo "Apple Silicon 搭載の macOS で実行してください。" >&2
  exit 1
fi
for name in APPLE_CERT_APP_P12_BASE64 APPLE_CERT_INSTALLER_P12_BASE64 APPLE_CERT_PASSWORD \
  APPLE_SIGN_APP_IDENTITY APPLE_SIGN_INSTALL_IDENTITY APPLE_ID APPLE_TEAM_ID APPLE_APP_PASSWORD; do
  if [[ -z "${!name:-}" ]]; then
    echo "署名・公証に必要な環境変数が未設定です: $name" >&2
    exit 1
  fi
done
sdk_version="$(python3 -c 'import json; print(json.load(open("global.json"))["sdk"]["version"])')"
if [[ "$(dotnet --version)" != "$sdk_version" ]]; then
  echo "global.json が指定する .NET SDK $sdk_version を使用してください。" >&2
  exit 1
fi
version="$(dotnet msbuild src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true -getProperty:Version)"
if [[ ! "$version" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "Info.plist に使える数値版を取得できませんでした。" >&2
  exit 1
fi
build_dir="$repo_dir/local-macos-build"
artifacts="$build_dir/artifacts"
verification="$build_dir/verification"
mkdir -p "$artifacts" "$verification"
if [[ -n "$(find "$artifacts" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "既存の配布物があるため停止します: $artifacts" >&2
  exit 1
fi
# 今回生成する秘密素材だけを専用一時ディレクトリで管理する。
work_dir="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/fumilume-release.XXXXXX")"
keychain="$work_dir/signing.keychain-db"
keychain_password="$(openssl rand -base64 32)"
original_keychains=()
while IFS= read -r item; do
  item="$(printf '%s' "$item" | sed 's/^[[:space:]]*"//;s/"[[:space:]]*$//')"
  [[ -n "$item" ]] && original_keychains+=("$item")
done < <(security list-keychains -d user)
cleanup() {
  status=$?
  trap - EXIT
  security list-keychains -d user -s "${original_keychains[@]}" || true
  security delete-keychain "$keychain" >/dev/null 2>&1 || true
  rm -rf -- "$work_dir"
  exit "$status"
}
trap cleanup EXIT
security create-keychain -p "$keychain_password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
for name in APPLE_CERT_APP_P12_BASE64 APPLE_CERT_INSTALLER_P12_BASE64; do
  p12="$work_dir/$name.p12"
  printf '%s' "${!name}" | base64 --decode > "$p12"
  security import "$p12" -P "$APPLE_CERT_PASSWORD" -t cert -f pkcs12 \
    -k "$keychain" -T /usr/bin/codesign -T /usr/bin/productsign >/dev/null
  rm -f -- "$p12"
done
security set-key-partition-list -S apple-tool:,apple:,codesign: -s \
  -k "$keychain_password" "$keychain" >/dev/null
security list-keychains -d user -s "$keychain" "${original_keychains[@]}"
xcrun notarytool store-credentials fumilume-notary --apple-id "$APPLE_ID" \
  --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --keychain "$keychain" >/dev/null
publish="$work_dir/publish"
dotnet restore src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true -r osx-arm64 --locked-mode
dotnet publish src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true \
  -r osx-arm64 -c Release --no-restore -o "$publish"
sed "s/@VERSION@/$version/g" scripts/macos/Info.plist > "$work_dir/Info.plist"
plutil -lint "$work_dir/Info.plist" scripts/macos/NativeAot.entitlements
iconset="$work_dir/Fumilume.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" src/Fumilume/icon/app_icon_crystal_warm.png \
    --out "$iconset/icon_${size}x${size}.png" >/dev/null
  doubled=$((size * 2))
  sips -s format png -z "$doubled" "$doubled" src/Fumilume/icon/app_icon_crystal_warm.png \
    --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$work_dir/Fumilume.icns"
chmod +x "$publish/Fumilume"
if [[ "$(lipo -archs "$publish/Fumilume")" != arm64 ]]; then
  echo "Native AOT 実行ファイルが Apple Silicon 専用ではありません。" >&2
  exit 1
fi
# --deep を使わず、すべてのネイティブ実行ファイル・ライブラリを署名する。
while IFS= read -r -d '' binary; do
  if file -b "$binary" | grep -q 'Mach-O'; then
    lipo -verify_arch arm64 "$binary"
    codesign --force --sign "$APPLE_SIGN_APP_IDENTITY" --keychain "$keychain" \
      --options runtime --timestamp --entitlements scripts/macos/NativeAot.entitlements "$binary"
    codesign --verify --strict "$binary"
  fi
done < <(find "$publish" -type f -print0)
dotnet tool install vpk --version 1.2.161 --tool-path "$work_dir/tools"
"$work_dir/tools/vpk" pack --packId Fumilume --packTitle Fumilume --packVersion "$version" \
  --mainExe Fumilume --packDir "$publish" --outputDir "$artifacts" \
  --runtime osx-arm64 --channel osx-arm64 --icon "$work_dir/Fumilume.icns" \
  --plist "$work_dir/Info.plist" --signAppIdentity "$APPLE_SIGN_APP_IDENTITY" \
  --signInstallIdentity "$APPLE_SIGN_INSTALL_IDENTITY" --signDisableDeep \
  --signEntitlements "$repo_dir/scripts/macos/NativeAot.entitlements" \
  --notaryProfile fumilume-notary --keychain "$keychain"
# 配布 ZIP の中身を検証し、そのまま起動確認に使う。
portable="$artifacts/Fumilume-osx-arm64-Portable.zip"
installer="$artifacts/Fumilume-osx-arm64-Setup.pkg"
test -f "$portable"
test -f "$installer"
test -f "$artifacts/releases.osx-arm64.json"
ditto -x -k "$portable" "$work_dir/verify"
bundle="$work_dir/verify/Fumilume.app"
codesign --verify --deep --strict "$bundle"
spctl --assess --type execute --verbose=4 "$bundle"
xcrun stapler validate "$bundle"
pkgutil --check-signature "$installer"
spctl --assess --type install --verbose=4 "$installer"
xcrun stapler validate "$installer"
while IFS= read -r -d '' binary; do
  if file -b "$binary" | grep -q 'Mach-O'; then
    lipo -verify_arch arm64 "$binary"
    codesign --verify --strict "$binary"
  fi
done < <(find "$bundle" -type f -print0)
bash scripts/macos/verify-native-launch.sh "$bundle" "$verification/native-launch"
if [[ "${CI:-}" == true ]]; then
  # 新規のCI Macでは実インストール後のアプリも起動して確認する。
  if [[ -e /Applications/Fumilume.app ]]; then
    echo "既存のインストールがあるため停止します。" >&2
    exit 1
  fi
  sudo installer -pkg "$installer" -target /
  codesign --verify --deep --strict /Applications/Fumilume.app
  spctl --assess --type execute --verbose=4 /Applications/Fumilume.app
  bash scripts/macos/verify-native-launch.sh /Applications/Fumilume.app "$verification/installed-launch"
fi
(cd "$artifacts" && shasum -a 256 ./*) > "$verification/SHA256SUMS"
printf '{"version":"%s","runtime":"osx-arm64","vpk":"1.2.161","signed":true,"notarizedApp":true,"notarizedInstaller":true,"machOVerified":true,"nativeLaunchVerified":true}\n' \
  "$version" > "$verification/distribution.json"
printf '配布物: %s\n検証記録: %s\n' "$artifacts" "$verification"
