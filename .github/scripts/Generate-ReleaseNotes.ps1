#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Generate the GitHub release body for a tag from the git-log slice plus
  per-repo template sections plus a per-file integrity table.

.DESCRIPTION
  Composes a multi-section markdown body for the release page. Section order:

    1. Title (h1: "<repo> <tag>")
    2. What's Changed (auto-changelog from the commit slice between prev tag
       and this tag; bucketed by conventional-commit prefix)
    3. File integrity (auto-generated SHA256 + size table for the release zip
       and every file inside it; reads inner-file metadata from the manifest
       emitted by build.ps1)
    4. More (from .github/release-template/links.md, with token substitution)
    5. Install (fresh) (from .github/release-template/install.md)
    6. Uninstall (from .github/release-template/uninstall.md)
    7. What you need to do (from .github/release-template/what-you-need-to-do.md)
    8. Optional extras (from .github/release-extras/<tag>.md if present;
       appended below with `---` separator and `## Additional notes` heading)

  Slice composition: walks commits between prev tag and current tag. Skips
  merge commits and commits containing "[skip changelog]". Strips trailing
  version-stamp noise of the form " (YYYY.M.D.N-XXXX)" that some repos append
  to subjects. Groups by conventional-commit prefix when at least one entry
  has one, otherwise emits a flat bullet list.

  Prev-tag resolution is layered for resilience against history rewrites that
  orphan the prior tag (rebase + force-push of main): describe + sanity gate,
  then subject-match against the most recent published GitHub release, then
  root-walk fallback. See Resolve-PrevTagForSlice for details.

  Templates and the optional extras file run through the same scrub gates as
  commit subjects: ASCII normalisation pass, then non-ASCII fail, then a
  voice / internal-vocab grep. Any violation in any input fails the workflow
  and prints a remediation hint.

  Outputs the markdown body to stdout. Throws on:
    * empty slice (no qualifying commits between prev and current tag)
    * voice or internal-only-vocabulary pattern in the final body
    * non-ASCII characters in the final body (after a normalisation pass)

  Each failure prints a clear remediation hint so the operator knows whether
  to amend a commit, mark one [skip changelog], or fix a template or the
  extras file.

  Requires the checkout step to have used fetch-depth: 0 (or otherwise have
  the full history + tags available).

.PARAMETER Tag
  The release tag being built (e.g. "v2026.4.28.0"). Defaults to env:TAG_NAME
  or env:GITHUB_REF_NAME.

.PARAMETER Repo
  GitHub "owner/repo" slug for the compare link. Defaults to env:GITHUB_REPOSITORY.

.PARAMETER Extras
  Optional path to a markdown file whose contents get appended verbatim
  below all auto sections. Used for release-specific narrative that does
  not fit a commit subject (migration steps, server-side coordination
  notes, etc.). Default: ".github/release-extras/<tag>.md".

.PARAMETER TemplateDir
  Directory containing the per-section evergreen templates: links.md,
  install.md, uninstall.md, what-you-need-to-do.md. Templates undergo
  token substitution (see README in this directory) before emission.
  Default: ".github/release-template".

.PARAMETER Manifest
  Path to the per-file manifest emitted by build.ps1 alongside the zip.
  Tab-separated <sha256>\t<size_bytes>\t<relative_path> per line. Used
  to compose the inner-file rows of the File integrity section.
  Required (along with -ZipPath, -ZipSize, -ZipSha256) for the File
  integrity section to render; otherwise that section is skipped.

.PARAMETER ZipPath
  Path to the release zip artifact. Used to derive the zip name when
  -ZipName is not set, and as a presence check for the File integrity
  section.

.PARAMETER ZipName
  Override for the zip's display name in the File integrity section.
  Defaults to the leaf of -ZipPath.

.PARAMETER ZipSize
  Size in bytes of the release zip. Used by the File integrity section.

