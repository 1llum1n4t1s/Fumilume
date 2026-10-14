#!/usr/bin/env bash
set -euo pipefail

artifact="${1:?検証するアプリまたはPKGを指定してください。}"
evidence_dir="${2:?検証ログの保存先を指定してください。}"
mkdir -p "$evidence_dir"

# 公証受理後でもチケットの照会は通信を行う。検証だけを限定再試行し、署名・提出は繰り返さない。
for attempt in 1 2 3 4 5; do
  attempt_log="$evidence_dir/attempt-$attempt.log"
  if xcrun stapler validate "$artifact" >"$attempt_log" 2>&1; then
    cat "$attempt_log"
    printf 'Notarization validation passed (attempt %s): %s\n' "$attempt" "$artifact"
    exit 0
  else
    status=$?
  fi
  cat "$attempt_log" >&2
  # exit 68だけでは原因を特定できない。不正なチケットや証明書の失敗はそのまま停止する。
  if [[ "$status" != 68 ]] || ! grep -Eq 'NSURLErrorDomain[[:space:]]+Code=-(1005|1001)([^0-9]|$)' "$attempt_log"; then
    exit "$status"
  fi
  if [[ "$attempt" == 5 ]]; then
    echo "Appleのチケット照会への接続に失敗し続けたため、検証を停止します。" >&2
    exit "$status"
  fi
  delay=$((2 ** attempt))
  printf 'Transient Apple ticket connection failure; retrying validation in %s seconds (%s/5).\n' "$delay" "$attempt" >&2
  sleep "$delay"
done
