#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$AssertScript = Join-Path $ScriptRoot "Assert-ReleaseVersionSequence.ps1"

function Invoke-TestGit {
    param(
        [string] $RepoRoot,
        [string[]] $Arguments
    )

    Push-Location $RepoRoot
    try {
        $output = & git @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
        }
        return @($output)
    }
    finally {
        Pop-Location
    }
}

function New-TestRepo {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("wkvrcproxy-release-sequence-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $root | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("init", "-q", ".") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("config", "user.name", "WKVRCProxy Tests") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("config", "user.email", "wkvrcproxy-tests@example.invalid") | Out-Null
    Set-Content -LiteralPath (Join-Path $root "sample.txt") -Value "base" -Encoding ASCII
    Invoke-TestGit -RepoRoot $root -Arguments @("add", ".") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("commit", "-q", "-m", "initial") | Out-Null
    return $root
}

function Assert-Passes {
    param(
        [string] $RepoRoot,
        [string] $Tag,
        [string] $Message
    )

    & $AssertScript -RepoRoot $RepoRoot -Tag $Tag | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw $Message
    }
}

function Assert-Fails {
    param(
        [string] $RepoRoot,
        [string] $Tag,
        [string] $Message
    )

    $failed = $false
    try {
        & $AssertScript -RepoRoot $RepoRoot -Tag $Tag | Out-Host
    }
    catch {
        $failed = $true
    }

    if (-not $failed) {
        throw $Message
    }
}

$tempRoots = [System.Collections.Generic.List[string]]::new()
try {
    $repo = New-TestRepo
    $tempRoots.Add($repo) | Out-Null
    Assert-Passes -RepoRoot $repo -Tag "v2026.6.15.0-beta" -Message "First same-day prerelease should be .0."
    Assert-Fails -RepoRoot $repo -Tag "v2026.6.15.1-beta" -Message ".1 should fail when no same-day release exists."

    $repo = New-TestRepo
    $tempRoots.Add($repo) | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("tag", "v2026.6.15.0") | Out-Null
    Assert-Passes -RepoRoot $repo -Tag "v2026.6.15.1-beta" -Message "Prerelease after same-day stable .0 should be .1."
    Assert-Fails -RepoRoot $repo -Tag "v2026.6.15.0-beta" -Message ".0-beta should fail after same-day stable .0."

    $repo = New-TestRepo
    $tempRoots.Add($repo) | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("tag", "v2026.6.15.0-beta") | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("tag", "v2026.6.15.1") | Out-Null
    Assert-Passes -RepoRoot $repo -Tag "v2026.6.15.2-beta" -Message "Sequence should increment the highest same-day tag."

    $repo = New-TestRepo
    $tempRoots.Add($repo) | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("tag", "v2026.6.15.0-beta") | Out-Null
    Assert-Passes -RepoRoot $repo -Tag "v2026.6.15.0-beta" -Message "Rerunning the exact release tag should be allowed."

    Write-Host "Release version sequence tests passed."
}
finally {
    foreach ($tempRoot in $tempRoots) {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}
