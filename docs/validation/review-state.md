# 保存・再読込・設定復元のレビュー修正検証

対象: S01（保存中入力の終了時喪失）、S02（`CaretPositions: null` の起動例外）、S03（再読込中入力の喪失）。

反証確認では、保存の `MarkSaved` が未保存状態を維持しても `ResolveUnsavedAsync` が書込成功を終了許可として返すこと、設定JSONの明示的nullがプロパティ初期値を上書きすること、手動再読込の `ReadAsync` 後に文書版の確認がないことを実コードへ接続した。

`ReviewStateE2ETests` は実際の `MainWindow` と `TextEditor` を構築する。ファイルサービスは実ディスクI/Oへ委譲し、読み書きの開始直後だけ完了待ちを差し込む。ダイアログの待機中にもエディタへ入力する。テスト用サービスの差替えはテスト内のリフレクションだけに限定する。

再現ケース:

- 保存中に追加入力してタブを閉じる／セッション復元無効の終了を試す。保存スナップショットはディスクへ書かれ、追加入力とタブは維持される。再保存後には閉じられる。
- 手動再読込の読込待機中に入力する。本文とUndoを維持し、後続の通常再読込は成功する。
- 手動／外部再読込の確認待機中に入力する。確認前の編集と新しい入力をともに維持する。
- 破棄確認待機中に入力する。旧版への破棄許可で新しい入力を失わない。
- 複数文書の終了確認中に既に確認済みの文書へ入力する。終了を止め、再確認で変更のない破棄は許可する。
- 実settings.jsonへ `CaretPositions: null` を書いてウィンドウを起動する。他の有効な設定を維持し、200件上限の通常保存と再読込、破損JSONからバックアップ／既定値復元を確認する。
- 文字コード／改行コードのステータスバー操作と同じコマンドを、保存・読込・破棄確認・手動／外部再読込確認・複数文書終了確認の待機中に実行する。本文の版が変わらないことを確認したうえで、保存形式の変更が失われず終了／再読込が止まることを検証する。

文書状態の照合は本文の版・文字コード・改行コードをひとまとまりとして扱う。保存形式の変更は本文の版を更新しないため、版だけの比較では確認済み状態を取り違える。保存自体は既存の `MarkSaved` による文字コード・改行コードの照合を維持する。

再実行（リポジトリルート、他のAvalonia検証と直列に実行）:

```powershell
dotnet build tests/Fumilume.Tests/Fumilume.Tests.csproj -c Release --no-restore
$env:FUMILUME_E2E_ARTIFACTS = Join-Path (Get-Location) 'docs/validation/review-state-artifacts'
& tests/Fumilume.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/Fumilume.Tests.exe -class Fumilume.Tests.ReviewStateE2ETests -result-trx (Join-Path $env:FUMILUME_E2E_ARTIFACTS 'review-state.trx')
```

`review-state-artifacts/` に結果JSONとTRXを保存する。テスト用ファイル・設定は `TemporaryStorage` でOS一時ディレクトリへ隔離し、各ケース終了時に削除する。実ユーザーの設定は使わない。実描画・macOS実機・Native AOTはこの検証の対象外。

2026-10-01 Windows x64 / .NET 10.0.12 の実行結果: Releaseビルドは警告0・エラー0。上記クラスは22件成功、失敗0・スキップ0（3.218秒）。`review-state-artifacts/review-state.trx` と22件のJSONを保存した。修正前の成立確認はコード経路による反証確認であり、この担当では修正前実行の失敗ログは取得していない。
