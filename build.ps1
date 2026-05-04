param(
    # Release.yml passes the bare git tag (no leading "v") so the published tag,
    # zip filename, and embedded assembly version stay in sync. Local builds
    # leave this empty and get an auto-derived YYYY.M.D.N-XXXX dev stamp.
    [string]$Version = "",

    # Skip building the release/ zip. dist/ is still produced.
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Activate the repo's tracked git hooks (.githooks/) on first build in a clone.
# Idempotent; harmlessly no-ops outside a git checkout.
try { & git config --local core.hooksPath .githooks 2>$null } catch {}

$BuildDir     = Join-Path $PSScriptRoot "dist"
$ReleaseDir   = Join-Path $PSScriptRoot "release"
$ToolsStage   = Join-Path $PSScriptRoot "_tools_staging"
$StateFile    = Join-Path $PSScriptRoot ".local_build_state.json"

# --- Versioning ---
if ($Version) {
    if ($Version -notmatch '^\d{4}\.\d+\.\d+\.\d+(-[A-Fa-f0-9]{4})?$') {
        throw "Invalid -Version '$Version'. Expected YYYY.M.D.N (release) or YYYY.M.D.N-XXXX (dev)."
    }
    $FullVersion = $Version
} else {
    $Today = Get-Date -Format "yyyy.M.d"
    $BuildCount = 0
    if (Test-Path $StateFile) {
        $State = Get-Content $StateFile | ConvertFrom-Json
        if ($State.Date -eq $Today) { $BuildCount = [int]$State.Count + 1 }
    }
    $UID = [Guid]::NewGuid().ToString().Substring(0,4).ToUpper()
    $FullVersion = "$Today.$BuildCount-$UID"
    @{ Date = $Today; Count = $BuildCount } | ConvertTo-Json | Out-File $StateFile -Encoding utf8
}
# AssemblyVersion must be pure numeric (no -XXXX dev suffix)
$AsmVersion = ($FullVersion -split '-')[0]
Write-Host "Building Version: $FullVersion" -ForegroundColor Magenta

# --- Clean dist ---
if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force }
New-Item -ItemType Directory $BuildDir -Force | Out-Null
if (-not (Test-Path $ToolsStage)) { New-Item -ItemType Directory $ToolsStage -Force | Out-Null }

# --- Bundled yt-dlp fallback (vanilla, for og-restore on missing backup) ---
$YtDlpFallback = Join-Path $ToolsStage "yt-dlp-og-fallback.exe"
$YtDlpVerFile  = Join-Path $ToolsStage "yt-dlp-og-fallback.version.txt"
Write-Host "`n--- Fetching bundled yt-dlp fallback ---" -ForegroundColor Cyan
$ApiHeaders = @{ "User-Agent" = "WKVRCProxy-build" }
$LatestYtDlp = (Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" -Headers $ApiHeaders).tag_name
$NeedRefresh = $true
if ((Test-Path $YtDlpFallback) -and (Test-Path $YtDlpVerFile)) {
    $current = (Get-Content $YtDlpVerFile -Raw -ErrorAction SilentlyContinue).Trim()
    if ($current -eq $LatestYtDlp) { $NeedRefresh = $false }
}
if ($NeedRefresh) {
    Invoke-WebRequest -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile $YtDlpFallback -UseBasicParsing
    $LatestYtDlp | Out-File $YtDlpVerFile -Encoding utf8 -NoNewline
    Write-Host "Fetched yt-dlp $LatestYtDlp." -ForegroundColor Green
} else {
    Write-Host "yt-dlp fallback up-to-date ($LatestYtDlp)." -ForegroundColor Green
}

# --- Publish .NET projects ---
Write-Host "`n--- Publishing ---" -ForegroundColor Cyan
$PubArgs = @("-c","Release","-r","win-x64","--self-contained","true",
             "/p:PublishSingleFile=true","/p:Version=$AsmVersion",
             "-o",$BuildDir,"--nologo")
dotnet publish "src/WKVRCProxy/WKVRCProxy.csproj" @PubArgs
if ($LASTEXITCODE -ne 0) { throw "WKVRCProxy publish failed" }
dotnet publish "src/WKVRCProxy.Updater/WKVRCProxy.Updater.csproj" @PubArgs
if ($LASTEXITCODE -ne 0) { throw "WKVRCProxy.Updater publish failed" }
dotnet publish "src/WKVRCProxy.Uninstaller/WKVRCProxy.Uninstaller.csproj" @PubArgs
if ($LASTEXITCODE -ne 0) { throw "WKVRCProxy.Uninstaller publish failed" }

# --- Stage tools/ subdir in dist ---
$BuildTools = Join-Path $BuildDir "tools"
New-Item -ItemType Directory $BuildTools -Force | Out-Null
Copy-Item $YtDlpFallback (Join-Path $BuildTools "yt-dlp-og-fallback.exe") -Force
Copy-Item $YtDlpVerFile  (Join-Path $BuildTools "yt-dlp-og-fallback.version.txt") -Force

# Publish the patched yt-dlp wrapper directly into dist/tools/. AssemblyName=yt-dlp
# in the project produces yt-dlp.exe; PatchManager copies this over VRChat's
# Tools/yt-dlp.exe at runtime.
#
# AOT publish for the wrapper specifically (csproj sets PublishAot/PublishTrimmed
# /TrimMode=full/InvariantGlobalization). Cuts the wrapper from ~79 MB to ~3 MB
# and removes JIT cold-start cost — VRChat invokes this binary per video player
# so size + startup directly impact in-game stutter on world load. The watchdog
# stays on regular self-contained .NET 10 (one-shot launch; size doesn't matter).
#
# Drops PublishSingleFile (AOT incompatible — produces a single native .exe
# inherently). Adds vswhere.exe to PATH for the link.exe lookup since MSBuild's
# AOT target invokes vswhere unqualified — VS Build Tools install it at
# %ProgramFiles(x86)%\Microsoft Visual Studio\Installer but don't put it on PATH.
$ProgFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$VsInstaller = Join-Path $ProgFilesX86 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $VsInstaller 'vswhere.exe')) {
    if ($env:PATH -notlike "*$VsInstaller*") { $env:PATH = "$VsInstaller;$env:PATH" }
} else {
    Write-Warning "vswhere.exe not found at $VsInstaller -- AOT link step may fail. Install Visual Studio Build Tools with the Desktop C++ workload."
}
$YtDlpPubArgs = @("-c","Release","-r","win-x64","--self-contained","true",
                  "/p:Version=$AsmVersion",
                  "-o",$BuildTools,"--nologo")
dotnet publish "src/WKVRCProxy.YtDlp/WKVRCProxy.YtDlp.csproj" @YtDlpPubArgs
if ($LASTEXITCODE -ne 0) { throw "WKVRCProxy.YtDlp publish failed" }

# AOT publish leaves a yt-dlp.pdb (~14 MB) we don't need to ship — the .pdb
# strip below picks it up.

# --- Trim debug symbols we don't ship ---
Get-ChildItem $BuildDir -Filter "*.pdb" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# --- Zip ---
if (-not $SkipZip) {
    if (-not (Test-Path $ReleaseDir)) { New-Item -ItemType Directory $ReleaseDir -Force | Out-Null }
    $ZipPath = Join-Path $ReleaseDir "WKVRCProxy-v$FullVersion.zip"
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path (Join-Path $BuildDir "*") -DestinationPath $ZipPath
    Write-Host "`nRelease zip: $ZipPath" -ForegroundColor Green
}

Write-Host "`nBuild complete: v$FullVersion" -ForegroundColor Green
