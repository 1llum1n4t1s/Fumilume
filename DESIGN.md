# Fumilume 設計

詳細は `docs/design/` のトピック別文書に置き、ここには概要と入口だけを残します。作業規約は [AGENTS.md](AGENTS.md)、利用者向けの機能は [README.md](README.md) を正本とします。

## 目的と範囲

複数のテキスト文書を垂直タブで扱うデスクトップエディタです。文字コード・改行コードを維持した編集、Markdown・CSVプレビュー、PDF閲覧、設定・更新・ファイル関連付けを提供します。Windows（`net11.0-windows10.0.26100.0`、Windows 10 1809以降）とApple Silicon macOS（`net11.0` / `osx-arm64`、macOS 15以降）向けに、自己完結型 Native AOT で配布します。

## 主要部品と境界

- 起動: `Program.cs`、`App.axaml.cs`（ログ、Velopack、設定、テーマ）
- View: `Views/`、`Styles/`（Avalonia UI、入力接続）
- ViewModel: `MainWindowViewModel`、`DocumentViewModel`、`PdfDocumentViewModel`、`AppOptionsViewModel`
- サービス: `Services/`（文書I/O、設定、更新、ダイアログ、検索、解析）
- 配布: `scripts/`、`../vps-web/lp/fumilume/`

ViewModel は状態と操作、View は表示とAvalonia固有の接続を担当し、ファイルシステム・OS API・更新UIなどの副作用はサービスへ分離して差し替え可能にします。主要データフローは、起動 → セッション復元 → ファイルを開く → 編集 → 保存の順です。

## 詳細

- [タブと状態モデル・起動](docs/design/workspace-tabs.md)、[不変条件](docs/design/invariants.md)、[採用済みの設計判断](docs/design/adopted-decisions.md)
- [テキストの読込・編集・保存](docs/design/text-file-io.md)、[Markdown](docs/design/markdown-preview.md)、[CSV](docs/design/csv-preview.md)、[PDF](docs/design/pdf-viewer.md)
- [設定と保存先](docs/design/settings-persistence.md)、[設定画面の検索](docs/design/settings-search.md)、[機能の入口](docs/design/command-entry.md)
- [左サイドパネル](docs/design/side-panel.md)、[アウトライン解析](docs/design/outline-analysis.md)、[フォルダツリー](docs/design/folder-tree.md)、[フォルダ横断検索](docs/design/folder-search.md)
- [セッションの復元](docs/design/session-restore.md)、[キーボードマクロ](docs/design/keyboard-macro.md)

### ファイルのドロップ

ローカルのファイルだけを受け付け、フォルダは除外し、既存の直列オープン経路へ渡します。詳細は [file-drop.md](docs/design/file-drop.md)。

### 構文ハイライト

One Dark / One Light の意味別配色で、入れ子のルールセットまで辿ります。詳細は [syntax-highlighting.md](docs/design/syntax-highlighting.md)。

### 更新と配布

Velopack更新、署名付きNative AOT、R2配布、配布物検証、通常のCIを扱います。詳細は [update-and-distribution.md](docs/design/update-and-distribution.md)。

## 製品ページの配信先

HTMLは `../vps-web/lp/fumilume/`、公開は `fumilume.kagayoi.com` です。詳細は [product-page-hosting.md](docs/design/product-page-hosting.md) と [AGENTS.md](AGENTS.md#製品ページの配信先) を参照してください。
