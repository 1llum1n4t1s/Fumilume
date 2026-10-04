# サイドバー上下分割の実経路検証

## 実装前の失敗候補

- フォルダツリーと縦タブが横並びのまま、左右位置や幅が一致しない。
- 上下の分割線をポインターで動かしても高さが変わらない、横幅が変わる、本文が失われる。
- 横幅の分割線を動かしたとき片側だけ幅が変わる、本文が画面からはみ出す。
- 720×460の最小ウィンドウで上下パネルが潰れる、エディタが画面外に出る。
- Ctrl+Bでツリーを隠した後も空白や分割線が残り、上パネルが全高を使わない。
- 再表示したとき上下比率が既定値へ戻る。
- 上下比率または共有幅が保存されず、再起動で元に戻る。

## 再現手順

`FolderExplorerIntegrationTests.SidebarPointerSplitResizeHideAndRestartPreserveLayout` は、実MainWindow・実ファイル・ポインターのドラッグ・Ctrl+B・実設定/セッション保存・ウィンドウ再生成を通す。

```powershell
dotnet restore Fumilume.slnx --locked-mode
dotnet test Fumilume.slnx -c Release --no-restore
$env:FUMILUME_E2E_RENDER = '1'
$env:FUMILUME_E2E_ARTIFACTS = Join-Path (Get-Location) 'docs/validation/sidebar-split'
dotnet tests/Fumilume.Tests/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.dll -method Fumilume.Tests.FolderExplorerIntegrationTests.SidebarPointerSplitResizeHideAndRestartPreserveLayout -result-ctrf docs/validation/sidebar-split/skia-results.json
Remove-Item Env:FUMILUME_E2E_RENDER
Remove-Item Env:FUMILUME_E2E_ARTIFACTS
```

通常プロセスで実レイアウトと入力を検証し、Skia専用プロセスでは同じE2Eの各段階をPNG、座標・比率・保存結果をJSONへ出す。設定と文書は既存TemporaryStorageで隔離する。

## 検証状態

2026-10-04、Windows 10.0.26300 / .NET SDK 10.0.401 / 実行時 .NET 10.0.12 / Avalonia 12.1.3 のローカルソースで検証。

- locked restore 成功。全テストは612件成功・0件失敗・1件スキップ。スキップは専用の実描画プロセスを要する既存の検索ボタンE2E。
- Skia実描画による上下分割E2Eは1件成功・0件失敗。実MainWindowへポインター入力を送り、各段階をPNGへ保存した。初期状態と復元後の画像で上下配置と本文領域を目視確認した。
- 初期状態では上下が同じ240px幅。上下分割線を65px下げると上328→393px、下328→263pxとなり、比率は0.5991。
- 幅分割線を40px左へ動かすと上下とも200px幅になった。720×460へ縮小しても本文は画面内に収まり、上249px・下167pxを確保した。
- Tabs・Outline・Bookmarksのいずれも、Ctrl+Bで下段と分割線だけが消え、上段が422pxの全高を使った。再表示とウィンドウ再生成で比率0.5991・幅200pxが復元され、本文も一致した。
- macOS向け locked restore とクロスビルドは成功（警告0、エラー0）。Apple Silicon実機とOSネイティブウィンドウでのドラッグは未検証。

数値・実行結果は [sidebar-split.json](sidebar-split/sidebar-split.json)、[skia-results.json](sidebar-split/skia-results.json) に保存。描画は [初期状態](sidebar-split/sidebar-initial.png)、[上下調整後](sidebar-split/sidebar-vertical-drag.png)、[幅調整後](sidebar-split/sidebar-width-drag.png)、[最小ウィンドウ](sidebar-split/sidebar-minimum-window.png)、[復元後](sidebar-split/sidebar-restored.png) を参照。
