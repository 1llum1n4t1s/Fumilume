# 署名・pack後の配布実物を検証する。失敗時は呼出元のuploadへ進ませない。
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$RecordRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateCount(1, 2)][ValidateSet('win-x64', 'win-arm64')][string[]]$Runtimes,
    [Parameter(Mandatory)][string]$CertificateSubjectName
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SafePath([string]$Path, [string]$Root) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "絶対パスが必要です: $Path" }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "保存先の範囲外です: $full" }
    if (@($full -split '[\\/]' | Where-Object { $_ -ieq 'artifacts' }).Count) { throw "禁止されたフォルダー名です: $full" }
    $cursor = $full
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "リンク経由のパスは使用できません: $cursor"
        }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}

function Get-FileEvidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{ Path = $item.FullName; Size = $item.Length; SHA256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

function Get-SignedExecutable([string]$Path, [string]$Machine, [switch]$Application) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "CN=$CertificateSubjectName*") {
        throw "署名検証に失敗しました: $Path ($($signature.Status))"
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $fileVersion = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"
    $productVersion = ($info.ProductVersion -split '\+')[0]
    if ($Application -and ($fileVersion -ne $Version -or $productVersion -notin @($Version, "$Version.0"))) {
        throw "配布実行ファイルの版が一致しません: $Path ($($info.FileVersion), $($info.ProductVersion))"
    }
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "MZヘッダーがありません: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt $stream.Length - 256) { throw "PEオフセットが不正です: $Path" }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "PEヘッダーがありません: $Path" }
        $actualMachine = '{0:X4}' -f $reader.ReadUInt16()
        $stream.Position = $peOffset + 24
        $magic = $reader.ReadUInt16()
        if ($magic -notin @(0x10B, 0x20B)) { throw "未対応のPE形式です: $Path" }
        $directoriesOffset = $peOffset + 24 + $(if ($magic -eq 0x20B) { 112 } else { 96 })
        $stream.Position = $directoriesOffset + 14 * 8
        $clrRva = $reader.ReadUInt32()
        $clrSize = $reader.ReadUInt32()
        if ($Application -and ($actualMachine -ne $Machine -or $magic -ne 0x20B -or $clrRva -ne 0 -or $clrSize -ne 0)) {
            throw "RID/Native AOT検証に失敗しました: $Path (machine=$actualMachine, CLR=$clrRva/$clrSize)"
        }
    } finally { $reader.Dispose() }
    $evidence = Get-FileEvidence $Path
    $evidence.FileVersion = $info.FileVersion
    $evidence.ProductVersion = $info.ProductVersion
    $evidence.Machine = $actualMachine
    $evidence.ClrHeaderRva = $clrRva
    $evidence.ClrHeaderSize = $clrSize
    $evidence.Signature = [ordered]@{ Status = [string]$signature.Status; Subject = $signature.SignerCertificate.Subject; Thumbprint = $signature.SignerCertificate.Thumbprint }
    return $evidence
}

function Expand-CheckedZip([string]$Path, [string]$Destination) {
    $Destination = Assert-SafePath $Destination $verificationRoot
    # 再実行でも古い展開内容を混ぜず、削除を必要としない固有ディレクトリを使う。
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            if ($relative.Contains(':') -or [IO.Path]::IsPathRooted($relative)) { throw "ZIP内のパスが不正です: $relative" }
            $target = Assert-SafePath (Join-Path $Destination $relative) $Destination
            if (-not $seen.Add($target)) { throw "ZIP内に重複パスがあります: $relative" }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "ZIP内のリンクは使用できません: $relative" }
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $Destination)
    } finally { $archive.Dispose() }
    return $Destination
}

