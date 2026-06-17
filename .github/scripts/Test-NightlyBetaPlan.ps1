#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Planner = Join-Path $ScriptRoot "Get-NightlyBetaPlan.ps1"

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

function Write-TestFile {
    param(
        [string] $Path,
        [string] $Content
    )

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent | Out-Null
    }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function New-TestRepo {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("wkvrcproxy-nightly-beta-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $root | Out-Null

    Invoke-TestGit -RepoRoot $root -Arguments @("init", "-q", ".") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("config", "user.name", "WKVRCProxy Tests") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("config", "user.email", "wkvrcproxy-tests@example.invalid") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("config", "core.autocrlf", "false") | Out-Null

    Write-TestFile -Path (Join-Path $root "src\package.txt") -Content "initial`n"
    Invoke-TestGit -RepoRoot $root -Arguments @("add", ".") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("commit", "-q", "-m", "initial") | Out-Null
    Invoke-TestGit -RepoRoot $root -Arguments @("tag", "v2026.6.1.0-beta") | Out-Null
    return $root
}

function Invoke-Plan {
    param(
        [string] $RepoRoot,
        [string] $BaseTag = "v2026.6.1.0-beta",
        [string] $Tag = "",
        [string] $Today = "",
        [datetime] $NowUtc = ([datetime]::Parse("2026-06-16T01:30:00Z"))
    )

    $outputPath = Join-Path $RepoRoot "plan.json"
    $arguments = @{
        RepoRoot = $RepoRoot
        BaseTag = $BaseTag
        OutputJsonPath = $outputPath
        NowUtc = $NowUtc
    }
    if (-not [string]::IsNullOrWhiteSpace($Tag)) {
        $arguments["Tag"] = $Tag
    }
    if (-not [string]::IsNullOrWhiteSpace($Today)) {
        $arguments["Today"] = $Today
    }

    & $Planner @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Planner failed with exit code $LASTEXITCODE"
    }
    return (Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json)
}

function Assert-Equal {
    param(
        [object] $Actual,
        [object] $Expected,
        [string] $Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message. Expected '$Expected', got '$Actual'."
    }
}

$tempRoots = New-Object System.Collections.Generic.List[string]
try {
    $repo = New-TestRepo
    [void]$tempRoots.Add($repo)
    $plan = Invoke-Plan -RepoRoot $repo
    Assert-Equal -Actual $plan.has_changes -Expected $false -Message "Latest tag at HEAD should skip nightly beta tag creation"
    Assert-Equal -Actual $plan.changed_path_count -Expected 0 -Message "Changed path count should be zero at the latest tag"

    $repo = New-TestRepo
    [void]$tempRoots.Add($repo)
    Write-TestFile -Path (Join-Path $repo "CHANGELOG.md") -Content "# Changelog`n"
    Invoke-TestGit -RepoRoot $repo -Arguments @("add", ".") | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("commit", "-q", "-m", "docs(changelog): promote release") | Out-Null
    $plan = Invoke-Plan -RepoRoot $repo
    Assert-Equal -Actual $plan.has_changes -Expected $false -Message "Changelog-only changes should skip nightly beta tag creation"
    Assert-Equal -Actual $plan.changed_path_count -Expected 0 -Message "Changelog-only changes should not count as release inputs"

    $repo = New-TestRepo
    [void]$tempRoots.Add($repo)
    Write-TestFile -Path (Join-Path $repo "data\known_wrapper_hashes.txt") -Content "abc123`n"
    Invoke-TestGit -RepoRoot $repo -Arguments @("add", ".") | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("commit", "-q", "-m", "chore(release): append wrapper hash [skip changelog]") | Out-Null
    $plan = Invoke-Plan -RepoRoot $repo
    Assert-Equal -Actual $plan.has_changes -Expected $false -Message "Wrapper-hash-only changes should skip nightly beta tag creation"
    Assert-Equal -Actual $plan.changed_path_count -Expected 0 -Message "Wrapper-hash-only changes should not count as release inputs"

    $repo = New-TestRepo
    [void]$tempRoots.Add($repo)
    Write-TestFile -Path (Join-Path $repo "src\package.txt") -Content "changed`n"
    Invoke-TestGit -RepoRoot $repo -Arguments @("add", ".") | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("commit", "-q", "-m", "fix: update proxy input") | Out-Null
    $plan = Invoke-Plan -RepoRoot $repo -Today "2026.6.9"
    Assert-Equal -Actual $plan.has_changes -Expected $true -Message "Changes since the latest release should create a plan"
    Assert-Equal -Actual $plan.changed_path_count -Expected 1 -Message "Planner should count release input path changes"
    Assert-Equal -Actual $plan.next_tag -Expected "v2026.6.9.0-beta" -Message "First same-day beta tag should use sequence zero"

    Invoke-TestGit -RepoRoot $repo -Arguments @("tag", "v2026.6.9.0-beta") | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("tag", "v2026.6.9.1") | Out-Null
    $plan = Invoke-Plan -RepoRoot $repo -Today "2026.6.9"
    Assert-Equal -Actual $plan.next_tag -Expected "v2026.6.9.2-beta" -Message "Same-day tags should increment from the highest release revision"

    $repo = New-TestRepo
    [void]$tempRoots.Add($repo)
    Write-TestFile -Path (Join-Path $repo "src\package.txt") -Content "changed after utc rollover`n"
    Invoke-TestGit -RepoRoot $repo -Arguments @("add", ".") | Out-Null
    Invoke-TestGit -RepoRoot $repo -Arguments @("commit", "-q", "-m", "fix: update after utc rollover") | Out-Null
    $plan = Invoke-Plan -RepoRoot $repo
    Assert-Equal -Actual $plan.next_tag -Expected "v2026.6.15.0-beta" -Message "Default beta tag date should use Central release date"

    $failed = $false
    try {
        Invoke-Plan -RepoRoot $repo -Tag "v2026.6.9.0-beta.1" | Out-Null
    }
    catch {
        $failed = $true
    }
    Assert-Equal -Actual $failed -Expected $true -Message "Planner should reject numbered beta suffixes"

    Write-Host "Nightly beta planner tests passed."
}
finally {
    foreach ($tempRoot in $tempRoots) {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}
