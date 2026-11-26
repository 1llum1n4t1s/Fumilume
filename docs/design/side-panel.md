# 左サイドパネル

左側は `SidePanelKind`（タブ一覧・アウトライン・ブックマーク）を切り替える 1 枚の面です。幅と中央領域の制約は [フォルダツリー](folder-tree.md)を参照してください。新規・開く・設定は文書ツールバーへ置きます。`SidePanelViewModel` が面の選択・一覧・選択項目・本文とブックマークの購読を所有します。`MainWindowViewModel` は選択文書を接続し、面の設定保存と行移動のステータス通知だけを受け持ちます。表示は `SidePanel` 配下へ compiled binding し、開いている面だけを引き直し、閉じている面は次に開くまで解析しません。中身が前回と同じときは差し替えないので、打鍵のたびに選択と表示位置が飛びません。ブックマークは選択中の文書の `DocumentBookmarks.Changed` を購読し、タブ切替のたびに購読先を張り替えます。

縦タブの見た目は姉妹アプリ Kiriha と同じ作りで、`ListBox.verticaltabs` の Style が `ListBoxItem` のテンプレートを `PART_ContentPresenter` だけへ差し替えます。待機・ホバー・選択の 3 段階と固定の目印はアクセント色ではなくテーマ内の明暗差で示し、`App.axaml` の `TabIdleBg`・`TabHoverBg`・`TabSelectedBg`・`TabSelectedBar`（選択中の左 2px）・`TabPinnedOverlay`・`TabPinnedBar`（固定の半透明ベールと左 3px、末尾のピン）・`TabCloseHoverBg` を正本にします。行の高さは設定「タブの厚み」が書き換える `TabItemHeight` に従います。ピン留めの入口は行の右クリックメニュー（設定タブは項目なし）で、固定タブは末尾が閉じるボタンの代わりにピンの目印になるため、閉じる手段はメニュー・`Ctrl+W`・コマンドパレットに残します。左パネル切り替えボタンの選択色は `PanelSwitchActiveBg` を使います。

フォルダ横断検索の結果はここへ入れず、中央のタブに残します。1 行 1 一致で「場所」と「本文」を横に並べる一覧なので、中央の表示幅を使います。
