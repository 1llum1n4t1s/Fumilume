# opop改善の検証（2026-10-01）

対象はFumilumeのソース、起動、設定、ファイル境界、配布スクリプト、検証資産。生成物・依存物は除外した。直前のrere修正とフォルダ・検索UIの未コミット変更を保持し、採用判断と編集は親が担当した。

## 観点の完了台帳

| 観点 | 担当 | 判定・根拠 |
| --- | --- | --- |
| Modernizer | opop_clean + 親 | 現状が最適。標準IMEクライアントはpreedit非対応で、既存ラッパーを維持 |
| Normalizer | opop_clean | 改善を採用。Windows/Mac配布でWranglerの固定版を共有 |
| Performance | opop_perf | 改善を採用。連続ドル記号・raw引用符のsuffix再走査を除去 |
| Cleaner | opop_clean | 改善を採用。書式整形の改行変換を既存の共通関数へ統一 |
| Hardening | opop_async | 改善を採用。古いPDF描画の失敗が現在の表示へ混入する経路を防止 |
| Tester | 親 + opop_clean | 改善を採用。実ウィンドウE2Eと再現可能な解析比較を追加、重複検証1件を削除 |
| Async | opop_async | 改善を採用。取消済みPDF描画のエラーはUIへ適用せず、ログは維持 |
| Startup | opop_perf | 改善を採用。設定画面を初めて設定タブを開く時点に生成し、同じ窓では保持 |
| Intel | 親 | 現状が最適。対象ライブラリの固定ソースと公開issueでIME仕様を照合 |

調査は読み取り専用3名へ委任。要求はanalyst / gpt-6.1-sol / medium、ツールに定義されたプロファイルも同じ。サーバ内部の実行時プロファイルは取得していない。初回調査の上限15分、Tester補完5分、採用分の最終照合3分。外部書込み・Git書込み・シークレット参照は行わない境界を共有した。

## 採用した変更と比較

| 対象 | 変更前 → 変更後 | 検証 |
| --- | --- | --- |
| `BracketPairService.cs:108,232,409` | 各文字から残りのrunを数える → ドル列の先頭と必要な`$@`境界だけを判定し、短いraw引用符runはまとめて進む | 固定seedと境界例10,102件でtokens完全一致。連続記号の当該経路O(N²)→O(N) |
| `PdfDocumentViewModel.cs:198` | 取消済み処理でもErrorMessageを書き戻す → 現行処理だけが書き戻す | 実MainWindowのPDFコマンド・画像binding・エラー表示。失敗/成功の両完了順、現行失敗、Dispose後の失敗 |
| `MainWindow.axaml:312` / `MainWindow.axaml.cs:566` | 非表示のSettingsViewもXAML読込時に構築 → 設定タブ初回オープンに1回だけ構築 | 設定未使用の初回画面にSettingsViewが0個。入力後の初回設定表示、検索、カテゴリ、閉鎖・再開で同じビューを保持 |
| `DocumentFormattingService.cs:485` | SplitLines配列とstring.Joinの専用処理 → DocumentFileService.NormalizeNewLines | 実エディタの整形コマンド。JSON/XML、CRLF/LF/CR、末尾改行あり/なしの12例とUndo |
| 配布スクリプト2本 | Wrangler版を2箇所で固定 → `scripts/release-tools.psd1`の1箇所を参照 | PowerShell構文解析成功、従来の4.135.0と実行パスを維持。アップロード・配布は未実行 |
| `BracketPairServiceTests.cs` | C#字下げ戦略の型だけを検証するFactを重複実行 → 削除 | `MainWindowIntegrationTests.CSharpEnterUsesStructuralIndentation`が実入力の本文と同じ型を検証。残る解析・JSON・既定戦略テストは保持 |

単体テストは新設していない。新設した4つのFactは実MainWindowを通るE2Eで、OS描画の完了順だけIPdfRenderer境界で制御する。既存のテストは、E2Eが直接確認しない返り値・選択開始位置・CSV本文範囲・並行要求・文字境界を守るため保持した。

## 測定結果

同じPowerShellプロセスで作業開始時（当該ファイルはHEADと同一）と変更後の実サービスをAdd-Typeで読み、同じ入力を比較した。全文表示やエディタの組版は含まない。

| 16,000文字のrun | Before | After |
| --- | ---: | ---: |
| ドル記号 | 475.98ms | 0.50ms |
| 通常raw引用符 | 478.54ms | 0.12ms |
| 補間raw引用符 | 477.44ms | 0.12ms |

