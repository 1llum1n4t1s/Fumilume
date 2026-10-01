param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'opop/bracket-comparison.json'),
    [string]$BeforeRevision = '929f9de3cf176adeab27d19253aaea4b1226e796'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
# 比較元はopop作業開始時のcommitへ固定し、リリース後も同じ結果を再現できるようにする。
$before = (& git -C $repoRoot show "${BeforeRevision}:src/Fumilume/Services/BracketPairService.cs") -join "`n"
if ($LASTEXITCODE -ne 0) { throw '比較元のソースを取得できませんでした。' }
$after = Get-Content (Join-Path $repoRoot 'src/Fumilume/Services/BracketPairService.cs') -Raw
$usings = "#nullable enable`nusing System;`nusing System.IO;`nusing System.Collections.Generic;`nusing System.Linq;`n"
Add-Type ($usings + $before.Replace('namespace Fumilume.Services;', 'namespace OpopBefore;'))
Add-Type ($usings + $after.Replace('namespace Fumilume.Services;', 'namespace OpopAfter;'))
$beforeType = [AppDomain]::CurrentDomain.GetAssemblies().GetTypes() | Where-Object FullName -EQ 'OpopBefore.BracketPairService'
$afterType = [AppDomain]::CurrentDomain.GetAssemblies().GetTypes() | Where-Object FullName -EQ 'OpopAfter.BracketPairService'
$beforeMethod = $beforeType.GetMethod('Analyze')
$afterMethod = $afterType.GetMethod('Analyze')
$beforeLanguage = [Enum]::Parse($beforeMethod.GetParameters()[1].ParameterType, 'CSharp')
$afterLanguage = [Enum]::Parse($afterMethod.GetParameters()[1].ParameterType, 'CSharp')
function Get-Tokens([Reflection.MethodInfo]$method, [object]$language, [string]$text) {
    $result = $method.Invoke($null, @($text, $language))
    return (($result.Tokens | ForEach-Object { "$($_.Offset):$($_.Character):$($_.Depth):$($_.PairOffset)" }) -join ',')
}
$samples = [Collections.Generic.List[string]]::new()
foreach ($prefix in @('$','$$','$$$','@$','$$@','@$$@')) {
    foreach ($body in @('"{F("x")} ()" []','"unfinished {F("x")','"""{[()]}""" ()','"a""{F("x")}b" ()')) {
        $samples.Add($prefix + $body)
    }
}
foreach ($count in 3..15) {
    foreach ($run in (($count - 1),$count,($count + 1))) {
        foreach ($prefix in @('','$$')) {
            $samples.Add($prefix + ('"' * $count) + 'x{' + ('"' * $run) + 'x}[]')
        }
    }
}
$random = [Random]::new(42001)
$alphabet = '$@"{}()[]\/' + "`r`n" + "'*x"
for ($sample = 0; $sample -lt 10000; $sample++) {
    $characters = [char[]]::new($random.Next(1,80))
    for ($index = 0; $index -lt $characters.Length; $index++) {
        $characters[$index] = $alphabet[$random.Next($alphabet.Length)]
    }
    $samples.Add([string]::new($characters))
}
foreach ($text in $samples) {
    $oldTokens = Get-Tokens $beforeMethod $beforeLanguage $text
    $newTokens = Get-Tokens $afterMethod $afterLanguage $text
    if ($oldTokens -cne $newTokens) { throw "括弧解析の結果が変わりました: $text / $oldTokens / $newTokens" }
}
$timings = foreach ($kind in @('Dollars','RawQuotes','InterpolatedRawQuotes')) {
    foreach ($length in @(2000,4000,8000,16000)) {
        $text = switch ($kind) {
            'Dollars' { ('$' * $length) + ' ()' }
            'RawQuotes' { ('"' * $length) + 'x' + ('"' * ($length - 1)) + 'x' }
            'InterpolatedRawQuotes' { '$$' + ('"' * $length) + 'x' + ('"' * ($length - 1)) + 'x' }
        }
        $row = [ordered]@{ Kind = $kind; Length = $length }
        foreach ($version in @('Before','After')) {
            $method = if ($version -eq 'Before') { $beforeMethod } else { $afterMethod }
            $language = if ($version -eq 'Before') { $beforeLanguage } else { $afterLanguage }
            $null = $method.Invoke($null, @($text, $language))
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $null = $method.Invoke($null, @($text, $language))
            $timer.Stop()
            $row[$version + 'Milliseconds'] = $timer.Elapsed.TotalMilliseconds
        }
        [pscustomobject]$row
    }
}
$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
$null = New-Item -ItemType Directory -Path $directory -Force
@{ BeforeRevision = $BeforeRevision; Seed = 42001; DifferentialCases = $samples.Count; OutputsIdentical = $true; Environment = 'PowerShell Add-Type / same service sources / pure scanner'; Timings = @($timings) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$timings | Format-Table -AutoSize
Write-Output "結果一致: $($samples.Count) cases; $OutputPath"
