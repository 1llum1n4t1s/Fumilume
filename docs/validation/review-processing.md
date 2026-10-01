# レビュー修正 P01・U01・P03 の再現記録

2026-10-01、Windows x64、固定 SDK 10.0.401 / .NET 10.0.12 で検証。

## 実行方法

リポジトリルートで次を実行する。`global.json` の MTP 選択では従来の `dotnet test --filter` が0件実行になったため、個別検証はビルド済み xUnit v3 ランナーのクラス指定を使った。

```powershell
dotnet build tests/Fumilume.Tests/Fumilume.Tests.csproj -c Release --no-restore
$env:FUMILUME_REVIEW_ARTIFACTS = Join-Path $PWD 'docs/validation/rere-fixes'
dotnet tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.dll -class Fumilume.Tests.ReviewProcessingE2ETests -result-ctrf docs/validation/review-processing-after.json
```

ビルドは警告0・エラー0。最終個別検証は5件成功、0件失敗・スキップ、1.806秒。全体の locked restore と全テストは親担当がまとめて実行する。

## 結果と入力

| ID | 修正前の実測 | 修正内容と確認 |
| --- | --- | --- |
| P01 | MainWindowで通常リンク・画像・強調と、別段落の60万個の `[` をプレビューすると45.58秒。末尾探索を分離した同じ本文のParseは3091.53ms、未接続の実MarkdownPreviewへ60万個の `[` と15万個の `[x](` を与える構築は3747.93ms | 次の `]` と `)` の探索位置を単調に進め、見つからない末尾も記憶する。修正後の同じParseは10.02ms、未接続プレビュー構築は5.84ms。MainWindowの通常リンク・画像・強調の表示も維持 |
| U01 | エディタの全文選択から末尾空白削除を実行すると、CRLFの後にあるLF行の空白が残る | `DocumentNewLines` で全てのCRLF・LF・CRを認識。本文だけの整形は各区切りを保存する。実エディタから先頭/末尾空白削除、昇順/降順ソート、連続重複統合を実行し、期待本文とUndoでの元本文復元を確認 |
| P03 | 最終ファイルの読み込み完了直前に中止しても3件を表示。中止/閉じた検索タブも遅延結果を1件掲載 | 検索開始・各行・完了時にキャンセルを検査。結果タブはキャンセル済み・破棄済みの結果を掲載しない。実ファイルの読み込みと制御した完了競合、MainWindowの結果タブ選択/除去を通して3シナリオを確認 |

ソート・重複統合は従来どおり検出した改行で選択範囲を構築する。エディタの行選択範囲は最終行の改行文字を含まないため、その外側にある末尾CRはそのまま残る。これは `GetLineRange` の既存契約であり、検証期待値もその経路に合わせた。

検索の上限16MiB・5000件と正規表現1行2秒の制限は変更していない。正規表現1回の実行中の即時中断、行分割1回の実行中の即時中断は提供していない。

## 実描画と長い段落の折返し

パーサーだけの修正では、通常のHeadless描画で60万文字の1行をMainWindowへ載せた全体時間が40.26秒、実Skiaでもプレビュー更新と組版に36547.974msかかった。そこで折返し対象の長い段落・見出し・リスト・引用を、同じSelectableTextBlock内の複数のRunへ分けた。約4096文字を目安に、Unicodeの文字境界と空白・句読点の境界で区切る。境界がない単語・接続形の文字列は長くても同じRunに保持する。

最終実装の同じMainWindow・実Skia・60万文字では、更新と組版は1660.349msへ改善した。全文一致・段落全体選択・4096境界をまたぐ選択・絵文字/結合文字・アラビア語の接続文字列の保持を確認した。段落は1つのコントロールのままなので、分割位置に改行を足さず、選択・コピーの領域も分断しない。これは当該入力のWindows実測であり、全入力・全機種の応答時間を保証する値ではない。区切りのない極端に長い単語の組版性能は未保証。文字の省略はしない。

単一コントロール内の選択処理は、使用版の[Avalonia 12.1.3 SelectableTextBlock](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Controls/SelectableTextBlock.cs#L143)のInlines対応と照合した。独立レビューで指摘された、独立コントロール分割による選択/改行の回帰と、単語途中のRun分割による接続形の回帰を修正し、最終静的レビューでも解消を確認した。

実Skiaで同じMainWindowの入力を確認する専用分岐は次で個別実行する。解析・コントロール構築は2秒、Skiaの実画面更新は5秒の回帰検出閾値を設定し、`AttachedRenderMilliseconds` へ実測値を記録する。

```powershell
$env:FUMILUME_E2E_RENDER = '1'
$env:FUMILUME_REVIEW_ARTIFACTS = Join-Path $PWD 'docs/validation/rere-fixes'
dotnet tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.dll -method Fumilume.Tests.ReviewProcessingE2ETests.MarkdownPreviewHandlesMalformedLinksAndKeepsValidLabels
```

## 保持した成果物

- `review-processing-before.json`: 最初の修正前検証、5件すべて失敗。
- `review-processing-first-after.json`: 最初の修正後検証。巨大な1行の全体組版時間と、調査前の最終改行期待値による2件の失敗を保持。
- `review-processing-parser-before.json`: 同じ未接続プレビュー経路を修正前のパーサーで再実行し、2秒閾値の失敗と時間を保持。パーサーだけをメモリに保存して一時的にHEAD版へ替え、単一PowerShellのfinallyで修正版へ復元。その後のビルド・最終検証は復元済み修正版で成功。
- `review-processing-after.json`: 最終5件成功。
- `rere-fixes/review-processing-markdown-metrics.json`: 全体テストで取得した最新の解析・プレビュー構築時間。
- `rere-fixes/skia/review-processing-markdown-metrics.json`: 最終専用Skia実行で取得した解析・構築・実画面更新時間。
- `rere-fixes/skia/markdown-results.json`: 最終単一コントロール内Run方式の実描画E2E成功。先行方式の実験結果は同ディレクトリの親に別名で保持。

テストは設定・入力ファイルを既存の `TemporaryStorage` へ隔離し、ウィンドウと検索タブを閉じて清掃する。今回のソース切り替えで退避ファイルは作成していない。生成された検証記録は成果物として保持する。
