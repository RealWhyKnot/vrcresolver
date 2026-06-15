#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$errors = New-Object System.Collections.Generic.List[string]

function Get-RepoRelativePath {
    param([string]$Path)

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if ($pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $pathFull.Substring($prefix.Length)
    }
    return $pathFull
}

function Add-ParseErrors {
    param(
        [string]$Source,
        $ParseErrors
    )
    if (-not $ParseErrors) { return }
    foreach ($err in $ParseErrors) {
        $errors.Add("$Source (line $($err.Extent.StartLineNumber):$($err.Extent.StartColumnNumber)): $($err.Message)") | Out-Null
    }
}

$trackedScripts = & git -C $Root ls-files '*.ps1' '*.psm1' '*.psd1'
foreach ($relative in $trackedScripts) {
    $path = Join-Path $Root $relative
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$parseErrors)
    Add-ParseErrors -Source $relative -ParseErrors $parseErrors
}

$workflowDir = Join-Path $Root '.github\workflows'
if (Test-Path -LiteralPath $workflowDir) {
    $ghaPattern = [regex]'\$\{\{[^}]*\}\}'
    Get-ChildItem -LiteralPath $workflowDir -Filter '*.yml' | ForEach-Object {
        $relativeWorkflow = Get-RepoRelativePath -Path $_.FullName
        $lines = Get-Content -LiteralPath $_.FullName
        $stepName = '<unnamed>'
        $isPwsh = $false
        $inRun = $false
        $runIndent = -1
        $runStart = 0
        $blockLines = New-Object System.Collections.Generic.List[string]

        $flush = {
            if ($blockLines.Count -eq 0) { return }
            $baseline = -1
            foreach ($line in $blockLines) {
                if ($line.Trim().Length -eq 0) { continue }
                $baseline = $line.Length - $line.TrimStart(' ').Length
                break
            }
            if ($baseline -lt 0) { return }
            $body = ($blockLines | ForEach-Object {
                if ($_.Length -gt $baseline) { $_.Substring($baseline) } else { '' }
            }) -join "`n"
            $stubbed = $ghaPattern.Replace($body, '__GHA_EXPR__')
            $parseErrors = $null
            [void][System.Management.Automation.Language.Parser]::ParseInput($stubbed, [ref]$null, [ref]$parseErrors)
            Add-ParseErrors -Source "$relativeWorkflow step '$stepName' run block starting line $runStart" -ParseErrors $parseErrors
        }

        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            $indent = $line.Length - $line.TrimStart(' ').Length
            $trimmed = $line.Trim()

            if ($inRun) {
                if ($trimmed.Length -gt 0 -and $indent -le $runIndent) {
                    & $flush
                    $blockLines.Clear()
                    $inRun = $false
                } else {
                    $blockLines.Add($line) | Out-Null
                    continue
                }
            }

            if ($trimmed -match '^- name:\s*(.+?)\s*$') {
                $stepName = $Matches[1]
                $isPwsh = $false
                continue
            }
            if ($trimmed -match '^shell:\s*(.+?)\s*$') {
                $isPwsh = ($Matches[1] -eq 'pwsh')
                continue
            }
            if ($isPwsh -and $trimmed -match '^run:\s*\|\s*$') {
                $inRun = $true
                $runIndent = $indent
                $runStart = $i + 1
                $blockLines.Clear()
            }
        }
        if ($inRun) { & $flush }
    }
}

$releaseWorkflow = Join-Path $workflowDir 'release.yml'
if (Test-Path -LiteralPath $releaseWorkflow) {
    $releaseText = Get-Content -LiteralPath $releaseWorkflow -Raw
    if ($releaseText -notmatch '\.integrity\.tsv') {
        $errors.Add('release.yml does not create an integrity TSV asset.') | Out-Null
    }
    if ($releaseText -notmatch 'gh release create \$tag \$zip \$integrity') {
        $errors.Add('release.yml does not upload both the zip and integrity TSV assets.') | Out-Null
    }
    if ($releaseText -match 'gh pr create|Open promotion PR|promote-changelog') {
        $errors.Add('release.yml contains the deprecated changelog-promotion PR path.') | Out-Null
    }
    if ($releaseText -notmatch 'Preloading changelog entries from') {
        $errors.Add('release.yml does not preload changelog entries before promotion.') | Out-Null
    }
    if ($releaseText -notmatch 'Commit promoted CHANGELOG\.md back to main \(verified\)') {
        $errors.Add('release.yml does not commit promoted CHANGELOG.md back through the verified path.') | Out-Null
    }
}

if ($errors.Count -gt 0) {
    Write-Host 'PowerShell syntax errors:'
    foreach ($err in $errors) { Write-Host "  $err" }
    throw "Found $($errors.Count) PowerShell syntax error(s)."
}

Write-Host 'PowerShell scripts and inline workflow blocks parsed cleanly.'
