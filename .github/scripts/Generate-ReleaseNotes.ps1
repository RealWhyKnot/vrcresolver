#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Generate the GitHub release body for a tag from the git-log slice.

.DESCRIPTION
  Walks commits between the previous tag and the current tag (or initial commit
  if no prior tag exists). Skips merge commits and commits containing the marker
  "[skip changelog]". Strips trailing version-stamp noise of the form
  " (YYYY.M.D.N-XXXX)" that some repos append to commit subjects. Groups by
  conventional-commit prefix when at least one entry has one, otherwise emits a
  flat bullet list.

  Optionally appends an "extras" file (free-form per-release prose) below the
  auto-generated section, separated by --- and an `## Additional notes` heading.
  This is the only supported path for adding hand-written content;
  every other path goes through the auto-generator.

  Outputs the markdown body to stdout. Throws on:
    * empty slice (no qualifying commits between prev and current tag)
    * voice or internal-only-vocabulary pattern in the final body
    * non-ASCII characters in the final body (after a normalisation pass)

  Each failure prints a clear remediation hint so the operator knows whether
  to amend a commit, mark one [skip changelog], or fix the extras file.

  Requires the checkout step to have used fetch-depth: 0 (or otherwise have
  the full history + tags available).

.PARAMETER Tag
  The release tag being built (e.g. "v2026.4.28.0"). Defaults to env:TAG_NAME
  or env:GITHUB_REF_NAME.

.PARAMETER Repo
  GitHub "owner/repo" slug for the compare link. Defaults to env:GITHUB_REPOSITORY.

.PARAMETER Extras
  Optional path to a markdown file whose contents get appended verbatim
  below the auto-changelog section. If absent, the auto section is the
  whole body. Default: ".github/release-extras/<tag>.md".

.PARAMETER AllowEmpty
  Skip the empty-slice guard. Use only for the very first release on a repo
  where there is no prior tag and the tag-range trick yields nothing
  meaningful. Default: $false.

.PARAMETER SkipScrub
  Skip the voice + ASCII scrub. Escape hatch for unblocking edge cases;
  the workflow should never set this. Default: $false.
#>
[CmdletBinding()]
param(
    [string] $Tag      = $(if ($env:TAG_NAME) { $env:TAG_NAME } else { $env:GITHUB_REF_NAME }),
    [string] $Repo     = $env:GITHUB_REPOSITORY,
    [string] $Extras   = $null,
    [switch] $AllowEmpty,
    [switch] $SkipScrub
)

$ErrorActionPreference = 'Stop'

if (-not $Tag) { throw "No tag provided (pass -Tag or set TAG_NAME / GITHUB_REF_NAME)." }

# Default extras path conventionally lives under .github/release-extras/<tag>.md
# next to the workflow that consumes it. Resolve relative to the caller's CWD,
# not the script dir, so the workflow can invoke the script from repo root.
if (-not $Extras) {
    $Extras = Join-Path -Path (Get-Location) -ChildPath ".github/release-extras/$Tag.md"
}

# Resolve previous tag. `git describe --tags --abbrev=0 <tag>^` finds the most
# recent tag reachable from the parent of $Tag -- i.e. the tag before this one
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
# separator are safe -- git rejects literal tabs in author names and subjects
# don't contain them in any of these repos.
$raw = & git log $range --no-merges --pretty=format:"%H`t%h`t%an`t%s" 2>$null
if ($LASTEXITCODE -ne 0) { $raw = @() }

$lines = @()
if ($raw) { $lines = $raw -split "`r?`n" | Where-Object { $_ } }

# Git author.name to GitHub @-handle. Auto-changelog emits "by @<author>" and
# GitHub @-mentions only resolve when the handle is the actual login. Local
# git config uses the brand "WhyKnot" but the GitHub login is "RealWhyKnot".
# Any commit author not in this map passes through unchanged.
$AuthorHandleMap = @{
    'WhyKnot' = 'RealWhyKnot'
}