$WorkRoot = Assert-SafePath $WorkRoot 'D:\Codex\Fumilume'
$RecordRoot = Assert-SafePath $RecordRoot 'D:\Codex\TaskArtifacts\Fumilume'
$PackageRoot = Assert-SafePath $PackageRoot $WorkRoot
$verificationRoot = Assert-SafePath (Join-Path $WorkRoot 'verification\windows-package') $WorkRoot
$runId = [Guid]::NewGuid().ToString('N')
$recordPath = Assert-SafePath (Join-Path $RecordRoot "windows-package-$runId.json") $RecordRoot
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceHead = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw '候補source HEADを取得できません' }
$report = [ordered]@{
    SourceHEAD = [string]$sourceHead; Version = $Version; Configuration = 'Release'; CheckedAtUtc = [DateTime]::UtcNow.ToString('o')
    Status = 'InProgress'; Packages = @(); Unexecuted = @('Windows x64/ARM64配布物の起動・実画面操作・終了。このゲートは静的検証のみ。')
}
try {
    foreach ($runtime in $Runtimes) {
        $channel, $machine = switch ($runtime) {
            'win-x64' { 'win'; '8664' }
            'win-arm64' { 'win-arm64'; 'AA64' }
            default { throw "未対応のRIDです: $runtime" }
        }
        $feedPath = Assert-SafePath (Join-Path $PackageRoot "releases.$channel.json") $PackageRoot
        $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
        $assets = @($feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version -and $_.PackageId -eq 'Fumilume' })
        if ($assets.Count -ne 1) { throw "Full assetを一意に特定できません: $feedPath" }
        $asset = $assets[0]
        if ([IO.Path]::GetFileName($asset.FileName) -cne $asset.FileName -or $asset.FileName -notlike '*.nupkg') { throw 'Full assetのファイル名が不正です' }
        $packagePath = Assert-SafePath (Join-Path $PackageRoot $asset.FileName) $PackageRoot
        $package = Get-FileEvidence $packagePath
        if ($package.Size -ne $asset.Size -or $package.SHA256 -ine $asset.SHA256) { throw "フィードのsize/SHA256が一致しません: $packagePath" }
        $sha1 = $asset.PSObject.Properties['SHA1']
        if ($sha1 -and $sha1.Value -and (Get-FileHash -LiteralPath $packagePath -Algorithm SHA1).Hash -ine $sha1.Value) { throw "フィードのSHA1が一致しません: $packagePath" }
        $packageDir = Expand-CheckedZip $packagePath (Join-Path $verificationRoot "$runId\$runtime\nupkg")
        $nuspecs = @(Get-ChildItem -LiteralPath $packageDir -Filter '*.nuspec' -File)
        if ($nuspecs.Count -ne 1) { throw 'nupkgのnuspecが一意ではありません' }
        [xml]$nuspec = Get-Content -LiteralPath $nuspecs[0].FullName -Raw
        $id = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="id"]').InnerText
        $packageVersion = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="version"]').InnerText
        if ($id -ne 'Fumilume' -or $packageVersion -ne $Version) { throw "nupkgのID/版が不一致です: $id/$packageVersion" }
        $packageExecutables = @(Get-ChildItem -LiteralPath (Join-Path $packageDir 'lib') -Filter Fumilume.exe -File -Recurse)
        if ($packageExecutables.Count -ne 1) { throw 'nupkgのFumilume.exeが一意ではありません' }
        $portablePath = Assert-SafePath (Join-Path $PackageRoot "Fumilume-$channel-Portable.zip") $PackageRoot
        $portableDir = Expand-CheckedZip $portablePath (Join-Path $verificationRoot "$runId\$runtime\portable")
        # PortableのルートはVelopack launcher、current配下が配布アプリ本体。
        $portableExecutablePath = Assert-SafePath (Join-Path $portableDir 'current\Fumilume.exe') $portableDir
        $portableExecutable = Get-Item -LiteralPath $portableExecutablePath
        $launcherPath = Assert-SafePath (Join-Path $portableDir 'Fumilume.exe') $portableDir
        $launcher = Get-SignedExecutable $launcherPath '014C'
        if ($launcher.Machine -ne '014C' -or ($launcher.ProductVersion -split '\+')[0] -notin @($Version, "$Version.0")) {
            throw "Portable launcherの版/PE machineが不一致です: $launcherPath"
        }
        $updaterPath = Assert-SafePath (Join-Path $portableDir 'Update.exe') $portableDir
        $updater = Get-SignedExecutable $updaterPath '014C'
        if ($updater.Machine -ne '014C') { throw "Portable updaterのPE machineが不一致です: $updaterPath" }
        $payloads = @()
        foreach ($exe in @($packageExecutables[0], $portableExecutable)) {
            $payloadRoot = $exe.DirectoryName
            foreach ($required in @('README.txt', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'Assets\Fonts\LICENSE-FLUENT-SYSTEM-ICONS.txt', 'Assets\Fonts\LICENSE-IBM-PLEX.txt', 'Assets\Fonts\LICENSE-UDEV-GOTHIC.txt')) {
                $requiredPath = Assert-SafePath (Join-Path $payloadRoot $required) $payloadRoot
                if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -eq 0) { throw "必須ファイルがありません: $requiredPath" }
            }
            $payloads += Get-SignedExecutable $exe.FullName $machine -Application
        }
        if ($payloads[0].SHA256 -ne $payloads[1].SHA256) { throw "nupkg/PortableのFumilume.exeが一致しません: $runtime" }
        $installerPath = Assert-SafePath (Join-Path $PackageRoot "Fumilume-$channel-Setup.exe") $PackageRoot
        $installer = Get-SignedExecutable $installerPath $machine
        if (($installer.ProductVersion -split '\+')[0] -notin @($Version, "$Version.0") -and $installer.FileVersion -notin @($Version, "$Version.0")) {
            throw "Setupの版をreleaseと照合できません: $installerPath ($($installer.FileVersion), $($installer.ProductVersion))"
        }
        $report.Packages += [ordered]@{ Runtime = $runtime; Channel = $channel; Feed = (Get-FileEvidence $feedPath); Package = $package; PackageId = $id; PackageVersion = $packageVersion; Payloads = $payloads; Portable = (Get-FileEvidence $portablePath); PortableLauncher = $launcher; PortableUpdater = $updater; Installer = $installer }
        Write-Host "  ✅ 配布物検証: $runtime / $Version"
    }
    $report.Status = 'Passed'
} catch {
    $report.Status = 'Failed'
    $report.Error = $_.Exception.Message
    throw
} finally {
    [IO.Directory]::CreateDirectory($RecordRoot) | Out-Null
    [IO.File]::WriteAllText($recordPath, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "配布物検証記録: $recordPath"
}
