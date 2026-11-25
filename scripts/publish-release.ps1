param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Notes = "Release v$Version",
    [switch]$Push = $false
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Push-Location $Root
try {
    $CleanVer = $Version.TrimStart('v', 'V')
    $Tag = "v$CleanVer"

    # Bump versions in both .csproj files
    foreach ($proj in @("src\AetherBrowser\AetherBrowser.csproj", "src\AetherInstaller\AetherInstaller.csproj")) {
        $full = Join-Path $Root $proj
        $xml = Get-Content $full -Raw
        $xml = $xml -replace '<Version>[^<]+</Version>', "<Version>$CleanVer</Version>"
        $xml = $xml -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$CleanVer.0</AssemblyVersion>"
        $xml = $xml -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$CleanVer.0</FileVersion>"
        Set-Content -Path $full -Value $xml -NoNewline
    }

    # Build installer & portable artifacts
    & (Join-Path $Root "scripts\build-installer.ps1") -Version $CleanVer -CleanReleases

    # Commit & tag in Git
    git add -A
    git commit -m "Release $Tag - $Notes"
    git tag -a $Tag -m "Aether Browser $Tag"

    if ($Push) {
        git push origin HEAD
        git push origin $Tag
        Write-Host "==> Pushed $Tag! GitHub Actions will build & publish the GitHub Release automatically." -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "==> Committed and tagged $Tag locally!" -ForegroundColor Green
        Write-Host "    To publish to GitHub Releases, link your remote and run:" -ForegroundColor Yellow
        Write-Host "      git remote add origin https://github.com/<your-user>/<your-repo>.git" -ForegroundColor Gray
        Write-Host "      git push -u origin main --tags" -ForegroundColor Gray
    }
}
finally {
    Pop-Location
}
