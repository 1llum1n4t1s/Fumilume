# Apple Silicon 実経路 E2E

実装前に列挙した失敗条件: 日本語パスを開けない、保存で文字コード/BOM/改行が変わる、重複タブができる、設定が次回起動へ残らない、未保存バッファ/選択タブ/設定タブが復元されない、後続プロセスが一次プロセスになる/引数が届かない、Core Graphics が PDF の通常ページ/回転ページを白紙または誤ったサイズで描く、壊れた PDF で停止する、実ウィンドウ/compiled bindings/エディタ/プレビューが起動できない。

Apple Silicon Mac のログイン済みデスクトップセッションで、リポジトリ指定 SDK を使って実行する。

```sh
dotnet restore tests/Fumilume.MacE2E/Fumilume.MacE2E.csproj -p:FumilumeTargetMac=true --locked-mode
dotnet run --project tests/Fumilume.MacE2E/Fumilume.MacE2E.csproj -c Release -p:FumilumeTargetMac=true --no-restore -- "$PWD/mac-e2e-artifacts"
```

Headless を使わず、製品の App、MainWindow、保存コマンド、セッション復元、DocumentFileService、MacPdfRenderer、別プロセスとの single instance 通信を実行する。PDF は通常/90度回転ページのサイズ・色・非対称図形の画面位置を確認する。実 TextArea に KeyDown を流し、Option/Command 付き削除の無操作、Control 付き単語削除（記号/日本語/改行/選択範囲）の実編集とマクロ再生を比較する。物理キー入力やファイル選択ダイアログの操作は含まない。実アプリの Native AOT パッケージ起動は別の配布 smoke 検証で行う。

出力先は毎回新しい空ディレクトリを指定する。JSON の各ケース名・結果・例外と PNG（ウィンドウ内容、通常/回転 PDF）、fixture、保存後文書、設定/セッションを再現成果物として保持する。失敗時にも結果 JSON を書き、非ゼロで終了する。CI ではタイムアウトと `if: always()` の成果物アップロードを設定する。ユーザー設定は成果物下の隔離ディレクトリへ差し替える。Windows 既存テストのプロジェクト・件数には影響しない。

通常の起動コマンドは外側の driver を実行する。最初の子プロセスで全 E2E と最終セッション保存・通常のウィンドウ終了を行い、終了コード 0 を確認してから別の子プロセスで同じ保存先を読み、実 MainWindow の復元結果を確認する。初回は `results.json`、再起動は `restart-results.json` と `restart-restored-document.png`、両プロセスの終了コードは `process-results.json` へ記録する。復元側は起動前の設定初期化を行わない。通常の Back/Delete の CRLF 行結合と Control 削除の空行境界も実編集とマクロ再生で比較する。

更新の保存境界では、更新ウィンドウを実際にモーダル表示し、製品の更新プロセス境界とセッション保存を通す。正常時は実子プロセスが起動時点のセッションと未保存本文を成果物へコピーし、日本語本文を照合する。保存先を通常ファイルで塞いだ失敗と、セッション復元を無効にした未保存確認の取消では、実ダイアログのボタンを操作し、適用プロセスの起動と終了要求が拒否されることを確認する。`update-start-boundary.sh`、保存時点のコピー、障害用ファイル、画面PNG、既存の結果JSONを再現成果物として保持する。更新パッケージによるアプリ置換はこの保存境界検証に含まない。

## 更新 SDK と配布物の検証

署名・パッケージ生成後に、実 Velopack SDK の `SimpleFileSource` から Mac ARM64 チャネルを選び、`CheckForUpdatesAsync` と `DownloadUpdatesAsync` を実行する。

```sh
dotnet run --project tests/Fumilume.MacE2E/Fumilume.MacE2E.csproj -c Release -p:FumilumeTargetMac=true --no-restore -- --verify-updates "$PWD/local-release/macos/artifacts" "$PWD/update-e2e-artifacts"
```

`TestVelopackLocator` で隔離した旧版 1.0.0 のインストール状態を与え、実フィードが選ぶ版・アプリ ID・チャネル・ダウンロードサイズ・SHA256 を生成物と照合する。JSON とダウンロード済み nupkg を成果物へ残す。これは認証やデスクトップを要求せず、Windows でも managed DLL から実行可能。

この SDK 検証単独では実アプリの置換を行わず、結果 JSON は `appReplacementVerified: false` とする。配布スクリプトは別途 `scripts/macos/verify-update-apply.sh` で署名済み実 `.app` のコピーを同版 full.nupkg へ置換し、inode変更、署名、Gatekeeper、自動再起動と通常終了を確認する。専用 HOME を使い、ログと JSON を `verification/update-apply` に残す。実インストール先の所有者・権限も記録するが、管理者認証の対話を必要とする更新はこの CI 検証に含まない。
