#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $PSCommandPath
$Updater = Join-Path $ScriptRoot 'Update-Changelog.ps1'
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("WKVRCProxy.Changelog." + [Guid]::NewGuid().ToString('N'))
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$PushedLocation = $false

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Write-TestFile {
    param([string]$Path, [string]$Content)
    $fullPath = Join-Path (Get-Location) $Path
    $dir = Split-Path -Parent $fullPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    [System.IO.File]::WriteAllText($fullPath, $Content, $Utf8NoBom)
}

function Commit-TestChange {
    param([string]$Path, [string]$Content, [string]$Subject)
    Write-TestFile -Path $Path -Content $Content
    Invoke-Git add -- $Path
    Invoke-Git commit -q -m $Subject
}

function Assert-Contains {
    param([string]$Text, [string]$Expected)
    if (-not $Text.Contains($Expected)) {
        throw "Expected text to contain '$Expected'."
    }
}

function Assert-NotContains {
    param([string]$Text, [string]$Unexpected)
    if ($Text.Contains($Unexpected)) {
        throw "Expected text not to contain '$Unexpected'."
    }
}

try {
    New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
    Push-Location $TempRoot
    $PushedLocation = $true

    Invoke-Git init -q
    Invoke-Git config core.autocrlf false
    Invoke-Git config user.name WhyKnot
    Invoke-Git config user.email whyknot@example.invalid

    $initialChangelog = @'
# Changelog

## Unreleased

_No notable changes since the last release._

---

'@

    Commit-TestChange -Path 'CHANGELOG.md' -Content $initialChangelog -Subject 'chore: initial changelog'
    Commit-TestChange -Path 'src.txt' -Content "feature`n" -Subject 'feat(mesh): test changelog append'

    & $Updater -Mode Append -Range 'HEAD~1..HEAD' -RepoRoot $TempRoot
    if ($LASTEXITCODE -ne 0) { throw "Update-Changelog Append failed with exit code $LASTEXITCODE" }

    $changelog = [System.IO.File]::ReadAllText((Join-Path $TempRoot 'CHANGELOG.md'), $Utf8NoBom)
    Assert-Contains -Text $changelog -Expected '### Added'
    Assert-Contains -Text $changelog -Expected '**mesh:** Test changelog append'

    & $Updater -Mode Promote -Version 'v2026.6.4.0' -RepoRoot $TempRoot -Repo 'RealWhyKnot/WKVRCProxy' -NowUtc ([datetime]::Parse('2026-06-16T01:30:00Z'))
    if ($LASTEXITCODE -ne 0) { throw "Update-Changelog Promote failed with exit code $LASTEXITCODE" }

    $promoted = [System.IO.File]::ReadAllText((Join-Path $TempRoot 'CHANGELOG.md'), $Utf8NoBom)
    Assert-Contains -Text $promoted -Expected '## [v2026.6.4.0](https://github.com/RealWhyKnot/WKVRCProxy/releases/tag/v2026.6.4.0) - 2026-06-15'

    $notes = (& $Updater -Mode Notes -ForVersion -Version 'v2026.6.4.0' -RepoRoot $TempRoot) -join "`n"
    Assert-Contains -Text $notes -Expected '**mesh:** Test changelog append'

    & $Updater -Mode Promote -Version 'v2026.6.5.0' -RepoRoot $TempRoot -Repo 'RealWhyKnot/WKVRCProxy' -NowUtc ([datetime]::Parse('2026-06-16T01:30:00Z'))
    if ($LASTEXITCODE -ne 0) { throw "Update-Changelog empty Promote failed with exit code $LASTEXITCODE" }

    $emptyPromoted = [System.IO.File]::ReadAllText((Join-Path $TempRoot 'CHANGELOG.md'), $Utf8NoBom)
    Assert-Contains -Text $emptyPromoted -Expected '_No user-visible changes in this release._'
    Assert-NotContains -Text $emptyPromoted -Unexpected 'Maintenance release'

    Write-Host 'Update-Changelog tests passed.'
}
finally {
    if ($PushedLocation) { Pop-Location }
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force
    }
}
