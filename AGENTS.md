# Fumilume 作業ガイド

Fumilume を変更するエージェント向けのリポジトリ固有規約。構造と設計判断は [DESIGN.md](DESIGN.md)、利用者向け説明は [README.md](README.md)、変更履歴は [CHANGELOG.md](CHANGELOG.md)。

## 構成

- `src/Fumilume/`: WindowsとApple Silicon macOS向けAvaloniaデスクトップアプリ
- `tests/Fumilume.Tests/`: xUnit v3 と Avalonia.Headless のテスト
- `tests/Fumilume.MacE2E/`: Apple Silicon Macの実アプリE2E
- `scripts/`: リリース・配布スクリプト（`release-local.ps1`、`build-macos-arm64.sh` など）
- `Directory.Build.props`: 対象フレームワーク、版番号、対応プラットフォーム、警告・lock file 方針の正本
- `../vps-web/lp/fumilume/`: 製品ページ。配信先と手順は [DESIGN.md の配信先](DESIGN.md#製品ページの配信先)、配信は `../vps-web/deploy/deploy-lp.ps1`

## 共通ルール

- C# は nullable と暗黙 using を有効にし、ビルド警告をエラーとして解消する。
- 通常のコード変更は `dotnet restore Fumilume.slnx --locked-mode` と `dotnet test --solution Fumilume.slnx -c Release --no-restore` で検証する。
- 秘密値をコード、ログ、fixture、Git差分へ出力しない。

## 詳細（変更前に読む）

- 画面・設定・サービス・起動・ログ・コマンド入口・設定画面を変更する前に [implementation-rules.md](docs/agents/implementation-rules.md) を読む。
- タブ・ファイルを開く経路・検索・サイドパネル・アウトライン・マクロ・ドロップを変更する前に [workspace-rules.md](docs/agents/workspace-rules.md) を読む。
- 保存形式・構文ハイライト・PDF・Markdown/CSV プレビュー・フォントを変更する前に [document-formats.md](docs/agents/document-formats.md) を読む。
- 検証の範囲を決める前（UI、R2、macOS、NuGet lockfile、Native AOT の変更時）に [validation-commands.md](docs/agents/validation-commands.md) を読む。
- GitHub Actions、Dependabot、NuGet キャッシュ、自動マージを変更する前に [ci-workflows.md](docs/agents/ci-workflows.md) を読む。
- リリース、署名、R2 配布、再実行・復旧を扱う前に [release-process.md](docs/agents/release-process.md) を読む。通常の公開は `$vava` に従い、軽量タグ `v<版>` の push で CI が配信する。
- R2 の署名・転送・manifest・path 検証を変更する前に [r2-transfer-core.md](docs/agents/r2-transfer-core.md) を読む。
- 配布スクリプトの作業先・再開記録・清掃を変更する前に [release-workspace.md](docs/agents/release-workspace.md) を読む。
- Windows 配布物の pack・署名後の検証を変更する前に [windows-package-verification.md](docs/agents/windows-package-verification.md) を読む。
