$ErrorActionPreference = "Stop"

# Pin the working directory to the script's own root so relative paths (src/..., dist/...)
# resolve consistently regardless of how the script is invoked. Without this, a prior failed
# run that left a persistent shell inside src/WKVRCProxy.UI/ui would break every subsequent
# relative-path lookup here.
Set-Location $PSScriptRoot

# Activate the repo's tracked git hooks (.githooks/) the first time the build runs in a clone.
# We do this from the build script — rather than asking users to run `git config core.hooksPath`
# manually — because forgetting the setup silently disables the hooks, which defeats the point of
# checks like the commit-msg version-stamp guard. Idempotent: only writes when it would actually
# change. If we're not inside a git checkout (e.g. someone runs the script from an extracted zip),
# the call no-ops harmlessly.
try {
    $current = (& git config --local --get core.hooksPath 2>$null)
    if ($LASTEXITCODE -eq 0 -and $current -ne ".githooks") {
        & git config --local core.hooksPath .githooks
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Configured local git core.hooksPath = .githooks (commit-msg guard now active)." -ForegroundColor DarkGray
        }
    } elseif ($LASTEXITCODE -ne 0) {
        # `git config --get` returns 1 when the key is unset. Treat that as "needs setup" too.
        & git config --local core.hooksPath .githooks 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Configured local git core.hooksPath = .githooks (commit-msg guard now active)." -ForegroundColor DarkGray
        }
    }
} catch { }

$BuildDir = Join-Path $PSScriptRoot "dist"
$VendorDir = Join-Path $PSScriptRoot "vendor"
$VersionFile = Join-Path $VendorDir "versions.json"
$LocalVersionState = Join-Path $VendorDir "local_build_state.json"

$WasRunning = ($null -ne (Get-Process "WKVRCProxy" -ErrorAction SilentlyContinue)) -or
              ($null -ne (Get-Process "WKVRCProxy.UI" -ErrorAction SilentlyContinue))

if (Test-Path $BuildDir) {
    Write-Host "Cleaning dist folder..." -ForegroundColor Cyan

    # Terminate running instances to release file locks
    Get-Process "WKVRCProxy.UI" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process "WKVRCProxy" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process "redirector" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process "bgutil-ytdlp-pot-provider" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process "curl-impersonate-win" -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process "streamlink" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    try {
        Remove-Item -Path $BuildDir -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Host "Warning: Failed to fully clean dist folder. Some files may be in use." -ForegroundColor Yellow
    }
}
New-Item -ItemType Directory $BuildDir -Force | Out-Null
if (!(Test-Path $VendorDir)) { New-Item -ItemType Directory $VendorDir }

# --- Dependency Tracking ---
# Use a hashtable, not the PSCustomObject ConvertFrom-Json hands back. PSCustomObject silently
# refuses property assignment for keys that didn't exist in the source JSON (PS 5.1 behaviour),
# which previously caused curlimp / ytdlp / deno version stamps to never persist for users whose
# versions.json predated those keys — every build re-fetched even when nothing changed. Hashtables
# accept new keys via simple `=`, so each fetcher's `$Versions.X = $latest` survives the round-trip.
$Versions = @{ ytdlp = ""; deno = ""; curlimp = ""; bgutil = ""; streamlink = ""; wgcf = ""; wireproxy = "" }
if (Test-Path $VersionFile) {
    $Loaded = Get-Content $VersionFile | ConvertFrom-Json
    foreach ($prop in $Loaded.psobject.properties) {
        $Versions[$prop.Name] = $prop.Value
    }
}

Write-Host "--- Checking Dependencies ---" -ForegroundColor Cyan

