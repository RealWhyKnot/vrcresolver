<#
.SYNOPSIS
  Maintains CHANGELOG.md (root, embedded into the in-app viewer) and
  wiki/Changelog.md (synced to the GitHub Wiki) so the changelog stays
  current without manual editing on every release.

.DESCRIPTION
  Three modes:

    Append   Parses commit subjects in -Range, buckets them by conventional-
             commit type, and inserts bullets under the "## Unreleased" heading
             at the top of both files. Skips merge commits, bot commits, and
             commits explicitly tagged "[skip changelog]" in the subject. Skips
             types that aren't user-visible (docs/build/ci/chore/test) so the
             changelog stays focused on behavior changes.

    Promote  Renames the "## Unreleased" heading to "## [vTAG](...) -- DATE",
             links to the GitHub release page, and inserts a fresh empty
             "## Unreleased" section above. Used by release.yml on tag push so
             the embedded CHANGELOG.md inside the shipped exe carries the
             versioned notes for that release.

    Notes    Reads the current "## Unreleased" section content (or, with
             -ForVersion, reads the section for that version) and writes it to
             stdout. Used to inject the section into the GitHub release body.

  The script is invoked from .github/workflows/changelog-append.yml and
  .github/workflows/release.yml. PowerShell Core (pwsh) is used because both
  Windows and Ubuntu runners ship it, and the existing build tooling is
  PowerShell-native.

.PARAMETER Mode
  Append | Promote | Notes

.PARAMETER Range
  git-log range, e.g. "abc123..def456". Required for Append.

.PARAMETER Version
  Tag, e.g. "v2026.4.27.3". Required for Promote and Notes (with -ForVersion).

.PARAMETER ForVersion
  When set on Notes mode, returns the section for that already-promoted
  version instead of the live "## Unreleased" section.

.PARAMETER Repo
  owner/repo for release-link generation, defaults to $env:GITHUB_REPOSITORY.

.PARAMETER RepoRoot
  Repo root, defaults to the script's parent's parent's parent (.github/scripts/.. -> .github/.. -> repo root).
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Append', 'Promote', 'Notes')]
    [string]$Mode,

    [string]$Range,
    [string]$Version,
    [switch]$ForVersion,
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

# Both files are kept in lock-step. The root copy is the canonical CHANGELOG;
# the wiki/ copy is mirrored to the GitHub Wiki by .github/workflows/wiki-sync.yml.
$RootChangelog = Join-Path $RepoRoot 'CHANGELOG.md'
$WikiChangelog = Join-Path $RepoRoot 'wiki/Changelog.md'

if (-not (Test-Path $RootChangelog)) { throw "CHANGELOG.md not found at $RootChangelog" }
if (-not (Test-Path $WikiChangelog)) { throw "wiki/Changelog.md not found at $WikiChangelog" }

# --- Helpers ---------------------------------------------------------------

