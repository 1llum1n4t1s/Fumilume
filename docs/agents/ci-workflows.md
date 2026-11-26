# CI と Dependabot

- 通常の検証は `.github/workflows/dependency-validation.yml`（main への push・全 PR・手動）が GitHub 提供ランナーで行う。Windows（windows-2025）は R2 mock チェック後に `dotnet build` と `dotnet test --solution Fumilume.slnx` を実行し、macOS（macos-latest）は R2 mock チェックと `tests/Fumilume.MacE2E` のビルドを行う。実アプリ経路の Mac E2E、Native AOT、署名・公証は `macos-release.yml` が配信時に行う。
- 同ワークフローの concurrency グループは PR 番号（PR 以外は run id）単位で、同じ PR の古い実行だけを取り消す。main への連続 push は互いに取り消さない。
- NuGet パッケージ（`~/.nuget/packages`）は `actions/cache` で共有する。キーは OS・ジョブ名と、csproj・Directory.Build.*・lockfile・global.json・nuget.config のハッシュで、保存は main の実行でキャッシュが無かったときだけ行う。タグと `release/` ブランチでは復元しない。
- `.github/dependabot.yml` は github-actions・dotnet-sdk・nuget（`/src/Fumilume`、`/tests/Fumilume.MacE2E`、`/tests/Fumilume.Tests`）を毎週月曜 09:00（日本時間）に確認し、minor/patch は集約、major は個別 PR にする。
- `.github/workflows/dependabot-auto-merge.yml` は、Dependabot の依存差分だけの PR で、`Dependency validation` が当該 head で成功し、他の check も成功していれば自動マージする。競合や保留は Dependabot の rebase を待つ。
- 配信の `release.yml` も署名・配信より前に同じ単体テストを実行し、失敗したら配信しない。
