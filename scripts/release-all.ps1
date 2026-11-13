[CmdletBinding()]
param([string]$WorkRoot, [string]$RecordRoot, [string]$StatePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'R2Artifact.Common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
# 出力先の安全検査（Assert-ReleasePath）は 3 つのリリーススクリプトで共通の実装を使う。
. (Join-Path $PSScriptRoot 'ReleasePath.Common.ps1')
$releaseTaskId = 'release-' + [Guid]::NewGuid().ToString('N')
# 既定の作業先・記録先は OS の一時フォルダー（[IO.Path]::GetTempPath()）配下に置く。
# C ドライブになっても構わない。明示指定の -WorkRoot / -RecordRoot は場所を問わず、
# Assert-ReleasePath の場所に依存しない安全検査（リポジトリ外・リンク拒否など）だけを通す。
$tempReleaseRoot = Join-Path ([IO.Path]::GetTempPath()) 'Fumilume'
$recordReleaseRoot = Join-Path ([IO.Path]::GetTempPath()) 'TaskArtifacts\Fumilume'
if (-not $WorkRoot) { $WorkRoot = Join-Path $tempReleaseRoot $releaseTaskId }
if (-not $RecordRoot) { $RecordRoot = Join-Path $recordReleaseRoot $releaseTaskId }
$WorkRoot = Assert-ReleasePath $WorkRoot
$RecordRoot = Assert-ReleasePath $RecordRoot
Write-Host "作業先: $WorkRoot"
Write-Host "再開・清掃記録: $RecordRoot (再開時も同じ -WorkRoot / -RecordRoot を指定)"
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
$download = Join-Path $WorkRoot "packages\mac-ci-$($run.id)-$attemptId"
$evidence = Join-Path $WorkRoot "verification\mac-ci-$($run.id)-$attemptId"
$download = Assert-ReleasePath $download $WorkRoot
$evidence = Assert-ReleasePath $evidence $WorkRoot
if (-not $StatePath) { $StatePath = Join-Path $RecordRoot "release-$head.json" }
$StatePath = Assert-ReleasePath $StatePath $RecordRoot
New-Item -ItemType Directory -Path (Split-Path -Parent $StatePath) -Force | Out-Null
$state = @{head=$head;runId=$run.id;workRoot=$WorkRoot;windowsPublished=$false;macPublished=$false}
if (Test-Path -LiteralPath $statePath) {
    $saved = Get-Content $statePath -Raw | ConvertFrom-Json
    if ($saved.head -ne $head -or $saved.runId -ne $run.id -or $saved.workRoot -ne $WorkRoot) { throw '再開記録とCI入力・作業先が一致しません。' }
    $state.windowsPublished = $saved.windowsPublished
    $state.macPublished = $saved.macPublished
}
# 成功した単一 Mac job の commit/run/attempt を固定し、新しい専用ディレクトリへ取得する。
$artifactToken = $env:CLOUDFLARE_API_TOKEN
if (-not $artifactToken) { $artifactToken = (Get-Content 'D:\Dev\Secret\secrets.json' -Raw | ConvertFrom-Json).cloudflare.api_token }
$artifactIdentity = @{
    AccountId = '10901bfadbf1005164774a7350082985'
    Bucket = 'fumilume-builds'
    Repository = $repository
    Commit = [string]$run.head_sha
    RunId = [string]$run.id
    RunAttempt = [string]$run.run_attempt
    ApiToken = $artifactToken
    AccessKeyId = ''
    SecretAccessKey = ''
}
try {
    Receive-R2Artifact @artifactIdentity -Name 'velopack.osx-arm64' -Destination $download | Out-Null
    Receive-R2Artifact @artifactIdentity -Name 'verification.osx-arm64' -Destination $evidence | Out-Null
} finally { $artifactToken = $null; $artifactIdentity = $null }
$checksum = @(Get-ChildItem -LiteralPath $evidence -Recurse -Filter SHA256SUMS)
if ($checksum.Count -ne 1) { throw 'Mac CIのハッシュ記録を特定できません。' }
foreach ($line in Get-Content $checksum[0].FullName) {
    if ($line -notmatch '^([a-fA-F0-9]{64})\s+\./([^/\\]+)$') { throw '不正なCIハッシュ記録。' }
    if ((Get-FileHash (Join-Path $download $Matches[2]) -Algorithm SHA256).Hash -ne $Matches[1]) { throw 'CI成果物のSHA256不一致。' }
}
if (-not $state.windowsPublished) {
    & (Join-Path $PSScriptRoot 'release-local.ps1') -WorkRoot $WorkRoot -RecordRoot $RecordRoot
    $state.windowsPublished = $true
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
if (-not $state.macPublished) {
    & (Join-Path $PSScriptRoot 'publish-macos.ps1') -ArtifactsPath $download -WorkRoot $WorkRoot -RecordRoot $RecordRoot
    $state.macPublished = $true
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
Write-Host "Windows / Apple Silicon Mac リリース完了: source=$head, Mac CI=$($run.html_url)"
