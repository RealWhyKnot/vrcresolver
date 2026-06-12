#!/usr/bin/env pwsh

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $PSCommandPath
$Generator = Join-Path $ScriptRoot 'Generate-ReleaseNotes.ps1'
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("WKVRCProxy.ReleaseNotes." + [Guid]::NewGuid().ToString('N'))
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$PushedLocation = $false

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Write-TestFile {
    param(
        [string]$Path,
        [string]$Content
    )

    $FullPath = Join-Path (Get-Location) $Path
    $Dir = Split-Path -Parent $FullPath
    if ($Dir -and -not (Test-Path -LiteralPath $Dir)) {
        New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    }
    [System.IO.File]::WriteAllText($FullPath, $Content, $Utf8NoBom)
}

function Commit-TestChange {
    param(
        [string]$Path,
        [string]$Content,
        [string]$Subject
    )

    Write-TestFile -Path $Path -Content $Content
    Invoke-Git add -- $Path
    Invoke-Git commit -q -m $Subject
}

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Expected
    )

    if (-not $Text.Contains($Expected)) {
        throw "Expected release notes to contain '$Expected'."
    }
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Unexpected
    )

    if ($Text.Contains($Unexpected)) {
        throw "Expected release notes not to contain '$Unexpected'."
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

    New-Item -ItemType Directory -Force -Path '.github/release-template' | Out-Null
    foreach ($TemplateName in @('links', 'install', 'uninstall', 'what-you-need-to-do')) {
        Write-TestFile -Path ".github/release-template/$TemplateName.md" -Content ''
    }

    Commit-TestChange `
        -Path 'CHANGELOG.md' `
        -Content "# Changelog`n`n## Unreleased`n`n_No notable changes since the last release._`n" `
        -Subject 'chore: initial changelog'
    Invoke-Git tag -a v2026.5.1.0 -m 'WKVRCProxy 2026.5.1.0'

    Commit-TestChange `
        -Path 'src.txt' `
        -Content "plain-prerelease`n" `
        -Subject 'fix(helper): plain prerelease fallback'
    Invoke-Git tag -a v2026.5.2.0 -m 'WKVRCProxy 2026.5.2.0'

    Commit-TestChange `
        -Path 'src.txt' `
        -Content "beta`n" `
        -Subject 'fix(relay): beta port fallback'
    Invoke-Git tag -a v2026.5.2.1-beta -m 'WKVRCProxy 2026.5.2.1-beta'

    Commit-TestChange `
        -Path 'src.txt' `
        -Content "stable`n" `
        -Subject 'fix(vrclog): stable startup tail'
    Invoke-Git tag -a v2026.5.3.0 -m 'WKVRCProxy 2026.5.3.0'

    $TemplateDir = Join-Path $TempRoot '.github/release-template'
    $StableNotes = (& $Generator `
            -Tag v2026.5.3.0 `
            -Repo RealWhyKnot/WKVRCProxy `
            -TemplateDir $TemplateDir `
            -PrereleaseTags v2026.5.2.0 `
            -SkipScrub) -join "`n"

    Assert-Contains -Text $StableNotes -Expected 'fix(helper): plain prerelease fallback'
    Assert-Contains -Text $StableNotes -Expected 'fix(relay): beta port fallback'
    Assert-Contains -Text $StableNotes -Expected 'fix(vrclog): stable startup tail'
    Assert-Contains -Text $StableNotes -Expected 'compare/v2026.5.1.0...v2026.5.3.0'
    Assert-NotContains -Text $StableNotes -Unexpected 'compare/v2026.5.2.0...v2026.5.3.0'
    Assert-NotContains -Text $StableNotes -Unexpected 'compare/v2026.5.2.1-beta...v2026.5.3.0'

    $BetaNotes = (& $Generator `
            -Tag v2026.5.2.1-beta `
            -Repo RealWhyKnot/WKVRCProxy `
            -TemplateDir $TemplateDir `
            -PrereleaseTags v2026.5.2.0 `
            -SkipScrub) -join "`n"

    Assert-Contains -Text $BetaNotes -Expected 'fix(relay): beta port fallback'
    Assert-Contains -Text $BetaNotes -Expected 'compare/v2026.5.2.0...v2026.5.2.1-beta'
    Assert-NotContains -Text $BetaNotes -Unexpected 'fix(helper): plain prerelease fallback'
    Assert-NotContains -Text $BetaNotes -Unexpected 'fix(vrclog): stable startup tail'

    $PlainPrereleaseNotes = (& $Generator `
            -Tag v2026.5.2.0 `
            -Repo RealWhyKnot/WKVRCProxy `
            -TemplateDir $TemplateDir `
            -PrereleaseTags v2026.5.2.0 `
            -SkipScrub) -join "`n"

    Assert-Contains -Text $PlainPrereleaseNotes -Expected 'fix(helper): plain prerelease fallback'
    Assert-Contains -Text $PlainPrereleaseNotes -Expected 'compare/v2026.5.1.0...v2026.5.2.0'
    Assert-NotContains -Text $PlainPrereleaseNotes -Unexpected 'fix(relay): beta port fallback'

    New-Item -ItemType Directory -Force -Path 'artifacts' | Out-Null
    $zipPath = Join-Path $TempRoot 'artifacts/WKVRCProxy-v2026.5.3.0.zip'
    $manifestPath = Join-Path $TempRoot 'artifacts/WKVRCProxy-v2026.5.3.0.manifest.tsv'
    $zipSha = 'E8966F33BE8246922756E3E8234CF8309FB6D3151665594203F53BBF5725164B'
    Write-TestFile -Path 'artifacts/WKVRCProxy-v2026.5.3.0.zip' -Content 'zip'
    Write-TestFile -Path 'artifacts/WKVRCProxy-v2026.5.3.0.manifest.tsv' -Content "A24EA7D3DF2B0718AFF60B5B9EBEBDF590ED4938D81A6B08CDEC7A880B326B0C`t123`tWKVRCProxy.exe`n"

    $IntegrityNotes = (& $Generator `
            -Tag v2026.5.3.0 `
            -Repo RealWhyKnot/WKVRCProxy `
            -TemplateDir $TemplateDir `
            -PrereleaseTags v2026.5.2.0 `
            -ZipPath $zipPath `
            -ZipName 'WKVRCProxy-v2026.5.3.0.zip' `
            -ZipSize 157017922 `
            -ZipSha256 $zipSha `
            -Manifest $manifestPath `
            -SkipScrub) -join "`n"

    Assert-Contains -Text $IntegrityNotes -Expected "SHA256: $zipSha"
    Assert-Contains -Text $IntegrityNotes -Expected 'WKVRCProxy-v2026.5.3.0.integrity.tsv'
    Assert-NotContains -Text $IntegrityNotes -Unexpected 'WKVRCProxy.exe                        '

    Write-Host 'Generate-ReleaseNotes tests passed.'
}
finally {
    if ($PushedLocation) {
        Pop-Location
    }
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force
    }
}