.PARAMETER ZipSha256
  SHA256 of the release zip. Used by the File integrity section.

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
    [string] $Tag         = $(if ($env:TAG_NAME) { $env:TAG_NAME } else { $env:GITHUB_REF_NAME }),
    [string] $Repo        = $env:GITHUB_REPOSITORY,
    [string] $Extras      = $null,
    [string] $TemplateDir = $null,
    [string] $Manifest    = $null,
    [string] $ZipPath     = $null,
    [string] $ZipName     = $null,
    [long]   $ZipSize     = 0,
    [string] $ZipSha256   = $null,
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

# Default template dir for the per-section evergreen content (More, Install,
# Uninstall, What you need to do). Each repo curates its own content here;
# the composer reads `<TemplateDir>/<section>.md` and runs token substitution
# on the contents before emitting it as a section of the release body.
if (-not $TemplateDir) {
    $TemplateDir = Join-Path -Path (Get-Location) -ChildPath ".github/release-template"
}

# Resolve previous tag. Layered fallback so a history rewrite that orphans the
# prior tag does not produce a giant slice walking from root:
#   1. `git describe --tags --abbrev=0 <tag>^` finds the most recent tag
#      reachable from $Tag^ along the current line of history. Sanity gate:
#      if the slice is more than 50 commits, treat the describe result as
#      stale (likely orphaned by a rewrite) and fall through.
#   2. Ask GitHub for the most recent published non-prerelease release. Look
#      up its tag in the local repo (the SHA may be orphaned in current
#      history but the object still exists in git's DB). Read the orphan
#      commit's subject. Walk current $Tag history looking for that exact
#      subject; use the matched SHA as the slice anchor. This works because
#      a typical rebase preserves commit subjects even when SHAs change.
#   3. Date anchoring is NOT used: a force-push rebase rewrites every
#      commit's committer-date, so --since against the prev release's
#      publishedAt walks the full rewritten history instead of the slice.
#   4. If layers 1+2 yield nothing, walk from the repo's root commit.
#      First-release fallback.
# Surfaces a ::warning:: when layer 2 or 4 fires so the operator sees in
# workflow logs that a fallback was used.
function Resolve-PrevTagForSlice([string]$Tag, [string]$Repo) {
    # Function-local relaxation of EAP. The script's outer Stop is intact
    # for non-git logic, but the prev-tag probes legitimately fail in
    # several cases (no tags yet, orphaned tags after rewrite, gh not
    # authed) and need to be soft-failures here.
    $ErrorActionPreference = 'Continue'

    # Layer 1: describe + sanity gate.
    $prevRef = & git describe --tags --abbrev=0 "$Tag^" 2>$null
    if ($LASTEXITCODE -eq 0 -and $prevRef) {
        $prevTag = $prevRef.Trim()
        $count = & git rev-list --count "$prevTag..$Tag" 2>$null
        if ($LASTEXITCODE -eq 0 -and [int]$count -le 50) {
            return @{
                Tag     = $prevTag
                LogArgs = @("$prevTag..$Tag")
                Display = "$prevTag..$Tag"
                Source  = 'describe'
            }
        }
        Write-Host "::warning::Slice from $prevTag..$Tag is $count commits (>50 cap). Falling back to subject-match against the most recent published release."
    }

    # Layer 2: subject-match the most recent published GitHub release.
    if ($Repo) {
        $listJson = & gh release list --repo $Repo --limit 20 --json tagName,publishedAt,isPrerelease 2>$null
        if ($LASTEXITCODE -eq 0 -and $listJson) {
            $candidatePrevTag = $null
            try {
                $releases = $listJson | ConvertFrom-Json
                $candidate = $releases |
                    Where-Object { $_.tagName -ne $Tag -and -not $_.isPrerelease } |
                    Sort-Object publishedAt -Descending |
                    Select-Object -First 1
                if ($candidate) { $candidatePrevTag = $candidate.tagName }
            } catch {
                Write-Host "::warning::Failed to parse 'gh release list' output: $_."
            }

            if ($candidatePrevTag) {
                $orphanSha = & git rev-parse $candidatePrevTag 2>$null
                if ($LASTEXITCODE -eq 0 -and $orphanSha) {
                    $orphanSha = $orphanSha.Trim()
                    $orphanSubject = & git show -s --format=%s $orphanSha 2>$null
                    if ($LASTEXITCODE -eq 0 -and $orphanSubject) {
                        $rebasedSha = $null
                        $logLines = & git log $Tag --format='%H%x09%s' 2>$null
                        if ($LASTEXITCODE -eq 0 -and $logLines) {
                            $lineArr = if ($logLines -is [array]) { $logLines } else { ,$logLines }
                            foreach ($line in $lineArr) {
                                if (-not $line) { continue }
                                $parts = $line -split "`t", 2
                                if ($parts.Count -eq 2 -and $parts[1] -eq $orphanSubject) {
                                    $rebasedSha = $parts[0]
                                    break
                                }
                            }
                        }
                        if ($rebasedSha) {
                            $shortSha = $rebasedSha.Substring(0, 12)
                            Write-Host "::warning::Subject-matched slice: prev tag $candidatePrevTag (orphan sha $($orphanSha.Substring(0,12))) matches current-history sha $shortSha by subject; using $shortSha..$Tag."
                            return @{
                                Tag     = $candidatePrevTag
                                LogArgs = @("$rebasedSha..$Tag")
                                Display = "$candidatePrevTag..$Tag (subject-matched at $shortSha)"
                                Source  = 'subject-match'
                            }
                        }
                        Write-Host "::warning::Prev tag $candidatePrevTag subject '$orphanSubject' not found in current $Tag history. Falling back to root walk."
                    }
                }
            }
        } else {
            Write-Host "::warning::'gh release list' produced no usable output (gh not authed or no releases yet). Falling back to root walk."
        }
    }

    # Layer 3: root walk.
    $root = (& git rev-list --max-parents=0 HEAD | Select-Object -First 1).Trim()
    Write-Host "::warning::No prior tag matched; walking from root $root."
    return @{
        Tag     = $null
        LogArgs = @("$root..$Tag")
        Display = "$root..$Tag (root walk)"
        Source  = 'root'
    }
}