# 1. Fetch Latest yt-dlp. Skipped when the cached vendor binary already matches the latest tag —
# previously this section re-checked GitHub on every build but the *binary* download was guarded
# only by the version-string compare; if the cached exe was missing, the deploy step at the end
# would fail. Now also Test-Path-gates the re-download and falls back to the cached binary on
# offline runs (matches the other fetchers).
$YtDlpVendorPath = Join-Path $VendorDir "yt-dlp.exe"
try {
    $YtDlpRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest" -ErrorAction Stop
    $LatestYtDlpVersion = $YtDlpRelease.tag_name
    if ($Versions.ytdlp -ne $LatestYtDlpVersion -or !(Test-Path $YtDlpVendorPath)) {
        Write-Host "Updating yt-dlp to $LatestYtDlpVersion..." -ForegroundColor Yellow
        $DownloadUrl = ($YtDlpRelease.assets | Where-Object { $_.name -eq "yt-dlp.exe" }).browser_download_url
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $YtDlpVendorPath
        $Versions.ytdlp = $LatestYtDlpVersion
        Write-Host "yt-dlp.exe ready ($LatestYtDlpVersion)." -ForegroundColor Green
    } else {
        Write-Host "yt-dlp.exe is up-to-date ($LatestYtDlpVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path $YtDlpVendorPath) {
        Write-Host "yt-dlp.exe found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        throw "yt-dlp.exe not vendored and could not fetch from GitHub. Aborting build."
    }
}

# 2. Fetch Latest Deno. Same skip-when-current pattern as yt-dlp; deno extracts to deno.exe in vendor/.
$DenoVendorPath = Join-Path $VendorDir "deno.exe"
try {
    $DenoRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/denoland/deno/releases/latest" -ErrorAction Stop
    $LatestDenoVersion = $DenoRelease.tag_name
    if ($Versions.deno -ne $LatestDenoVersion -or !(Test-Path $DenoVendorPath)) {
        Write-Host "Updating Deno to $LatestDenoVersion..." -ForegroundColor Yellow
        $DownloadUrl = ($DenoRelease.assets | Where-Object { $_.name -eq "deno-x86_64-pc-windows-msvc.zip" }).browser_download_url
        if (!$DownloadUrl) {
            $DownloadUrl = ($DenoRelease.assets | Where-Object { $_.name -match "x86_64-pc-windows-msvc\.zip" } | Select-Object -First 1).browser_download_url
        }
        $ZipPath = Join-Path $VendorDir "deno.zip"
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath
        Expand-Archive -Path $ZipPath -DestinationPath $VendorDir -Force
        Remove-Item $ZipPath
        $Versions.deno = $LatestDenoVersion
        Write-Host "deno.exe ready ($LatestDenoVersion)." -ForegroundColor Green
    } else {
        Write-Host "deno.exe is up-to-date ($LatestDenoVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path $DenoVendorPath) {
        Write-Host "deno.exe found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        throw "deno.exe not vendored and could not fetch from GitHub. Aborting build (the bgutil sidecar compile and the JS-runtime hookup both depend on it)."
    }
}
# 3. Fetch Latest curl-impersonate-win (RealWhyKnot/curl-impersonate-win)
# Go-based Windows CLI wrapper around bogdanfinn/tls-client for Chrome TLS fingerprint impersonation.
$CurlImpVendorPath = Join-Path $VendorDir "curl-impersonate-win.exe"
try {
    $CurlImpRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/RealWhyKnot/curl-impersonate-win/releases/latest" -ErrorAction Stop
    $LatestCurlImpVersion = $CurlImpRelease.tag_name
    if ($Versions.curlimp -ne $LatestCurlImpVersion -or !(Test-Path $CurlImpVendorPath)) {
        Write-Host "Updating curl-impersonate-win to $LatestCurlImpVersion..." -ForegroundColor Yellow
        $CurlImpAsset = ($CurlImpRelease.assets | Where-Object { $_.name -eq "curl-impersonate-win.exe" } | Select-Object -First 1)
        if ($CurlImpAsset) {
            Invoke-WebRequest -Uri $CurlImpAsset.browser_download_url -OutFile $CurlImpVendorPath
            $Versions.curlimp = $LatestCurlImpVersion
            Write-Host "curl-impersonate-win.exe ready ($LatestCurlImpVersion)." -ForegroundColor Green
        } else {
            Write-Host "Warning: curl-impersonate-win.exe asset not found in release. Relay will use standard HttpClient." -ForegroundColor Yellow
        }
    } else {
        Write-Host "curl-impersonate-win.exe is up-to-date ($LatestCurlImpVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path $CurlImpVendorPath) {
        Write-Host "curl-impersonate-win.exe found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        Write-Host "Note: Could not fetch curl-impersonate-win release. Relay will use standard HttpClient." -ForegroundColor Yellow
    }
}

