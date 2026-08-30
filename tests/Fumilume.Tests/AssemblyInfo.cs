// 設定の置き場（AppStoragePaths）はプロセス全体で 1 つの静的値を差し替える仕組みのため、
// テストを並列に走らせると隔離が崩れ、実ユーザーの %LocalAppData%\Fumilume\settings.json へ
// 書き込んでしまう。Avalonia のヘッドレス UI スレッドも 1 本しか立てられないので、
// このアセンブリは直列実行に固定する（全体で 1 秒程度なので実害は無い）。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
