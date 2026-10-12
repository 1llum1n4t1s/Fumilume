[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactsPath, [string]$WorkRoot, [string]$RecordRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
# 出力先は作成前に検証し、リンク経由のリポジトリ内保存も拒否する。
function Assert-ReleasePath {
    param([string]$Path, [string]$AllowedRoot)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "絶対パスを指定してください: $Path" }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $allowed = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $full.StartsWith($allowed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "許可された保存先の配下を指定してください: $full (保存先: $allowed)"
    }
    $codexRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    foreach ($forbidden in @($repoRoot, (Join-Path $env:USERPROFILE '.codex'), $codexRoot)) {
        $boundary = [IO.Path]::GetFullPath($forbidden).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ($full.Equals($boundary, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "禁止された保存先です: $full"
        }
    }
    if (@($full -split '[\\/]' | Where-Object { $_ -ieq 'artifacts' }).Count -gt 0) { throw "artifacts フォルダーは使用できません: $full" }
    $cursor = $full
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "リンクを含む保存先は使用できません: $cursor"
        }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}
$releaseTaskId = 'release-' + [Guid]::NewGuid().ToString('N')
$tempReleaseRoot = Join-Path ([IO.Path]::GetTempPath()) 'Codex\Fumilume'
$recordReleaseRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Codex\TaskArtifacts\Fumilume'
if (-not $WorkRoot) { $WorkRoot = Join-Path $tempReleaseRoot $releaseTaskId }
if (-not $RecordRoot) { $RecordRoot = Join-Path $recordReleaseRoot $releaseTaskId }
$WorkRoot = Assert-ReleasePath $WorkRoot $tempReleaseRoot
$RecordRoot = Assert-ReleasePath $RecordRoot $recordReleaseRoot
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
    $env:CLOUDFLARE_API_TOKEN = (Get-Content 'C:\Users\IMT\dev\Secret\secrets.json' -Raw | ConvertFrom-Json).cloudflare.api_token
    $env:CLOUDFLARE_ACCOUNT_ID = '10901bfadbf1005164774a7350082985'
    $headers = @{Authorization="Bearer $env:CLOUDFLARE_API_TOKEN"}
    $wranglerVersion = (Import-PowerShellDataFile (Join-Path $PSScriptRoot 'release-tools.psd1')).WranglerVersion
    $wrangler = Assert-ReleasePath (Join-Path $WorkRoot "tools\wrangler-$wranglerVersion\node_modules\.bin\wrangler.cmd") $WorkRoot
    if (-not (Test-Path -LiteralPath $wrangler)) { throw 'Windowsリリースの事前確認を先に実行してください。' }
    $zone = Invoke-RestMethod 'https://api.cloudflare.com/client/v4/zones?name=kagayoi.com' -Headers $headers -TimeoutSec 30
    if (-not $zone.success -or @($zone.result).Count -ne 1) { throw '公開先のゾーンを一意に確認できません。' }
    $ordered = @($files | Where-Object Name -NE $manifestName) + @($files | Where-Object Name -EQ $manifestName)
    foreach ($file in $ordered) {
        & $wrangler r2 object put "$bucket/$($file.Name)" --file $file.FullName --remote
        if ($LASTEXITCODE -ne 0) { throw "Macアップロード失敗: $($file.Name)" }
    }
    $verify = Assert-ReleasePath (Join-Path $WorkRoot 'verification\remote-macos') $WorkRoot
    New-Item -ItemType Directory -Path $verify -Force | Out-Null
    $stale = @()
    foreach ($file in $ordered) {
        $destination = Join-Path $verify $file.Name
        & curl.exe --fail --silent --show-error --location --retry 2 --connect-timeout 20 --max-time 180 --output $destination "$baseUrl/$($file.Name)?verify=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
        if ($LASTEXITCODE -ne 0) { throw "Mac公開物の取得失敗: $($file.Name)" }
        if ((Get-FileHash $destination -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash) {
            $stale += $file
            continue
        }
        # 更新クライアントが使う固定URLも照合する。query別キャッシュの旧版・404も検出する。
        & curl.exe --silent --show-error --location --retry 2 --connect-timeout 20 --max-time 180 --output $destination "$baseUrl/$($file.Name)"
        if ($LASTEXITCODE -ne 0) { throw "Mac固定URLの取得失敗: $($file.Name)" }
        if ((Get-FileHash $destination -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash) { $stale += $file }
    }
    if ($stale.Count -gt 0) {
        $body = @{files=@($stale | ForEach-Object { "$baseUrl/$($_.Name)" })} | ConvertTo-Json -Compress
        $purge = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$($zone.result[0].id)/purge_cache" -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 30
        if (-not $purge.success) { throw 'Mac配信のキャッシュ更新失敗。' }
        foreach ($file in $stale) {
            $destination = Join-Path $verify $file.Name
            foreach ($url in @("$baseUrl/$($file.Name)?verify=$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())", "$baseUrl/$($file.Name)")) {
                & curl.exe --fail --silent --show-error --location --retry 2 --max-time 180 --output $destination $url
                if ($LASTEXITCODE -ne 0 -or (Get-FileHash $destination -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash) { throw "Mac公開ハッシュ不一致: $($file.Name)" }
            }
        }
    }
    Write-Host "Mac v$version 配信確認完了: $($files.Count)ファイルのSHA256一致"
} finally {
    $env:CLOUDFLARE_API_TOKEN = $previousToken
    $env:CLOUDFLARE_ACCOUNT_ID = $previousAccount
}