# Read a file as UTF-8 text. We deliberately avoid BOM round-trips: the prepare-
# commit-msg hook already had to defend against PowerShell-written BOMs in
# version.txt; let's not seed any new ones in changelog files.
function Read-TextUtf8 {
    param([string]$Path)
    return [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
}

function Write-TextUtf8 {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

# Strip the (YYYY.M.D.N-XXXX) or (YYYY.M.D.N) build stamp the prepare-commit-msg
# hook appends to every subject in this repo. Without this every changelog bullet
# would carry a noisy "(2026.4.27.14-A130)" tail.
function Strip-BuildStamp {
    param([string]$Subject)
    return ($Subject -replace ' \(\d{4}\.\d+\.\d+\.\d+(-[A-Fa-f0-9]{4,8})?\)$', '').Trim()
}

# Conventional-commit bucketing. Returns $null for types we deliberately skip
# (docs/build/ci/test/non-deps chore) -- those are real work but not user-visible
# release notes. Returns @{Bucket=...; Bullet=...} otherwise.
function Parse-CommitSubject {
    param([string]$Sha, [string]$Subject)

    $stripped = Strip-BuildStamp -Subject $Subject
    if ([string]::IsNullOrWhiteSpace($stripped)) { return $null }

    # Skip explicit opt-out token. Lets a user push a no-op-from-changelog-
    # perspective commit (typo fix, doc reword) without it showing up.
    if ($stripped -match '\[skip changelog\]') { return $null }

    # Skip merge subjects. `git log --no-merges` already excludes them in
    # Append mode, but be defensive.
    if ($stripped -match '^Merge ') { return $null }

    $pattern = '^(?<type>feat|fix|perf|refactor|docs|build|ci|chore|test|revert)(?:\((?<scope>[^)]+)\))?(?<bang>!)?:\s+(?<desc>.+)$'
    $m = [regex]::Match($stripped, $pattern)
    if (-not $m.Success) {
        # Non-conventional commit -- surface it under "Changed" rather than
        # silently dropping. Engineering-Standards mandates the format, but
        # one slipped through is better in the changelog than missing.
        return @{
            Bucket = 'Changed'
            Bullet = "- $stripped (" + $Sha.Substring(0, 7) + ')'
        }
    }

    $type = $m.Groups['type'].Value
    $scope = $m.Groups['scope'].Value
    $isBreaking = $m.Groups['bang'].Success
    $desc = $m.Groups['desc'].Value

    # Capitalise the description to match the existing changelog's "Added"
    # / "Fixed" prose style.
    if ($desc.Length -gt 0) {
        $desc = $desc.Substring(0, 1).ToUpper() + $desc.Substring(1)
    }

    $bucket = switch ($type) {
        'feat'     { 'Added' }
        'fix'      { 'Fixed' }
        'perf'     { 'Changed' }
        'refactor' { 'Changed' }
        'revert'   { 'Changed' }
        'chore'    {
            # Surface dependency bumps (Dependabot et al.) -- those are user-
            # facing in the security/footprint sense -- but skip everything
            # else. `chore(deps)` and `chore(deps-dev)` both qualify.
            if ($scope -and $scope -match '^deps') { 'Changed' } else { $null }
        }
        default    { $null }  # docs / build / ci / test
    }

    if (-not $bucket) { return $null }
    if ($isBreaking)  { $bucket = 'Breaking' }

    $scopePrefix = if ($scope) { "**${scope}:** " } else { '' }
    $shortSha = $Sha.Substring(0, 7)
    $bullet = "- $scopePrefix$desc ($shortSha)"

    return @{ Bucket = $bucket; Bullet = $bullet }
}

# Order matters for rendering -- Breaking comes first, then Added, Changed, Fixed.
$BucketOrder = @('Breaking', 'Added', 'Changed', 'Fixed')

# Find the "## Unreleased" section in $content. Returns @{Start=int; End=int;
# Body=string} where Body is everything between the heading and the next "---"
# separator (exclusive). Returns $null if the section isn't present.
function Find-UnreleasedSection {
    param([string]$Content)

    $lines = $Content -split "`n"
    $startIdx = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match '^##\s+Unreleased\s*$') {
            $startIdx = $i
            break
        }
    }
    if ($startIdx -lt 0) { return $null }

    # Section body runs from startIdx+1 up to (but not including) the next
    # "---" separator. The convention in CHANGELOG.md is "---" between sections.
    $endIdx = $lines.Length
    for ($j = $startIdx + 1; $j -lt $lines.Length; $j++) {
        if ($lines[$j] -match '^---\s*$') {
            $endIdx = $j
            break
        }
        # Defensive: another ## heading without a separator means we ran past.
        if ($lines[$j] -match '^##\s+') {
            $endIdx = $j
            break
        }
    }

    return @{
        StartIdx = $startIdx
        EndIdx   = $endIdx
        Lines    = $lines
    }
}

# Parse an existing Unreleased body into bucket -> bullets[] map.
# Body lines look like:
#   ### Added
#   - foo (abc1234)
#   - bar (def5678)
#
#   ### Fixed
#   - baz (...)
function Parse-UnreleasedBody {
    param([string[]]$BodyLines)

    $buckets = [ordered]@{}
    $current = $null
    foreach ($line in $BodyLines) {
        if ($line -match '^###\s+(?<name>.+?)\s*$') {
            $current = $matches['name']
            if (-not $buckets.Contains($current)) { $buckets[$current] = @() }
            continue
        }
        if ($line -match '^\s*- ' -and $current) {
            $buckets[$current] += $line
        }
    }
    return $buckets
}

