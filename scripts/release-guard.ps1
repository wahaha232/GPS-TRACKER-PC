param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,

    [Parameter(Mandatory = $true)]
    [string]$ChangelogPath,

    [string]$CurrentReleaseName = ""
)

$ErrorActionPreference = "Stop"

function Get-NormalizedVersion([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $match = [regex]::Match($Text, '(\d+)\.(\d+)\.(\d+)')
    if (-not $match.Success) {
        return $null
    }

    return $match.Value
}

function ConvertTo-VersionParts([string]$Version) {
    if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "版本格式不正確：$Version（需要 主版本.次版本.修訂）"
    }

    return @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
}

function Compare-Version([string]$Left, [string]$Right) {
    $leftParts = ConvertTo-VersionParts $Left
    $rightParts = ConvertTo-VersionParts $Right
    for ($index = 0; $index -lt 3; $index++) {
        if ($leftParts[$index] -lt $rightParts[$index]) { return -1 }
        if ($leftParts[$index] -gt $rightParts[$index]) { return 1 }
    }

    return 0
}

function Get-ChangelogSection([string]$Path, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "找不到 CHANGELOG.md：$Path"
    }

    $lines = [IO.File]::ReadAllLines($Path, [Text.Encoding]::UTF8)
    $pattern = '^##\s+\[?' + [regex]::Escape($Version) + '\]?(\s|$)'
    $start = -1
    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index].TrimEnd() -match $pattern) {
            $start = $index
            break
        }
    }

    if ($start -lt 0) {
        throw "CHANGELOG.md 找不到版本 $Version 的段落"
    }

    $end = $lines.Length
    for ($index = $start + 1; $index -lt $lines.Length; $index++) {
        if ($lines[$index].StartsWith("## ")) {
            $end = $index
            break
        }
    }

    $section = (($lines[$start..($end - 1)]) -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($section)) {
        throw "CHANGELOG.md 的版本 $Version 段落是空的"
    }

    return $section
}

$projectXml = [xml](Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8)
$versionNode = $projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.Version)) {
    throw "csproj 沒有設定 Version"
}

$newVersion = Get-NormalizedVersion $versionNode.Version
if ($null -eq $newVersion) {
    throw "csproj 的 Version 格式不正確：$($versionNode.Version)"
}

$currentVersion = Get-NormalizedVersion $CurrentReleaseName
if (-not [string]::IsNullOrWhiteSpace($CurrentReleaseName) -and $null -eq $currentVersion) {
    throw "目前 Release 名稱看不出舊版本號：$CurrentReleaseName"
}

if ($null -ne $currentVersion) {
    $comparison = Compare-Version $newVersion $currentVersion
    if ($comparison -le 0) {
        throw "版本未進版：新版本 $newVersion 必須大於目前 Release 的 $currentVersion"
    }
}

$notes = Get-ChangelogSection $ChangelogPath $newVersion

[pscustomobject]@{
    Version = $newVersion
    Notes = $notes
} | ConvertTo-Json -Compress
