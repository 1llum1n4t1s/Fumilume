# release-local.ps1 — 署名付き Velopack リリース (Windows)
#
# 署名は Sigilune (Certum SimplySign のクラウド鍵を signtool /dlib で使う CLI) で行う。
# 通常のリリースは GitHub Actions の .github/workflows/release.yml (手動起動・Environment "release") が
# scripts/release-all.ps1 経由で本スクリプトを実行する。ローカル実行も同じ手順で動き、検証や再開に使う。
#
# 前提:
#   - Sigilune を使えること (scripts/Sigilune.Common.ps1 参照)。
#     ローカルは `sigilune setup` で Certum アカウントを登録済み、CI は SIGILUNE_EMAIL / SIGILUNE_TOTP_URI を渡す
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - 環境変数 CLOUDFLARE_API_TOKEN (CI)、なければ D:\Dev\Secret\secrets.json の cloudflare.api_token (ローカル)
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)
#   pwsh scripts/release-local.ps1 -PreflightOnly # 外部ツール・認証・R2 の事前確認のみ
#   pwsh scripts/release-local.ps1 -Runtimes win-x64   # 対象 RID を絞る (テスト用)

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [switch]$PreflightOnly,
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$WorkRoot,
    [string]$RecordRoot
)


# 検証済みの作業対象だけを標準APIで削除する。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'R2Distribution.Common.ps1')
$ReleaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$Bucket = 'fumilume-updates'
$BaseUrl = 'https://fumilume.kagayoi.com'
$ZoneName = 'kagayoi.com'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'D:\Dev\Secret\secrets.json'
# 署名者の CN。証明書の年次更新で thumbprint が変わっても同じ値で照合できる。
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
$RuntimeMatrix = @{
    'win-x64'   = @{ PlatformTarget = 'x64';   Channel = 'win' }
    'win-arm64' = @{ PlatformTarget = 'ARM64'; Channel = 'win-arm64' }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
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
$WorkDir = $WorkRoot
$ArtifactsDir = Join-Path $WorkDir 'packages\windows'
$ArtifactsDir = Assert-ReleasePath $ArtifactsDir $WorkRoot

function Invoke-Native {
    param([string]$Description, [scriptblock]$Block)
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    & $Block
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    Write-Host ("  ⏱️  {0}: {1:N1} 秒" -f $Description, $timer.Elapsed.TotalSeconds)
    if ($exitCode -ne 0) {
        throw "$Description が失敗しました (exit $exitCode)"
    }
}

function Assert-CleanWorkOutputs {
    # ファイル削除は実行担当がPrunlet CLIをCMDから直接行う。スクリプトは残存を拒否する。
    foreach ($name in @('packages\windows', 'publish-win-x64', 'publish-win-arm64')) {
        $outputPath = Assert-ReleasePath (Join-Path $WorkRoot $name) $WorkRoot
        if (-not (Test-Path -LiteralPath $outputPath)) { continue }
        $remaining = @(Get-ChildItem -LiteralPath $outputPath -Recurse -Force -ErrorAction Stop |
            Where-Object { -not $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })
        if ($remaining.Count -gt 0) {
            throw "既存の配布・検証出力があります。今回の対象を確認し、Prunlet CLIで清掃してから同じ作業先で再開してください: $outputPath"
        }
    }
}

Write-Host '== プリフライト ==' -ForegroundColor Cyan
if (-not ${env:ProgramFiles(x86)}) {
    ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)'
}
# 一部の非対話実行環境ではOS環境変数が欠落し、Native AOTがWindows間の
# publishまでCross-OSと誤判定するため、Windows標準値を補完する。
if (-not $env:OS) {
    $env:OS = 'Windows_NT'
}
$vsInstallerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$vsInstallerDir*") {
    $env:PATH = "$env:PATH;$vsInstallerDir"
}
$env:DOTNET_ROLL_FORWARD = 'Major'