# 4. Fetch and Compile bgutil-ytdlp-pot-provider implementation
try {
    $BgutilRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Brainicism/bgutil-ytdlp-pot-provider/commits/main" -ErrorAction SilentlyContinue
} catch {
    $BgutilRelease = $null
}

if (!$BgutilRelease) {
    try {
        $BgutilRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Brainicism/bgutil-ytdlp-pot-provider/commits/master" -ErrorAction Stop
    } catch {
        Write-Host "Warning: Failed to fetch bgutil commits. GitHub API might be rate limited." -ForegroundColor Yellow
        $BgutilRelease = $null
    }
}

if ($BgutilRelease) {
    $LatestBgutilCommit = $BgutilRelease.sha.Substring(0, 7)
    $VendorPluginDir = Join-Path $VendorDir "yt-dlp-plugins"
    # yt-dlp requires the layout <plugin-dir>/<PACKAGE-NAME>/yt_dlp_plugins/. Without the
    # intermediate package-name directory it silently reports "Plugin directories: none" and the
    # bgutil PO provider never registers.
    $VendorPluginPkg = Join-Path $VendorPluginDir "bgutil-ytdlp-pot-provider"
    $VendorSidecarExe = Join-Path $VendorDir "bgutil-ytdlp-pot-provider.exe"
    # Rebuild if SHA drifted OR if either artifact (sidecar exe / plugin dir) is missing —
    # handles fresh checkouts where the vendor cache exists but the plugin was never staged.
    $NeedsBgutil = ($Versions.bgutil -ne $LatestBgutilCommit) -or
                   !(Test-Path $VendorSidecarExe) -or
                   !(Test-Path (Join-Path $VendorPluginPkg "yt_dlp_plugins"))

    if (-not $NeedsBgutil) {
        Write-Host "bgutil-ytdlp-pot-provider is up-to-date (commit $LatestBgutilCommit)." -ForegroundColor Green
    }
    if ($NeedsBgutil) {
        Write-Host "Compiling bgutil-ytdlp-pot-provider server + staging plugin at commit $LatestBgutilCommit..." -ForegroundColor Yellow
        $BgutilDir = Join-Path $VendorDir "bgutil_repo"
        if (Test-Path $BgutilDir) { Remove-Item -Path $BgutilDir -Recurse -Force }

        # git clone writes progress to stderr; under PS 5.1 with EAP=Stop that becomes a
        # terminating NativeCommandError even on exit 0. Same pattern as the npm build step below.
        $PrevEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            git clone --depth 1 https://github.com/Brainicism/bgutil-ytdlp-pot-provider.git $BgutilDir
            if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)" }

            Push-Location (Join-Path $BgutilDir "server")
            & (Join-Path $VendorDir "deno.exe") install
            if ($LASTEXITCODE -ne 0) { Pop-Location; throw "deno install failed (exit $LASTEXITCODE)" }
            & (Join-Path $VendorDir "deno.exe") compile -A --output $VendorSidecarExe src/main.ts
            if ($LASTEXITCODE -ne 0) { Pop-Location; throw "deno compile failed (exit $LASTEXITCODE)" }
            Pop-Location
        } finally {
            $ErrorActionPreference = $PrevEap
        }

        # Stage the yt-dlp plugin (pure-Python) into vendor. Shipped alongside yt-dlp.exe and
        # pointed at with --plugin-dirs so yt-dlp can resolve youtubepot-bgutilhttp:base_url=...
        # at request time. This is the only path that mints correctly-bound PO tokens on the client.
        if (Test-Path $VendorPluginDir) { Remove-Item -Path $VendorPluginDir -Recurse -Force }
        New-Item -ItemType Directory -Path $VendorPluginPkg -Force | Out-Null
        Copy-Item -Path (Join-Path $BgutilDir "plugin/yt_dlp_plugins") -Destination (Join-Path $VendorPluginPkg "yt_dlp_plugins") -Recurse -Force
        # SHA marker so the runtime updater can compare without calling `yt-dlp.exe --version`.
        Set-Content -Path (Join-Path $VendorPluginDir ".version") -Value $LatestBgutilCommit -NoNewline -Encoding ASCII

        Remove-Item -Path $BgutilDir -Recurse -Force

        $Versions.bgutil = $LatestBgutilCommit
    }
}

