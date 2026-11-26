# 検証コマンド

`global.json` が指定する .NET SDK を用意する。SDK は `rollForward: disable` で固定されているため、指定版で検証する。通常のコード変更では、リポジトリルートから順に実行する。

```powershell
dotnet restore Fumilume.slnx --locked-mode
dotnet test --solution Fumilume.slnx -c Release --no-restore
```

- UI、binding、テーマ、タブ状態を変更した場合は `MainWindowIntegrationTests` を含む全テストを実行する。
- R2 中核・入口の変更時は `pwsh -NoProfile -File tests/R2Transfer/Run.ps1 -WorkRoot <未使用の外部絶対パス>` を新しい pwsh で実行する。SDK・外部サービスを使わず、mock transport と外向き接続を転送しない loopback proxy で公開入口・条件付き転送・manifest・相対path・秘密のマスクを検証する。
- macOS 向けの変更では `dotnet restore src/Fumilume/Fumilume.csproj -p:FumilumeTargetMac=true -r osx-arm64 --locked-mode` を通す。Apple Silicon Mac で `bash scripts/build-macos-arm64.sh` を実行し、文書の開く・保存・再起動後の復元・PDFの通常ページと回転ページを実機確認する。Windows では macOS Native AOT の発行と実機確認を完了扱いにしない。
- ファイルI/O、設定永続化、文字変換、Markdown、PDFを変更した場合は、実際の経路を通る E2E テストで正常系と境界条件を確認し、再現可能な成果物を残す。
- NuGet依存関係を変更した場合は、影響するWindows・macOS両方のlockfileを更新する。対象は `src/Fumilume/packages.lock.json`、`src/Fumilume/packages.macos.lock.json`、`tests/Fumilume.Tests/packages.lock.json`、`tests/Fumilume.MacE2E/packages.lock.json`。通常の検証とmacOS向けrestoreに加え、`dotnet restore tests/Fumilume.MacE2E/Fumilume.MacE2E.csproj -p:FumilumeTargetMac=true --locked-mode` を通す。
- Native AOT、RID依存API、配布物へ影響する変更では、リリース前に両アーキテクチャを検証する。直接発行する場合は `win-x64` に `PlatformTarget=x64`、`win-arm64` に `PlatformTarget=ARM64` を対応させる。