$versionNode = ([xml](Get-Content 'Directory.Build.props' -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) { throw 'Directory.Build.props からバージョンを取得できませんでした' }
Write-Host "バージョン: $version"

# 署名の準備 (Sigilune の場所とクラウドへのログインを、ビルド前に確認して早く落とす)。
# -PreflightOnly でもログインまで確認し、署名できない状態を事前確認で検出する。
. (Join-Path $PSScriptRoot 'Sigilune.Common.ps1')
$Sigilune = Resolve-SigiluneExe
Connect-Sigilune -SigiluneExe $Sigilune
# Velopack はファイルごとに別プロセスで署名テンプレートを実行する。Sigilune はログインを共有し、
# 署名後に有効性・署名者・タイムスタンプを検証して失敗なら非0で終わる。
# --skip-signed: vpk が渡す署名済みのファイル（他社製の同梱 DLL など）は有効な署名ならそのまま残す（Sigilune 1.1.0 以降）。
$SignTemplate = "`"$Sigilune`" sign --skip-signed {{file}}"

# Velopack (vpk) は常に最新安定版を使う (ゆろ君ルール): NuGet から実行時に最新を解決して pin する
$VpkVersion = (Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/vpk/index.json' -TimeoutSec 30).versions |
    Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
if (-not $VpkVersion) { throw 'vpk の最新安定版バージョンの取得に失敗しました (NuGet API)' }
Write-Host "vpk 最新安定版: $VpkVersion"

$VpkToolDir = Assert-ReleasePath (Join-Path $WorkRoot "tools\vpk-$VpkVersion") $WorkRoot
$VpkCommand = Join-Path $VpkToolDir 'vpk.exe'
# グローバルツールの更新・削除に影響されない専用配置を使う。
if (-not (Test-Path -LiteralPath $VpkCommand)) {
    Invoke-Native 'vpk のインストール' { dotnet tool install --tool-path $VpkToolDir vpk --version $VpkVersion }
}
Invoke-Native 'vpk の起動確認' { & $VpkCommand --help | Out-Null }
Write-Host "vpk: $VpkVersion"

# Cloudflare トークン (アップロード時のみ必要)
# zone 解決もここで行う: トークンに zone:read / cache purge 権限が無い場合に
# R2 アップロード後の途中失敗 (新ファイルだけ R2 に乗ってパージ・クリーンアップが
# 走らない半端なリリース) を避け、何もアップロードしていない時点で fail fast する
if (-not $SkipUpload) {
    # CI では Environment "release" の Secret が環境変数で渡る。ローカルは secrets.json から必要なキーだけ読む。
    if (-not $env:CLOUDFLARE_API_TOKEN) {
        $secrets = Get-Content $SecretsPath -Raw | ConvertFrom-Json
        if (-not $secrets.cloudflare.api_token) { throw "secrets.json に cloudflare.api_token が見つかりません" }
        $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    }
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId
    $r2Headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
    $bucketResponse = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket" -Headers $r2Headers -TimeoutSec 30 -MaximumRedirection 0
    if (-not $bucketResponse.success -or $bucketResponse.result.name -cne $Bucket) { throw "R2 bucket '$Bucket' の読み取り事前確認に失敗しました" }
    Write-Host "R2 bucket: $Bucket (存在・読み取り権限を確認。書き込みは配布時に確認)"

    $cfHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
    $zoneResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$ZoneName" -Headers $cfHeaders -TimeoutSec 30
    if (-not $zoneResp.success -or @($zoneResp.result).Count -eq 0) { throw "Cloudflare zone '$ZoneName' の取得に失敗しました (トークンの zone:read 権限を確認してください)" }
    $zoneId = $zoneResp.result[0].id
    Write-Host "Cloudflare zone: $ZoneName ($zoneId)"
}

if ($PreflightOnly) {
    $ReleaseStopwatch.Stop()
    Write-Host ("✅ リリース事前確認完了 ({0:N1} 秒)" -f $ReleaseStopwatch.Elapsed.TotalSeconds) -ForegroundColor Green
    return
}

Assert-CleanWorkOutputs
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

$publishPlans = @(
foreach ($runtime in $Runtimes) {
    $config = $RuntimeMatrix[$runtime]
    if (-not $config) { throw "未対応のRuntimeIdentifierです: $runtime" }
    [pscustomobject]@{
        Runtime          = $runtime
        PlatformTarget   = $config.PlatformTarget
        Channel          = $config.Channel
        PublishDir       = Assert-ReleasePath (Join-Path $WorkDir "publish-$runtime") $WorkRoot
        BuildArtifactsDir = Assert-ReleasePath (Join-Path $WorkDir "build-$runtime") $WorkRoot
    }
})

$publishStageTimer = [System.Diagnostics.Stopwatch]::StartNew()
foreach ($plan in $publishPlans) {
    Write-Host "== publish: $($plan.Runtime) ==" -ForegroundColor Cyan
    # VSの版別コンポーネントIDに依存せず、導入済みの標準開発環境を使う。
    $vsWhere = Join-Path $vsInstallerDir 'vswhere.exe'
    $vsPath = (& $vsWhere -latest -products '*' -property installationPath).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $vsPath) { throw 'Visual Studioの導入先を取得できません。' }
    $vcVars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path -LiteralPath $vcVars)) { throw "C++開発環境がありません: $vcVars" }
    $vcArchitecture = if ($plan.Runtime -eq 'win-arm64') { 'amd64_arm64' } else { 'amd64' }
    $nativeEnvironment = & $env:ComSpec /d /c ('call "' + $vcVars + '" ' + $vcArchitecture + ' >nul && set')
    if ($LASTEXITCODE -ne 0) { throw "C++開発環境の初期化に失敗しました: $vcArchitecture" }
    foreach ($nativeVariable in $nativeEnvironment) {
        if ($nativeVariable -match '^(PATH|LIB|LIBPATH|INCLUDE|VCToolsInstallDir|WindowsSdkDir|WindowsSDKVersion|UCRTVersion|UniversalCRTSdkDir|VSINSTALLDIR|VCINSTALLDIR)=(.*)$') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
        }
    }
    if (-not (Get-Command link.exe -ErrorAction SilentlyContinue) -or -not $env:LIB) {
        throw "リンカーまたはライブラリの検索先がありません: $vcArchitecture"
    }
    Invoke-Native "dotnet publish ($($plan.Runtime))" {
        dotnet publish src/Fumilume/Fumilume.csproj -c Release -r $plan.Runtime `
            -p:PlatformTarget=$($plan.PlatformTarget) -p:IlcUseEnvironmentalTools=true -o $plan.PublishDir `
            --artifacts-path $plan.BuildArtifactsDir
    }
}
$publishStageTimer.Stop()
Write-Host ("  ⏱️  publish 全体: {0:N1} 秒" -f $publishStageTimer.Elapsed.TotalSeconds)

foreach ($plan in $publishPlans) {
    if (-not (Test-Path (Join-Path $plan.PublishDir 'Fumilume.exe'))) {
        throw "Fumilume.exe がpublish出力にありません: $($plan.Runtime)"
    }

    # README.txt 生成 (Markdown 記法を簡易除去してプレーンテキスト化)
    $content = Get-Content 'README.md' -Raw -Encoding utf8
    $content = $content -replace '!\[.*?\]\(.*?\)\r?\n?', ''
    $content = $content -replace '<img[^>]*/?>\r?\n?', ''
    $content = $content -replace '\[([^\]]+)\]\(([^\)]+)\)', '$1 ($2)'
    $content = $content -replace '(?m)^#{1,6}\s+', ''
    $content = $content -replace '\*\*([^*]+)\*\*', '$1'
    $content = $content -replace '`([^`]+)`', '$1'
    $content = $content -replace '(?m)^\| .+\|$', ''
    $content = $content -replace '(?m)^\|[-: ]+\|$', ''
    $content = $content -replace '(?m)^>\s*', ''
    $content = $content -replace '\r?\n{3,}', "`n`n"
    [System.IO.File]::WriteAllText((Join-Path $plan.PublishDir 'README.txt'), $content.Trim(), [System.Text.Encoding]::UTF8)

    Write-Host "== vpk pack + 署名: $($plan.Runtime) ==" -ForegroundColor Cyan
    Invoke-Native "vpk pack ($($plan.Runtime))" {
        & $VpkCommand pack `
            --packId Fumilume `
            --packVersion $version `
            --packTitle 'Fumilume' `
            --packAuthors 'Kagayoi' `
            --mainExe Fumilume.exe `
            --icon (Join-Path 'src' 'Fumilume' 'icon' 'app_crystal_warm.ico') `
            --packDir $plan.PublishDir `
            --outputDir $ArtifactsDir `
            --channel $plan.Channel `
            --shortcuts 'StartMenuRoot,Desktop' `
            --signTemplate $SignTemplate
    }
}

Write-Host '== 署名検証 ==' -ForegroundColor Cyan
foreach ($exe in Get-ChildItem $ArtifactsDir -Filter '*.exe') {
    $signature = Get-AuthenticodeSignature $exe.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "署名検証に失敗しました: $($exe.Name) ($($signature.Status))"
    }
    Write-Host "  ✅ $($exe.Name)"
}

Write-Host '== Windows配布実物の検証 ==' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'verify-windows-package.ps1') `
    -PackageRoot $ArtifactsDir -WorkRoot $WorkRoot -RecordRoot $RecordRoot `
    -Version $version -Runtimes $Runtimes -CertificateSubjectName $CertSubjectName

if ($SkipUpload) {
    $ReleaseStopwatch.Stop()
    Write-Host "`n✅ -SkipUpload 指定のためここで終了。成果物: $ArtifactsDir" -ForegroundColor Green
    Write-Host ("⏱️  合計: {0:N1} 秒" -f $ReleaseStopwatch.Elapsed.TotalSeconds)
    Get-ChildItem $ArtifactsDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
}

Write-Host '== R2 アップロード ==' -ForegroundColor Cyan
$uploadTimer = [System.Diagnostics.Stopwatch]::StartNew()
$files = Get-ChildItem $ArtifactsDir -File
$orderedFiles = @($files | Where-Object { $_.Name -notmatch '^(?:releases\..*\.json|assets\..*\.json|RELEASES(?:-.*)?)$' }) +
    @($files | Where-Object { $_.Name -match '^(?:releases\..*\.json|assets\..*\.json|RELEASES(?:-.*)?)$' })
$uploaded = 0
foreach ($file in $orderedFiles) {
    Write-Host "  ↑ $($file.Name)"
    $immutable = $file.Name -match '(?:\.nupkg$|[-_.]\d+\.\d+\.\d+(?:[-_.]|$))'
    $cacheControl = if ($immutable) { 'public, max-age=31536000, immutable' } else { 'no-cache, must-revalidate' }
    $contentType = switch -Regex ($file.Name) { '\.json$' { 'application/json'; break }; '^RELEASES' { 'text/plain'; break }; '\.(zip|nupkg)$' { 'application/zip'; break }; default { 'application/octet-stream' } }
    Publish-R2Object -AccountId $AccountId -Bucket $Bucket -FilePath $file.FullName -Immutable:$immutable -CacheControl $cacheControl -ContentType $contentType | Out-Null
    $uploaded++
}
$uploadTimer.Stop()
Write-Host "✅ R2 アップロード完了: $uploaded ファイル"
Write-Host ("  ⏱️  R2 アップロード全体: {0:N1} 秒" -f $uploadTimer.Elapsed.TotalSeconds)

# ---- Cloudflare エッジキャッシュのパージ ----
# 固定名ファイル (Setup.exe / Portable.zip / RELEASES* / releases.*.json / assets.*.json) は
# 毎リリースで中身が変わるのに URL が不変。CDN エッジが旧版を Cache-Control の max-age 分保持するため、
# パージしないと新規ダウンロード・自動更新が旧バージョンを掴む。バージョン付き nupkg は URL が一意
# (旧キャッシュなし) のためパージ不要。
# R2 アップロードは既に成功済みのため、パージ失敗はリリースを止めず warning-and-continue にする
# (CDN は max-age 経過で自然に新版へ追従する)。
Write-Host '== Cloudflare キャッシュパージ ==' -ForegroundColor Cyan
$purgeTimer = [System.Diagnostics.Stopwatch]::StartNew()
$purgeUrls = @($orderedFiles | Where-Object { $_.Name -notlike '*.nupkg' } | ForEach-Object { "$BaseUrl/$($_.Name)" })
if ($purgeUrls.Count -gt 0) {
    try {
        # purge_cache は 1 リクエストあたり最大 30 URL までのため分割送信する
        for ($i = 0; $i -lt $purgeUrls.Count; $i += 30) {
            $batch = $purgeUrls[$i..[Math]::Min($i + 29, $purgeUrls.Count - 1)]
            $purgeBody = ConvertTo-Json -InputObject @{ files = $batch } -Compress
            $purgeResp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
                -Headers $cfHeaders -ContentType 'application/json' -Body $purgeBody -TimeoutSec 30
            if (-not $purgeResp.success) { throw "Cloudflare キャッシュパージに失敗しました: $($purgeResp.errors | ConvertTo-Json -Compress)" }
        }
        Write-Host "  ✅ パージ: $($purgeUrls.Count) URL"
        $purgeUrls | ForEach-Object { Write-Host "     $_" }
    } catch {
        Write-Warning "  Cloudflare キャッシュパージに失敗しました（アップロード済みリリースには影響なし、max-age 経過で自然反映されます）— $($_.Exception.Message)"
    }
} else {
    Write-Host '  パージ対象なし'
}
$purgeTimer.Stop()
Write-Host ("  ⏱️  キャッシュパージ: {0:N1} 秒" -f $purgeTimer.Elapsed.TotalSeconds)

# ---- 旧バージョン nupkg のクリーンアップ (Aggressive 戦略) ----
# ローカル配布物の manifest (= 今アップロードしたものと同一) から keep set を作り、
# R2 上の「.nupkg かつ manifest 外」だけを削除する。固定ファイル名 (Setup.exe /
# Portable.zip / RELEASES* / releases.*.json / assets.*.json) は対象外なので安全。
Write-Host '== 旧 nupkg クリーンアップ ==' -ForegroundColor Cyan
$cleanupTimer = [System.Diagnostics.Stopwatch]::StartNew()
$keep = @{}
$manifests = Get-ChildItem $ArtifactsDir -Filter 'releases.*.json'
if (-not $manifests) { throw '配布物に releases.*.json が見つかりません' }
foreach ($m in $manifests) {
    foreach ($asset in (Get-Content $m.FullName -Raw | ConvertFrom-Json).Assets) {
        if ($asset.FileName) { $keep[$asset.FileName] = $true }
    }
}
# 別OSの公開フィードが参照する成果物も保持する。Windowsだけの発行でMac更新を壊さない。
foreach ($channel in @('win', 'win-arm64', 'osx-arm64')) {
    if (Test-Path -LiteralPath (Join-Path $ArtifactsDir "releases.$channel.json")) { continue }
    try {
        $publishedFeed = Invoke-RestMethod -Uri "$BaseUrl/releases.$channel.json" -TimeoutSec 30
        foreach ($asset in $publishedFeed.Assets) {
            if ($asset.FileName) { $keep[$asset.FileName] = $true }
        }
    } catch {
        if ($_.Exception.Response.StatusCode -ne 404) { throw }
    }
}
Write-Host "  保持対象 nupkg: $($keep.Count) 件"

$api = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket"
$headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }

$allKeys = [System.Collections.Generic.List[string]]::new()
$cursor = ''
while ($true) {
    $uri = "$api/objects?per_page=1000" + $(if ($cursor) { "&cursor=$cursor" })
    $resp = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
    foreach ($obj in $resp.result) { $allKeys.Add($obj.key) }
    # 全件 1 ページに収まると result_info が省略される (StrictMode 下では直接参照が throw)
    $info = $resp.PSObject.Properties['result_info']
    if (-not $info -or -not $info.Value) { break }
    $truncated = $info.Value.PSObject.Properties['is_truncated']
    if (-not $truncated -or -not $truncated.Value) { break }
    $cursorProp = $info.Value.PSObject.Properties['cursor']
    $cursor = if ($cursorProp) { $cursorProp.Value } else { '' }
    if (-not $cursor) { break }
}

# 全プロジェクト共通の保持ポリシー: 直近 2 バージョン。
# 旧実装は '*.nupkg' だけを削除対象にしていたため、バージョン付きの配布物
# (zip / deb / rpm / AppImage 等) が R2 に永久に溜まっていた (Ferry で 351 個 7.2GB)。
$KeepVersionCount = 2
$versionPattern = '(\d+\.\d+\.\d+)'
$allVersions = @(
    $allKeys | ForEach-Object {
        $m = [regex]::Match($_, $versionPattern)
        if ($m.Success) { $m.Groups[1].Value }
    } | Sort-Object -Property { [version]$_ } -Unique
)
$keepVersions = @($allVersions | Select-Object -Last $KeepVersionCount)
Write-Host "  保持バージョン: $($keepVersions -join ', ') (全 $($allVersions.Count) 世代)"

$toDelete = $allKeys | Where-Object {
    # manifest が参照するファイルは絶対保持 (消すと自動更新が壊れる)
    if ($keep.ContainsKey($_)) { return $false }
    # 固定ファイル名はバージョン文字列を含まない = 毎リリース上書きなので保持
    $m = [regex]::Match($_, $versionPattern)
    if (-not $m.Success) { return $false }
    return $keepVersions -notcontains $m.Groups[1].Value
}
if (-not $toDelete) {
    Write-Host '  ✅ 削除対象なし'
} else {
    $deleted = 0; $failed = 0
    foreach ($key in $toDelete) {
        $encoded = [uri]::EscapeDataString($key)
        try {
            Invoke-RestMethod -Method Delete -Uri "$api/objects/$encoded" -Headers $headers -TimeoutSec 30 | Out-Null
            Write-Host "  🗑️  $key"
            $deleted++
        } catch {
            Write-Warning "  削除失敗: $key — $($_.Exception.Message)"
            $failed++
        }
    }
    Write-Host "  🧹 クリーンアップ: $deleted 削除 / $failed 失敗"
    # 全件失敗は token 権限等の異常なので fail (一部失敗は次回リリースで再試行される)
    if ($failed -gt 0 -and $deleted -eq 0) { throw '旧 nupkg の削除がすべて失敗しました。API token の権限を確認してください。' }
}
$cleanupTimer.Stop()
Write-Host ("  ⏱️  旧成果物クリーンアップ: {0:N1} 秒" -f $cleanupTimer.Elapsed.TotalSeconds)

$ReleaseStopwatch.Stop()
Write-Host "`n🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
Write-Host ("⏱️  合計: {0:N1} 秒" -f $ReleaseStopwatch.Elapsed.TotalSeconds)
