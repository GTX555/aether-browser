param(
    [string]$Version = "2.2.0",
    [switch]$SelfContained = $false,
    [switch]$CleanReleases = $false
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Push-Location $Root
try {
    $BrowserProj = Join-Path $Root "src\AetherBrowser\AetherBrowser.csproj"
    $PublishDir = Join-Path $Root "dist\publish-v$Version"
    $ReleasesDir = Join-Path $Root "dist\releases"
    $NsisScript = Join-Path $Root "installer\AetherBrowser.nsi"

    # Locate makensis (PATH, Program Files, or local .tools/nsis-*)
    $MakeNsis = $null
    $cmd = Get-Command makensis -ErrorAction SilentlyContinue
    if ($cmd) { $MakeNsis = $cmd.Source }
    if (-not $MakeNsis -and (Test-Path "C:\Program Files (x86)\NSIS\makensis.exe")) {
        $MakeNsis = "C:\Program Files (x86)\NSIS\makensis.exe"
    }
    if (-not $MakeNsis -and (Test-Path (Join-Path $Root ".tools"))) {
        $found = Get-ChildItem -Path (Join-Path $Root ".tools") -Recurse -Filter "makensis.exe" | Select-Object -First 1
        if ($found) { $MakeNsis = $found.FullName }
    }
    if (-not $MakeNsis) {
        throw "NSIS (makensis.exe) not found. Install NSIS or place portable NSIS in .tools/."
    }

    Write-Host "==> [1/3] Publishing Aether Browser v$Version (100% Open-Source CEF, win-x64)..." -ForegroundColor Cyan
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    if ($CleanReleases -and (Test-Path $ReleasesDir)) { Remove-Item -Recurse -Force $ReleasesDir }
    New-Item -ItemType Directory -Force -Path $ReleasesDir | Out-Null

    $scFlag = if ($SelfContained) { "true" } else { "false" }
    dotnet publish $BrowserProj `
        -c Release `
        -r win-x64 `
        --self-contained $scFlag `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish AetherBrowser failed ($LASTEXITCODE)" }

    foreach ($asset in @("libcef.dll", "CefSharp.BrowserSubprocess.exe", "icudtl.dat", "resources.pak")) {
        if (-not (Test-Path (Join-Path $PublishDir $asset))) {
            throw "Missing required CEF win-x64 native asset: $asset"
        }
    }

    # Strip unused Chromium locale files (keep only en-US & de -> saves ~48.5 MB!)
    $LocalesDir = Join-Path $PublishDir "locales"
    if (Test-Path $LocalesDir) {
        $removed = 0
        Get-ChildItem -Path $LocalesDir -File | Where-Object {
            $_.Name -notlike "en-US*.pak" -and $_.Name -notlike "de*.pak"
        } | ForEach-Object {
            Remove-Item -Force $_.FullName
            $removed++
        }
        Write-Host "    Trimmed $removed unused Chromium locale files (kept en-US & de)." -ForegroundColor DarkGray
    }

    Write-Host "==> [2/3] Creating Portable ZIP archive..." -ForegroundColor Cyan
    $PortableZip = Join-Path $ReleasesDir "AetherBrowser-win-Portable.zip"
    if (Test-Path $PortableZip) { Remove-Item -Force $PortableZip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PublishDir,
        $PortableZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host "==> [3/3] Compiling Native NSIS Windows Installer (AetherBrowser-Setup.exe)..." -ForegroundColor Cyan
    $SetupExe = Join-Path $ReleasesDir "AetherBrowser-Setup.exe"
    if (Test-Path $SetupExe) { Remove-Item -Force $SetupExe }

    & $MakeNsis `
        "/DAPP_VERSION=$Version" `
        "/DPUBLISH_DIR=$PublishDir" `
        "/DOUT_FILE=$SetupExe" `
        $NsisScript
    if ($LASTEXITCODE -ne 0) { throw "makensis failed with exit code $LASTEXITCODE" }

    # Write Git-friendly releases.json metadata
    $manifest = @{
        version = $Version
        publishedUtc = (Get-Date).ToUniversalTime().ToString("o")
        notes = "Aether Browser v$Version — 100% Open-Source CEF, native Windows NSIS Setup, Git auto-updater, neutral grey theme."
        downloadUrl = "https://github.com/GTX555/aether-browser/releases/download/v$Version/AetherBrowser-Setup.exe"
        releaseUrl = "https://github.com/GTX555/aether-browser/releases/tag/v$Version"
    } | ConvertTo-Json -Depth 4
    Set-Content -Path (Join-Path $ReleasesDir "releases.json") -Value $manifest -Encoding UTF8

    Write-Host ""
    Write-Host "Build Complete! Artifacts in $ReleasesDir" -ForegroundColor Green
    Get-ChildItem $ReleasesDir | Format-Table Name, @{N="Size (MB)";E={[math]::Round($_.Length/1MB,2)}}
}
finally {
    Pop-Location
}