# 5. Fetch Latest Streamlink (Windows portable zip)
$StreamlinkVendorDir = Join-Path $VendorDir "streamlink"
try {
    $StreamlinkRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/streamlink/streamlink/releases/latest" -ErrorAction Stop
    $LatestStreamlinkVersion = $StreamlinkRelease.tag_name
    $SlExePath = Join-Path $StreamlinkVendorDir "bin\streamlink.exe"
    if ($Versions.streamlink -ne $LatestStreamlinkVersion -or !(Test-Path $SlExePath)) {
        Write-Host "Updating Streamlink to $LatestStreamlinkVersion..." -ForegroundColor Yellow
        # Portable zip ships as streamlink-X.Y.Z-1-py3XX-x86_64.zip
        $SlAsset = ($StreamlinkRelease.assets | Where-Object { $_.name -match "x86_64\.zip$" } | Select-Object -First 1)
        if ($SlAsset) {
            $ZipPath = Join-Path $VendorDir "streamlink.zip"
            Invoke-WebRequest -Uri $SlAsset.browser_download_url -OutFile $ZipPath
            $TempExtract = Join-Path $VendorDir "streamlink_extract"
            if (Test-Path $TempExtract) { Remove-Item -Path $TempExtract -Recurse -Force }
            Expand-Archive -Path $ZipPath -DestinationPath $TempExtract -Force
            Remove-Item $ZipPath
            # Zip extracts to a versioned subdir; move its contents to vendor/streamlink/
            $ExtractedSubdir = Get-ChildItem -Path $TempExtract -Directory | Select-Object -First 1
            if ($ExtractedSubdir) {
                if (Test-Path $StreamlinkVendorDir) { Remove-Item -Path $StreamlinkVendorDir -Recurse -Force }
                Move-Item -Path $ExtractedSubdir.FullName -Destination $StreamlinkVendorDir
            }
            Remove-Item -Path $TempExtract -Recurse -Force -ErrorAction SilentlyContinue
            $Versions.streamlink = $LatestStreamlinkVersion
            Write-Host "Streamlink $LatestStreamlinkVersion ready." -ForegroundColor Green
        } else {
            Write-Host "Warning: Streamlink x86_64 zip asset not found in release. Tier 0 resolution will be skipped." -ForegroundColor Yellow
        }
    } else {
        Write-Host "Streamlink is up-to-date ($LatestStreamlinkVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path (Join-Path $StreamlinkVendorDir "bin\streamlink.exe")) {
        Write-Host "Streamlink found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        Write-Host "Warning: Could not fetch Streamlink release. Tier 0 live-stream resolution will be skipped." -ForegroundColor Yellow
    }
}

# 6. Fetch Latest wgcf (ViRb3/wgcf) — Cloudflare WARP account registration + config generator.
$WgcfVendorPath = Join-Path $VendorDir "wgcf.exe"
try {
    $WgcfRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/ViRb3/wgcf/releases/latest" -ErrorAction Stop
    $LatestWgcfVersion = $WgcfRelease.tag_name
    if ($Versions.wgcf -ne $LatestWgcfVersion -or !(Test-Path $WgcfVendorPath)) {
        Write-Host "Updating wgcf to $LatestWgcfVersion..." -ForegroundColor Yellow
        # Pick the windows amd64 asset — release asset naming uses wgcf_<ver>_windows_amd64.exe
        $WgcfAsset = ($WgcfRelease.assets | Where-Object { $_.name -match "windows_amd64\.exe$" } | Select-Object -First 1)
        if ($WgcfAsset) {
            Invoke-WebRequest -Uri $WgcfAsset.browser_download_url -OutFile $WgcfVendorPath
            $Versions.wgcf = $LatestWgcfVersion
            Write-Host "wgcf.exe ready ($LatestWgcfVersion)." -ForegroundColor Green
        } else {
            Write-Host "Warning: wgcf windows_amd64 asset not found in release. WARP disabled in this build." -ForegroundColor Yellow
        }
    } else {
        Write-Host "wgcf.exe is up-to-date ($LatestWgcfVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path $WgcfVendorPath) {
        Write-Host "wgcf.exe found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        Write-Host "Note: Could not fetch wgcf release. WARP will be disabled unless users drop wgcf.exe into tools/warp/." -ForegroundColor Yellow
    }
}

