# タブと状態モデル

`MainWindowViewModel.Tabs` は `WorkspaceTabViewModel` の集合で、次の4種類を扱います。設定以外はピン留めでき、ピン留め群、通常群、設定の順を保ちます。

- `DocumentViewModel`: 編集可能なテキスト文書。未保存状態、パス、文字コード、改行、カーソル、Markdownプレビュー状態を保持する。
- `PdfDocumentViewModel`: 読み取り専用PDF。ページと表示画像を保持し、編集・保存コマンドの対象外になる。
- `GrepResultTabViewModel`: フォルダ横断検索の結果。検索条件と一致行を保持し、選んだ行から該当ファイルを開く。
- `SettingsTabViewModel`: 単一の設定画面。重複して開かず、常に文書・PDFタブの後ろへ置く。

選択タブの型から、エディタ、Markdownプレビュー、PDFビュー、検索結果、設定ビューの表示とコマンド可否を導出します。表示可能な文書・PDF・検索結果が0件になった場合は空の文書を作り、設定タブだけの状態を残しません。

## 起動

1. `Program` が `AppLogger` を初期化し、未処理例外を記録する。
2. Velopackがインストール・更新後の関連付け更新と、アンインストール前の関連付け解除を処理する。
3. `App` が `SettingsService` から設定を1回読み、初回描画前に `ThemeService` へ適用する。
4. `MainWindow` と `MainWindowViewModel` が作られ、セッション復元後に起動引数のファイルを開く。後続プロセスからの転送も同じ順序を保つ（[セッションの復元](session-restore.md)）。

検証は `global.json` で Microsoft.Testing.Platform を選択し、xUnitの実行可能プロジェクトを `dotnet test` から実行します。VSTest向けのTest SDK・アダプターには依存しません。
