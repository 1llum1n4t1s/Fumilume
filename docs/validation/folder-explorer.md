# フォルダエクスプローラーの実経路検証

## 実装前に列挙した失敗候補

- フォルダを開いてもツリーが表示されない、compiled binding が解決しない。
- 子フォルダを開く前に全階層を列挙し、大きなフォルダで操作が止まる。
- 同じファイルの再クリックや並行クリックでタブが重複する。
- 1クリックの一時タブが置換されない、固定したタブまで置換する。
- 編集中の一時タブを別ファイル選択で破棄し、未保存テキストを失う。
- 2クリック、ピン留め、保存で一時タブが固定されない。
- 外部で作成・名前変更・削除したファイルがツリーに反映されない。
- 更新や折りたたみで未保存文書を閉じる、開いているフォルダの状態が壊れる。
- 存在しないフォルダを開くと例外で操作経路が止まる。
- 終了後の再起動でフォルダと未保存の本文が復元されない。
- フォルダを閉じた後も監視やUI表示が残る。

## 再現方法

Windowsのリポジトリルートから次を実行する。テストは実ファイルを隔離した一時フォルダへ生成し、実際のDocumentFileService、FolderTreeService、MainWindowをAvalonia.Headlessで通す。

```powershell
dotnet test tests/Fumilume.Tests/Fumilume.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FolderExplorerIntegrationTests
```

テスト用フォルダと設定はTemporaryStorageで隔離し、終了時に削除する。公開・外部通信は行わない。

## 2026-10-01 の実測（Windows / .NET SDK 10.0.401）

8本のHeadless統合テストを追加。実MainWindowからサービス、XAML binding、AvaloniaEdit、ディスク保存、session.json保存・再起動まで通す。

| 検証 | 確認する条件 |
| --- | --- |
| 起動直後の表示 | フォルダ未選択でも枠と「フォルダを開く」ボタンが見える、720px幅で本文が画面内に収まる、未選択時のCtrl+B、閉じた後の入口表示 |
| ツリーと外部変更 | 子フォルダの遅延列挙、実本文読込、create/rename/delete、選択維持、折りたたみ維持、閉じる |
| 一時タブと未保存保持 | 同一ファイルの並行クリック、別ファイルへの置換、エディタ上の編集、ピン、実保存 |
| 再起動 | フォルダと未保存本文の永続化・復元、missingフォルダからの回復 |
| 一時タブの固定 | permanent=true、未編集の一時タブ保存後も別クリックで残る |
| 実ポインター入力 | 720px幅でツリー・タブ・エディタの並び、単クリック、二クリック |
| 実キー入力 | ツリーフォーカス、Enter固定とエディタフォーカス、Ctrl+B非表示・再表示 |
| 矢印キー | 単クリックでフォーカスした項目からDown/Up選択、子のあるフォルダのLeft/Right折りたたみ・展開 |

対象の8件は全体テストで全件成功。監視後の選択維持、折りたたみ後の監視更新、Enter後のエディタフォーカスを含む。起動直後の未選択画面は修正前にIsEffectivelyVisible=falseで失敗し、既定表示と空状態の入口追加後、同じ経路で成功を確認した。途中で見つかったTreeViewフォーカス不足と監視更新による選択消失も製品側の修正後、同じ経路で成功を確認した。

リポジトリ必須検証の最終結果:

- `dotnet restore Fumilume.slnx --locked-mode`: 成功。
- `dotnet test Fumilume.slnx -c Release --no-restore`: 579成功・0失敗・0スキップ（23.182秒）。MainWindowIntegrationTestsを含む。
- `dotnet restore src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true -r osx-arm64 --locked-mode`: 成功。確認後はWindowsのlocked restoreへ戻した。
- `git diff --check`: 成功。

既存Headless基盤のCaptureRenderedFrameはnullを返すため、画像を視覚確認の証拠として保存しない。この文書と実ファイルを生成する統合テストを再現可能な成果物とする。macOS実機、巨大フォルダ、アクセス権拒否、OSネイティブフォルダ選択ダイアログは未検証。

空フォルダをクリック展開後のLeftキーでは、子がないため標準TreeViewが折りたたまずモデルのIsExpanded=trueが残ることを観測した。子のあるフォルダはLeft/Rightで正しく折りたたみ・展開する。空フォルダのモデル状態をそろえる仕様追加は今回行っていない。Ctrl+K→Ctrl+OのOS picker操作と、初回復元とフォルダオープンを同時に開始する競合条件は未検証。
