# ワークスペース・検索・サイドパネル・マクロ

- ワークスペースには文書・PDF・検索結果・設定を `WorkspaceTabViewModel` 派生型として載せる。設定タブは1個だけで末尾に置き、文書・PDF・検索結果が0件になると空文書を補う。
- ファイルを開く経路は直列化し、大文字・小文字を区別しない絶対パスごとに文書・PDFタブを1個だけ保つ。既に開いている場合は同じファイルを読み直さず、そのタブを選択する。
- フォルダ横断検索は `IDocumentFileService` 経由で読み、文字コード判定を文書と一致させる。読めないファイルと上限超過は数えて飛ばし、検索全体を止めない。検索結果タブは中央に出し、左サイドパネルへ移さない。
- 左サイドパネルは `SidePanelKind` を 1 つだけ表示する。面を足すときは列挙体・切り替えボタン・XAML の `IsVisible` を同じ変更で揃え、開いていない面を引き直さない構造（`_outlineStale` / `_bookmarksStale`）を維持する。
- アウトラインは `OutlineService` が対応を宣言した拡張子だけを解析し、判定できない形式では空を返す。宣言でない行を拾わないことを、拾えることより優先する。
- キーボードマクロは操作（`MacroStep`）を記録し、キー入力そのものを溜めない。再生は `DocumentViewModel` へ当て直す経路だけにし、View の状態（`SearchPanel` など）へ依存させない。手を増やすときは `MacroStepKind`、View 側の翻訳、`ApplyAsync` の分岐、`MacroTests` を同じ変更で揃える。
- マクロへ記録できないコマンド（入力ダイアログを開くもの）を足したときは `NotRecordable` へ加える。再生側で読み飛ばす作りにしない。
- OSのファイル操作（改名・移動・コピー・削除・作成・クリップボード・表示・ターミナル・プロパティ）は `IShellFileService` を経由し、ViewModel から OS API を直接呼ばない。
- 改名・移動・削除でタブと整合させるときは、`_openPathsGate` を保持した内側で `RunShellOperationAsync` を通す。ゲートの内側で確認付きの閉じ方（`CloseTabCoreAsync`）やダイアログを呼ばない。`SaveDocumentAsync` がゲートを取り直してデッドロックするため、閉じるなら確認なしの `RemoveTabCore` を使い、入力・確認ダイアログはゲートの外で出す。
- タブ・ツリーの右クリックメニューへ項目を足すときは、`MainWindowViewModel.ContextMenus.cs` と `ContextMenuTests` を同じ変更で揃える。有効判定は ViewModel の `Can…` に置く。
- ファイルのドロップ経路を変更するときは、DESIGN.md の「ファイルのドロップ」（入力境界）を維持し、`MainWindowIntegrationTests` で未知の拡張子・拡張子なし・重複ドロップ・フォルダ除外・文字ドラッグ設定の両状態を確認する。
