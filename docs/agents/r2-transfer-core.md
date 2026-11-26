# R2転送の中核

- 署名・転送・artifact manifest と path 検証の正本は `R2Transfer.Core.cs` です。2 本の PS helper は既存の関数名・引数・環境変数・戻り値を保つ入口で、Add-Type で中核をメモリ内に読み込みます。
- 同一 pwsh での再読込と別 repo の同梱コピーの再利用は source 全体の SHA-256 一致時だけ許可し、異版の同名型は認証前に停止します。
- 相対pathは FileSystem provider の現在位置の実体パスを基準に解決し、拒否する pattern は正規化前に検査します。非 FileSystem provider の現在地からの相対入力は通信前に拒否します。
