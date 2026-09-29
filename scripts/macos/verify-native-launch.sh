#!/usr/bin/env bash
set -euo pipefail
# 配布物の Native AOT 実行ファイルを LaunchServices から起動し、通常終了を確認する。
bundle="$(cd "$1" && pwd)"
mkdir -p "$2"
record_dir="$(cd "$2" && pwd)"
if pgrep -x Fumilume >/dev/null; then
  echo "既存 Fumilume が起動中のため、起動検証を停止します。" >&2
  exit 1
fi
home_dir="$(mktemp -d "$record_dir/home.XXXXXX")"
mkdir -p "$home_dir/Library/Application Support/Fumilume"
printf '{"CheckUpdatesOnStartup":false,"RestoreSession":true,"ConfirmOnExit":false}\n' \
  > "$home_dir/Library/Application Support/Fumilume/settings.json"
pid=""
open_pid=""
cleanup() {
  status=$?
  trap - EXIT
  # 今回起動したプロセスだけを、失敗時にも残さない。
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
  fi
  if [[ -n "$open_pid" ]] && kill -0 "$open_pid" 2>/dev/null; then
    kill "$open_pid" 2>/dev/null || true
  fi
  # ユーザー設定は一時 HOME 内だけに生成。診断ログを残して設定を清掃する。
  if [[ -d "$home_dir/Library/Application Support/Fumilume/logs" ]]; then
    mkdir -p "$record_dir/logs"
    cp -R "$home_dir/Library/Application Support/Fumilume/logs/." "$record_dir/logs/"
  fi
  rm -rf -- "$home_dir"
  exit "$status"
}
trap cleanup EXIT
open -n -W -a "$bundle" --env "HOME=$home_dir" \
  --stdout "$record_dir/stdout.log" --stderr "$record_dir/stderr.log" &
open_pid=$!
for attempt in {1..30}; do
  pid="$(pgrep -x Fumilume || true)"
  [[ -n "$pid" ]] && break
  kill -0 "$open_pid" 2>/dev/null || { echo "LaunchServices が起動に失敗しました。" >&2; exit 1; }
  sleep 1
done
if [[ ! "$pid" =~ ^[0-9]+$ ]]; then
  echo "起動した Fumilume の単一プロセスを確認できませんでした。" >&2
  exit 1
fi
sleep 5
kill -0 "$pid"
logs="$home_dir/Library/Application Support/Fumilume/logs"
test -d "$logs"
grep -R -q 'Fumilume を起動します。' "$logs"
# Finder と同じ AppleEvent 経路で、配布した Native AOT アプリへ文書を渡す。
document="$record_dir/Native AOT 日本語.txt"
printf 'Native AOT で開く日本語の文書\n' > "$document"
open -a "$bundle" "$document"
sleep 2
# quit AppleEvent を送り、強制終了なしで閉じることを確認する。
osascript - "$bundle" <<'APPLESCRIPT'
on run arguments
  tell application (item 1 of arguments) to quit
end run
APPLESCRIPT
for attempt in {1..30}; do
  if ! kill -0 "$pid" 2>/dev/null; then
    break
  fi
  sleep 1
done
if kill -0 "$pid" 2>/dev/null; then
  echo "通常の終了操作後に Fumilume が終了しませんでした。" >&2
  exit 1
fi
wait "$open_pid"
grep -R -q 'Fumilume を終了します。' "$logs"
python3 - "$home_dir/Library/Application Support/Fumilume/session.json" "$document" <<'PY'
import json, pathlib, sys
session, document = pathlib.Path(sys.argv[1]), sys.argv[2]
state = json.loads(session.read_text(encoding='utf-8-sig'))
assert any(tab.get('FilePath') == document for tab in state['Tabs']), 'Finder document activation was not persisted'
assert pathlib.Path(document).read_text(encoding='utf-8') == 'Native AOT で開く日本語の文書\n'
PY
if grep -R -E '\[(ERROR|FATAL)\]|Unhandled exception|Segmentation fault|dyld\[' \
  "$logs" "$record_dir/stdout.log" "$record_dir/stderr.log"; then
  echo "Native AOT 起動・終了中に実行エラーを検出しました。" >&2
  exit 1
fi
printf '{"architecture":"arm64","launchServices":true,"finderDocumentOpen":true,"survivedStartup":true,"normalExit":true,"runtimeErrors":false}\n' \
  > "$record_dir/result.json"
