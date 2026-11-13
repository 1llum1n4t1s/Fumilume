[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactsPath, [string]$WorkRoot, [string]$RecordRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'R2Distribution.Common.ps1')
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
$artifacts = Assert-ReleasePath ((Resolve-Path -LiteralPath $ArtifactsPath).Path) $WorkRoot
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$baseUrl = 'https://fumilume.kagayoi.com'
$bucket = 'fumilume-updates'
$manifestName = 'releases.osx-arm64.json'
$manifest = Get-Content (Join-Path $artifacts $manifestName) -Raw | ConvertFrom-Json
$asset = @($manifest.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $version })
if ($asset.Count -ne 1 -or $manifest.Assets.Count -ne 1) { throw 'Mac更新フィードの版と成果物が一致しません。' }
$files = @(Get-ChildItem -LiteralPath $artifacts -File)
foreach ($file in $files) {
    if ($file.Name -notmatch '^(Fumilume-.+-osx-arm64-full\.nupkg|Fumilume-osx-arm64-(Setup\.pkg|Portable\.zip)|(?:releases|assets)\.osx-arm64\.json|RELEASES-osx-arm64)$') {
        throw "未許可のMac成果物: $($file.Name)"
    }
}
foreach ($required in @($manifestName,$asset[0].FileName,'Fumilume-osx-arm64-Setup.pkg','Fumilume-osx-arm64-Portable.zip')) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifacts $required))) { throw "Mac成果物なし: $required" }
}
$package = Get-Item -LiteralPath (Join-Path $artifacts $asset[0].FileName)
if ($package.Length -ne $asset[0].Size -or (Get-FileHash $package.FullName -Algorithm SHA256).Hash -ne $asset[0].SHA256) { throw 'Mac更新パッケージのハッシュが不一致です。' }
$previousToken = $env:CLOUDFLARE_API_TOKEN
$previousAccount = $env:CLOUDFLARE_ACCOUNT_ID
try {
    # CI (release.yml) では Environment "release" の Secret が環境変数で渡る。ローカルは secrets.json から必要なキーだけ読む。
    if (-not $env:CLOUDFLARE_API_TOKEN) {
        $env:CLOUDFLARE_API_TOKEN = (Get-Content 'D:\Dev\Secret\secrets.json' -Raw | ConvertFrom-Json).cloudflare.api_token
    }
    if (-not $env:CLOUDFLARE_API_TOKEN) { throw 'Cloudflare の API トークンがありません。' }
    $env:CLOUDFLARE_ACCOUNT_ID = '10901bfadbf1005164774a7350082985'
    $headers = @{Authorization="Bearer $env:CLOUDFLARE_API_TOKEN"}
    $zone = Invoke-RestMethod 'https://api.cloudflare.com/client/v4/zones?name=kagayoi.com' -Headers $headers -TimeoutSec 30
    if (-not $zone.success -or @($zone.result).Count -ne 1) { throw '公開先のゾーンを一意に確認できません。' }
    $ordered = @($files | Where-Object { $_.Name -notmatch '^(?:releases\..*\.json|assets\..*\.json|RELEASES(?:-.*)?)$' }) +
        @($files | Where-Object { $_.Name -match '^(?:releases\..*\.json|assets\..*\.json|RELEASES(?:-.*)?)$' })
    foreach ($file in $ordered) {
        $immutable = $file.Name -match '(?:\.nupkg$|[-_.]\d+\.\d+\.\d+(?:[-_.]|$))'
        $cacheControl = if ($immutable) { 'public, max-age=31536000, immutable' } else { 'no-cache, must-revalidate' }
        $contentType = switch -Regex ($file.Name) { '\.json$' { 'application/json'; break }; '^RELEASES' { 'text/plain'; break }; '\.(zip|nupkg)$' { 'application/zip'; break }; default { 'application/octet-stream' } }
        Publish-R2Object -AccountId $env:CLOUDFLARE_ACCOUNT_ID -Bucket $bucket -FilePath $file.FullName -Immutable:$immutable -CacheControl $cacheControl -ContentType $contentType | Out-Null
    }
    $fixedFiles = @($ordered | Where-Object { $_.Name -notmatch '(?:\.nupkg$|[-_.]\d+\.\d+\.\d+(?:[-_.]|$))' })
    if ($fixedFiles.Count -gt 0) {
        $body = @{files=@($fixedFiles | ForEach-Object { "$baseUrl/$($_.Name)" })} | ConvertTo-Json -Compress
        $purge = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$($zone.result[0].id)/purge_cache" -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if (-not $purge.success) { throw 'Mac配信のキャッシュ更新失敗。' }
    }
    Write-Host "Mac v$version アップロード完了: $($files.Count)ファイル、固定URLのキャッシュ削除要求は受理済み"
} finally {
    $env:CLOUDFLARE_API_TOKEN = $previousToken
    $env:CLOUDFLARE_ACCOUNT_ID = $previousAccount
}
