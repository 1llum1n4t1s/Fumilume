# 更新と配布

## R2配布

Windows・macOSの配布スクリプトは同梱の `scripts/R2Distribution.Common.ps1` を使い、PowerShell 7.2 以降と .NET 標準 API で R2 に配布します。Node.js・pnpm・Wrangler は不要です。バージョン付き成果物は不変として条件付きで作成し、固定名は ETag を照合して更新します。ペイロードを先に、assets・RELEASES・releases メタデータを最後に送信し、R2 から再取得した SHA-256 で照合します。固定URLはCloudflareのキャッシュ削除を要求します。

署名・転送・path 検証の正本は `R2Transfer.Core.cs` で、PS helper はそれを読み込む入口です。詳細は [R2転送の中核](../agents/r2-transfer-core.md)。

## アプリ内更新

`UpdateService` は `VelopackUpdateDialog.Avalonia` を介して更新確認・ダウンロード・適用・再起動を行います。updater の起動前に未保存文書の確認とセッション保存を完了し、取消・保存失敗時は適用も終了も行いません。ダウンロード済み更新の起動時自動適用は無効で、次回も更新UIから同じ保存境界を通します。

macOS の通常終了では、Cocoa が管理コードの `finally` より先に終了を確定することがあります。セッション保存後の desktop lifetime の `Exit` と `Program.Main` の `finally` は共通の冪等な終了処理を呼び、終了ログの書き出しを完了します。

## Windows配布

`scripts/release-local.ps1` が x64 / ARM64 を順番にNative AOT発行し、コード署名、Velopackパッケージ化、R2アップロード、固定URLのキャッシュパージを行います。署名は Sigilune（Certum SimplySign のクラウド鍵を signtool `/dlib` で使う CLI）で行い、SimplySign Desktop は使いません。通常の公開は `.github/workflows/release.yml`（main の commit に付けた軽量タグ `v<版>` の push で起動、`guard` ジョブがタグ・commit・版を検査、windows-2025、Environment `release`、承認待ちなし）が `scripts/release-all.ps1` を実行し、Secrets は Environment の `SIGILUNE_EMAIL` / `SIGILUNE_TOTP_URI` / `CLOUDFLARE_API_TOKEN` だけを環境変数で渡します。ローカル実行は検証と再開に使います。署名用アカウント（ローカルは資格情報マネージャー、CIはEnvironmentのSecrets）とCloudflare認証情報はリポジトリ外に置きます。

## 配布物検証

アップロード前に `scripts/verify-windows-package.ps1` が配布実物（フィード・nupkg・Portable ZIP・Setup）の版・ハッシュ・署名を静的検証し、失敗は公開を止めます。詳細は [Windows配布物の検証](../agents/windows-package-verification.md)。

## 作業先と清掃

配布スクリプトは `WorkRoot` へ出力し `RecordRoot` へ再開記録と清掃台帳を残し、清掃は検証済みの作業範囲に限ります。詳細は [配布の作業先と清掃](../agents/release-workspace.md)。

## macOS配布

`.github/workflows/macos-release.yml` のarm64 Macで `scripts/build-macos-arm64.sh` を実行し、locked restore、Native AOT発行、Developer ID署名、Apple公証、PKG・ZIP・更新パッケージ作成を行います。実AvaloniaとCore Graphicsを使う `tests/Fumilume.MacE2E`、署名・公証とNative AOT起動結果をCI成果物へ残します。主アプリは空entitlementsを使い、同梱ネイティブライブラリを同じTeam IDで個別署名します。公証チケット検証は `validate-notarization.sh` を通し、exit 68と接続切れ（-1005）またはタイムアウト（-1001）が揃う場合だけ最大5回（待機2・4・8・16秒）再試行し、署名・公証提出は繰り返しません。

`scripts/release-all.ps1` は軽量タグ `v<版>` が指すcommitとHEADの一致を確認し、そのタグを `--ref` にしたMac CI成功結果を取得し、Windows版リリース後にMac成果物を `scripts/publish-macos.ps1` で同じR2 bucketへアップロードしてキャッシュ削除を要求します。Mac専用チャンネルは `osx-arm64`、更新フィードは `releases.osx-arm64.json` です。Windows の `packages.lock.json` と macOS の lockfile は分離し、片方の restore が他方の依存グラフを消さないようにします。

## 通常のCI

`.github/workflows/dependency-validation.yml` は main への push・全 PR・Dependabot・手動で、GitHub提供ランナー上のWindows単体テストとmacOS向けE2Eプロジェクトのビルドを行います。NuGetパッケージは lockfile・csproj・`global.json` 等のハッシュをキーにキャッシュし、保存は main のときだけ、タグと `release/` ブランチでは復元しません。同時実行の取消は PR 単位だけで、main への push の実行は後続の push で取り消しません。
