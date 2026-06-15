#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag,
    [string] $RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = "Stop"

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
    return @($output)
}

function Get-ExpectedReleaseRevision {
    param(
        [string] $DateStamp,
        [string] $ExcludeTag
    )

    $escapedDate = [regex]::Escape($DateStamp)
    $pattern = "^v$escapedDate\.(\d+)(-[A-Za-z0-9]{4})?$"
    $existingTags = @(Invoke-Git -Arguments @("tag", "--list", "v$DateStamp.*"))
    $highest = -1

    foreach ($existingTag in $existingTags) {
        if ($existingTag -eq $ExcludeTag) {
            continue
        }
        if ($existingTag -match $pattern) {
            $value = [int] $Matches[1]
            if ($value -gt $highest) {
                $highest = $value
            }
        }
    }

    return $highest + 1
}

$repoRootPath = (Resolve-Path -LiteralPath $RepoRoot).Path
Push-Location $repoRootPath
try {
    if ($Tag -notmatch "^v(\d{4})\.(\d+)\.(\d+)\.(\d+)(-[A-Za-z0-9]{4})?$") {
        throw "Release tag must be vYYYY.M.D.N or vYYYY.M.D.N-XXXX, got '$Tag'."
    }

    $dateStamp = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
    $actualRevision = [int] $Matches[4]
    $expectedRevision = Get-ExpectedReleaseRevision -DateStamp $dateStamp -ExcludeTag $Tag

    if ($actualRevision -ne $expectedRevision) {
        throw "Release tag $Tag uses revision $actualRevision, expected $expectedRevision for $dateStamp. Use .0 when no release exists for the day; otherwise increment the highest same-day release or prerelease revision by one."
    }

    Write-Host "Release tag $Tag uses the expected same-day revision $expectedRevision."
}
finally {
    Pop-Location
}
