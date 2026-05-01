#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Generate a "What's Changed" markdown block for a GitHub release from git log.

.DESCRIPTION
  Walks commits between the previous tag and the current tag (or initial commit
  if no prior tag exists). Skips merge commits and commits containing the marker
  "[skip changelog]". Strips trailing version-stamp noise of the form
  " (YYYY.M.D.N-XXXX)" that some repos append to commit subjects. Groups by
  conventional-commit prefix when at least one entry has one, otherwise emits a
  flat bullet list.

  Outputs the markdown body to stdout. If there are no qualifying commits,
  emits the empty string so callers can concatenate without special-casing.

  Requires the checkout step to have used fetch-depth: 0 (or otherwise have
  the full history + tags available).

.PARAMETER Tag
  The release tag being built (e.g. "v2026.4.28.0"). Defaults to env:TAG_NAME
  or env:GITHUB_REF_NAME.

.PARAMETER Repo
  GitHub "owner/repo" slug for the compare link. Defaults to env:GITHUB_REPOSITORY.
#>
[CmdletBinding()]
param(
    [string] $Tag  = $(if ($env:TAG_NAME) { $env:TAG_NAME } else { $env:GITHUB_REF_NAME }),
    [string] $Repo = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'

if (-not $Tag) { throw "No tag provided (pass -Tag or set TAG_NAME / GITHUB_REF_NAME)." }

# Resolve previous tag. `git describe --tags --abbrev=0 <tag>^` finds the most
# recent tag reachable from the parent of $Tag — i.e. the tag before this one
# along the same line of history. If there is no prior tag, fall back to the
# repo's root commit so we still get a full list on the first release.
$prevTag = $null
$prevRef = & git describe --tags --abbrev=0 "$Tag^" 2>$null
if ($LASTEXITCODE -eq 0 -and $prevRef) {
    $prevTag = $prevRef.Trim()
    $range   = "$prevTag..$Tag"
} else {
    $root  = (& git rev-list --max-parents=0 HEAD | Select-Object -First 1).Trim()
    $range = "$root..$Tag"
}

# %H = full sha, %h = short sha, %an = author name, %s = subject. Tabs as field
# separator are safe — git rejects literal tabs in author names and subjects
# don't contain them in any of these repos.
$raw = & git log $range --no-merges --pretty=format:"%H`t%h`t%an`t%s" 2>$null
if ($LASTEXITCODE -ne 0) { $raw = @() }

$lines = @()
if ($raw) { $lines = $raw -split "`r?`n" | Where-Object { $_ } }

$entries = foreach ($line in $lines) {
    if ($line -match '\[skip changelog\]') { continue }
    $parts = $line -split "`t", 4
    if ($parts.Count -lt 4) { continue }
    $sha     = $parts[0]
    $short   = $parts[1]
    $author  = $parts[2]
    $subject = $parts[3]

    # Strip embedded version-stamp noise like " (2026.4.30.13-EB4B)". Some
    # repos append it mid-subject before a trailing PR ref like " (#42)".
    $subject = $subject -replace '\s*\(\d{4}\.\d+\.\d+\.\d+-[A-Fa-f0-9]+\)\s*',' '
    $subject = $subject.Trim() -replace '\s{2,}',' '

    [pscustomobject]@{
        Sha     = $sha
        Short   = $short
        Author  = $author
        Subject = $subject
    }
}

if (-not $entries -or $entries.Count -eq 0) {
    return ''
}

function Get-Category([string] $subject) {
    if ($subject -match '^feat(\(.+?\))?!?:')     { return @{ Order = 1; Name = 'Features' } }
    if ($subject -match '^fix(\(.+?\))?!?:')      { return @{ Order = 2; Name = 'Bug Fixes' } }
    if ($subject -match '^perf(\(.+?\))?!?:')     { return @{ Order = 3; Name = 'Performance' } }
    if ($subject -match '^refactor(\(.+?\))?!?:') { return @{ Order = 4; Name = 'Refactors' } }
    if ($subject -match '^revert(\(.+?\))?!?:')   { return @{ Order = 5; Name = 'Reverts' } }
    if ($subject -match '^docs(\(.+?\))?!?:')     { return @{ Order = 6; Name = 'Documentation' } }
    if ($subject -match '^test(\(.+?\))?!?:')     { return @{ Order = 7; Name = 'Tests' } }
    if ($subject -match '^ci(\(.+?\))?!?:')       { return @{ Order = 8; Name = 'CI' } }
    if ($subject -match '^build(\(.+?\))?!?:')    { return @{ Order = 9; Name = 'Build' } }
    if ($subject -match '^chore(\(.+?\))?!?:')    { return @{ Order = 10; Name = 'Chores' } }
    return @{ Order = 99; Name = 'Other Changes' }
}

$useGroups = $false
foreach ($e in $entries) {
    if ($e.Subject -match '^(feat|fix|perf|refactor|revert|docs|test|ci|build|chore)(\(.+?\))?!?:') {
        $useGroups = $true
        break
    }
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("## What's Changed")
[void]$sb.AppendLine()

if ($useGroups) {
    $tagged = foreach ($e in $entries) {
        $cat = Get-Category $e.Subject
        [pscustomobject]@{ Order = $cat.Order; Name = $cat.Name; Entry = $e }
    }
    $groups = $tagged | Group-Object Name | Sort-Object { ($_.Group | Select-Object -First 1).Order }
    foreach ($g in $groups) {
        [void]$sb.AppendLine("### $($g.Name)")
        foreach ($t in $g.Group) {
            $e = $t.Entry
            [void]$sb.AppendLine("- $($e.Subject) by @$($e.Author) in $($e.Short)")
        }
        [void]$sb.AppendLine()
    }
} else {
    foreach ($e in $entries) {
        [void]$sb.AppendLine("- $($e.Subject) by @$($e.Author) in $($e.Short)")
    }
    [void]$sb.AppendLine()
}

if ($Repo -and $prevTag) {
    [void]$sb.AppendLine("**Full Changelog**: https://github.com/$Repo/compare/$prevTag...$Tag")
}

# Single trimmed string so $(...) capture in calling scripts gets clean text.
$sb.ToString().TrimEnd()
