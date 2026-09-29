[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$repository = '1llum1n4t1s/Fumilume'
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$releaseBranch = "release/$version"
$head = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Git HEADを取得できません。' }
$dirty = @(& git status --porcelain --untracked-files=all)
if ($dirty.Count -gt 0) { throw '検証済みソースをcommit/pushしてからリリースしてください。' }
function Get-MacRun {
    $response = (& gh api "repos/$repository/actions/workflows/macos-release.yml/runs?head_sha=$head&per_page=100" | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Mac CIの状態を取得できません。' }
    return $response.workflow_runs | Sort-Object created_at -Descending | Select-Object -First 1
}
$run = Get-MacRun
if (-not $run) {
    $remoteHead = (& gh api "repos/$repository/git/ref/heads/main" | ConvertFrom-Json).object.sha
    if ($LASTEXITCODE -ne 0 -or $remoteHead -ne $head) { throw '公開済みmainとローカルHEADが一致しません。' }
    $releaseHead = (& gh api "repos/$repository/git/ref/heads/$releaseBranch" | ConvertFrom-Json).object.sha
    if ($LASTEXITCODE -ne 0 -or $releaseHead -ne $head) { throw 'リリースブランチと検証対象HEADが一致しません。' }
    & gh workflow run macos-release.yml --repo $repository --ref $releaseBranch
    if ($LASTEXITCODE -ne 0) { throw 'Mac CIの開始に失敗しました。' }
    Start-Sleep -Seconds 5
    $run = Get-MacRun
    if (-not $run) { throw 'Mac CIの実行を特定できません。' }
}
if ($run.status -ne 'completed') {
    & gh run watch $run.id --repo $repository --compact --exit-status
    if ($LASTEXITCODE -ne 0) { throw "Mac CIが成功しませんでした: $($run.html_url)" }
    $run = (& gh api "repos/$repository/actions/runs/$($run.id)" | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Mac CIの取得失敗。' }
}
if ($run.conclusion -ne 'success' -or $run.head_sha -ne $head) { throw "Mac署名・公証・E2E検証が未完了: $($run.html_url)" }
$attemptId = [Guid]::NewGuid().ToString('N')
$download = Join-Path $repoRoot "local-macos-build\ci-$($run.id)-$attemptId"
$evidence = "$download-verification"
$statePath = Join-Path $repoRoot "local-macos-build\release-$head.json"
$state = @{head=$head;runId=$run.id;windowsPublished=$false;macPublished=$false}
if (Test-Path -LiteralPath $statePath) {
    $saved = Get-Content $statePath -Raw | ConvertFrom-Json
    if ($saved.head -ne $head -or $saved.runId -ne $run.id) { throw '再開記録とCI入力が一致しません。' }
    $state.windowsPublished = $saved.windowsPublished
    $state.macPublished = $saved.macPublished
}
# ghは既存ファイルを上書きしないため、再取得は新しい専用ディレクトリへ行う。
& gh run download $run.id --repo $repository --name velopack.osx-arm64 --dir $download
if ($LASTEXITCODE -ne 0) { throw '検証済みMac成果物の取得失敗。' }
& gh run download $run.id --repo $repository --name verification.osx-arm64 --dir $evidence
if ($LASTEXITCODE -ne 0) { throw 'Mac検証記録の取得失敗。' }
$checksum = @(Get-ChildItem -LiteralPath $evidence -Recurse -Filter SHA256SUMS)
if ($checksum.Count -ne 1) { throw 'Mac CIのハッシュ記録を特定できません。' }
foreach ($line in Get-Content $checksum[0].FullName) {
    if ($line -notmatch '^([a-fA-F0-9]{64})\s+\./([^/\\]+)$') { throw '不正なCIハッシュ記録。' }
    if ((Get-FileHash (Join-Path $download $Matches[2]) -Algorithm SHA256).Hash -ne $Matches[1]) { throw 'CI成果物のSHA256不一致。' }
}
if (-not $state.windowsPublished) {
    & (Join-Path $PSScriptRoot 'release-local.ps1')
    $state.windowsPublished = $true
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
if (-not $state.macPublished) {
    & (Join-Path $PSScriptRoot 'publish-macos.ps1') -ArtifactsPath $download
    $state.macPublished = $true
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
Write-Host "Windows / Apple Silicon Mac リリース完了: source=$head, Mac CI=$($run.html_url)"
