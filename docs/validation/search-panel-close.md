# 検索ボックスを閉じる操作の実経路検証

## 修正前の失敗候補

- 閉じるボタンのコマンドが検索パネルへ届かない。
- ボタンが無効化されるか、クリックが別コントロールへ届く。
- 閉じた検索パネルが再表示される、または検索の強調表示が残る。
- 検索モードだけ直り、置換モードでは閉じられない。
- 閉じた後にエディタへフォーカスが戻らず、再度検索を開けない。

## 再現

実MainWindowをAvalonia.Headless + Skia上で起動し、Ctrl+F / Ctrl+Hで検索・置換パネルを開く。
製品のクリップ・テンプレートを変更せず、実ボタンの画面座標へMouseDown/MouseUpを送信して、パネルの削除、フォーカス、本文保持と再オープン・Escapeを確認する。
同じウィンドウを再利用し、検索・置換それぞれで、一致あり・空欄・一致なし・不正な正規表現・ダークテーマで本文にフォーカスした状態を確認する（計10パターン）。

```powershell
$previousRenderer = $env:FUMILUME_E2E_RENDER
$previousArtifacts = $env:FUMILUME_E2E_ARTIFACTS
try {
    $env:FUMILUME_E2E_RENDER = '1'
    $env:FUMILUME_E2E_ARTIFACTS = Join-Path (Get-Location) 'docs/validation/search-panel-close'
    dotnet test tests/Fumilume.Tests/Fumilume.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SearchPanelCloseButton
    if ($LASTEXITCODE -ne 0) { throw '検索パネルの実描画E2Eが失敗しました。' }
} finally {
    $env:FUMILUME_E2E_RENDER = $previousRenderer
    $env:FUMILUME_E2E_ARTIFACTS = $previousArtifacts
}
```

## 原因と変更

修正前はCtrl+Fで初めて開くと、閉じるボタンのIsEnabledはtrueでもIsEffectivelyEnabledはfalseとなり、同じ座標へのクリックが背景のBorderへ届いた。
AvaloniaEdit 12.0.0のRoutedCommandはフォーカス先に基づくCanExecuteを使用するが、CanExecuteChangedを通知しないため、テンプレート生成時の無効状態が更新されなかった。
閉じるボタンのコマンドをTemplateApplied時にRelayCommand(SearchPanel.Close)へ差し替え、フォーカスに依存せず閉じられるようにした。
Closeの既存処理を使うため、検索強調の解除と本文へのフォーカス復帰も維持される。

## 実測（2026-10-01、Windows / .NET SDK 10.0.401）

- 実描画E2E: 1件成功、内部の10パターンすべて成功。検索パネルの開く・クリックで閉じる・再度開く・Escapeで閉じるを繰り返した。
- [操作前](search-panel-close/search-False-False-2-open.png) / [操作後](search-panel-close/search-False-False-2-closed.png)。10パターンの前後、計20枚のPNGを保存した。
- `dotnet restore Fumilume.slnx --locked-mode`: 成功。
- `dotnet test Fumilume.slnx -c Release --no-restore`: 579成功、0失敗、専用E2Eの1件はスキップ。上記の専用プロセスでその1件も成功を確認した。
- macOS向けlocked restore: 成功。Windows向けlocked restoreへ戻した。macOS実機の入力は未検証。

通常の簡易Headless描画では角丸クリップの入力判定を再現できないため、実描画E2EはSkiaを有効にした専用プロセスで実行する。
Skiaで複数ウィンドウを生成すると、この環境のフォントキャッシュに空白の重複した「Eras  ITC」が加わり、2回目以降の設定画面生成でFormatExceptionが発生することを観測した。
今回の修正とは別の不具合として製品側は変更していない。専用E2Eは単一ウィンドウで10パターンを通し、通常の全テストは従来の描画基盤で実行する。
