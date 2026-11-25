using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using AetherBrowser.Models;

namespace AetherBrowser.Services;

public sealed class InternalPagesService
{
    private readonly Dictionary<string, string> _svgCache = new(StringComparer.OrdinalIgnoreCase);

    public string GetLucideSvg(string name, int size = 16)
    {
        if (!_svgCache.TryGetValue(name, out var raw))
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Icons/{name}.svg");
                var info = Application.GetResourceStream(uri);
                if (info is not null)
                {
                    using var sr = new StreamReader(info.Stream);
                    raw = sr.ReadToEnd();
                }
                else
                {
                    raw = "";
                }
            }
            catch
            {
                raw = "";
            }
            _svgCache[name] = raw;
        }

        if (string.IsNullOrEmpty(raw))
            return "";

        return raw
            .Replace("width=\"24\"", $"width=\"{size}\"")
            .Replace("height=\"24\"", $"height=\"{size}\"")
            .Replace("stroke-width=\"2\"", "stroke-width=\"1.75\"");
    }

    public static (string Bg, string Surface, string Surface2, string Border, string Text, string Muted) GetThemePalette(BrowserSettings s)
    {
        return s.Theme switch
        {
            "Light" => ("#F7F7F8", "#FFFFFF", "#EFEFF1", "#E0E0E4", "#121214", "#6B6B72"),
            "Amoled" => ("#000000", "#0B0B0C", "#141416", "#242428", "#F4F4F5", "#8E8E93"),
            _ => ("#0E0E0F", "#161618", "#202023", "#2E2E33", "#F2F2F3", "#94949A")
        };
    }

    public static string GetAccentForeground(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 6)
            {
                var r = Convert.ToInt32(h[0..2], 16);
                var g = Convert.ToInt32(h[2..4], 16);
                var b = Convert.ToInt32(h[4..6], 16);
                var lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                return lum > 0.56 ? "#111113" : "#FFFFFF";
            }
        }
        catch { }
        return "#111113";
    }

    public string BuildBlankPageHtml(BrowserSettings settings)
    {
        var (bg, _, _, _, _, _) = GetThemePalette(settings);
        return $"<!doctype html><html><head><meta charset=\"utf-8\"><title>New Tab</title><style>html,body{{margin:0;padding:0;width:100%;height:100%;background:{bg};overflow:hidden;}}</style></head><body></body></html>";
    }

    public string BuildInternalPageHtml(
        string internalUrl,
        BrowserSettings settings,
        DataStoreService store,
        MemorySnapshot memory,
        UpdateInfo updateInfo,
        long lifetimeBlocked,
        string engineVersion)
    {
        var route = internalUrl.Replace("aether://", "", StringComparison.OrdinalIgnoreCase).Trim('/').ToLowerInvariant();
        if (string.IsNullOrEmpty(route)) route = "settings";

        var (bg, surface, surface2, border, text, muted) = GetThemePalette(settings);
        var accent = string.IsNullOrWhiteSpace(settings.AccentColor) ? "#A1A1AA" : settings.AccentColor;
        var accentFg = GetAccentForeground(accent);

        var payload = JsonSerializer.Serialize(new
        {
            route,
            settings,
            searchEngines = BrowserSettings.BuiltInSearchEngines,
            bookmarks = store.Bookmarks,
            history = store.History.Take(300),
            downloads = store.Downloads.Take(200),
            extensions = store.Extensions,
            memory,
            updateInfo,
            lifetimeBlocked,
            runtimeInfo = new
            {
                appVersion = UpdateService.CurrentVersion,
                engineVersion,
                dotnetVersion = Environment.Version.ToString(),
                osVersion = Environment.OSVersion.VersionString,
                isDefaultBrowser = DefaultBrowserService.IsDefaultBrowser(),
                adBlockRules = App.AdBlocker.TotalRuleCount
            }
        });

        var icons = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["settings"] = GetLucideSvg("settings", 16),
            ["sparkles"] = GetLucideSvg("sparkles", 16),
            ["cpu"] = GetLucideSvg("cpu", 16),
            ["shield"] = GetLucideSvg("shield-check", 16),
            ["search"] = GetLucideSvg("search", 16),
            ["layers"] = GetLucideSvg("layers", 16),
            ["sliders"] = GetLucideSvg("sliders-horizontal", 16),
            ["puzzle"] = GetLucideSvg("puzzle", 16),
            ["refresh"] = GetLucideSvg("refresh-cw", 16),
            ["keyboard"] = GetLucideSvg("keyboard", 16),
            ["history"] = GetLucideSvg("history", 16),
            ["bookmark"] = GetLucideSvg("bookmark", 16),
            ["download"] = GetLucideSvg("download", 16),
            ["trash"] = GetLucideSvg("trash-2", 15),
            ["external"] = GetLucideSvg("external-link", 15),
            ["folder"] = GetLucideSvg("folder-open", 15),
            ["package"] = GetLucideSvg("package", 15),
            ["zap"] = GetLucideSvg("zap", 15),
            ["moon"] = GetLucideSvg("moon", 15),
            ["feather"] = GetLucideSvg("feather", 18),
            ["check"] = GetLucideSvg("check", 15),
            ["globe"] = GetLucideSvg("globe", 15),
            ["upload"] = GetLucideSvg("upload", 15)
        });

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>Aether — {{route}}</title>
<style>
  :root {
    --bg: {{bg}};
    --surface: {{surface}};
    --surface2: {{surface2}};
    --border: {{border}};
    --text: {{text}};
    --muted: {{muted}};
    --accent: {{accent}};
    --accent-fg: {{accentFg}};
    --radius: 10px;
    --font: "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, sans-serif;
  }
  * { box-sizing: border-box; }
  html, body {
    margin: 0; padding: 0; height: 100%;
    background: var(--bg); color: var(--text);
    font-family: var(--font); font-size: 13.5px; line-height: 1.45;
    -webkit-font-smoothing: antialiased;
  }
  .layout { display: flex; height: 100vh; overflow: hidden; }
  .sidebar {
    width: 248px; flex-shrink: 0;
    background: var(--surface); border-right: 1px solid var(--border);
    display: flex; flex-direction: column; padding: 18px 12px; gap: 4px;
    overflow-y: auto;
  }
  .brand {
    display: flex; align-items: center; gap: 10px;
    padding: 6px 10px 16px; font-weight: 600; font-size: 15px; letter-spacing: -0.01em;
  }
  .brand-badge {
    width: 28px; height: 28px; border-radius: 8px;
    background: color-mix(in srgb, var(--accent) 18%, transparent);
    color: var(--accent); display: grid; place-items: center;
  }
  .nav-group-label {
    font-size: 11px; text-transform: uppercase; letter-spacing: .06em;
    color: var(--muted); padding: 12px 10px 4px; font-weight: 600;
  }
  .nav-item {
    display: flex; align-items: center; gap: 10px;
    padding: 8px 11px; border-radius: 8px; cursor: pointer;
    color: var(--muted); border: none; background: transparent;
    width: 100%; text-align: left; font: inherit; font-weight: 500;
    transition: background .12s, color .12s;
  }
  .nav-item:hover { background: var(--surface2); color: var(--text); }
  .nav-item.active {
    background: color-mix(in srgb, var(--accent) 15%, transparent);
    color: var(--text);
  }
  .nav-item.active svg { color: var(--accent); }
  .nav-item svg { flex-shrink: 0; opacity: .88; }

  .main { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
  .topbar {
    display: flex; align-items: center; justify-content: space-between; gap: 16px;
    padding: 16px 32px; border-bottom: 1px solid var(--border);
    background: var(--bg);
  }
  .topbar h1 { margin: 0; font-size: 19px; font-weight: 600; letter-spacing: -0.02em; }
  .search-wrap {
    position: relative; width: 320px; max-width: 100%;
  }
  .search-wrap svg {
    position: absolute; left: 11px; top: 50%; transform: translateY(-50%);
    color: var(--muted); pointer-events: none;
  }
  .search-input {
    width: 100%; padding: 8px 12px 8px 34px; border-radius: 99px;
    border: 1px solid var(--border); background: var(--surface);
    color: var(--text); font: inherit; outline: none;
    transition: border-color .15s;
  }
  .search-input:focus { border-color: var(--accent); }

  .content { flex: 1; overflow-y: auto; padding: 24px 32px 64px; }
  .container { max-width: 820px; margin: 0 auto; display: flex; flex-direction: column; gap: 18px; }

  .card {
    background: var(--surface); border: 1px solid var(--border);
    border-radius: var(--radius); overflow: hidden;
  }
  .card-header {
    padding: 14px 18px; border-bottom: 1px solid var(--border);
    display: flex; align-items: center; justify-content: space-between;
  }
  .card-title { font-weight: 600; font-size: 14px; }
  .card-sub { color: var(--muted); font-size: 12px; margin-top: 2px; }

  .row {
    display: flex; align-items: center; justify-content: space-between; gap: 16px;
    padding: 13px 18px; border-bottom: 1px solid var(--border);
  }
  .row:last-child { border-bottom: none; }
  .row-info { flex: 1; min-width: 0; }
  .row-title { font-weight: 500; font-size: 13.5px; }
  .row-desc { color: var(--muted); font-size: 12px; margin-top: 2px; }
  .row-ctrl { display: flex; align-items: center; gap: 8px; flex-shrink: 0; }

  /* Controls */
  .switch { position: relative; width: 40px; height: 22px; display: inline-block; cursor: pointer; }
  .switch input { opacity: 0; width: 0; height: 0; }
  .slider {
    position: absolute; inset: 0; border-radius: 99px;
    background: var(--surface2); border: 1px solid var(--border); transition: .16s;
  }
  .slider:before {
    content: ""; position: absolute; width: 16px; height: 16px; left: 2px; top: 2px;
    border-radius: 50%; background: var(--muted); transition: .16s;
  }
  input:checked + .slider { background: var(--accent); border-color: var(--accent); }
  input:checked + .slider:before { transform: translateX(18px); background: #fff; }

  select, input[type="text"], input[type="number"], textarea {
    background: var(--surface2); color: var(--text);
    border: 1px solid var(--border); border-radius: 7px;
    padding: 6px 10px; font: inherit; outline: none;
  }
  select:focus, input[type="text"]:focus, input[type="number"]:focus, textarea:focus {
    border-color: var(--accent);
  }
  input[type="range"] { accent-color: var(--accent); width: 130px; }

  .btn {
    display: inline-flex; align-items: center; gap: 7px;
    padding: 7px 13px; border-radius: 7px; font: inherit; font-weight: 500;
    cursor: pointer; border: 1px solid var(--border);
    background: var(--surface2); color: var(--text); transition: .12s;
  }
  .btn:hover { border-color: var(--accent); }
  .btn-primary { background: var(--accent); border-color: var(--accent); color: var(--accent-fg); font-weight: 600; }
  .btn-primary:hover { filter: brightness(1.08); }
  .btn-danger { color: #f87171; border-color: color-mix(in srgb, #f87171 35%, var(--border)); }
  .btn-danger:hover { background: color-mix(in srgb, #f87171 12%, transparent); }

  .stats-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; }
  .stat-box {
    background: var(--surface); border: 1px solid var(--border);
    border-radius: var(--radius); padding: 14px 16px;
  }
  .stat-val { font-size: 22px; font-weight: 600; letter-spacing: -0.02em; }
  .stat-lbl { font-size: 11.5px; color: var(--muted); margin-top: 3px; }

  .swatches { display: flex; gap: 8px; }
  .swatch {
    width: 24px; height: 24px; border-radius: 50%; cursor: pointer;
    border: 2px solid transparent; transition: transform .12s;
  }
  .swatch:hover { transform: scale(1.1); }
  .swatch.active { border-color: var(--text); box-shadow: 0 0 0 2px var(--bg) inset; }

  .pill {
    display: inline-flex; align-items: center; gap: 5px;
    padding: 2px 8px; border-radius: 99px; font-size: 11.5px; font-weight: 500;
    background: color-mix(in srgb, var(--accent) 16%, transparent); color: var(--accent);
  }
  .list-item {
    display: flex; align-items: center; justify-content: space-between; gap: 12px;
    padding: 11px 16px; border-bottom: 1px solid var(--border);
  }
  .list-item:last-child { border-bottom: none; }
  .list-meta { min-width: 0; flex: 1; }
  .list-title { font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .list-url { color: var(--muted); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .kbd {
    display: inline-block; padding: 2px 7px; border-radius: 5px; font-size: 11.5px;
    background: var(--surface2); border: 1px solid var(--border); font-family: Consolas, monospace;
  }
  .toast {
    position: fixed; bottom: 20px; right: 24px;
    background: var(--surface); border: 1px solid var(--accent);
    color: var(--text); padding: 10px 16px; border-radius: 8px;
    box-shadow: 0 10px 30px rgba(0,0,0,.35); opacity: 0; pointer-events: none;
    transform: translateY(8px); transition: .2s;
  }
  .toast.show { opacity: 1; transform: translateY(0); }
  .progress-bar { width: 100%; height: 6px; border-radius: 99px; background: var(--surface2); overflow: hidden; margin-top: 8px; }
  .progress-fill { height: 100%; background: var(--accent); width: 0%; transition: width .2s; }
  .empty { padding: 36px; text-align: center; color: var(--muted); }
  ::-webkit-scrollbar { width: 7px; height: 7px; }
  ::-webkit-scrollbar-thumb { background: var(--border); border-radius: 99px; }
  ::-webkit-scrollbar-track { background: transparent; }
</style>
</head>
<body>
<div class="layout">
  <aside class="sidebar">
    <div class="brand">
      <div class="brand-badge" id="brandIcon"></div>
      <span>Aether Browser</span>
    </div>
    <div class="nav-group-label">Settings</div>
    <button class="nav-item" data-tab="general"><span data-ico="settings"></span> General</button>
    <button class="nav-item" data-tab="appearance"><span data-ico="sparkles"></span> Appearance & UI</button>
    <button class="nav-item" data-tab="memory"><span data-ico="cpu"></span> Memory & Performance</button>
    <button class="nav-item" data-tab="privacy"><span data-ico="shield"></span> Privacy & Security</button>
    <button class="nav-item" data-tab="search"><span data-ico="search"></span> Search & Address Bar</button>
    <button class="nav-item" data-tab="tabs"><span data-ico="layers"></span> Tabs & New Tab</button>
    <button class="nav-item" data-tab="permissions"><span data-ico="sliders"></span> Permissions</button>
    <button class="nav-item" data-tab="extensions"><span data-ico="puzzle"></span> Extensions</button>
    <button class="nav-item" data-tab="updates"><span data-ico="refresh"></span> Updates & About</button>
    <button class="nav-item" data-tab="shortcuts"><span data-ico="keyboard"></span> Shortcuts</button>
    <div class="nav-group-label">Library</div>
    <button class="nav-item" data-tab="history"><span data-ico="history"></span> History</button>
    <button class="nav-item" data-tab="bookmarks"><span data-ico="bookmark"></span> Bookmarks</button>
    <button class="nav-item" data-tab="downloads"><span data-ico="download"></span> Downloads</button>
  </aside>

  <main class="main">
    <header class="topbar">
      <h1 id="pageTitle">Settings</h1>
      <div class="search-wrap">
        <span data-ico="search"></span>
        <input id="searchBox" class="search-input" type="text" placeholder="Search all 75+ settings or items..." />
      </div>
    </header>
    <div class="content">
      <div class="container" id="root"></div>
    </div>
  </main>
</div>
<div class="toast" id="toast"></div>

<script>
const STATE = {{payload}};
const ICONS = {{icons}};

const ACCENTS = ['#A1A1AA','#71717A','#D4D4D8','#52525B','#10B981','#06B6D4','#3B82F6','#F59E0B'];
let activeTab = ({
  settings: 'general',
  history: 'history',
  bookmarks: 'bookmarks',
  downloads: 'downloads',
  extensions: 'extensions'
})[STATE.route] || 'general';
let searchQuery = '';

function post(action, data = {}) {
  const payload = JSON.stringify({ action, ...data });
  if (window.aetherBridge) window.aetherBridge.postMessage(payload);
  else window.chrome?.webview?.postMessage(payload);
}

function showToast(msg) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(window._t);
  window._t = setTimeout(() => el.classList.remove('show'), 2600);
}

function updateSetting(key, value) {
  STATE.settings[key] = value;
  post('updateSetting', { key, value });
  if (key === 'Theme' || key === 'AccentColor') {
    setTimeout(() => post('reloadInternal'), 80);
  }
}

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

function toggleRow(key, title, desc) {
  const checked = STATE.settings[key] ? 'checked' : '';
  return `<div class="row" data-search="${esc(title + ' ' + desc)}">
    <div class="row-info"><div class="row-title">${esc(title)}</div><div class="row-desc">${esc(desc)}</div></div>
    <div class="row-ctrl"><label class="switch"><input type="checkbox" ${checked} onchange="updateSetting('${key}', this.checked)"><span class="slider"></span></label></div>
  </div>`;
}

function selectRow(key, title, desc, options) {
  const cur = STATE.settings[key];
  const opts = options.map(o => {
    const val = typeof o === 'string' ? o : o[0];
    const lbl = typeof o === 'string' ? o : o[1];
    return `<option value="${esc(val)}" ${String(cur) === String(val) ? 'selected' : ''}>${esc(lbl)}</option>`;
  }).join('');
  return `<div class="row" data-search="${esc(title + ' ' + desc)}">
    <div class="row-info"><div class="row-title">${esc(title)}</div><div class="row-desc">${esc(desc)}</div></div>
    <div class="row-ctrl"><select onchange="updateSetting('${key}', this.value)">${opts}</select></div>
  </div>`;
}

function numberRow(key, title, desc, min, max, step = 1, suffix = '') {
  const val = STATE.settings[key];
  return `<div class="row" data-search="${esc(title + ' ' + desc)}">
    <div class="row-info"><div class="row-title">${esc(title)}</div><div class="row-desc">${esc(desc)}</div></div>
    <div class="row-ctrl">
      <input type="range" min="${min}" max="${max}" step="${step}" value="${val}"
        oninput="this.nextElementSibling.textContent=this.value+'${suffix}'"
        onchange="updateSetting('${key}', Number(this.value))">
      <span class="pill" style="min-width:56px;justify-content:center">${val}${suffix}</span>
    </div>
  </div>`;
}

function textRow(key, title, desc, placeholder = '') {
  const val = STATE.settings[key] ?? '';
  return `<div class="row" data-search="${esc(title + ' ' + desc)}">
    <div class="row-info"><div class="row-title">${esc(title)}</div><div class="row-desc">${esc(desc)}</div></div>
    <div class="row-ctrl"><input type="text" style="width:240px" value="${esc(val)}" placeholder="${esc(placeholder)}" onchange="updateSetting('${key}', this.value)"></div>
  </div>`;
}

function sectionsHtml() {
  const s = STATE.settings;
  const m = STATE.memory;
  const u = STATE.updateInfo;

  return {
    general: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">Default Browser</div><div class="card-sub">Make Aether Browser your primary Windows browser for links and web pages</div></div></div>
        <div class="row" data-search="Default Browser main browser primary http https">
          <div class="row-info">
            <div class="row-title">${STATE.runtimeInfo.isDefaultBrowser ? 'Aether Browser is your default browser' : 'Aether Browser is not your default browser'}</div>
            <div class="row-desc">Registers Aether for HTTP, HTTPS, and HTML files and opens Windows Default Apps</div>
          </div>
          <div class="row-ctrl">
            ${STATE.runtimeInfo.isDefaultBrowser
              ? `<span class="pill">${ICONS.check} Default</span>`
              : `<button class="btn btn-primary" onclick="post('setDefaultBrowser')">${ICONS.globe} Set as Default Browser</button>`}
          </div>
        </div>
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Startup & Language</div><div class="card-sub">Control how Aether starts and displays text</div></div></div>
        ${selectRow('Language', 'Interface Language', 'Default language is English (restart recommended for full change)', [['en','English (Default)'],['de','Deutsch']])}
        ${selectRow('StartupMode', 'On Startup', 'Choose what opens when launching Aether Browser', [['BlankTab','Open a Blank Tab (Fastest)'],['RestoreSession','Restore previous tabs (Lazy-loaded)'],['SpecificUrls','Open specific URLs']])}
        ${textRow('StartupUrls', 'Startup URLs', 'Comma-separated URLs when Startup Mode is set to Specific URLs', 'https://duckduckgo.com')}
        ${textRow('HomePageUrl', 'Home Page URL', 'Destination opened by the Home button', 'about:blank')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Downloads & System</div><div class="card-sub">File handling and rendering behavior</div></div></div>
        ${textRow('DownloadDirectory', 'Download Folder', 'Default location where downloaded files are saved')}
        ${toggleRow('AskDownloadLocation', 'Ask where to save each file', 'Show a save dialog before starting every download')}
        ${toggleRow('ConfirmCloseMultipleTabs', 'Confirm before closing multiple tabs', 'Ask for confirmation when closing a window with multiple tabs')}
        ${toggleRow('RestoreWindowBounds', 'Remember window size & position', 'Restore your previous window geometry on launch')}
        ${toggleRow('HardwareAcceleration', 'Use GPU hardware acceleration', 'Disable on low-VRAM systems (requires restart)')}
        ${toggleRow('SmoothScrolling', 'Smooth scrolling', 'Animate page scrolling transitions (requires restart)')}
        ${toggleRow('ShowStatusBar', 'Show bottom status bar', 'Display compact status and RAM bar at bottom of window')}
      </div>
    `,

    appearance: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">Theme & Accent</div><div class="card-sub">Minimalist dark, AMOLED black, or crisp light aesthetic</div></div></div>
        ${selectRow('Theme', 'Color Theme', 'Choose the browser surface palette', [['Dark','Dark Obsidian (Default)'],['Amoled','AMOLED Pitch Black'],['Light','Minimal Light']])}
        <div class="row" data-search="Accent Color highlight theme">
          <div class="row-info"><div class="row-title">Accent Color</div><div class="row-desc">Primary color used for active highlights and controls</div></div>
          <div class="row-ctrl">
            <div class="swatches">
              ${ACCENTS.map(c => `<div class="swatch ${s.AccentColor.toLowerCase()===c.toLowerCase()?'active':''}" style="background:${c}" onclick="updateSetting('AccentColor','${c}')"></div>`).join('')}
            </div>
          </div>
        </div>
        ${toggleRow('CompactMode', 'Compact Density', 'Reduce toolbar and tab height for maximum webpage viewport')}
        ${toggleRow('VerticalTabs', 'Vertical Sidebar Tabs', 'Move the tab strip to a clean collapsible left sidebar')}
        ${numberRow('SidebarWidth', 'Vertical Sidebar Width', 'Width of the vertical tab sidebar in pixels', 170, 340, 10, 'px')}
        ${toggleRow('RoundedAddressBar', 'Pill-shaped Address Bar', 'Use rounded pill geometry for the omnibox')}
        ${numberRow('DefaultZoomPercent', 'Default Page Zoom', 'Default zoom scale for websites', 50, 200, 10, '%')}
        ${numberRow('AddressBarWidth', 'Address Bar Width', 'Compact width of the address bar on the unified top bar', 220, 520, 10, 'px')}
        ${numberRow('TabMaxWidth', 'Uniform Tab Width', 'Consistent width shared by all horizontal tabs', 110, 260, 6, 'px')}
        ${toggleRow('ShowCloseButtonOnInactiveTabs', 'Show close button on inactive tabs', 'Keep tab close icons visible without hovering')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Toolbar Customization</div><div class="card-sub">Keep only the buttons you actually use</div></div></div>
        ${toggleRow('ShowBookmarksBar', 'Show Bookmarks Bar', 'Display a minimalist bookmarks bar below the address bar')}
        ${toggleRow('ShowHomeButton', 'Show Home Button', 'Display the Home button next to navigation controls')}
        ${toggleRow('ShowPrivacyShieldButton', 'Show Privacy Shield', 'Display live ad & tracker block counter in the address bar')}
        ${toggleRow('ShowExtensionsButton', 'Show Extensions Button', 'Quick access button to installed Chrome extensions')}
        ${toggleRow('ShowDownloadsButton', 'Show Downloads Button', 'Quick access button to recent downloads')}
        ${toggleRow('ShowMemoryBadge', 'Show Live RAM Indicator', 'Display real-time browser memory footprint in the toolbar')}
        ${toggleRow('ShowSidebarToggleButton', 'Show Vertical Tabs Toggle', 'One-click switch between horizontal and vertical tabs')}
      </div>
    `,

    memory: `
      <div class="stats-grid">
        <div class="stat-box"><div class="stat-val" id="ramTotal">${m.TotalMb} MB</div><div class="stat-lbl">Total Memory (UI + CEF / Chromium)</div></div>
        <div class="stat-box"><div class="stat-val">${m.AppWorkingSetMb} MB</div><div class="stat-lbl">Native Shell RAM</div></div>
        <div class="stat-box"><div class="stat-val">${m.ActiveTabs} / ${m.TotalTabs}</div><div class="stat-lbl">Active Tabs (${m.SleepingTabs} sleeping, ${m.DiscardedTabs} unloaded)</div></div>
        <div class="stat-box"><div class="stat-val">${m.EngineProcessCount ?? m.WebViewProcessCount ?? 0}</div><div class="stat-lbl">CEF / Chromium Processes</div></div>
      </div>
      <div class="card">
        <div class="card-header">
          <div><div class="card-title">Instant Memory Actions</div><div class="card-sub">Reclaim RAM immediately without closing your tabs</div></div>
          <div style="display:flex;gap:8px">
            <button class="btn" onclick="post('sleepAllTabs');showToast('Suspended inactive tabs')">${ICONS.moon} Sleep Inactive Tabs</button>
            <button class="btn btn-primary" onclick="post('purgeMemory');showToast('Purged memory & trimmed working set')">${ICONS.zap} Purge RAM Now</button>
          </div>
        </div>
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Lightweight Memory Saver</div><div class="card-sub">Open-Source CEF / Chromium tab suspension, tab hibernation (0 MB), and OS working-set trimming</div></div></div>
        ${selectRow('MemoryTargetLevel', 'Engine Memory Target Level', 'Instructs CEF / Chromium renderer processes to aggressively release unused heap memory', [['Low','Low (Recommended — Minimum RAM)'],['Normal','Normal (Higher cache retention)']])}
        ${toggleRow('LowEndMemoryMode', 'Low-End Memory Mode', 'Enable Chromium low-end device memory profile to shrink tile & JS heap budgets (requires restart)')}
        ${toggleRow('ThrottleBackgroundTabs', 'Throttle Background Tabs', 'Intensively throttle background tab timers and frame rates to reduce CPU & RAM (requires restart)')}
        ${toggleRow('DisablePreconnect', 'Disable Speculative Preconnect', 'Stop background DNS prefetching and speculative network connections (saves RAM & privacy, requires restart)')}
        ${numberRow('DiskCacheSizeMb', 'Disk Cache Size Limit', 'Maximum HTTP disk cache size in MB (0 = minimal/in-memory cache, requires restart)', 0, 250, 5, ' MB')}
        ${toggleRow('EnableAutoSleepTabs', 'Auto-Sleep Inactive Tabs', 'Hide and throttle inactive background tabs to slash CPU and RAM usage')}
        ${numberRow('AutoSleepMinutes', 'Sleep Inactive Tabs After', 'Minutes of inactivity before suspending a background tab', 1, 60, 1, ' min')}
        ${toggleRow('EnableAutoDiscardTabs', 'Auto-Hibernate (Unload to 0 MB)', 'Fully dispose background CEF / Chromium browser instances; tabs reload instantly when clicked')}
        ${numberRow('AutoDiscardMinutes', 'Hibernate Tabs After', 'Minutes of inactivity before unloading background tab completely', 3, 120, 1, ' min')}
        ${toggleRow('ExcludePinnedFromSleep', 'Never sleep pinned tabs', 'Keep pinned tabs awake in the background')}
        ${toggleRow('ExcludeAudioFromSleep', 'Never sleep tabs playing audio', 'Avoid suspending music, video, or call tabs')}
        ${toggleRow('LazyLoadRestoredTabs', 'Lazy-load restored session tabs', 'Keep restored tabs at 0 MB RAM until you click them')}
        ${toggleRow('AggressiveGcOnTabClose', 'Reclaim RAM on tab close', 'Run compacting GC and trim working set whenever a tab closes')}
        ${toggleRow('TrimWorkingSetOnMinimize', 'Trim OS RAM on window minimize', 'Release working set pages to Windows immediately when minimized')}
        ${numberRow('MaxRendererProcesses', 'Max Renderer Process Limit', '0 = Automatic. Lower values cap multi-process memory overhead (requires restart)', 0, 16, 1, '')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Ultra-Lite Data & CPU Saver</div><div class="card-sub">Strip heavy page resources for extreme speed and minimal RAM</div></div></div>
        ${toggleRow('BlockWebFonts', 'Block External Web Fonts', 'Use fast local system fonts instead of downloading .woff2/.ttf fonts')}
        ${toggleRow('BlockImagesDataSaver', 'Data-Saver Mode (Block Images)', 'Block remote images from loading to cut bandwidth and memory')}
        ${toggleRow('DisableWebAnimations', 'Disable CSS Animations & Transitions', 'Eliminate heavy webpage animations for lower CPU/GPU usage')}
      </div>
    `,

    privacy: `
      <div class="stats-grid">
        <div class="stat-box"><div class="stat-val">${STATE.lifetimeBlocked}</div><div class="stat-lbl">Ads & Trackers Blocked</div></div>
        <div class="stat-box"><div class="stat-val">${STATE.runtimeInfo.adBlockRules.toLocaleString()}</div><div class="stat-lbl">Active AdBlock Rules</div></div>
        <div class="stat-box"><div class="stat-val">${s.HttpsOnlyMode ? 'ON' : 'OFF'}</div><div class="stat-lbl">HTTPS-Only Mode</div></div>
        <div class="stat-box"><div class="stat-val">${s.DnsOverHttpsProvider}</div><div class="stat-lbl">DNS-over-HTTPS</div></div>
      </div>
      <div class="card">
        <div class="card-header">
          <div><div class="card-title">Built-In AdBlocker & Privacy Shield</div><div class="card-sub">Network ad blocking + cosmetic CSS ad hiding + YouTube ad skipper</div></div>
          <button class="btn btn-primary" onclick="post('updateAdBlockLists');showToast('Updating community adblock filter lists...')">${ICONS.refresh} Update Filter Lists</button>
        </div>
        ${toggleRow('AdTrackerBlockerEnabled', 'Enable Aether Privacy Shield', 'Intercept and cancel ad, analytics, telemetry, and miner requests')}
        ${selectRow('BlockerMode', 'Protection Strictness', 'Strict adds heuristic path blocking for ad/tracker endpoints', [['Off','Off'],['Standard','Standard'],['Strict','Strict (Recommended)'],['Aggressive','Aggressive']])}
        ${toggleRow('BlockAds', 'Block Ads & Ad Networks', 'DoubleClick, Google Ads, Criteo, Outbrain, Taboola, Amazon Ads, etc.')}
        ${toggleRow('BlockTrackers', 'Block Analytics & Trackers', 'Google Analytics, Meta Pixel, Hotjar, Mixpanel, Segment, Clarity, etc.')}
        ${toggleRow('BlockTelemetry', 'Block Telemetry Endpoints', 'Microsoft, Mozilla, Firebase, Crashlytics, and OS telemetry pings')}
        ${toggleRow('BlockCryptominers', 'Block Browser Cryptominers', 'Block known in-browser WebAssembly/JS mining domains')}
        ${toggleRow('BlockAnnoyances', 'Block Cookie Banners & Push Prompts', 'OneTrust, Cookiebot, OneSignal, and newsletter popups')}
        ${toggleRow('BlockSocialWidgets', 'Block Embedded Social Widgets', 'Block third-party Facebook, X/Twitter, and LinkedIn embeds')}
        ${toggleRow('StripTrackingParameters', 'Strip URL Tracking Parameters (Clean URLs)', 'Automatically remove utm_*, fbclid, gclid, igshid, si, and click IDs')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Anti-Fingerprinting & Network Hardening</div><div class="card-sub">Defend against cross-site tracking and browser fingerprinting</div></div></div>
        ${toggleRow('AntiFingerprintCanvas', 'Canvas Fingerprint Protection', 'Inject subtle per-session noise into HTML5 Canvas readouts')}
        ${toggleRow('AntiFingerprintWebGL', 'WebGL GPU Spoofing', 'Normalize WebGL vendor and renderer identifiers')}
        ${toggleRow('AntiFingerprintAudio', 'AudioContext Fingerprint Protection', 'Add tiny imperceptible variance to audio buffer readouts')}
        ${toggleRow('SpoofHardwareInfo', 'Normalize CPU, RAM & Block Battery API', 'Report generic hardwareConcurrency/deviceMemory and block Battery API')}
        ${toggleRow('BlockWebRtcIpLeak', 'WebRTC IP Leak Protection', 'Restrict non-proxied UDP candidates to protect your real local/VPN IP')}
        ${toggleRow('HttpsOnlyMode', 'HTTPS-Only Mode', 'Automatically upgrade insecure http:// navigations to https://')}
        ${toggleRow('BlockThirdPartyCookies', 'Strict Tracking & Third-Party Cookie Prevention', 'Block cross-site tracking storage and third-party cookies in CEF / Chromium')}
        ${toggleRow('SendGlobalPrivacyControl', 'Send Global Privacy Control (Sec-GPC: 1)', 'Signal legally binding opt-out of data selling/sharing')}
        ${toggleRow('SendDoNotTrack', 'Send Do Not Track (DNT: 1)', 'Attach DNT header to outgoing web requests')}
        ${selectRow('ReferrerPolicy', 'Referrer Policy', 'Control how much URL origin info is sent when clicking links', [['no-referrer','No Referrer (Strictest)'],['same-origin','Same-Origin Only'],['origin','Origin Only'],['strict-origin-when-cross-origin','Strict Origin When Cross-Origin']])}
        ${selectRow('UserAgentPreset', 'User-Agent Identity', 'Mask your browser fingerprint string', [['Default','Default (Open-Source Chromium)'],['MinimalChrome','Standard Chrome Windows'],['Firefox','Mozilla Firefox Windows'],['Safari','Apple Safari macOS'],['Custom','Custom User-Agent']])}
        ${textRow('CustomUserAgent', 'Custom User-Agent String', 'Used when User-Agent Identity is set to Custom', 'Mozilla/5.0 ...')}
        ${selectRow('DnsOverHttpsProvider', 'DNS-over-HTTPS (DoH)', 'Encrypt DNS lookups (requires restart)', [['System','System Default'],['Cloudflare','Cloudflare (1.1.1.1)'],['Quad9','Quad9 (Privacy & Security)'],['Mullvad','Mullvad DNS (No Logging)'],['AdGuard','AdGuard DNS (Ad-Blocking)'],['Custom','Custom DoH Endpoint']])}
        ${textRow('CustomDohUrl', 'Custom DoH Template URL', 'Used when DNS-over-HTTPS is set to Custom', 'https://dns.example.com/dns-query')}
        ${toggleRow('DisableAutofill', 'Disable Form Autofill', 'Prevent storing and auto-filling personal form fields')}
        ${toggleRow('DisablePasswordSaving', 'Disable Built-in Password Saving', 'Prefer a dedicated password manager extension')}
        ${toggleRow('DisableSmartScreen', 'Disable Cloud Reputation Telemetry', 'Prevent sending visited URLs to cloud phishing or reputation telemetry servers')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Custom Filter Lists & Data Hygiene</div><div class="card-sub">Manage per-site exceptions and automatic cleanup</div></div></div>
        <div class="row" data-search="Custom Blocked Domains blocklist">
          <div class="row-info"><div class="row-title">Custom Blocked Domains</div><div class="row-desc">One domain per line (e.g. ads.example.com)</div></div>
          <div class="row-ctrl"><textarea rows="3" style="width:260px" onchange="updateSetting('CustomBlockedDomains', this.value.split(/\\r?\\n/).map(x=>x.trim()).filter(Boolean))">${esc((s.CustomBlockedDomains||[]).join('\n'))}</textarea></div>
        </div>
        <div class="row" data-search="Custom Allowed Domains whitelist allowlist">
          <div class="row-info"><div class="row-title">Allowed / Paused Sites</div><div class="row-desc">Sites where Aether Shield is paused (one domain per line)</div></div>
          <div class="row-ctrl"><textarea rows="3" style="width:260px" onchange="updateSetting('CustomAllowedDomains', this.value.split(/\\r?\\n/).map(x=>x.trim()).filter(Boolean))">${esc((s.CustomAllowedDomains||[]).join('\n'))}</textarea></div>
        </div>
        ${toggleRow('ClearHistoryOnExit', 'Clear browsing history on exit', 'Automatically wipe local history when closing Aether')}
        ${toggleRow('ClearCookiesOnExit', 'Clear cookies & site data on exit', 'Ephemeral session: clear cookies and storage on close')}
        ${toggleRow('ClearCacheOnExit', 'Clear disk cache on exit', 'Delete cached web files when closing Aether')}
        ${toggleRow('ClearDownloadsOnExit', 'Clear download history on exit', 'Keep your downloaded files but clear the list')}
        <div class="row">
          <div class="row-info"><div class="row-title">Clear Browsing Data Now</div><div class="row-desc">Immediately erase cookies, cache, storage, and browsing history</div></div>
          <div class="row-ctrl"><button class="btn btn-danger" onclick="post('clearBrowsingData');showToast('Browsing data cleared')">${ICONS.trash} Clear All Data</button></div>
        </div>
      </div>
    `,

    search: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">Search Engine</div><div class="card-sub">DuckDuckGo is set as your private default search engine</div></div></div>
        ${selectRow('DefaultSearchEngine', 'Default Search Engine', 'Used when typing queries in the address bar', STATE.searchEngines.map(e => [e.Name, e.Name + ' — ' + e.Description]))}
        ${textRow('CustomSearchUrl', 'Custom Search URL Template', 'Use {query} as placeholder when Default Search Engine is Custom', 'https://duckduckgo.com/?q={query}')}
        ${toggleRow('SearchSuggestionsEnabled', 'Live Search Suggestions', 'Disabled by default so keystrokes are never sent to search servers before pressing Enter')}
        ${toggleRow('EnableBangShortcuts', 'Enable !Bang Shortcuts', 'Type !ddg, !ddgl, !w, !gh, !yt, !mdn, !r, !sp, !g followed by a query')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Address Bar</div><div class="card-sub">Local-only autocomplete and URL formatting</div></div></div>
        ${toggleRow('ShowFullUrl', 'Always show full URL (https://)', 'Display full protocol scheme in the address bar')}
        ${toggleRow('AutocompleteFromBookmarks', 'Suggest matching bookmarks', 'Show local bookmarks while typing in the address bar')}
        ${toggleRow('AutocompleteFromHistory', 'Suggest matching history', 'Show previously visited URLs from local history')}
      </div>
    `,

    tabs: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">New Tab & Tab Behavior</div><div class="card-sub">New tabs open completely blank by default for instant speed and zero distractions</div></div></div>
        ${selectRow('NewTabMode', 'New Tab Page', 'What opens when you click + or press Ctrl+T', [['Blank','Blank Page (Default — 0ms instant)'],['CustomUrl','Custom URL']])}
        ${textRow('CustomNewTabUrl', 'Custom New Tab URL', 'Used only if New Tab Page is set to Custom URL', 'about:blank')}
        ${toggleRow('OpenNewTabNextToCurrent', 'Open new tabs next to current tab', 'Insert newly opened tabs adjacent to your active tab')}
        ${toggleRow('SwitchToNewTab', 'Switch to newly opened tabs immediately', 'Focus new tabs automatically when created')}
        ${toggleRow('MiddleClickCloseTab', 'Middle-click to close tab', 'Click any tab with the middle mouse button to close it')}
        ${toggleRow('DoubleClickCloseTab', 'Double-click to close tab', 'Double-click a tab to close it quickly')}
        ${toggleRow('ShowTabMemoryTooltip', 'Show memory state in tab tooltip', 'Show Active / Sleeping / Hibernated state on hover')}
      </div>
    `,

    permissions: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">Default Site Permissions</div><div class="card-sub">Privacy-first defaults — block intrusive requests automatically</div></div></div>
        ${selectRow('PermissionNotifications', 'Notifications', 'Allow websites to send desktop notifications', ['Block','Ask','Allow'])}
        ${selectRow('PermissionGeolocation', 'Location (Geolocation)', 'Allow websites to request your physical location', ['Block','Ask','Allow'])}
        ${selectRow('PermissionCamera', 'Camera', 'Allow websites to access your webcam', ['Ask','Block','Allow'])}
        ${selectRow('PermissionMicrophone', 'Microphone', 'Allow websites to access your microphone', ['Ask','Block','Allow'])}
        ${selectRow('PermissionClipboard', 'Clipboard Read', 'Allow websites to read text from your clipboard', ['Ask','Block','Allow'])}
        ${toggleRow('BlockPopups', 'Block Pop-up Windows', 'Prevent unsolicited popup windows from opening automatically')}
        ${toggleRow('BlockAutoplayMedia', 'Block Media Autoplay', 'Require user interaction before videos/audio autoplay (requires restart)')}
        ${toggleRow('JavaScriptEnabled', 'Allow JavaScript', 'Disable globally for maximum security and ultra-low memory')}
      </div>
    `,

    extensions: `
      <div class="card">
        <div class="card-header">
          <div><div class="card-title">Install Chrome / Chromium Extensions</div><div class="card-sub">Native Chromium MV3 Extension Engine — load unpacked folders, .crx / .zip packages, or install by Chrome Web Store URL / ID</div></div>
          <button class="btn" onclick="post('openUrl',{url:'chrome://extensions'})">${ICONS.external} chrome://extensions</button>
        </div>
        ${toggleRow('ExtensionsEnabled', 'Enable Native Chromium MV3 Extension Engine', 'Enables Open-Source CEF / Chromium native extension runtime (requires restart)')}
        ${toggleRow('ExtensionDeveloperMode', 'Developer Mode', 'Show extension IDs, install paths, and unpacked loading controls')}
        <div class="row">
          <div class="row-info"><div class="row-title">Install from Local Files</div><div class="row-desc">Load an unpacked extension folder (manifest.json) or a .crx / .zip archive</div></div>
          <div class="row-ctrl">
            <button class="btn" onclick="post('installExtensionFolder')">${ICONS.folder} Load Unpacked</button>
            <button class="btn btn-primary" onclick="post('installExtensionFile')">${ICONS.package} Install .CRX / .ZIP</button>
          </div>
        </div>
        <div class="row">
          <div class="row-info"><div class="row-title">Install from Chrome Web Store</div><div class="row-desc">Paste a Chrome Web Store link or 32-character Extension ID (e.g. cjpalhdlnbpafiamejdnhcphjbkeiagm)</div></div>
          <div class="row-ctrl">
            <input id="cwsInput" type="text" style="width:240px" placeholder="Chrome Web Store URL or ID..." />
            <button class="btn btn-primary" onclick="const v=document.getElementById('cwsInput').value; if(v){post('installExtensionCws',{input:v}); showToast('Downloading extension...');}">Install</button>
          </div>
        </div>
      </div>
      <div class="card">
        <div class="card-header">
          <div><div class="card-title">Installed Extensions (${STATE.extensions.length})</div><div class="card-sub">Native Chromium MV3 Extension Engine supports content scripts, declarativeNetRequest, storage, and service workers</div></div>
          <button class="btn" onclick="post('openUrl',{url:'chrome://extensions'})">${ICONS.external} Manage in Chromium</button>
        </div>
        ${STATE.extensions.length === 0
          ? `<div class="empty">No extensions installed yet. Load an unpacked folder, a .crx/.zip file, or paste a Chrome Web Store ID above.</div>`
          : STATE.extensions.map(e => `
            <div class="list-item">
              <div class="list-meta">
                <div class="list-title">${esc(e.Name)} <span class="pill">v${esc(e.Version)}</span></div>
                <div class="list-url">${esc(e.Description || 'Chromium WebExtension')}${s.ExtensionDeveloperMode ? ' · ID: ' + esc(e.ExtensionId || e.WebViewExtensionId || e.Id) : ''}</div>
              </div>
              <div class="row-ctrl">
                ${(e.PopupPage || e.OptionsPage) ? `<button class="btn" onclick="post('openExtensionPage',{id:'${esc(e.Id)}'})">${ICONS.external} Open UI</button>` : ''}
                <label class="switch"><input type="checkbox" ${e.IsEnabled?'checked':''} onchange="post('toggleExtension',{id:'${esc(e.Id)}',enabled:this.checked})"><span class="slider"></span></label>
                <button class="btn btn-danger" onclick="post('removeExtension',{id:'${esc(e.Id)}'})">${ICONS.trash}</button>
              </div>
            </div>
          `).join('')}
      </div>
    `,

    updates: `
      <div class="card">
        <div class="card-header">
          <div>
            <div class="card-title">Git Software Updates</div>
            <div class="card-sub">${esc(u.StatusMessage)}${s.LastUpdateCheckUtc ? ' · Last scanned ' + new Date(s.LastUpdateCheckUtc).toLocaleString() : ''}</div>
          </div>
          <div style="display:flex;gap:8px">
            <button class="btn" onclick="post('checkUpdates');showToast('Scanning Git repository for new releases...')">${ICONS.refresh} Scan for Updates</button>
            ${u.IsUpdateAvailable ? `<button class="btn btn-primary" onclick="post('applyUpdate');showToast('Downloading update...')">${ICONS.download} Update to v${esc(u.LatestVersion)}</button>` : ''}
          </div>
        </div>
        <div class="row">
          <div class="row-info">
            <div class="row-title">Aether Browser v${esc(STATE.runtimeInfo.appVersion)}</div>
            <div class="row-desc">Open-Source CEF / Chromium ${esc(STATE.runtimeInfo.engineVersion)} · .NET ${esc(STATE.runtimeInfo.dotnetVersion)}</div>
            <div class="progress-bar" id="updProgressWrap" style="display:none"><div class="progress-fill" id="updProgressFill"></div></div>
          </div>
          <div class="row-ctrl"><span class="pill">${u.IsUpdateAvailable ? 'Update Ready: v' + esc(u.LatestVersion) : 'Up to date'}</span></div>
        </div>
        ${u.IsUpdateAvailable && u.ReleaseNotes ? `
        <div class="row">
          <div class="row-info">
            <div class="row-title">Release Notes (v${esc(u.LatestVersion)})</div>
            <div class="row-desc" style="white-space:pre-wrap;margin-top:6px;max-height:140px;overflow-y:auto">${esc(u.ReleaseNotes)}</div>
          </div>
        </div>` : ''}
        ${toggleRow('CheckUpdatesOnStartup', 'Scan for updates automatically on startup', 'Checks your Git repository Releases / Tags when Aether Browser launches')}
        ${selectRow('UpdateCheckFrequency', 'Startup Scan Frequency', 'How often Aether checks Git for newer releases on launch', [['EveryStartup','Every Startup'],['Daily','Once per Day'],['Weekly','Once per Week'],['Manual','Manual Scan Only']])}
        ${toggleRow('AutoDownloadUpdates', 'Automatically download updates in background', 'Download new versions automatically when detected')}
        ${toggleRow('IncludePreReleases', 'Include Pre-Release / Beta tags', 'Opt into pre-release GitHub builds and tags')}
        ${textRow('GitHubRepository', 'Git Repository (GitHub)', 'Accepts owner/repo or full https://github.com/owner/repo URL', 'owner/aether-browser')}
        ${textRow('CustomUpdateFeedUrl', 'Custom Git / JSON Update Feed URL (Optional)', 'Overrides GitHub API — supports raw releases.json in any Git repo or static host', 'https://raw.githubusercontent.com/owner/repo/main/releases.json')}
      </div>
      <div class="card">
        <div class="card-header"><div><div class="card-title">Backup & Reset</div><div class="card-sub">Export your complete Aether configuration as JSON or restore defaults</div></div></div>
        <div class="row">
          <div class="row-info"><div class="row-title">Settings Backup</div><div class="row-desc">Transfer all 75+ settings cleanly between devices</div></div>
          <div class="row-ctrl">
            <button class="btn" onclick="post('exportSettings')">${ICONS.download} Export JSON</button>
            <button class="btn" onclick="post('importSettings')">${ICONS.upload} Import JSON</button>
            <button class="btn btn-danger" onclick="if(confirm('Reset all settings to defaults?')) post('resetSettings')">Reset Defaults</button>
          </div>
        </div>
      </div>
    `,

    shortcuts: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">Keyboard Shortcuts</div><div class="card-sub">Fast keyboard-first navigation</div></div></div>
        ${[
          ['Ctrl + T', 'Open new blank tab'],
          ['Ctrl + W', 'Close current tab'],
          ['Ctrl + Shift + T', 'Reopen last closed tab'],
          ['Ctrl + N', 'Open new browser window'],
          ['Ctrl + Shift + N', 'Open new Private (Incognito) window'],
          ['Ctrl + L / Alt + D', 'Focus address bar'],
          ['Ctrl + R / F5', 'Reload current page'],
          ['Ctrl + F', 'Find in page'],
          ['Ctrl + D', 'Bookmark current page'],
          ['Ctrl + H', 'Open History'],
          ['Ctrl + J', 'Open Downloads'],
          ['Ctrl + ,', 'Open Settings'],
          ['Ctrl + Tab / Ctrl + Shift + Tab', 'Next / Previous tab'],
          ['Ctrl + 1..9', 'Switch to tab 1–9'],
          ['Alt + Left / Alt + Right', 'Navigate Back / Forward'],
          ['Ctrl + + / Ctrl + - / Ctrl + 0', 'Zoom In / Out / Reset'],
          ['F11', 'Toggle Fullscreen'],
          ['F12', 'Toggle Developer Tools']
        ].map(([k, d]) => `<div class="row"><div class="row-title">${esc(d)}</div><span class="kbd">${esc(k)}</span></div>`).join('')}
      </div>
    `,

    history: `
      <div class="card">
        <div class="card-header">
          <div><div class="card-title">Browsing History (${STATE.history.length})</div><div class="card-sub">Stored strictly locally on your device</div></div>
          <button class="btn btn-danger" onclick="post('clearHistory');showToast('History cleared')">${ICONS.trash} Clear History</button>
        </div>
        ${STATE.history.length === 0 ? `<div class="empty">No history entries yet.</div>` : STATE.history.map(h => `
          <div class="list-item" data-search="${esc(h.Title + ' ' + h.Url)}">
            <div class="list-meta" style="cursor:pointer" onclick="post('openUrl',{url:'${esc(h.Url)}'})">
              <div class="list-title">${esc(h.Title || h.Url)}</div>
              <div class="list-url">${esc(h.Url)} · ${new Date(h.VisitedUtc).toLocaleString()}</div>
            </div>
            <button class="btn btn-danger" onclick="post('removeHistory',{id:'${esc(h.Id)}'})">${ICONS.trash}</button>
          </div>
        `).join('')}
      </div>
    `,

    bookmarks: `
      <div class="card">
        <div class="card-header"><div><div class="card-title">Bookmarks (${STATE.bookmarks.length})</div><div class="card-sub">Press Ctrl+D on any page to bookmark it</div></div></div>
        ${STATE.bookmarks.length === 0 ? `<div class="empty">No bookmarks saved yet.</div>` : STATE.bookmarks.map(b => `
          <div class="list-item" data-search="${esc(b.Title + ' ' + b.Url)}">
            <div class="list-meta" style="cursor:pointer" onclick="post('openUrl',{url:'${esc(b.Url)}'})">
              <div class="list-title">${esc(b.Title || b.Url)}</div>
              <div class="list-url">${esc(b.Url)}</div>
            </div>
            <button class="btn btn-danger" onclick="post('removeBookmark',{id:'${esc(b.Id)}'})">${ICONS.trash}</button>
          </div>
        `).join('')}
      </div>
    `,

    downloads: `
      <div class="card">
        <div class="card-header">
          <div><div class="card-title">Downloads (${STATE.downloads.length})</div><div class="card-sub">Saved to ${esc(STATE.settings.DownloadDirectory)}</div></div>
          <button class="btn btn-danger" onclick="post('clearDownloads');showToast('Download list cleared')">${ICONS.trash} Clear List</button>
        </div>
        ${STATE.downloads.length === 0 ? `<div class="empty">No downloads yet.</div>` : STATE.downloads.map(d => `
          <div class="list-item" data-search="${esc(d.FileName + ' ' + d.Url)}">
            <div class="list-meta">
              <div class="list-title">${esc(d.FileName)} <span class="pill">${esc(d.State)}</span></div>
              <div class="list-url">${esc(d.FilePath || d.Url)}</div>
            </div>
            ${d.FilePath ? `<button class="btn" onclick="post('openDownloadFolder', { path: ${JSON.stringify(d.FilePath)} })">${ICONS.folder} Show in Folder</button>` : ''}
          </div>
        `).join('')}
      </div>
    `
  };
}

const TITLES = {
  general: 'General Settings', appearance: 'Appearance & Minimal UI', memory: 'Memory & Performance',
  privacy: 'Privacy & Security', search: 'Search & Address Bar', tabs: 'Tabs & New Tab',
  permissions: 'Permissions & Content', extensions: 'Extensions', updates: 'Updates & About',
  shortcuts: 'Keyboard Shortcuts', history: 'History', bookmarks: 'Bookmarks', downloads: 'Downloads'
};

function render() {
  document.querySelectorAll('.nav-item').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.tab === activeTab && !searchQuery);
  });

  const sections = sectionsHtml();
  const root = document.getElementById('root');
  const titleEl = document.getElementById('pageTitle');

  if (searchQuery.trim()) {
    const q = searchQuery.trim().toLowerCase();
    titleEl.textContent = `Search results for "${searchQuery.trim()}"`;
    const allHtml = ['general','appearance','memory','privacy','search','tabs','permissions','extensions','updates']
      .map(k => sections[k]).join('');
    const tmp = document.createElement('div');
    tmp.innerHTML = allHtml;
    const matches = Array.from(tmp.querySelectorAll('[data-search]'))
      .filter(el => el.dataset.search.toLowerCase().includes(q));
    if (matches.length === 0) {
      root.innerHTML = `<div class="card"><div class="empty">No settings matching "${esc(searchQuery)}".</div></div>`;
    } else {
      const card = document.createElement('div');
      card.className = 'card';
      matches.forEach(m => card.appendChild(m));
      root.innerHTML = '';
      root.appendChild(card);
    }
  } else {
    titleEl.textContent = TITLES[activeTab] || 'Settings';
    root.innerHTML = sections[activeTab] || sections.general;
  }
}

document.getElementById('brandIcon').innerHTML = ICONS.feather;
document.querySelectorAll('[data-ico]').forEach(el => {
  el.innerHTML = ICONS[el.dataset.ico] || '';
});
document.querySelectorAll('.nav-item').forEach(btn => {
  btn.addEventListener('click', () => {
    searchQuery = '';
    document.getElementById('searchBox').value = '';
    activeTab = btn.dataset.tab;
    render();
  });
});
document.getElementById('searchBox').addEventListener('input', e => {
  searchQuery = e.target.value;
  render();
});

window.aetherOnMessage = function(msg) {
  if (msg.type === 'toast') showToast(msg.text);
  if (msg.type === 'updateProgress') {
    const wrap = document.getElementById('updProgressWrap');
    const fill = document.getElementById('updProgressFill');
    if (wrap && fill) { wrap.style.display = 'block'; fill.style.width = msg.percent + '%'; }
  }
  if (msg.type === 'refreshState') {
    Object.assign(STATE, msg.state);
    render();
  }
};

render();
</script>
</body>
</html>
""";
    }
}