$prevInfo = Resolve-PrevTagForSlice -Tag $Tag -Repo $Repo
$prevTag  = $prevInfo.Tag
$logArgs  = $prevInfo.LogArgs
$range    = $prevInfo.Display

# %H = full sha, %h = short sha, %an = author name, %s = subject. Tabs as field
# separator are safe -- git rejects literal tabs in author names and subjects
# don't contain them in any of these repos.
$raw = & git log @logArgs --no-merges --pretty=format:"%H`t%h`t%an`t%s" 2>$null
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

# Token substitution map. Templates and emitted sections reference these by
# {key} string (literal curly braces in the template). Any value the resolver
# could not compute is left as the literal {key} in the output so the operator
# sees the omission rather than blank space.
$ownerOnly = ''
$repoShort = ''
if ($Repo -and ($Repo -match '/')) {
    $parts = $Repo -split '/', 2
    $ownerOnly = $parts[0]
    $repoShort = $parts[1]
} elseif ($Repo) {
    $repoShort = $Repo
}
$tagCommitSha = ''
$tagCommitShort = ''
$tagSha = & git rev-parse $Tag 2>$null
if ($LASTEXITCODE -eq 0 -and $tagSha) {
    $tagCommitSha = $tagSha.Trim()
    if ($tagCommitSha.Length -ge 12) { $tagCommitShort = $tagCommitSha.Substring(0, 12) }
}
$priorTagToken = if ($prevTag) { $prevTag } else { '' }
$zipNameToken  = if ($ZipName) { $ZipName } elseif ($ZipPath) { (Split-Path -Leaf $ZipPath) } else { '' }
$tokens = @{
    '{tag}'              = $Tag
    '{version}'          = ($Tag -replace '^v', '')
    '{owner}'            = $ownerOnly
    '{repo}'             = $repoShort
    '{full-repo}'        = $Repo
    '{commit-sha}'       = $tagCommitSha
    '{commit-sha-short}' = $tagCommitShort
    '{prior-tag}'        = $priorTagToken
    '{zip-name}'         = $zipNameToken
}

function Expand-Tokens([string] $text, [hashtable] $map) {
    if (-not $text) { return $text }
    foreach ($key in $map.Keys) {
        $val = $map[$key]
        if ($null -eq $val) { $val = '' }
        $text = $text.Replace($key, $val)
    }
    return $text
}

