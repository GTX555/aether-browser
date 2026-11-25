# Aether Browser

An ultra-lightweight, memory-saving, privacy-first, minimalist native Windows web browser built with **.NET 8 WPF + 100% Open-Source CEF (Chromium Embedded Framework) + Velopack** — **zero Microsoft WebView2 or Edge runtime dependencies**.

## Key Features

- **100% Open-Source Chromium Engine (CEF v2.0.0)**:
  - Powered by `CefSharp.Wpf.HwndHost` running Open-Source **Chromium Embedded Framework (CEF)** (`CefRuntimeStyle.Alloy` embedded Chrome bootstrap) on `win-x64`.
  - Zero Microsoft WebView2, zero Edge runtime, and zero proprietary browser telemetry (`--disable-background-networking`, `--disable-sync`, `--disable-component-update`, `--disable-domain-reliability`, `--disable-breakpad`, `--no-pings`, Privacy Sandbox / Topics / FLEDGE disabled).
  - Real Chromium loading progress bar embedded along the bottom of the pill address bar, native Chromium Find-in-Page (`Ctrl+F`), DevTools (`F12`), per-tab zoom, and per-tab audio muting.
- **Ultra-Lightweight & Memory Saver**:
  - Configurable **Low-End Memory Mode** (`--enable-low-end-device-mode`, `--optimize-for-size`, BackForwardCache disabled), **Background Tab Throttling** (`IntensiveWakeUpThrottling`), **Speculative Preconnect Disabling**, and configurable **Disk Cache Size Limit** (`DiskCacheSizeMb`, default 50 MB).
  - Automatic **Tab Sleeping** (`IBrowserHost.WasHidden(true)` + subprocess working-set trimming) and **Tab Hibernation / Discarding** (disposes background `ChromiumWebBrowser` instances to **0 MB** RAM and restores seamlessly on click).
  - Bounded LRU favicon cache (150 entries), 500ms background debounced atomic JSON persistence, lazy-loaded session restoration, OS working-set trimming on window minimize, live RAM badge, and 1-click **Sleep Inactive Tabs / Purge RAM**.
- **Privacy-First by Default & High-Speed AdBlocker**:
  - Built-in network-level **Ad, Tracker, Telemetry, Cryptominer, Annoyance & Social Widget Blocker** backed by `.NET 8 FrozenSet<string>` and zero-allocation `ReadOnlySpan<char>` domain/path matching with per-site shield badge, 1-click site pause, and community filter list updates.
  - Event-driven `MutationObserver` YouTube ad skipper (zero idle polling CPU burn) + cosmetic CSS ad hiding.
  - **Clean URLs** (automatic stripping of `utm_*`, `fbclid`, `gclid`, `igshid`, `si`, and click-tracking parameters).
  - **Anti-Fingerprinting** protections (Canvas noise, WebGL vendor/renderer spoofing, AudioContext noise, normalized `hardwareConcurrency`/`deviceMemory`, Battery API blocking, WebRTC IP leak mitigation).
  - **HTTPS-Only Mode**, `Sec-GPC: 1`, `DNT: 1`, strict Referrer Policy, DNS-over-HTTPS (Cloudflare, Quad9, Mullvad, AdGuard, Custom), User-Agent spoofing, isolated in-memory **Private Windows** (`Ctrl+Shift+N`), and Clear-on-Exit hygiene.
  - **DuckDuckGo** default search engine (with search suggestions disabled by default + `!bang` shortcuts).
  - **Blank New Tab** (`about:blank` matched seamlessly to active theme — 0ms instant open).
- **Native Chromium MV3 Extension Engine**:
  - Install unpacked Manifest V3/V2 folders, `.crx` (CRX2/CRX3) or `.zip` packages, or install directly by **Chrome Web Store URL / Extension ID**.
  - Manage extensions in `aether://extensions` or natively via `chrome://extensions`.
- **Minimalist Single-Line UI & 75+ Searchable Settings (`aether://settings`)**:
  - Unified single-line top bar with compact pill address bar, dynamically compressing uniform tabs, smooth animated drag-reorder & tear-off/dock-back between windows, right-aligned popups, optional vertical sidebar tabs, and Dark / AMOLED / Light themes.
  - **Set as Default Browser** Windows registration, toolbar customization, permissions manager, and JSON Export/Import.
- **Professional Native Setup Wizard & Git Auto-Updater**:
  - Custom minimalist dark-grey WPF installer ([AetherBrowser-Setup.exe](dist/releases/AetherBrowser-Setup.exe)) with Welcome, Customize (install folder, Desktop/Start Menu shortcuts, Windows Default Apps registration), live extraction progress bar, standalone Windows Add/Remove Programs uninstaller (`AetherUninstall.exe`), and silent self-update mode (`--silent --wait-pid --relaunch`).
  - Scans your **Git / GitHub repository** (`owner/repo`, full URL, Releases, Git Tags, or raw `releases.json`) automatically on startup or on demand via **Settings → Updates & About → Scan for Updates**.
  - Includes a ready-to-use GitHub Actions workflow (`.github/workflows/release.yml`) and `scripts/publish-release.ps1` so pushing a git tag (`v2.2.0`) automatically builds and publishes a new GitHub Release.
- **Real Open-Source Vector Icons & Neutral Grey Theme**:
  - Uses 62 genuine **Lucide** SVG vector icons across the entire browser UI and internal pages, styled in a pure neutral dark-graphite grey palette (zero violet tint).

## Building & Packaging the Installer

```powershell
# 1. Build & run locally (Release, win-x64)
dotnet build src\AetherBrowser\AetherBrowser.csproj -c Release
dotnet run --project src\AetherBrowser\AetherBrowser.csproj -c Release

# 2. Build the Professional Setup Wizard + Portable ZIP + releases.json
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Version 2.1.0 -CleanReleases
```

## Publishing New Versions on Git / GitHub Releases

1. Link your GitHub repository:
   ```powershell
   git remote add origin https://github.com/<your-user>/<your-repo>.git
   git push -u origin main --tags
   ```
2. Set `<your-user>/<your-repo>` (or the full URL) in **Settings → Updates & About → Git Repository (GitHub)**.
3. Whenever you want to release a new version (e.g. `2.2.0`):
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1 -Version 2.2.0 -Notes "What's new" -Push
   ```
   Pushing the `v2.2.0` tag triggers `.github/workflows/release.yml` to build & publish `AetherBrowser-Setup.exe` automatically — existing installs will detect it on startup or via **Scan for Updates**!
