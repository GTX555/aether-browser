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

    $proj = Join-Path $Root "src\AetherBrowser\AetherBrowser.csproj"
    $xml = Get-Content $proj -Raw
    $xml = $xml -replace '<Version>[^<]+</Version>', "<Version>$CleanVer</Version>"
    $xml = $xml -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$CleanVer.0</AssemblyVersion>"
    $xml = $xml -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$CleanVer.0</FileVersion>"
    Set-Content -Path $proj -Value $xml -NoNewline

    & (Join-Path $Root "scripts\build-installer.ps1") -Version $CleanVer -CleanReleases

    git add -A
    git commit -m "Release $Tag - $Notes"
    git tag -a $Tag -m "Aether Browser $Tag"

    if ($Push) {
        git push origin HEAD
        git push origin $Tag
        Write-Host "==> Pushed $Tag! GitHub Actions will build & publish the GitHub Release automatically." -ForegroundColor Green
    } else {
        Write-Host "==> Committed and tagged $Tag locally. Run with -Push to publish to GitHub!" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