# Render bucket map to body lines.
function Render-UnreleasedBody {
    param([hashtable]$Buckets)

    if ($Buckets.Count -eq 0) {
        # Empty body -- match the seed placeholder so the file stays consistent
        # and the seed comment in the file makes sense.
        return @('', '_No notable changes since the last release._', '')
    }

    $out = @('')
    $emitted = @{}
    foreach ($name in $BucketOrder) {
        if ($Buckets.Contains($name) -and $Buckets[$name].Count -gt 0) {
            $out += "### $name"
            $out += $Buckets[$name]
            $out += ''
            $emitted[$name] = $true
        }
    }
    # Anything not in the canonical order (custom bucket someone hand-added)
    # -- preserve it after the canonical ones.
    foreach ($name in $Buckets.Keys) {
        if (-not $emitted.ContainsKey($name) -and $Buckets[$name].Count -gt 0) {
            $out += "### $name"
            $out += $Buckets[$name]
            $out += ''
        }
    }
    return $out
}

function Update-OneFile {
    param(
        [string]$Path,
        [hashtable]$NewBullets   # bucket -> string[] of bullets
    )

    $content = Read-TextUtf8 -Path $Path
    $section = Find-UnreleasedSection -Content $content
    if (-not $section) {
        throw "$Path is missing the '## Unreleased' section. Add a stub heading at the top before running the appender."
    }

    $bodyStart = $section.StartIdx + 1
    $bodyEnd   = $section.EndIdx - 1
    $bodyLines = if ($bodyEnd -ge $bodyStart) { $section.Lines[$bodyStart..$bodyEnd] } else { @() }

    $existing = Parse-UnreleasedBody -BodyLines $bodyLines

    foreach ($bucket in $NewBullets.Keys) {
        if (-not $existing.Contains($bucket)) { $existing[$bucket] = @() }
        foreach ($bullet in $NewBullets[$bucket]) {
            # De-dupe: skip if the same short-sha is already present (rerun-
            # safety for cases where the workflow re-fires for some reason).
            $sha = if ($bullet -match '\(([a-f0-9]{7})\)\s*$') { $matches[1] } else { $null }
            if ($sha) {
                $alreadyHas = $false
                foreach ($line in $existing[$bucket]) {
                    if ($line -match "\($sha\)") { $alreadyHas = $true; break }
                }
                if ($alreadyHas) { continue }
            }
            $existing[$bucket] += $bullet
        }
    }

    $rendered = Render-UnreleasedBody -Buckets $existing
    $before = if ($section.StartIdx -gt 0) { $section.Lines[0..($section.StartIdx)] } else { @($section.Lines[0]) }
    $after  = $section.Lines[$section.EndIdx..($section.Lines.Length - 1)]

    $newLines = @()
    $newLines += $before
    $newLines += $rendered
    $newLines += $after

    $newContent = ($newLines -join "`n")
    Write-TextUtf8 -Path $Path -Content $newContent
}

# --- Mode: Append ----------------------------------------------------------

if ($Mode -eq 'Append') {
    if (-not $Range) { throw "Append mode requires -Range (e.g. abc..def)." }

    Push-Location $RepoRoot
    try {
        # %H = full sha, %s = subject, %ae = author email, %P = parents (for
        # merge filtering -- we also pass --no-merges as a belt-and-braces).
        $log = & git log --no-merges --format='%H%x09%s%x09%ae' $Range 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "git log returned non-zero for range '$Range' -- treating as no commits."
            return
        }
    } finally {
        Pop-Location
    }

    if (-not $log) {
        Write-Host "No commits in range $Range -- nothing to append."
        return
    }

    $newBullets = @{}
    $considered = 0
    $included = 0
    foreach ($line in ($log -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split "`t"
        if ($parts.Length -lt 3) { continue }
        $sha = $parts[0]; $subject = $parts[1]; $email = $parts[2]
        $considered++

        # Skip bot commits. The appender itself pushes as github-actions[bot]
        # via GITHUB_TOKEN, so any future re-runs across that boundary
        # would otherwise self-cite.
        if ($email -match 'github-actions\[bot\]' -or $email -match 'noreply@github.com') { continue }

        $parsed = Parse-CommitSubject -Sha $sha -Subject $subject
        if (-not $parsed) { continue }

        $bucket = $parsed.Bucket
        if (-not $newBullets.ContainsKey($bucket)) { $newBullets[$bucket] = @() }
        $newBullets[$bucket] += $parsed.Bullet
        $included++
    }

    Write-Host "Considered $considered commit(s), included $included in changelog."

    if ($included -eq 0) {
        Write-Host "Nothing user-visible in this push; CHANGELOG unchanged."
        return
    }

    Update-OneFile -Path $RootChangelog -NewBullets $newBullets
    Update-OneFile -Path $WikiChangelog -NewBullets $newBullets

    Write-Host "Appended $included entr(ies) to both CHANGELOG.md and wiki/Changelog.md."
    return
}

