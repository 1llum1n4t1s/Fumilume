# Windows配布物の検証

pack・署名後、アップロード前に `scripts/verify-windows-package.ps1` が配布実物を静的検証します。

- 更新フィードとFull nupkgのサイズ・ハッシュ、nuspecのID・版、必須同梱ファイル、署名を照合します。
- Portable ZIPのルートの `Fumilume.exe` はVelopackランチャー、`current/Fumilume.exe` がアプリ本体です。本体はnupkg内の本体とSHA256で照合し、RIDに対応するPE machine・版・CLRヘッダー不在を確認します。ランチャーと `Update.exe` はx86のPEとして別に署名等を検証します。
- Setupの署名・版も確認し、結果を `RecordRoot/windows-package-<run-id>.json` へ残します。
- `-SkipUpload` でも動き、失敗は公開を止めます。配布物の起動・実画面操作は行いません。
