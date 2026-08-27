# Fumilume 設計

## 目的と範囲

Fumilume は、複数のテキスト文書を垂直タブで扱う Windows 専用エディタです。文字コードと改行コードを維持した編集、Markdownプレビュー、PDF閲覧、サクラエディタを参考にした編集操作、設定・更新・ファイル関連付けを単一のデスクトップアプリとして提供します。

対象は `net10.0-windows10.0.26100.0`、最低対応OSは Windows 10 バージョン1809です。配布物は x64 / ARM64 の自己完結型Native AOTアプリです。

## システム構成

| 領域 | 主な実装 | 責務 |
| --- | --- | --- |
| 起動・ライフサイクル | `Program.cs`, `App.axaml.cs` | ログ初期化、Velopackコールバック、設定読込、テーマ適用、メインウィンドウ生成 |
| View | `Views/`, `Styles/`, `App.axaml` | Avalonia UI、エディタ接続、Markdown描画、PDF画像表示、テーマ別リソース |
| ワークスペース | `MainWindowViewModel`, `WorkspaceTabViewModel` | タブ集合、選択状態、ファイル操作、保存確認、コマンド可否、設定タブ管理 |
| 文書モデル | `DocumentViewModel`, `DocumentBookmarks` | AvaloniaEdit文書、変更状態、カーソル、検索・編集・変換、Markdown表示状態 |
| PDFモデル | `PdfDocumentViewModel` | PDFの読込、ページ範囲、拡大率、非同期レンダリング、画像ライフタイム |
| 設定モデル | `AppOptionsViewModel`, `SettingsTabViewModel` | UIへ公開する設定、値の即時反映、永続化、関連付け操作 |
| サービス | `Services/` | 文書I/O、設定、ログ、更新、ダイアログ、テーマ、関連付け、Markdown解析、PDFレンダリング |
| 配布 | `scripts/release-local.ps1`, `web/` | 署名付きVelopack成果物、R2配信、更新マニフェスト、ランディングページ |

ViewModel は状態と操作を持ち、View は表示とAvalonia固有の接続を担当します。ファイルシステム、Windows API、更新UIなどの副作用はサービスへ分離し、ViewModelテストから代替実装を渡せる境界を保ちます。

## タブと状態モデル

`MainWindowViewModel.Tabs` は `WorkspaceTabViewModel` の集合で、次の3種類を扱います。

- `DocumentViewModel`: 編集可能なテキスト文書。未保存状態、パス、文字コード、改行、カーソル、Markdownプレビュー状態を保持する。
- `PdfDocumentViewModel`: 読み取り専用PDF。ページと表示画像を保持し、編集・保存コマンドの対象外になる。
- `SettingsTabViewModel`: 単一の設定画面。重複して開かず、常に文書・PDFタブの後ろへ置く。

選択タブの型から、エディタ、Markdownプレビュー、PDFビュー、設定ビューの表示とコマンド可否を導出します。表示可能な文書・PDFが0件になった場合は空の文書を作り、設定タブだけの状態を残しません。

## 主要データフロー

### 起動

1. `Program` が `AppLogger` を初期化し、未処理例外を記録する。
2. Velopackがインストール・更新後の関連付け更新と、アンインストール前の関連付け解除を処理する。
3. `App` が `SettingsService` から設定を1回読み、初回描画前に `ThemeService` へ適用する。
4. `MainWindow` と `MainWindowViewModel` が作られ、起動引数のファイルをワークスペースへ開く。

### テキストの読込・編集・保存

1. `MainWindowViewModel` がダイアログまたは起動引数から絶対パスを受け取る。
2. `DocumentFileService` がBOMと厳密なUTF-8判定から文字コードを決め、最初に現れる改行形式を記録する。
3. `TextDocumentContent` を `DocumentViewModel` へ読み込み、AvaloniaEditの文書を編集元データとする。
4. 保存時は元の文字コードと改行へ正規化し、同じディレクトリの一時ファイルから置き換える。設定が有効な場合だけ置換前の内容を `.bak` に残す。
5. 保存・クローズ時に、設定が有効ならファイルごとのカーソル位置を記録する。

### Markdown

`.md` の `DocumentViewModel` だけがプレビューへ切り替えられます。`MarkdownDocumentParser` が編集元テキストを表示用ブロックへ変換し、`MarkdownPreview` がAvaloniaコントロールとして描画します。プレビューは派生表示であり、保存される正本は常にエディタ内のMarkdown文字列です。

