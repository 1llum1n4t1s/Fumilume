# rere 9件の修正・検証記録

2026-10-01 / Windows x64 / SDK 10.0.401、.NET 10.0.12。既存のフォルダ機能・検索ボックス修正と未コミット差分を維持し、バージョン・依存関係を変更せず、未ステージで保存した。

| ID | 判定と修正 | 検証 |
| --- | --- | --- |
| S01 | 採用。保存成功でも未保存変更が残れば閉鎖を止める。破棄・複数文書の終了確認にも版/文字コード/改行コードの状態照合を適用 | MainWindowで遅延した実ファイル書込み・確認中の追加入力と設定変更。保存スナップショットと残った編集、再保存後の閉鎖を確認 |
| S02 | 採用。設定JSONのnullカーソル辞書を空辞書へ補正 | 実settings.jsonからウィンドウを起動。通常設定維持、200件上限、破損JSONのバックアップ・既定値復帰を確認 |
| S03 | 採用。読込/確認前の文書状態を照合し、手動・外部再読込の古い結果を適用しない | 待機中の追加入力・文字コード/改行変更、Undo保持、通常再読込を確認 |
| P01 | 差し替え採用。末尾探索のキャッシュと、段落内Runによる長文組版の改善 | 修正前の失敗を保存。実Skiaで60万文字の表示更新を36.548秒から1.660秒へ改善、全文と選択を維持 |
| P02 | 採用。同じ位置の同一ノードを先に判定 | 512実ファイルのMainWindowツリー更新。変更なし約10ms・通知0、挿入/削除後も選択と既存ノード保持。計算量は無変更時O(n) |
| P03 | 採用。各行と検索完了時の取消確認、取消/破棄後の結果掲載を防止 | 最終実ファイルの読込完了と取消の競合、遅延結果が取消/閉鎖後に掲載されない3ケース |
| P04 | 採用。Macのページサイズを不変キャッシュにし、ネイティブ解放を描画終了後のバックグラウンドへ移す | macOS locked restoreとMacアプリ/E2EのReleaseコンパイル成功。実Core Graphicsの並行描画・リサイズ・閉鎖E2Eを追加。Apple Silicon実行は未実施 |
| U01 | 採用。共通の混在改行認識へ統一 | 実エディタで空白削除・昇順/降順ソート・連続重複削除・Undoを確認 |
| U02 | 採用。設定5スライダーに項目名を付与 | MainWindow設定タブを切替し、各AutomationPeerの名前が空でなく5項目を識別できることを確認。実スクリーンリーダー操作は未実施 |

## 最終検証

- `dotnet restore Fumilume.slnx --locked-mode` 成功。
- `dotnet test Fumilume.slnx -c Release --no-restore` は607件成功、失敗0、スキップ1（検索の実Skia専用ケース）。追加E2E28件（状態22・処理5・フォルダ/設定1）を含む。`rere-fixes/full-test.txt` に保存。
- 実Skia専用プロセスでMarkdown E2E1件と、検索閉鎖E2E1件/10シナリオを直列実行して成功。検索のスキップ分はここで検証。
- macOSアプリの必須locked restore、MacE2Eのlocked restore・Release build成功。警告0・エラー0。Windows上でNative AOTやMac実機検証を完了扱いにしない。
- 独立レビューで見つかった状態設定変更の保護漏れ、描画分割の選択/改行/接続形の回帰を是正し、最終コードで追加指摘なし。

## 再実行

GUI検証は同時起動せず直列に行う。テスト用の設定・ファイルはOS一時領域へ隔離する。

```powershell
dotnet restore Fumilume.slnx --locked-mode
$env:FUMILUME_REVIEW_ARTIFACTS = Join-Path $PWD 'docs/validation/rere-fixes'
$env:FUMILUME_E2E_ARTIFACTS = Join-Path $PWD 'docs/validation/review-state-artifacts'
dotnet test Fumilume.slnx -c Release --no-restore

# 実Skiaの専用ケースを別プロセスで実行
dotnet build tests/Fumilume.Tests/Fumilume.Tests.csproj -c Release --no-restore
$env:FUMILUME_E2E_RENDER = '1'
$env:FUMILUME_REVIEW_ARTIFACTS = Join-Path $PWD 'docs/validation/rere-fixes/skia'
$runner = Join-Path $PWD 'tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.exe'
& $runner -method Fumilume.Tests.ReviewProcessingE2ETests.MarkdownPreviewHandlesMalformedLinksAndKeepsValidLabels
& $runner -method Fumilume.Tests.MainWindowIntegrationTests.SearchPanelCloseButtonClosesSearchAndReplacement
Remove-Item Env:FUMILUME_E2E_RENDER
```

MacはApple Silicon上で既存の `scripts/build-macos-arm64.sh` のE2E経路を実行する。追加の3ケースは50,000図形の再生成可能なPDF、サイズ取得/破棄/リサイズ/閉鎖の所要時間・未完了描画数・重なりの観測有無を既存results.jsonへ残す。描画終了後のネイティブ解放時刻自体は直接測定していない。

## 横展開と成果物

保存/終了・再読込の確認待機、設定の参照型プロパティ、Markdownのリンク/画像解析と折返し表示、行変換の全呼出、Grepの結果タブ、両OSのPDFレンダラー、フォルダの照合ループ、全Sliderを検索した。追加必要: 保存/終了の状態確認2箇所と外部再読込1箇所、Grep結果タブ1箇所、Markdown折返しの共通表示経路1箇所。状態照合4箇所は同じhelperへ統一。不要: 保存単独/SaveAll等5箇所と既存MarkSavedの形式照合、Windows PDFレンダラー1箇所。設定の追加null集合・他のフォルダ照合・追加Sliderは0件。安全な必要箇所に未判定なし。

詳細・入力・途中失敗ログは `review-state.md`、`review-state-artifacts/`、`review-processing.md`、`review-processing-*.json`、`rere-fixes/` を参照する。スクリーンショット・JSON・TRX・全文ログは検証成果物として保持。ソース退避コピーは作らず、一時的なパーサーの比較はメモリ退避とfinallyによる復元で完結。テスト用一時領域は各ケース終了時に解放し、今回の新規E2Eソース作成（UTC 2026-09-30 17:43:51）以降に作られた領域の残存0件を確認した。それ以前の他作業の一時領域は変更していない。
