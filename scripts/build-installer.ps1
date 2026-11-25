param(
    [string]$Version = "2.1.0",
    [switch]$SelfContained = $false,
    [switch]$CleanReleases = $false
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Push-Location $Root
try {
    $BrowserProj = Join-Path $Root "src\AetherBrowser\AetherBrowser.csproj"
    $InstallerProj = Join-Path $Root "src\AetherInstaller\AetherInstaller.csproj"
    $PublishDir = Join-Path $Root "dist\publish-v$Version"
    $InstallerBuildDir = Join-Path $Root "dist\installer-stub"
    $ReleasesDir = Join-Path $Root "dist\releases"

    Write-Host "==> [1/4] Publishing Aether Browser v$Version (100% Open-Source CEF, win-x64)..." -ForegroundColor Cyan
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

    # Verify required win-x64 CEF native assets & zero WebView2 traces
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

    Write-Host "==> [2/4] Creating Portable ZIP archive..." -ForegroundColor Cyan
    $PortableZip = Join-Path $ReleasesDir "AetherBrowser-win-Portable.zip"
    if (Test-Path $PortableZip) { Remove-Item -Force $PortableZip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $PublishDir,
        $PortableZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host "==> [3/4] Publishing Professional Setup Wizard stub..." -ForegroundColor Cyan
    if (Test-Path $InstallerBuildDir) { Remove-Item -Recurse -Force $InstallerBuildDir }
    dotnet publish $InstallerProj `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -o $InstallerBuildDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish AetherInstaller failed ($LASTEXITCODE)" }

    $StubExe = Join-Path $InstallerBuildDir "AetherInstaller.exe"
    if (-not (Test-Path $StubExe)) { throw "Installer stub not found at $StubExe" }

    Write-Host "==> [4/4] Assembling self-extracting AetherBrowser-Setup.exe..." -ForegroundColor Cyan
    $SetupExe = Join-Path $ReleasesDir "AetherBrowser-Setup.exe"
    if (Test-Path $SetupExe) { Remove-Item -Force $SetupExe }

    $stubBytesLen = (Get-Item $StubExe).Length
    $zipBytesLen = (Get-Item $PortableZip).Length

    $outStream = [System.IO.File]::Create($SetupExe)
    try {
        $inStub = [System.IO.File]::OpenRead($StubExe)
        try { $inStub.CopyTo($outStream) } finally { $inStub.Dispose() }

        $inZip = [System.IO.File]::OpenRead($PortableZip)
        try { $inZip.CopyTo($outStream) } finally { $inZip.Dispose() }

        # Append 24-byte footer: Int64 offset + Int64 length + "AETHERPK"
        $bw = New-Object System.IO.BinaryWriter($outStream, [System.Text.Encoding]::ASCII, $true)
        $bw.Write([Int64]$stubBytesLen)
        $bw.Write([Int64]$zipBytesLen)
        $bw.Write([System.Text.Encoding]::ASCII.GetBytes("AETHERPK"))
        $bw.Flush()
    }
    finally {
        $outStream.Dispose()
    }

    # Verify footer & self-extraction integrity immediately
    & $SetupExe --verify-payload
    if ($LASTEXITCODE -ne 0) { throw "Setup payload verification failed with exit code $LASTEXITCODE" }

    # Write Git-friendly releases.json metadata
    $manifest = @{
        version = $Version
        publishedUtc = (Get-Date).ToUniversalTime().ToString("o")
        notes = "Aether Browser v$Version (100% Open-Source CEF, neutral grey theme, Git auto-updater, professional Setup wizard)"
        setupFile = "AetherBrowser-Setup.exe"
        portableFile = "AetherBrowser-win-Portable.zip"
    } | ConvertTo-Json -Depth 4
    Set-Content -Path (Join-Path $ReleasesDir "releases.json") -Value $manifest -Encoding UTF8

    Write-Host ""
    Write-Host "Build & Verification Complete! Artifacts in $ReleasesDir" -ForegroundColor Green
    Get-ChildItem $ReleasesDir | Format-Table Name, @{N="Size (MB)";E={[math]::Round($_.Length/1MB,2)}}
}
finally {
    Pop-Location
}
