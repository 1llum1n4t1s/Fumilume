# Fumilume 作業ガイド

このファイルは、Fumilume を変更するエージェント向けのリポジトリ固有規約です。現在の構造と設計判断は [DESIGN.md](DESIGN.md)、利用者向けの説明は [README.md](README.md) を参照してください。

## 対象と構成

- `src/Fumilume/`: Windows 向け Avalonia デスクトップアプリ
- `tests/Fumilume.Tests/`: xUnit v3 と Avalonia.Headless による単体・統合テスト
- `scripts/release-local.ps1`: x64 / ARM64 の Native AOT、署名、Velopack パッケージ化、R2 配布、公開検証
- `web/`: `fumilume.kagayoi.com` のランディングページと Cloudflare Worker
- `Directory.Build.props`: 対象フレームワーク、版番号、対応プラットフォーム、警告・lock file 方針の正本

## 実装規約

- C# は nullable と暗黙 using を有効にし、ビルド警告をエラーとして解消する。
- UI は Avalonia の compiled bindings を維持する。ViewModel の公開プロパティ名を変更するときは、AXAML の binding と Headless 統合テストも同時に更新する。
- UI 状態と操作は `ViewModels/`、ファイル・設定・更新・関連付けなどの外部処理は `Services/` に置く。OS API やダイアログはサービス境界で隔離し、テストでは差し替え可能にする。
- テキスト保存では、利用者がステータスバーから変更しない限り、読込時の文字コードと改行コードを維持する。対応形式や変換操作を増やす場合は `TextDocumentContent`、`DocumentFileService`、README、テストを同じ変更で揃える。
- 設定は `%LocalAppData%\Fumilume\settings.json` を正本とし、既存プロパティ名との互換性、範囲補正、破損時の既定値フォールバック、原子的な保存を維持する。
- 起動・セッション経路を変更するときは、同じユーザーの通常起動を単一プロセスへ集約し、後続起動のファイル引数を初回セッション復元後に開く順序を維持する。相対パスは送信側プロセスで絶対化する。
- ワークスペースには文書・PDF・検索結果・設定を `WorkspaceTabViewModel` 派生型として載せる。設定タブは1個だけで末尾に置き、文書・PDF・検索結果が0件になると空文書を補う。
- ファイルを開く経路は直列化し、大文字・小文字を区別しない絶対パスごとに文書・PDFタブを1個だけ保つ。既に開いている場合は同じファイルを読み直さず、そのタブを選択する。
- フォルダ横断検索は `IDocumentFileService` 経由で読み、文字コード判定を文書と一致させる。読めないファイルと上限超過は数えて飛ばし、検索全体を止めない。検索結果タブは中央に出し、左サイドパネルへ移さない。
- 機能を足すときの入口は `EditorCommandCatalog` とコマンドパレット（`MainWindowViewModel.EnumeratePaletteEntries`）にする。常時出すツールバーのボタンを増やさない。
- 左サイドパネルは `SidePanelKind` を 1 つだけ表示する。面を足すときは列挙体・切り替えボタン・XAML の `IsVisible` を同じ変更で揃え、開いていない面を引き直さない構造（`_outlineStale` / `_bookmarksStale`）を維持する。
- アウトラインは `OutlineService` が対応を宣言した拡張子だけを解析し、判定できない形式では空を返す。宣言でない行を拾わないことを、拾えることより優先する。
- キーボードマクロは操作（`MacroStep`）を記録し、キー入力そのものを溜めない。再生は `DocumentViewModel` へ当て直す経路だけにし、View の状態（`SearchPanel` など）へ依存させない。手を増やすときは `MacroStepKind`、View 側の翻訳、`ApplyAsync` の分岐、`MacroTests` を同じ変更で揃える。
- マクロへ記録できないコマンド（入力ダイアログを開くもの）を足したときは `NotRecordable` へ加える。再生側で読み飛ばす作りにしない。
- 設定画面へまとまりを足すときは `HeaderedContentControl Classes="settingscard"` で囲う。検索の索引はこの目印で作るので、付け忘れると検索から漏れる。
- 構文ハイライトの定義は AvaloniaEdit 同梱のものを使い、色を書き換えるときは元の色を控えてテーマ往復で積み上がらないようにする。定義の一括読み込みはしない（単体で読めない部品が含まれる）。
- PDF は `Windows.Data.Pdf` による読み取り専用表示として扱う。テキスト文書向けの保存・編集コマンドをPDF選択中に有効化しない。
- Markdown プレビューは `.md` 文書だけに提供し、編集元テキストを正本とする。プレビューは Avalonia コントロールで構築し、外部ブラウザ実行環境へ依存させない。
- エディタ用フォント候補は等幅フォントに限定する。UIフォントとエディタフォントの設定・プレビューを分離し、VS Code形式のフォントフォールバックを維持する。
- ログは `SuperLightLogger` を `AppLogger` 経由で使用し、更新UIは `VelopackUpdateDialog.Avalonia` を `UpdateService` 経由で使用する。各ライブラリをViewから直接呼び出さない。
- ファイル関連付けの追加・変更では、インストール後、更新後、アンインストール前のVelopackコールバックと関連付けテストを確認する。
- README はアプリ利用者向けに保ち、開発・配布の詳細はこのファイルと `DESIGN.md` に記載する。

## 必須検証

通常のコード変更では、リポジトリルートから次を順番に実行します。

```powershell
dotnet restore Fumilume.slnx --locked-mode
dotnet test Fumilume.slnx -c Release --no-restore
```

- UI、binding、テーマ、タブ状態を変更した場合は `MainWindowIntegrationTests` を含む全テストを実行する。
- ファイルI/O、設定永続化、文字変換、Markdown、PDFを変更した場合は対応するサービステストに正常系と境界条件を追加する。
- NuGet依存関係を変更した場合は、アプリとテストの `packages.lock.json` を同時に更新し、locked restore を通す。
- Native AOT、RID依存API、配布物へ影響する変更では、リリース前に両アーキテクチャを検証する。直接発行する場合は `win-x64` に `PlatformTarget=x64`、`win-arm64` に `PlatformTarget=ARM64` を対応させる。

## リリース

- リリースは既存の `$vava` ワークフローと `vava.config.json` に従い、版番号を一度だけ決定する。
- 外部状態を変えない事前確認は次で行う。

```powershell
pwsh -NoProfile -File scripts/release-local.ps1 -PreflightOnly
```

- 署名付きローカル成果物までの確認には `-SkipUpload` を使う。完全なリリースは引数なしで1回だけ実行し、x64 / ARM64 の発行・署名・R2アップロード・キャッシュパージ・公開ハッシュ検証を同じ処理で完走させる。
- リリーススクリプトが読む証明書・Cloudflare認証情報はリポジトリ外の正本を使う。秘密値をコード、ログ、fixture、Git差分へ出力しない。
- GitHub Actions のリリースCIは存在しないため、ローカル検証と `scripts/release-local.ps1` の成功結果を出荷判定にする。

## 変更時の確認先

- アーキテクチャ、不変条件、主要データフロー: [DESIGN.md](DESIGN.md)
- 利用者向け機能、インストール、設定、トラブルシュート: [README.md](README.md)
- 利用者向け変更履歴: [CHANGELOG.md](CHANGELOG.md)