# 7. Fetch Latest wireproxy (pufferffish/wireproxy) — user-space WG → SOCKS5 bridge. No TUN, no admin.
$WireproxyVendorPath = Join-Path $VendorDir "wireproxy.exe"
try {
    $WireproxyRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/pufferffish/wireproxy/releases/latest" -ErrorAction Stop
    $LatestWireproxyVersion = $WireproxyRelease.tag_name
    if ($Versions.wireproxy -ne $LatestWireproxyVersion -or !(Test-Path $WireproxyVendorPath)) {
        Write-Host "Updating wireproxy to $LatestWireproxyVersion..." -ForegroundColor Yellow
        # Pick the Windows amd64 asset. Upstream currently ships wireproxy_windows_amd64.tar.gz
        # (older releases used .zip; some forks publish a bare .exe). Try formats in that order.
        $WpAsset = ($WireproxyRelease.assets | Where-Object { $_.name -match "windows.*amd64.*\.tar\.gz$" } | Select-Object -First 1)
        if (-not $WpAsset) {
            $WpAsset = ($WireproxyRelease.assets | Where-Object { $_.name -match "windows.*amd64.*\.zip$" } | Select-Object -First 1)
        }
        $WpExeAsset = $null
        if (-not $WpAsset) {
            $WpExeAsset = ($WireproxyRelease.assets | Where-Object { $_.name -match "windows.*amd64.*\.exe$" } | Select-Object -First 1)
        }
        if ($WpAsset) {
            $ArchivePath = Join-Path $VendorDir $WpAsset.name
            Invoke-WebRequest -Uri $WpAsset.browser_download_url -OutFile $ArchivePath
            $TempExtract = Join-Path $VendorDir "wireproxy_extract"
            if (Test-Path $TempExtract) { Remove-Item -Path $TempExtract -Recurse -Force }
            New-Item -ItemType Directory -Path $TempExtract | Out-Null
            $extractOk = $false
            if ($WpAsset.name -match "\.tar\.gz$") {
                # Windows 10 1803+ ships tar.exe (libarchive). It handles .tar.gz natively, no
                # external dependency. -C sets the working directory; -xzf does extract+gunzip.
                & tar.exe -xzf $ArchivePath -C $TempExtract
                $extractOk = ($LASTEXITCODE -eq 0)
            } else {
                try { Expand-Archive -Path $ArchivePath -DestinationPath $TempExtract -Force; $extractOk = $true }
                catch { $extractOk = $false }
            }
            Remove-Item $ArchivePath -ErrorAction SilentlyContinue
            if ($extractOk) {
                $WpExtractedExe = Get-ChildItem -Path $TempExtract -Filter "wireproxy*.exe" -Recurse | Select-Object -First 1
                if ($WpExtractedExe) {
                    Copy-Item -Path $WpExtractedExe.FullName -Destination $WireproxyVendorPath -Force
                    $Versions.wireproxy = $LatestWireproxyVersion
                    Write-Host "wireproxy.exe ready ($LatestWireproxyVersion)." -ForegroundColor Green
                } else {
                    Write-Host "Warning: no wireproxy*.exe found inside $($WpAsset.name). WARP disabled in this build." -ForegroundColor Yellow
                }
            } else {
                Write-Host "Warning: failed to extract $($WpAsset.name). WARP disabled in this build." -ForegroundColor Yellow
            }
            Remove-Item -Path $TempExtract -Recurse -Force -ErrorAction SilentlyContinue
        } elseif ($WpExeAsset) {
            Invoke-WebRequest -Uri $WpExeAsset.browser_download_url -OutFile $WireproxyVendorPath
            $Versions.wireproxy = $LatestWireproxyVersion
            Write-Host "wireproxy.exe ready ($LatestWireproxyVersion)." -ForegroundColor Green
        } else {
            Write-Host "Warning: no wireproxy windows amd64 asset found in release (looked for .tar.gz, .zip, .exe). WARP disabled in this build." -ForegroundColor Yellow
        }
    } else {
        Write-Host "wireproxy.exe is up-to-date ($LatestWireproxyVersion)." -ForegroundColor Green
    }
} catch {
    if (Test-Path $WireproxyVendorPath) {
        Write-Host "wireproxy.exe found in vendor/ (offline - could not check for updates)." -ForegroundColor Green
    } else {
        Write-Host "Note: Could not fetch wireproxy release. WARP will be disabled unless users drop wireproxy.exe into tools/warp/." -ForegroundColor Yellow
    }
}

