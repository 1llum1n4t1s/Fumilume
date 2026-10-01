#!/usr/bin/env bash
set -euo pipefail
# Velopack 1.2.161 の UpdateMac apply で実 .app を同版パッケージに置換する。
# 検出対象: CLI 不整合、誤った適用先、置換失敗、署名破損、再起動失敗、正常終了失敗。
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
  echo "更新適用の検証には Apple Silicon macOS が必要です。" >&2
  exit 1
fi
if [[ "$#" -ne 3 ]]; then
  echo "使用法: verify-update-apply.sh SOURCE.app ARTIFACTS RECORD_DIRECTORY" >&2
  exit 1
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_bundle="$(cd "$1" && pwd)"
artifacts="$(cd "$2" && pwd)"
mkdir -p "$3"
record_dir="$(cd "$3" && pwd)"
if pgrep -x Fumilume >/dev/null; then
  echo "既存 Fumilume が起動中のため、更新適用検証を停止します。" >&2
  exit 1
fi
shopt -s nullglob
full_packages=("$artifacts"/*-full.nupkg)
shopt -u nullglob
if [[ "${#full_packages[@]}" -ne 1 ]]; then
  echo "新規配布物に full.nupkg が 1 個必要です。" >&2
  exit 1
fi
work_dir="$(mktemp -d "$record_dir/work.XXXXXX")"
root="$work_dir/Fumilume.app"
home_dir="$work_dir/home"
package_dir="$home_dir/Library/Caches/velopack/Fumilume/packages"
settings_dir="$home_dir/Library/Application Support/Fumilume"
logs="$settings_dir/logs"
updater_pid=""
native_pid=""
stage=prepare
cleanup() {
  status=$?
  trap - EXIT
  if [[ -n "$updater_pid" ]] && kill -0 "$updater_pid" 2>/dev/null; then
    kill "$updater_pid" 2>/dev/null || true
  fi
  # 失敗時も今回のコピーから起動したアプリだけを止める。
  if [[ -z "$native_pid" ]]; then
    native_pid="$(pgrep -x Fumilume || true)"
  fi
  if [[ "$native_pid" =~ ^[0-9]+$ ]] && kill -0 "$native_pid" 2>/dev/null; then
    command_line="$(ps -p "$native_pid" -o command= || true)"
    if [[ "$command_line" == "$root/Contents/MacOS/Fumilume"* ]]; then
      kill "$native_pid" 2>/dev/null || true
    fi
  fi
  if [[ -d "$logs" ]]; then
    mkdir -p "$record_dir/native-logs"
    cp -R "$logs/." "$record_dir/native-logs/"
  fi
  if [[ "$status" -ne 0 ]]; then
    printf '{"success":false,"stage":"%s","exitCode":%s}\n' "$stage" "$status" > "$record_dir/result.json"
  fi
  rm -rf -- "$work_dir"
  exit "$status"
}
trap cleanup EXIT
mkdir -p "$package_dir" "$settings_dir"
printf '{"CheckUpdatesOnStartup":false,"RestoreSession":true,"ConfirmOnExit":false}\n' \
  > "$settings_dir/settings.json"
ditto "$source_bundle" "$root"
package="$package_dir/$(basename "${full_packages[0]}")"
cp "${full_packages[0]}" "$package"
updater="$root/Contents/MacOS/UpdateMac"
test -x "$updater"
env HOME="$home_dir" "$updater" --help > "$record_dir/updater-help.txt" 2>&1
env HOME="$home_dir" "$updater" apply --help > "$record_dir/apply-help.txt" 2>&1
for option in rootDir packageDir log silent; do
  grep -q -- "--$option" "$record_dir/updater-help.txt"
done
grep -q -- '--package' "$record_dir/apply-help.txt"
grep -q -- '--norestart' "$record_dir/apply-help.txt"
# 更新前後が同版であることをパッケージと実 .app のメタデータで照合する。
version="$(python3 - "$root" "$package" <<'PY'
import pathlib, plistlib, sys, zipfile, xml.etree.ElementTree as ET
root, package = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
def version_from_xml(data):
    node = ET.fromstring(data)
    return next(e.text for e in node.iter() if e.tag.split('}')[-1] == 'version')
current = version_from_xml((root / 'Contents/Resources/sq.version').read_bytes())
with zipfile.ZipFile(package) as archive:
    manifests = [n for n in archive.namelist() if n.endswith('.nuspec') and '/' not in n]
    assert len(manifests) == 1, 'Package must contain one root nuspec'
    assert version_from_xml(archive.read(manifests[0])) == current, 'Same-version apply fixture required'
with (root / 'Contents/Info.plist').open('rb') as f:
    assert plistlib.load(f)['CFBundleShortVersionString'] == current
print(current)
PY
)"
cp "$root/Contents/Resources/sq.version" "$record_dir/sq.version.before.xml"
codesign --verify --deep --strict "$root" 2> "$record_dir/codesign-before.log"
codesign -d --verbose=4 "$root" 2> "$record_dir/signature-before.log"
before_inode="$(stat -f '%i' "$root")"
before_exe_inode="$(stat -f '%i' "$root/Contents/MacOS/Fumilume")"
before_hash="$(shasum -a 256 "$root/Contents/MacOS/Fumilume" | awk '{print $1}')"
stat -f 'path=%N inode=%i uid=%u gid=%g owner=%Su group=%Sg mode=%Sp' "$root" \
  "$root/Contents/MacOS/Fumilume" > "$record_dir/stat-before.txt"
if [[ -e /Applications/Fumilume.app ]]; then
  stat -f 'path=%N inode=%i uid=%u gid=%g owner=%Su group=%Sg mode=%Sp' \
    /Applications /Applications/Fumilume.app /Applications/Fumilume.app/Contents/MacOS/Fumilume \
    > "$record_dir/installed-owner-mode.txt"
else
  printf '/Applications/Fumilume.app is not installed at verification time.\n' > "$record_dir/installed-owner-mode.txt"
fi
stage=apply
# --norestart を省略し、UpdateMac 自身の open による再起動を検証する。
# HOME は updater と子プロセスへ渡し、アプリのログも専用 HOME にあることを必須にする。
env HOME="$home_dir" "$updater" --rootDir "$root" --packageDir "$package_dir" \
  --log "$record_dir/update.log" --silent --verbose apply --package "$package" \
  > "$record_dir/updater-stdout.log" 2> "$record_dir/updater-stderr.log" &
updater_pid=$!
for attempt in {1..180}; do
  if ! kill -0 "$updater_pid" 2>/dev/null; then
    break
  fi
  sleep 1
done
if kill -0 "$updater_pid" 2>/dev/null; then
  echo "更新適用が制限時間内に完了しませんでした。" >&2
  exit 1
fi
wait "$updater_pid"
updater_pid=""
stage=verify-replacement
after_inode="$(stat -f '%i' "$root")"
after_exe_inode="$(stat -f '%i' "$root/Contents/MacOS/Fumilume")"
after_hash="$(shasum -a 256 "$root/Contents/MacOS/Fumilume" | awk '{print $1}')"
[[ "$before_inode" != "$after_inode" ]]
[[ "$before_exe_inode" != "$after_exe_inode" ]]
[[ "$before_hash" == "$after_hash" ]]
cp "$root/Contents/Resources/sq.version" "$record_dir/sq.version.after.xml"
cmp "$record_dir/sq.version.before.xml" "$record_dir/sq.version.after.xml"
codesign --verify --deep --strict "$root" 2> "$record_dir/codesign-after.log"
codesign -d --verbose=4 "$root" 2> "$record_dir/signature-after.log"
spctl --assess --type execute --verbose=4 "$root" 2> "$record_dir/gatekeeper-after.log"
lipo "$root/Contents/MacOS/Fumilume" -verify_arch arm64
stat -f 'path=%N inode=%i uid=%u gid=%g owner=%Su group=%Sg mode=%Sp' "$root" \
  "$root/Contents/MacOS/Fumilume" > "$record_dir/stat-after.txt"
grep -q 'applied successfully' "$record_dir/update.log"
grep -q 'Starting application: open' "$record_dir/update.log"
stage=verify-auto-restart
for attempt in {1..30}; do
  native_pid="$(pgrep -x Fumilume || true)"
  [[ -n "$native_pid" ]] && break
  sleep 1
done
[[ "$native_pid" =~ ^[0-9]+$ ]]
command_line="$(ps -p "$native_pid" -o command=)"
[[ "$command_line" == "$root/Contents/MacOS/Fumilume"* ]]
sleep 5
kill -0 "$native_pid"
test -d "$logs"
grep -R -q 'Fumilume を起動します。' "$logs"
ps -p "$native_pid" -o pid=,ppid=,command= > "$record_dir/restarted-process.txt"
stage=verify-normal-exit
osascript - "$root" <<'APPLESCRIPT'
on run arguments
  try
    tell application (item 1 of arguments) to quit
  on error messageText number errorNumber
    -- 非同期の保存中は終了要求が一旦キャンセルされる。後段で実際の終了と保存を検証する。
    if errorNumber is not -128 then error messageText number errorNumber
  end try
end run
APPLESCRIPT
for attempt in {1..30}; do
  if ! kill -0 "$native_pid" 2>/dev/null; then
    break
  fi
  sleep 1
done
if kill -0 "$native_pid" 2>/dev/null; then
  echo "自動再起動したアプリが正常終了しませんでした。" >&2
  exit 1
fi
grep -R -q 'Fumilume を終了します。' "$logs"
if grep -R -E '\[(ERROR|FATAL)\]|Unhandled exception|Segmentation fault|dyld\[' \
  "$logs" "$record_dir/update.log" "$record_dir/updater-stdout.log" "$record_dir/updater-stderr.log"; then
  echo "更新適用・再起動中に実行エラーを検出しました。" >&2
  exit 1
fi
installed_tested=false
installed_before_inode=""
installed_after_inode=""
if [[ "${CI:-}" == true && -d /Applications/Fumilume.app ]]; then
  stage=installed-apply
  installed_root=/Applications/Fumilume.app
  installed_before_inode="$(stat -f '%i' "$installed_root")"
  installed_before_exe_inode="$(stat -f '%i' "$installed_root/Contents/MacOS/Fumilume")"
  installed_before_hash="$(shasum -a 256 "$installed_root/Contents/MacOS/Fumilume" | awk '{print $1}')"
  cp "$installed_root/Contents/Resources/sq.version" "$record_dir/installed-sq.version.before.xml"
  cmp "$record_dir/sq.version.before.xml" "$record_dir/installed-sq.version.before.xml"
  codesign --verify --deep --strict "$installed_root" 2> "$record_dir/installed-codesign-before.log"
  # CI の使い捨て VM だけで管理者として適用する。認証ダイアログは検証対象外。
  sudo -n env HOME="$home_dir" "$updater" --rootDir "$installed_root" --packageDir "$package_dir" \
    --log "$record_dir/installed-update.log" --silent --verbose apply --package "$package" --norestart \
    > "$record_dir/installed-updater-stdout.log" 2> "$record_dir/installed-updater-stderr.log"
  installed_after_inode="$(stat -f '%i' "$installed_root")"
  installed_after_exe_inode="$(stat -f '%i' "$installed_root/Contents/MacOS/Fumilume")"
  installed_after_hash="$(shasum -a 256 "$installed_root/Contents/MacOS/Fumilume" | awk '{print $1}')"
  [[ "$installed_before_inode" != "$installed_after_inode" ]]
  [[ "$installed_before_exe_inode" != "$installed_after_exe_inode" ]]
  [[ "$installed_before_hash" == "$installed_after_hash" ]]
  [[ "$installed_after_hash" == "$after_hash" ]]
  cp "$installed_root/Contents/Resources/sq.version" "$record_dir/installed-sq.version.after.xml"
  cmp "$record_dir/installed-sq.version.before.xml" "$record_dir/installed-sq.version.after.xml"
  codesign --verify --deep --strict "$installed_root" 2> "$record_dir/installed-codesign-after.log"
  codesign -d --verbose=4 "$installed_root" 2> "$record_dir/installed-signature-after.log"
  spctl --assess --type execute --verbose=4 "$installed_root" 2> "$record_dir/installed-gatekeeper-after.log"
  lipo "$installed_root/Contents/MacOS/Fumilume" -verify_arch arm64
  stat -f 'path=%N inode=%i uid=%u gid=%g owner=%Su group=%Sg mode=%Sp' \
    /Applications "$installed_root" "$installed_root/Contents/MacOS/Fumilume" \
    > "$record_dir/installed-owner-mode-after.txt"
  grep -q 'applied successfully' "$record_dir/installed-update.log"
  if grep -E '\[(ERROR|FATAL)\]|Unhandled exception|Segmentation fault|dyld\[' \
    "$record_dir/installed-update.log" "$record_dir/installed-updater-stdout.log" "$record_dir/installed-updater-stderr.log"; then
    echo "インストール済みアプリの更新中に実行エラーを検出しました。" >&2
    exit 1
  fi
  stage=installed-user-launch
  # root として GUI を起動せず、通常ユーザーに戻って実際の起動・終了を確認する。
  bash "$script_dir/verify-native-launch.sh" "$installed_root" "$record_dir/installed-native-launch"
  installed_tested=true
fi
python3 - "$record_dir/result.json" "$version" "$before_inode" "$after_inode" \
  "$before_exe_inode" "$after_exe_inode" "$after_hash" "$native_pid" \
  "$installed_tested" "$installed_before_inode" "$installed_after_inode" <<'PY'
import json, pathlib, sys
output, version, before, after, exe_before, exe_after, sha256, pid, installed, installed_before, installed_after = sys.argv[1:]
pathlib.Path(output).write_text(json.dumps({
    'success': True, 'version': version, 'sameVersionApply': True, 'runtime': 'osx-arm64',
    'bundleInodeBefore': before, 'bundleInodeAfter': after,
    'executableInodeBefore': exe_before, 'executableInodeAfter': exe_after,
    'executableSha256': sha256, 'signatureVerified': True, 'gatekeeperAccepted': True,
    'updaterAutoRestart': True, 'restartedPid': int(pid), 'normalExit': True,
    'isolatedHomeLogsVerified': True, 'runtimeErrors': False,
    'installedPrivilegedApplyVerified': installed == 'true',
    'installedBundleInodeBefore': installed_before or None,
    'installedBundleInodeAfter': installed_after or None,
    'installedNormalUserLaunchVerified': installed == 'true',
    'administratorAuthenticationPromptTested': False,
}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
PY
printf '更新適用・自動再起動の検証記録: %s\n' "$record_dir"
