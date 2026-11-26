# 実装規約

- C# は nullable と暗黙 using を有効にし、ビルド警告をエラーとして解消する。
- UI は Avalonia の compiled bindings を維持する。ViewModel の公開プロパティ名を変更するときは、AXAML の binding と Headless 統合テストも同時に更新する。
- UI 状態と操作は `ViewModels/`、ファイル・設定・更新・関連付けなどの外部処理は `Services/` に置く。OS API やダイアログはサービス境界で隔離し、テストでは差し替え可能にする。
- 設定は Windows では `%LocalAppData%\Fumilume\settings.json`、macOS では `~/Library/Application Support/Fumilume/settings.json` を正本とし、既存プロパティ名との互換性、範囲補正、破損時の既定値フォールバック、原子的な保存を維持する。
- 起動・セッション経路を変更するときは、同じユーザーの通常起動を単一プロセスへ集約し、後続起動のファイル引数を初回セッション復元後に開く順序を維持する。相対パスは送信側プロセスで絶対化する。
- ログは `SuperLightLogger` を `AppLogger` 経由で使用し、更新UIは `VelopackUpdateDialog.Avalonia` を `UpdateService` 経由で使用する。各ライブラリをViewから直接呼び出さない。
- ファイル関連付けの追加・変更では、インストール後、更新後、アンインストール前のVelopackコールバックと関連付けテストを確認する。
- 機能を足すときの入口は `EditorCommandCatalog` とコマンドパレット（`MainWindowViewModel.EnumeratePaletteEntries`）にする。常時出すツールバーのボタンを増やさない。
- 設定画面へまとまりを足すときは `HeaderedContentControl Classes="settingscard"` で囲う。検索の索引はこの目印で作るので、付け忘れると検索から漏れる。OSなどの表示条件はカードの親へ置き、検索によって非表示の項目が再表示されたり件数に含まれたりしないことを `SettingsSearchTests` で確認する。
- README はアプリ利用者向けに保ち、開発・配布の詳細は AGENTS.md、`docs/agents/`、DESIGN.md に記載する。