$Versions | ConvertTo-Json | Out-File $VersionFile

# --- Daily Versioning Logic ---
$Today = Get-Date -Format "yyyy.M.d"
$BuildCount = 0
$UID = [Guid]::NewGuid().ToString().Substring(0, 4).ToUpper()

if (Test-Path $LocalVersionState) {
    $State = Get-Content $LocalVersionState | ConvertFrom-Json
    if ($State.Date -eq $Today) {
        $BuildCount = $State.Count + 1
    }
}

$FullVersion = "$Today.$BuildCount-$UID"
@{ "Date" = $Today; "Count" = $BuildCount } | ConvertTo-Json | Out-File $LocalVersionState

Write-Host "Building Version: $FullVersion" -ForegroundColor Magenta

# --- Inject Version into Store ---
$AppStorePath = "src/WKVRCProxy.UI/ui/src/stores/appStore.ts"
$StoreContent = Get-Content $AppStorePath -Raw
$RegexPattern = 'version = ref\(''(.+?)''\)'
$RegexReplace = 'version = ref(''{0}'')' -f $FullVersion
$NewStoreContent = $StoreContent -replace $RegexPattern, $RegexReplace
# -NoNewline: Get-Content -Raw preserves the file's existing trailing newline, and Set-Content
# appends one by default. Without -NoNewline the file grows by one blank line per build.
Set-Content $AppStorePath $NewStoreContent -NoNewline

# --- Build Frontend ---
# npm (and vite) write informational warnings to stderr. Under PS 5.1 with
# ErrorActionPreference=Stop, stderr output from a native command becomes a
# terminating NativeCommandError — even when the tool exits 0. Scope the
# preference down for just this call and gate on $LASTEXITCODE instead.
Write-Host "`n--- Building Frontend ---" -ForegroundColor Cyan
Push-Location "src/WKVRCProxy.UI/ui"
$PrevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    npm run build
    if ($LASTEXITCODE -ne 0) {
        Pop-Location
        $ErrorActionPreference = $PrevEap
        throw "Frontend build failed (npm exit $LASTEXITCODE)"
    }
} finally {
    $ErrorActionPreference = $PrevEap
}
Pop-Location

# --- Build .NET Projects ---
Write-Host "`n--- Building .NET Projects ---" -ForegroundColor Cyan
# Production build: Release mode
dotnet publish src/WKVRCProxy.UI/WKVRCProxy.UI.csproj -c Release -o $BuildDir --self-contained true -r win-x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -warnaserror
dotnet publish src/WKVRCProxy.Redirector/WKVRCProxy.Redirector.csproj -c Release -o $BuildDir --self-contained true -r win-x64 /p:PublishSingleFile=true -warnaserror

# --- Final Packaging ---
Write-Host "`n--- Packaging Assets ---" -ForegroundColor Cyan
$ToolsDir = Join-Path $BuildDir "tools"
if (!(Test-Path $ToolsDir)) { New-Item -ItemType Directory $ToolsDir }