# --- Mode: Promote ---------------------------------------------------------

if ($Mode -eq 'Promote') {
    if (-not $Version) { throw "Promote mode requires -Version (e.g. v2026.4.27.3)." }
    if (-not $Repo)    { throw "Promote mode requires -Repo or `$env:GITHUB_REPOSITORY (e.g. owner/repo)." }

    $today = (Get-Date -Format 'yyyy-MM-dd')
    $emdash = [char]0x2014  # avoid embedding raw em-dash so the script parses identically on PS 5.1 (ANSI-default) and pwsh
    $heading = "## [$Version](https://github.com/$Repo/releases/tag/$Version) $emdash $today"

    foreach ($path in @($RootChangelog, $WikiChangelog)) {
        $content = Read-TextUtf8 -Path $path
        $section = Find-UnreleasedSection -Content $content
        if (-not $section) {
            throw "$path is missing the '## Unreleased' section. Cannot promote."
        }

        $lines = $section.Lines
        $bodyStart = $section.StartIdx + 1
        $bodyEnd   = $section.EndIdx - 1
        $bodyLines = if ($bodyEnd -ge $bodyStart) { $lines[$bodyStart..$bodyEnd] } else { @() }

        # Drop the placeholder if it's the only thing in the body.
        $hasReal = $false
        foreach ($l in $bodyLines) {
            if ($l -match '^\s*- ' -or $l -match '^###\s+') { $hasReal = $true; break }
        }
        if (-not $hasReal) {
            # Empty section: the released version still gets an entry, just
            # with a stub note. This keeps the heading -> release-page link
            # alive so users browsing the changelog can click through.
            $bodyLines = @('', '_Maintenance release; see commit log for details._', '')
        }

        # Build the new file:
        #   <preamble through line StartIdx-1>
        #   ## Unreleased
        #
        #   _No notable changes since the last release._
        #
        #   ---
        #
        #   ## [vX] - DATE
        #   <bodyLines>
        #   ---
        #   <rest>
        $before = if ($section.StartIdx -gt 0) { $lines[0..($section.StartIdx - 1)] } else { @() }
        $after  = $lines[$section.EndIdx..($lines.Length - 1)]

        $newLines = @()
        $newLines += $before
        $newLines += '## Unreleased'
        $newLines += ''
        $newLines += '_No notable changes since the last release._'
        $newLines += ''
        $newLines += '---'
        $newLines += ''
        $newLines += $heading
        $newLines += $bodyLines
        # $after starts with the existing "---" separator (or next ## heading).
        $newLines += $after

        Write-TextUtf8 -Path $path -Content (($newLines -join "`n"))
    }

    Write-Host "Promoted Unreleased -> $heading in both files."
    return
}

# --- Mode: Notes -----------------------------------------------------------

if ($Mode -eq 'Notes') {
    # Read from the root copy -- same content as wiki copy by construction.
    $content = Read-TextUtf8 -Path $RootChangelog

    if ($ForVersion) {
        if (-not $Version) { throw "Notes -ForVersion requires -Version." }
        # Find "## [vTag](...) -- DATE" -- the heading pattern Promote writes.
        $escaped = [regex]::Escape($Version)
        $pattern = "(?ms)^##\s+\[" + $escaped + "\][^\n]*\n(.*?)(?=^---\s*$|^##\s+|\z)"
        $m = [regex]::Match($content, $pattern)
        if (-not $m.Success) {
            Write-Error "No section found for $Version in CHANGELOG.md."
            exit 1
        }
        Write-Output $m.Groups[1].Value.Trim()
        return
    }

    $section = Find-UnreleasedSection -Content $content
    if (-not $section) { Write-Error "No '## Unreleased' section found."; exit 1 }
    $bodyStart = $section.StartIdx + 1
    $bodyEnd   = $section.EndIdx - 1
    $bodyLines = if ($bodyEnd -ge $bodyStart) { $section.Lines[$bodyStart..$bodyEnd] } else { @() }
    Write-Output (($bodyLines -join "`n").Trim())
    return
}