最終ビルドのSkiaを使った実MainWindowの入力更新・組版・描画は、それぞれ360.47ms、121.04ms、103.04ms。実際の本文が変わらないことと3秒未満で描画することを検証し、[画面](opop/skia/brackets.png)を残した。画像用の文書は隔離した実ファイルとして作成した。

設定画面の起動比較は別の専用プロセスで同じE2E手順を1回ずつ実行。Window構築から初回layoutまで1335.34ms→708.55ms、初回設定オープン183.97ms→534.90ms。未使用画面のフォント候補取得と索引構築が初回設定オープンへ移ったことを示す。単回・ヘッドレスSkiaの測定であり、配布版の通常起動時間や平均値を保証しない。

## 見送った候補

| 候補 | 判断 |
| --- | --- |
| `DocumentViewModel.Text.Length`を`TextLength`へ置換 | 純TextDocumentの割当削減は実測できたが、実UIでは非表示プレビューの全文bindingが先に全文cacheを作り得るため、単独変更の効果は立証不足 |
| 単一起動IPCの読込timeout | 接続後にEOFを送らないクライアントが後続転送を止め得る。新しいtimeout/転送失敗方針の選定を含むため今回の契約維持改善には含めない |
| 標準IMEへ置換 | 対象版AvaloniaEdit 12.0.0の`SupportsPreedit`はfalse、`SetPreeditText`は空。変換中表示とUndoの既存契約を保てない |
| 起動設定読込やテーマ適用を遅延化 | 初回テーマのちらつきを避ける既存の順序を維持 |
| 既存単体テストの一括削除 | E2Eが見逃す境界を検証するものがあり、削除の根拠がない。立証できた重複1件だけ削除 |

IMEの一次資料は[AvaloniaEdit固定commitのTextArea.cs](https://github.com/AvaloniaUI/AvaloniaEdit/blob/86fdebec4cff7affb0e14c7885df71d28edce777/src/AvaloniaEdit/Editing/TextArea.cs#L1144)と[preedit対応の未完了issue #524](https://github.com/AvaloniaUI/AvaloniaEdit/issues/524)。2026-10-01確認。AvaloniaDocs検索では該当資料なし。現在使用している版に仕様を接続して判断した。

## 検証と再現

開始時: SDK10.0.401、.NET10.0.12。locked restore成功、`dotnet test Fumilume.slnx -c Release --no-restore`は607成功・0失敗・1skip、22.390秒。

同じコマンドによる最終結果は610成功・0失敗・1skip、24.591秒。追加E2E4件と重複テスト削除1件により成功件数は3件増えた。時間はビルドを含み、全体実行の高速化は主張しない。[全テストログ](opop/full-test.txt)と[実行台帳](opop/execution.json)に保存した。検索閉鎖の1skipは既存の専用Skia実行条件によるもので、直前の修正作業で個別検証済み。

macOS用locked restoreとReleaseクロスビルドは成功、警告0。Apple Silicon上の実行、Native AOT、署名、配布は今回行っておらず実機確認は未検証。

```powershell
dotnet restore Fumilume.slnx --locked-mode
dotnet test Fumilume.slnx -c Release --no-restore
pwsh -NoProfile -File docs/validation/opop-bracket-benchmark.ps1
dotnet build tests/Fumilume.Tests/Fumilume.Tests.csproj -c Release --no-restore
$env:FUMILUME_OPOP_ARTIFACTS = "$PWD/docs/validation/opop/headless"
& ./tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.exe -class Fumilume.Tests.OpopIntegrationTests
# Skiaは設定と括弧描画を別々の新しいプロセスで実行する。
$env:FUMILUME_E2E_RENDER = '1'
$env:FUMILUME_OPOP_ARTIFACTS = "$PWD/docs/validation/opop/skia"
& ./tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.exe -method Fumilume.Tests.OpopIntegrationTests.SettingsRetainSearchAndNavigationAfterClosingAndReopening
& ./tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.exe -method Fumilume.Tests.OpopIntegrationTests.BracketHighlightingHandlesLongRunsInEditor
Remove-Item Env:FUMILUME_E2E_RENDER
Remove-Item Env:FUMILUME_OPOP_ARTIFACTS
```

この作業の一時物は各E2Eが作る`%TEMP%/fumilume-tests-<GUID>`で、TemporaryStorageのDisposeにより削除する。今回のE2Eソース作成時刻以降の同形式フォルダは終了時に0件と確認した。それ以前のフォルダは他作業との所有権を区別できないため触れていない。比較scriptはソースをメモリで読み、比較前コードを一時保存・差し替えしない。`docs/validation/opop/`のJSON・PNG・ログと本手順は再現可能な成果物として保持する。アプリ版・NuGet lock・ユーザー所有のTitleBarHitTestTests差分を変更せず、すべて未ステージで残す。