### PDF

`.pdf` は `WindowsPdfRenderer` が `Windows.Data.Pdf` で開き、要求ページをビットマップへ変換します。`PdfDocumentViewModel` はページ移動と25～400%の拡大率を管理し、古いレンダリング結果とストリームを破棄します。PDFは閲覧専用で、テキスト抽出・編集・保存は責務に含めません。

### 設定

設定Viewの操作は `AppOptionsViewModel` を経て共有中の `AppSettings` を更新し、テーマ・フォント・エディタ・タブへ即時反映します。`SettingsService` は未知・破損JSONを既定値へフォールバックし、数値や記録件数を許容範囲へ補正します。保存は `AtomicFile` により、同一ディレクトリの一時ファイルを書き切ってから置き換えます。

### 更新と配布

アプリ側の `UpdateService` は `VelopackUpdateDialog.Avalonia` を介して更新確認・ダウンロード・適用・再起動を行います。配布側は `scripts/release-local.ps1` が x64 / ARM64 を順番にNative AOT発行し、コード署名、Velopackパッケージ化、Cloudflare R2へのアップロード、固定URLのキャッシュパージ、公開ファイルの版・ハッシュ・サイズ・署名照合を行います。

## 永続データと外部境界

- `%LocalAppData%\Fumilume\settings.json`: 利用者設定とカーソル位置
- `%LocalAppData%\Fumilume\logs`: `SuperLightLogger` による日別ログ
- Windowsユーザー別レジストリ: 対応拡張子のファイル関連付け
- `fumilume.kagayoi.com`: ランディングページ、固定セットアップURL、更新マニフェストとパッケージ
- Cloudflare R2 `fumilume-updates`: 配布成果物の正本

テストでは `AppStoragePaths` の保存先を一時ディレクトリへ差し替え、実利用者の設定を変更しません。署名証明書とCloudflare認証情報はリポジトリ外に置き、製品コードやテストデータへ取り込みません。

## 重要な不変条件

- テキスト文書は、対応範囲内で読込時の文字コードと改行コードを保存後も維持する。
- 未保存文書を閉じる前に、保存・破棄・取消の決定を完了する。
- 設定タブは最大1個で末尾にあり、コンテンツタブが0個の状態を作らない。
- PDF選択中はテキスト編集・保存操作を有効にしない。
- Markdownプレビューは `.md` だけに提供し、編集元テキストを変更しない。
- UIフォントとエディタフォントは独立し、エディタ候補は等幅フォントだけにする。
- 設定保存の失敗で既存の正常な設定ファイルを途中内容へ置き換えない。
- x64とARM64の成果物は同じアプリ版を持ち、それぞれ対応する更新チャンネルへ公開する。

## 採用済みの設計判断

- **Avalonia + AvaloniaEdit**: Windowsのネイティブデスクトップ体験と高度な編集基盤を得る。Windows API利用とNative AOTを優先するため、現在はクロスプラットフォーム化を目的にしない。
- **MVVMとサービス境界**: タブ・コマンド状態をViewModelで検証し、ファイルやOS依存処理を差し替え可能にする。Avalonia固有の描画・入力接続だけはView側へ残す。
- **Windows.Data.Pdf**: 追加PDFランタイムを同梱せずWindows標準機能で描画できる。一方でWindows専用となり、閲覧以外のPDF機能は持たない。
- **WebViewを使わないMarkdown表示**: 外部ブラウザランタイムやHTML実行を避け、テーマと入力境界をAvalonia内で完結させる。対応構文はパーサーとViewが実装する範囲に限られる。
- **原子的なローカル保存**: 一時ファイルと置き換えでクラッシュ時の破損を抑える。文書の `.bak` は利用者設定で明示的に有効化した場合だけ作る。
- **自己完結Native AOT配布**: .NETランタイム不要の起動と配布を優先する。RIDごとの発行・署名・検証が必要になるため、リリース処理は逐次実行する。
- **共有更新・ログライブラリの薄い統合**: 更新UIとログ出力を共通ライブラリへ集約し、Fumilume固有コードは起動、設定、エラー表示の接着に限定する。

実装変更時の必須コマンドと作業規約は [AGENTS.md](AGENTS.md)、利用者から見える現在の機能は [README.md](README.md) を正本とします。
