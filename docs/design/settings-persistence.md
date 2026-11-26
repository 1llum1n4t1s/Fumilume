# 設定

設定Viewの操作は `AppOptionsViewModel` を経て共有中の `AppSettings` を更新し、テーマ・フォント・エディタ・タブへ即時反映します。IBM Plex Sans JP と UDEV Gothic JPDOC は `FumilumeFontCollection` からアプリ同梱リソースとして解決し、各々をUI・エディタの既定値にします。

`SettingsService` は未知のJSONプロパティを読み飛ばし、本体を読めない場合は退避コピー、それも読めない場合は既定値へフォールバックします。数値の範囲補正は `AppSettingsDefaults` に集約し、設定読込とUIからの変更で共有します。記録件数も読込時に制限します。保存は `AtomicFile` により、同一ディレクトリの一時ファイルを書き切ってから置き換えます。

## 保存先

- `%LocalAppData%\Fumilume\settings.json`: 利用者設定とカーソル位置
- `%LocalAppData%\Fumilume\session.json` と `session`: 前回終了時のタブ構成と未保存の本文
- `%LocalAppData%\Fumilume\macros.json`: キーボードマクロ
- `%LocalAppData%\Fumilume\logs`: `SuperLightLogger` による日別ログ
- macOS は `~/Library/Application Support/Fumilume` に同じ構成
- Windowsユーザー別レジストリ: ファイル関連付け。macOS は `Info.plist` の文書関連付け宣言

テストでは `AppStoragePaths` の保存先を一時ディレクトリへ差し替え、実利用者の設定を変更しません。
