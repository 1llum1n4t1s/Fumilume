# フォルダツリーの行高調整の検証

対象: フォルダツリーの各項目の縦方向の余白。TreeViewItemの最小高を24px、内容の最小高を28pxから22pxへ変更した。文字サイズと階層のインデントは維持する。

確認対象の失敗: テーマの最小高によって行が縮まらない、子階層だけ行高が異なる、文字やアイコンが切れる、ポインターの選択先がずれる、展開や矢印キーの選択が動かない。

既存の `PointerFocusAllowsArrowSelectionAndFolderExpansion` E2Eを拡張し、実MainWindowで実フォルダと子階層を展開した。ポインターによる選択、上下左右キーによる移動・展開、子ファイルのオープンを実行し、5行すべてが24pxであることと文字・アイコンの収まりを確認した。

2026-10-04の検証結果: locked restore成功。全テスト612件成功、失敗0件、既存の実描画専用テスト1件スキップ。対象E2EはSkia実描画でも1件成功。生成画像を目視確認し、`git diff --check` も成功。

再現コマンド:

```powershell
dotnet restore Fumilume.slnx --locked-mode
dotnet test Fumilume.slnx -c Release --no-restore
$env:FUMILUME_E2E_RENDER = '1'
$env:FUMILUME_E2E_ARTIFACTS = 'C:\Users\IMT\dev\Fumilume\docs\validation\folder-tree-density'
dotnet tests/Fumilume.Tests/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.dll -method Fumilume.Tests.FolderExplorerIntegrationTests.PointerFocusAllowsArrowSelectionAndFolderExpansion -result-ctrf C:/Users/IMT/dev/Fumilume/docs/validation/folder-tree-density/skia-results.json
```

成果物: `folder-tree-density/folder-tree-density.png`、行高と開いた文書を記録した `folder-tree-density/folder-tree-density.json`、実描画テスト結果 `folder-tree-density/skia-results.json`。
