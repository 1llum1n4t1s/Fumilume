[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactsPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = (Resolve-Path -LiteralPath $ArtifactsPath).Path
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
    $wrangler = Join-Path $repoRoot ".release-tools\wrangler-$wranglerVersion\node_modules\.bin\wrangler.cmd"
    if (-not (Test-Path -LiteralPath $wrangler)) { throw 'Windowsリリースの事前確認を先に実行してください。' }
    $zone = Invoke-RestMethod 'https://api.cloudflare.com/client/v4/zones?name=kagayoi.com' -Headers $headers -TimeoutSec 30
    if (-not $zone.success -or @($zone.result).Count -ne 1) { throw '公開先のゾーンを一意に確認できません。' }
    $ordered = @($files | Where-Object Name -NE $manifestName) + @($files | Where-Object Name -EQ $manifestName)
    foreach ($file in $ordered) {
        & $wrangler r2 object put "$bucket/$($file.Name)" --file $file.FullName --remote
        if ($LASTEXITCODE -ne 0) { throw "Macアップロード失敗: $($file.Name)" }
    }
    $verify = Join-Path $repoRoot 'local-macos-build\remote-verification'
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