# Renaming logic needs to account for the -windows TFM
$UiExeName = "WKVRCProxy.UI.exe"
$UiBuildPath = Join-Path $BuildDir $UiExeName

Move-Item $UiBuildPath (Join-Path $BuildDir "WKVRCProxy.exe") -Force
Move-Item (Join-Path $BuildDir "WKVRCProxy.Redirector.exe") (Join-Path $ToolsDir "redirector.exe") -Force

Copy-Item -Path "src/WKVRCProxy.UI/wwwroot" -Destination $BuildDir -Recurse -Force
Copy-Item (Join-Path $VendorDir "yt-dlp.exe") (Join-Path $ToolsDir "yt-dlp.exe")
if (Test-Path (Join-Path $VendorDir "curl-impersonate-win.exe")) {
    Copy-Item (Join-Path $VendorDir "curl-impersonate-win.exe") (Join-Path $ToolsDir "curl-impersonate-win.exe")
}
Copy-Item (Join-Path $VendorDir "bgutil-ytdlp-pot-provider.exe") (Join-Path $ToolsDir "bgutil-ytdlp-pot-provider.exe")
# yt-dlp needs a JS runtime to solve signature and n-challenges on modern YouTube — without one,
# SABR-guarded formats all get skipped and resolution ends with "Only images are available".
# Ship deno (already vendored for the bgutil sidecar compile) alongside yt-dlp and point at it
# via --js-runtimes in ResolutionEngine.
if (Test-Path (Join-Path $VendorDir "deno.exe")) {
    Copy-Item (Join-Path $VendorDir "deno.exe") (Join-Path $ToolsDir "deno.exe") -Force
}
$VendorPluginDir = Join-Path $VendorDir "yt-dlp-plugins"
if (Test-Path $VendorPluginDir) {
    Copy-Item -Path $VendorPluginDir -Destination (Join-Path $ToolsDir "yt-dlp-plugins") -Recurse -Force
}
$StreamlinkVendorDir = Join-Path $VendorDir "streamlink"
if (Test-Path $StreamlinkVendorDir) {
    Copy-Item -Path $StreamlinkVendorDir -Destination (Join-Path $ToolsDir "streamlink") -Recurse -Force
}

# WARP (Cloudflare) binaries — ship in tools/warp/ if vendored. Missing binaries cause WarpService
# to stay BinariesMissing with a warning; no crash. The two warp+ strategies in the cold race
# fail-fast in that state and skip without affecting other resolution paths.
$WarpToolsDir = Join-Path $ToolsDir "warp"
if (!(Test-Path $WarpToolsDir)) { New-Item -ItemType Directory $WarpToolsDir | Out-Null }
if (Test-Path (Join-Path $VendorDir "wireproxy.exe")) {
    Copy-Item (Join-Path $VendorDir "wireproxy.exe") (Join-Path $WarpToolsDir "wireproxy.exe") -Force
}
if (Test-Path (Join-Path $VendorDir "wgcf.exe")) {
    Copy-Item (Join-Path $VendorDir "wgcf.exe") (Join-Path $WarpToolsDir "wgcf.exe") -Force
}

# Cleanup
Get-ChildItem -Path $BuildDir -Filter "*.pdb" -Recurse | Remove-Item -Force
Get-ChildItem -Path $BuildDir -Filter "*.log" | Remove-Item -Force

$FullVersion | Set-Content -Path (Join-Path $BuildDir "version.txt") -Encoding UTF8
$FullVersion | Set-Content -Path (Join-Path $PSScriptRoot "version.txt") -Encoding UTF8

Write-Host "`nBuild $FullVersion Complete! Output in: $BuildDir" -ForegroundColor Green

# --- Relaunch app if it was running before the build ---
if ($WasRunning) {
    $ExePath = Join-Path $BuildDir "WKVRCProxy.exe"
    Write-Host "`nRelaunching WKVRCProxy..." -ForegroundColor Cyan
    Start-Process -FilePath $ExePath -WorkingDirectory $BuildDir
}
