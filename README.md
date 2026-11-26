# Fumilume

Fumilume（フミルメ）は、複数の文書を Chrome のような垂直タブで行き来できる無料テキストエディタです。開いたファイルの文字コードと改行コードを保ったまま編集できます。WindowsとApple Silicon Macに対応しています。

[公式サイト](https://fumilume.kagayoi.com/) ・ [Windows x64 版](https://fumilume.kagayoi.com/Fumilume-win-Setup.exe) ・ [Windows ARM64 版](https://fumilume.kagayoi.com/Fumilume-win-arm64-Setup.exe) ・ [Apple Silicon Mac版](https://fumilume.kagayoi.com/Fumilume-osx-arm64-Setup.pkg)

![Fumilume の画面](https://fumilume.kagayoi.com/hero-app.png)

## 主な機能

垂直タブ、文字コード・改行コードの維持、正規表現検索、フォルダ横断検索、Markdown / CSV / PDF の表示、マクロなどを備えています。一覧は [主な機能](docs/readme/features.md) を参照してください。

## インストール

Windows 10 1809 以降・Windows 11（x64 / ARM64）と Apple Silicon Mac（macOS 15 以降）に対応し、追加のランタイムは不要です。詳しくは [インストール](docs/readme/installation.md) を参照してください。

## 使い方

ツールバーから文書を開くかウィンドウへドロップし、複数開いたときは左の垂直タブで切り替えます。機能はコマンドパレット（`Ctrl+Shift+P`）から探せます。未保存でも終了でき、次回起動時に復元されます。

- [基本的な使い方](docs/readme/basic-usage.md): 開く・保存・復元
- [Markdown / CSV のプレビュー](docs/readme/markdown-csv-preview.md): `Ctrl+Shift+M` で切り替え、CSV は表で編集
- [PDF の表示](docs/readme/pdf-viewer.md): 連続スクロールと文字のコピー
- [左のパネル](docs/readme/side-panel.md): タブ、アウトライン、ブックマーク
- [フォルダを開く](docs/readme/folder-explorer.md): エクスプローラー
- [フォルダから探す](docs/readme/folder-search.md): `Ctrl+Shift+F` で横断検索
- [キーボードマクロ](docs/readme/keyboard-macros.md): `Shift+F1` で記録
- [ショートカット一覧](docs/readme/keyboard-shortcuts.md)
- [設定](docs/readme/settings.md): 項目と保存先
- [文字コードとファイル関連付け](docs/readme/encodings-associations.md)
- [更新](docs/readme/updates.md)
- [困ったときは](docs/readme/troubleshooting.md)

変更内容は [変更履歴](CHANGELOG.md) で確認できます。

## ライセンス

Fumilume は MIT License で公開されています。詳しくは [LICENSE](LICENSE) を参照してください。
