# リリース手順

- リリースは既存の `$vava` ワークフローに従い、版番号を一度だけ決定する。
- 外部状態を変えない事前確認は `pwsh -NoProfile -File scripts/release-local.ps1 -PreflightOnly`。署名付きローカル成果物までの確認には `-SkipUpload` を使う。事前確認と本実行には同じ外部の `-WorkRoot` と `-RecordRoot` を明示し、再開も同じ値を使う。
- 通常の公開は、main の commit に付けた軽量タグ `v<版>` の push で起動する `.github/workflows/release.yml`（Environment `release`、承認待ちなし。`guard` ジョブがタグ・commit・版を検査する）が `scripts/release-all.ps1` を実行して行う。手元から重ねて配信しない。GitHub Release は作らない。
- `release-all.ps1` は同じHEADのMac CIの成功を確認し、Windows x64/ARM64の署名・検証・アップロードと Mac arm64 の配信を完走させる。`/vava` が監視するのは `release.yml` だけで、`macos-release.yml` は `release-all.ps1` が同じタグを `--ref` にして内部で起動・待機する。
- Windows の署名は Sigilune（`scripts/Sigilune.Common.ps1`）で、CI は `SIGILUNE_EMAIL` / `SIGILUNE_TOTP_URI` / `CLOUDFLARE_API_TOKEN` を Environment の Secrets から受け取る。Mac は `macos-latest` で Native AOT・署名・公証・E2E を行い、証明書は Secrets から一時キーチェーンへ取り込む。
- Windowsのpack・署名後は `release-local.ps1` が `scripts/verify-windows-package.ps1` を自動実行する（`-SkipUpload` でも同じ）。失敗時は公開へ進めない。検証対象と記録は DESIGN.md の「更新と配布」を参照し、静的検証の成功を配布物の起動・画面操作の確認として扱わない。
- 出荷判定は、Windowsが `release-local.ps1` の署名・配布物検証、Macが `macos-release.yml` のNative AOT・署名・公証・E2E成功とR2アップロード成功。
- 作業出力は既定でOSの一時フォルダー配下の `Fumilume/<task-id>`、最小限の再開・清掃記録は `TaskArtifacts/Fumilume/<task-id>`（`-WorkRoot` / `-RecordRoot` で変更でき、CIはランナーの一時領域を指定する）。配布物は `WorkRoot/packages/`、ツールは `WorkRoot/tools/`。今回の不要な配布物・検証環境・ツールは対象を確認してPrunlet CLIをCMDから直接呼び、ファイル単位で清掃する。`build-*` 内の `bin` / `obj` とその親ディレクトリは保持する。既存の旧出力・共有ツールは今回の清掃に含めない。
- 証明書・Cloudflare認証情報はリポジトリ外の正本を使い、秘密値をコード、ログ、fixture、Git差分へ出力しない。

## R2 配布

R2 配布は PowerShell 7.2 以降と同梱の `scripts/R2Distribution.Common.ps1` を使い、Node.js・pnpm・Wrangler は不要。署名・転送・検証の正本は `scripts/R2Transfer.Core.cs`、PS helper は公開関数の互換入口とする。更新時は中核と使う入口を同じディレクトリへ同梱する。固定名は `no-cache, must-revalidate`、バージョン付き成果物は不変として 1 年キャッシュし、payload を先、assets・RELEASES・releases を最後に送信する。

## 再実行と復旧

- `release.yml` の再実行は同じタグで行う: `gh workflow run release.yml --repo 1llum1n4t1s/Fumilume --ref v<version>`。タグ以外（main など）の dispatch は `guard` に拒否される。Windows の配信後に再実行すると同じ版を作り直して配信し直す。
- Mac CI が失敗した場合、`Get-MacRun` は同じ SHA・タグ名の最新 run が失敗だと止まるため、`release.yml` の再実行だけでは直らない。一時的な失敗は Mac の run を `gh run rerun <id>` で成功させてから `release.yml` を dispatch する。コード修正が要るときは `vava` の復旧手順に従い、patch を上げた新タグで配信し直す。
- ローカルの検証・再開は事前確認（Sigilune のクラウドログインを含む）を行い、失敗時は「リリースの事前確認に失敗しました。Sigilune の登録（sigilune status）、ネットワーク、Cloudflare 認証の状態を確認してください」と案内し、ログインを自動再試行しない。

```powershell
pwsh -NoProfile -File scripts/release-local.ps1 -PreflightOnly
if ($LASTEXITCODE -eq 0) { pwsh -NoProfile -File scripts/release-local.ps1 -SkipUpload } else { throw 'リリースの事前確認に失敗しました。Sigilune の登録（sigilune status）、ネットワーク、Cloudflare 認証の状態を確認してください' }
```
