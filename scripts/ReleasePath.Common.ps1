# ReleasePath.Common.ps1 — リリース作業の出力先を検証する共通関数
#
# release-all.ps1 / release-local.ps1 / publish-macos.ps1 が同じ安全検査を使うため、
# 1 か所にまとめてドットソースで読み込む。以前は 3 ファイルへ同じ本体を写していたが、
# 安全検査の修正が一部のスクリプトにだけ入るずれを防ぐために共通化した。
#
# リポジトリのルートは呼出元の変数に頼らず、このファイルの場所（scripts/）から求める。

$script:ReleasePathRepoRoot = Split-Path -Parent $PSScriptRoot

# 出力先は作成前に検証し、リンク経由のリポジトリ内保存も拒否する。
function Assert-ReleasePath {
    # AllowedRoot を省略すると「配下にあること」の判定を行わない。
    # 最上位の作業先・記録先は特定ドライブ・フォルダーへ固定しない方針のため省略して呼び、
    # 作業先内部のパスだけ AllowedRoot に WorkRoot 等を渡して配下へ閉じ込める。
    # リポジトリ・.codex 配下の禁止、artifacts 名の禁止、リンク拒否は場所と無関係に常に行う。
    param([string]$Path, [string]$AllowedRoot = '')
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "絶対パスを指定してください: $Path" }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($AllowedRoot) {
        $allowed = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not $full.StartsWith($allowed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "許可された保存先の配下を指定してください: $full (保存先: $allowed)"
        }
    }
    $codexRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    foreach ($forbidden in @($script:ReleasePathRepoRoot, (Join-Path $env:USERPROFILE '.codex'), $codexRoot)) {
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