$entries = foreach ($line in $lines) {
    if ($line -match '\[skip changelog\]') { continue }
    $parts = $line -split "`t", 4
    if ($parts.Count -lt 4) { continue }
    $sha     = $parts[0]
    $short   = $parts[1]
    $author  = $parts[2]
    if ($AuthorHandleMap.ContainsKey($author)) { $author = $AuthorHandleMap[$author] }
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

# Empty-slice guard. A release with no qualifying commits in the tag range
# means either the prev-tag detection is wrong, every commit was skipped,
# or the tag was pushed from an empty branch. All three are operator
# mistakes worth catching before publish.
if (-not $entries -or $entries.Count -eq 0) {
    if ($AllowEmpty) {
        # First-release escape hatch. Emit a stub body the workflow can still
        # publish; downstream consumers can reformat or replace.
        return "## What's Changed`n`n_First release; see commit log for details._`n"
    }
    throw "No commits found in range $range. " +
          "Either the previous tag is misdetected, every commit in the range " +
          "carries [skip changelog], or the tag points at an empty branch. " +
          "Pass -AllowEmpty for a first release. Otherwise amend the offending " +
          "commits or push a real change before tagging."
}

function Get-Category([string] $subject) {
    if ($subject -match '^feat(\(.+?\))?!?:')     { return @{ Order = 1;  Name = 'Features' } }
    if ($subject -match '^fix(\(.+?\))?!?:')      { return @{ Order = 2;  Name = 'Bug Fixes' } }
    if ($subject -match '^perf(\(.+?\))?!?:')     { return @{ Order = 3;  Name = 'Performance' } }
    if ($subject -match '^refactor(\(.+?\))?!?:') { return @{ Order = 4;  Name = 'Refactors' } }
    if ($subject -match '^revert(\(.+?\))?!?:')   { return @{ Order = 5;  Name = 'Reverts' } }
    if ($subject -match '^docs(\(.+?\))?!?:')     { return @{ Order = 6;  Name = 'Documentation' } }
    if ($subject -match '^style(\(.+?\))?!?:')    { return @{ Order = 7;  Name = 'Style' } }
    if ($subject -match '^test(\(.+?\))?!?:')     { return @{ Order = 8;  Name = 'Tests' } }
    if ($subject -match '^ci(\(.+?\))?!?:')       { return @{ Order = 9;  Name = 'CI' } }
    if ($subject -match '^build(\(.+?\))?!?:')    { return @{ Order = 10; Name = 'Build' } }
    if ($subject -match '^chore(\(.+?\))?!?:')    { return @{ Order = 11; Name = 'Chores' } }
    return @{ Order = 99; Name = 'Other Changes' }
}

# Conventional-commit coverage warning. Don't fail -- 'Other Changes' is the
# documented bucket for non-conforming subjects -- but log to stderr so the
# operator sees them in the workflow output and can amend if desired.
$nonConforming = @($entries | Where-Object {
    $_.Subject -notmatch '^(feat|fix|perf|refactor|revert|docs|style|test|ci|build|chore)(\(.+?\))?!?:'
})
if ($nonConforming.Count -gt 0) {
    Write-Host "::warning::$($nonConforming.Count) commit(s) in range $range do not follow conventional-commit prefixes; bucketed under 'Other Changes':"
    foreach ($e in $nonConforming) {
        Write-Host "::warning::  $($e.Short)  $($e.Subject)"
    }
}

$useGroups = $false
foreach ($e in $entries) {
    if ($e.Subject -match '^(feat|fix|perf|refactor|revert|docs|style|test|ci|build|chore)(\(.+?\))?!?:') {
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

# Optional extras append. Free-form prose for the rare case where a release
# needs to communicate something that isn't captured by commit subjects --
# server-side coordination notes, migration steps, a wiki link, etc. The
# file is read verbatim so the author has full markdown control; the same
# scrub gates run on the final composed body so voice violations in the
# extras fail the workflow just as if they were in commit subjects.
if (Test-Path -LiteralPath $Extras) {
    $extrasContent = (Get-Content -LiteralPath $Extras -Raw -Encoding UTF8).Trim()
    if ($extrasContent) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("---")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("## Additional notes")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine($extrasContent)
    }
}

$body = $sb.ToString().TrimEnd()

# ASCII normalisation. Common typographic patterns get substituted to
# their plain-ASCII equivalents. The substitution is one-way and silent;
# a commit subject that contains an em-dash gets emitted with `--` instead.
# After normalisation, anything still non-ASCII fails the scrub gate (so
# we surface the offending byte rather than letting it ship).
# Pattern is stored as a single-char string (not [char]) so the (string,string)
# overload of String.Replace binds cleanly. The (char,char) overload would
# reject a multi-char replacement like '--' or '...'.
$asciiSubs = @(
    @{ Pattern = [string][char]0x2014; Replacement = '--' }        # em-dash
    @{ Pattern = [string][char]0x2013; Replacement = '-'  }        # en-dash
    @{ Pattern = [string][char]0x2026; Replacement = '...' }       # ellipsis
    @{ Pattern = [string][char]0x201C; Replacement = '"'  }        # left double quote
    @{ Pattern = [string][char]0x201D; Replacement = '"'  }        # right double quote
    @{ Pattern = [string][char]0x2018; Replacement = "'"  }        # left single quote
    @{ Pattern = [string][char]0x2019; Replacement = "'"  }        # right single quote
    @{ Pattern = [string][char]0x00A0; Replacement = ' '  }        # non-breaking space
    @{ Pattern = [string][char]0x2022; Replacement = '*'  }        # bullet
    @{ Pattern = [string][char]0x00D7; Replacement = 'x'  }        # multiplication sign
    @{ Pattern = [string][char]0x2192; Replacement = '->' }        # right arrow
    @{ Pattern = [string][char]0x2190; Replacement = '<-' }        # left arrow
    @{ Pattern = [string][char]0x21D2; Replacement = '=>' }        # double right arrow
    @{ Pattern = [string][char]0x21D0; Replacement = '<=' }        # double left arrow
    @{ Pattern = [string][char]0x00A7; Replacement = 'section' }   # section sign
    @{ Pattern = [string][char]0x00B6; Replacement = 'paragraph' } # pilcrow
)
foreach ($sub in $asciiSubs) {
    $body = $body.Replace($sub.Pattern, $sub.Replacement)
}

if (-not $SkipScrub) {
    # Anything outside printable ASCII (plus tab/newline) after the
    # substitution pass fails. Prints the offending line + char code so the
    # operator can find and fix.
    $lineNumber = 0
    $offenders = foreach ($line in ($body -split "`r?`n")) {
        $lineNumber++
        for ($i = 0; $i -lt $line.Length; $i++) {
            $ch = $line[$i]
            $code = [int][char]$ch
            $isAllowed = ($code -ge 0x20 -and $code -le 0x7E) -or $code -eq 9
            if (-not $isAllowed) {
                [pscustomobject]@{
                    Line = $lineNumber
                    Col  = $i + 1
                    Char = $ch
                    Code = ('U+{0:X4}' -f $code)
                    Text = $line
                }
            }
        }
    }
    if ($offenders) {
        $report = $offenders | ForEach-Object { "  line $($_.Line) col $($_.Col): $($_.Code) in: $($_.Text)" }
        throw "Non-ASCII characters in release body after normalisation:`n$($report -join "`n")`n" +
              "Fix: amend the offending commit subject (or extras file) to use ASCII equivalents. " +
              "Common substitutes are pre-mapped in Generate-ReleaseNotes.ps1; if a new character " +
              "trips this, add it to `$asciiSubs and try again."
    }

    # Voice + internal-only-vocabulary grep. The release body is the public
    # face of the repo; these patterns make it read like marketing prose or
    # expose internal-only tooling references. Either fix the offending commit
    # subject to use plainer language, or mark the commit [skip changelog] if
    # it really does need the term.
    $forbiddenPatterns = @(
        # Marketing puffery
        '\bcomprehensive\b'
        '\bleveraging\b'
        '\bwhether\s+you''?re\b'
        '\bempowers?\b'
        '\bstreamline\b'
        '\belevate\b'
        '\bcutting-edge\b'
        '\bseamless(ly)?\b'
        '\belegant\b'
        # Internal-only vocabulary
        '\binvestigator\b'
        '\btriage\b'
        '\bscope plan\b'
        '\btier [0-9]\b'
        '\bdiagnostic gap\b'
        '\bship report\b'
        '\bmemory entry\b'
        '\bverification matrix\b'
        '\borchestrator\b'
        '\bcowork\b'
        # Future-tense rhetoric
        '\bfuture-you\b'
        '\bfuture contributor\b'
        '\bfuture spelunker\b'
        # Time-of-effort claims
        '\b\d+ weeks of work\b'
        '\bmonths of effort\b'
        '\byears in the making\b'
    )
    $matches = foreach ($pat in $forbiddenPatterns) {
        $found = [regex]::Matches($body, $pat, 'IgnoreCase')
        foreach ($m in $found) {
            [pscustomobject]@{ Pattern = $pat; Match = $m.Value; Index = $m.Index }
        }
    }
    if ($matches) {
        $report = $matches | ForEach-Object { "  pattern $($_.Pattern) matched '$($_.Match)' at index $($_.Index)" }
        throw "voice or internal-only-vocabulary patterns in release body:`n$($report -join "`n")`n" +
              "Fix: amend the offending commit subject (or extras file) to use plainer language, " +
              "or mark the commit [skip changelog] if the term is unavoidable."
    }
}

# Single trimmed string so $(...) capture in calling scripts gets clean text.
$body