function Format-Bytes([long] $bytes) {
    if ($bytes -ge 1MB) { return ('{0:F2} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:F2} KB' -f ($bytes / 1KB)) }
    return ('{0} B' -f $bytes)
}

function Read-TemplateSection([string] $name, [string] $dir, [hashtable] $tokenMap) {
    $path = Join-Path -Path $dir -ChildPath "$name.md"
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "::warning::Release-body template missing: $path. Section '$name' will not render."
        return $null
    }
    $content = (Get-Content -LiteralPath $path -Raw -Encoding UTF8).Trim()
    if (-not $content) { return $null }
    return (Expand-Tokens -text $content -map $tokenMap)
}

$sb = [System.Text.StringBuilder]::new()
if ($repoShort) {
    [void]$sb.AppendLine("# $repoShort $Tag")
    [void]$sb.AppendLine()
}
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

# --- File integrity ---
# Composes a code-block with the release zip on the first line and indented
# inner-file rows below. Inner-file hashes come from the manifest emitted by
# build.ps1 (release/<zip-name>.manifest.tsv); the zip itself is hashed by the
# workflow's "Locate release zip" step and passed in via -ZipPath/-ZipSha256/-ZipSize.
# If any of those are missing (running locally without a build, or the workflow
# wiring is incomplete), the section is skipped with a warning so the operator
# notices.
$includeIntegrity = $ZipPath -and $ZipSha256 -and $ZipSize -gt 0 -and $Manifest -and (Test-Path -LiteralPath $Manifest)
if ($includeIntegrity) {
    $manifestEntries = @()
    foreach ($line in Get-Content -LiteralPath $Manifest -Encoding UTF8) {
        if (-not $line) { continue }
        $parts = $line -split "`t", 3
        if ($parts.Count -ne 3) {
            Write-Host "::warning::Skipping malformed manifest line: $line"
            continue
        }
        $manifestEntries += [pscustomobject]@{
            Sha256 = $parts[0]
            Size   = [long]$parts[1]
            Path   = $parts[2]
        }
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("## File integrity")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("Every file in the release zip is hashed below. Verify with ``Get-FileHash <file> -Algorithm SHA256`` on PowerShell.")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('```')
    $zipNameForLine = if ($zipNameToken) { $zipNameToken } else { Split-Path -Leaf $ZipPath }
    $zipSizeStr = Format-Bytes $ZipSize
    [void]$sb.AppendLine(("{0,-36}    {1,8}    SHA256: {2}" -f $zipNameForLine, $zipSizeStr, $ZipSha256.ToUpper()))
    [void]$sb.AppendLine()
    # Top-level files before subdirectory files; alphabetical within each
    # group. Reads better for a user verifying a hash: the binary they
    # double-click on is at the top.
    $sortedEntries = $manifestEntries |
        Sort-Object @{Expression = { ($_.Path -split '/').Count }}, Path
    foreach ($entry in $sortedEntries) {
        $indented = "  " + $entry.Path
        $sizeStr = Format-Bytes $entry.Size
        [void]$sb.AppendLine(("{0,-36}    {1,8}    SHA256: {2}" -f $indented, $sizeStr, $entry.Sha256.ToUpper()))
    }
    [void]$sb.AppendLine('```')
} elseif ($Manifest -or $ZipPath -or $ZipSha256) {
    Write-Host "::warning::File-integrity section skipped: -Manifest, -ZipPath, -ZipSize, and -ZipSha256 must all be set. Got Manifest='$Manifest' ZipPath='$ZipPath' ZipSize=$ZipSize ZipSha256='$ZipSha256'."
}

# --- Templated evergreen sections ---
# Each template is repo-curated content under .github/release-template/<name>.md.
# Read in this fixed order; missing templates emit a warning and skip without
# failing the build. Token substitution happens inside Read-TemplateSection.
$templateOrder = @('links', 'install', 'uninstall', 'what-you-need-to-do')
foreach ($name in $templateOrder) {
    $section = Read-TemplateSection -name $name -dir $TemplateDir -tokenMap $tokens
    if ($section) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine($section)
    }
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
