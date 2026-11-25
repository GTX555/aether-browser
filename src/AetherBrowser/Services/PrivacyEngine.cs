using System.Collections.Frozen;
using System.Threading;
using AetherBrowser.Models;

namespace AetherBrowser.Services;

public sealed class PrivacyEngine
{
    private readonly AdBlockService _adBlocker;
    private long _totalBlockedLifetime;

    public PrivacyEngine(AdBlockService adBlocker)
    {
        _adBlocker = adBlocker;
    }

    private static readonly FrozenSet<string> AdDomains = new string[]
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "pagead2.googlesyndication.com", "tpc.googlesyndication.com", "adnxs.com", "adsrvr.org",
        "amazon-adsystem.com", "aax.amazon-adsystem.com", "taboola.com", "outbrain.com",
        "criteo.com", "criteo.net", "pubmatic.com", "rubiconproject.com", "openx.net",
        "casalemedia.com", "indexww.com", "advertising.com", "adform.net", "adroll.com",
        "bidswitch.net", "contextweb.com", "media.net", "moatads.com", "popads.net",
        "propellerads.com", "revcontent.com", "sharethrough.com", "smartadserver.com",
        "spotxchange.com", "teads.tv", "triplelift.com", "yieldmo.com", "zemanta.com",
        "33across.com", "adcolony.com", "applovin.com", "chartboost.com", "flurry.com",
        "inmobi.com", "ironsrc.com", "unityads.unity3d.com", "vungle.com", "mgid.com",
        "ads.yahoo.com", "ads.linkedin.com", "ads.twitter.com", "ads-twitter.com",
        "an.facebook.com", "pixel.advertising.com", "serving-sys.com", "turn.com",
        "rlcdn.com", "bluekai.com", "exelator.com", "krxd.net", "mathtag.com", "mookie1.com"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TrackerDomains = new string[]
    {
        "google-analytics.com", "googletagmanager.com", "googletagservices.com", "analytics.google.com",
        "connect.facebook.net", "pixel.facebook.com", "tr.snapchat.com", "analytics.tiktok.com",
        "bat.bing.com", "clarity.ms", "hotjar.com", "static.hotjar.com", "fullstory.com",
        "mixpanel.com", "segment.io", "segment.com", "amplitude.com", "heap.io", "heapanalytics.com",
        "kissmetrics.com", "crazyegg.com", "mouseflow.com", "luckyorange.com", "inspectlet.com",
        "quantserve.com", "scorecardresearch.com", "comscore.com", "chartbeat.com", "chartbeat.net",
        "newrelic.com", "bam.nr-data.net", "omtrdc.net", "2o7.net", "demdex.net", "everesttech.net",
        "hubspot.com", "hs-analytics.net", "hs-scripts.com", "marketo.net", "mktoresp.com",
        "pardot.com", "branch.io", "app.link", "adjust.com", "appsflyer.com", "kochava.com",
        "singular.net", "bugsnag.com", "sentry.io", "datadoghq.com", "logrocket.com",
        "optimizely.com", "vwo.com", "visualwebsiteoptimizer.com", "abtasty.com", "kameleoon.eu",
        "pendo.io", "intercom.io", "drift.com", "mc.yandex.ru", "px.ads.linkedin.com",
        "snap.licdn.com", "ct.pinterest.com", "analytics.twitter.com", "t.co", "pixel.wp.com",
        "stats.wp.com", "plausible.io", "matomo.cloud", "fingerprintjs.com", "fpjs.io"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TelemetryDomains = new string[]
    {
        "telemetry.microsoft.com", "vortex.data.microsoft.com", "settings-win.data.microsoft.com",
        "watson.telemetry.microsoft.com", "browser.events.data.msn.com", "events.data.microsoft.com",
        "telemetry.mozilla.org", "incoming.telemetry.mozilla.org", "clientservices.googleapis.com",
        "firebaselogging-pa.googleapis.com", "crashlytics.com", "app-measurement.com",
        "metrics.icloud.com", "xp.apple.com", "metrics.apple.com", "telemetry.dropbox.com",
        "stats.g.doubleclick.net", "play.google.com/log"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> CryptominerDomains = new string[]
    {
        "coinhive.com", "coin-hive.com", "jsecoin.com", "cryptoloot.pro", "crypto-loot.com",
        "authedmine.com", "minero.cc", "webminepool.com", "monerominer.rocks", "mineralt.io",
        "ppoi.org", "projectpoi.com", "deepminer.net", "browsermine.com", "coinimp.com"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SocialWidgetDomains = new string[]
    {
        "platform.twitter.com", "syndication.twitter.com", "connect.facebook.net",
        "platform.linkedin.com", "assets.pinterest.com", "apis.google.com",
        "buttons.github.io", "widgets.pinterest.com", "platform.instagram.com"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> AnnoyanceDomains = new string[]
    {
        "cookielaw.org", "cdn.cookielaw.org", "onetrust.com", "consent.cookiebot.com",
        "consent.trustarc.com", "cdn.privacy-mgmt.com", "quantcast.mgr.consensu.org",
        "cmp.osano.com", "onesignal.com", "cdn.onesignal.com", "pushengage.com",
        "pushwoosh.com", "subscribers.com", "gravitec.net", "sumo.com", "optinmonster.com",
        "privy.com", "sleeknote.com"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TrackingQueryParams = new string[]
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_cid",
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "twclid", "ttclid", "li_fat_id",
        "mc_cid", "mc_eid", "igshid", "igsh", "si", "_hsenc", "_hsmi", "hsCtaTracking",
        "yclid", "wickedid", "oly_anon_id", "oly_enc_id", "vero_id", "rb_clickid", "s_cid",
        "mkt_tok", "ref_src", "ref_url", "spm", "scm", "aff_id", "click_id"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] HeuristicTrackerPaths =
    {
        "/pagead/", "/ads/js/", "/adserver", "/pixel.gif", "/track.gif",
        "/beacon.js", "/analytics.js", "/gtag/js", "/fbevents.js"
    };

    public long TotalBlockedLifetime => Interlocked.Read(ref _totalBlockedLifetime);

    public bool ShouldBlockRequest(string requestUrl, string pageUrl, BrowserSettings settings, out string reason)
    {
        reason = "";
        if (!settings.AdTrackerBlockerEnabled || settings.BlockerMode == "Off")
            return false;

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri))
            return false;

        if (reqUri.Scheme is not ("http" or "https"))
            return false;

        var reqHost = reqUri.Host;
        if (string.IsNullOrEmpty(reqHost))
            return false;

        // Check site allowlist
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
        {
            if (MatchesDomainList(pageUri.Host, settings.CustomAllowedDomains))
                return false;
        }
        if (MatchesDomainList(reqHost, settings.CustomAllowedDomains))
            return false;

        // Custom blocklist
        if (MatchesDomainList(reqHost, settings.CustomBlockedDomains))
        {
            reason = "Custom Blocklist";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        if (settings.BlockAds && (_adBlocker.IsBlockedAdRequest(requestUrl, reqUri, settings) || MatchesDomainSet(reqHost, AdDomains)))
        {
            reason = "Ad";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        if (settings.BlockTrackers && MatchesDomainSet(reqHost, TrackerDomains))
        {
            reason = "Tracker";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        if (settings.BlockTelemetry && MatchesDomainSet(reqHost, TelemetryDomains))
        {
            reason = "Telemetry";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        if (settings.BlockCryptominers && MatchesDomainSet(reqHost, CryptominerDomains))
        {
            reason = "Cryptominer";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        if (settings.BlockSocialWidgets && MatchesDomainSet(reqHost, SocialWidgetDomains))
        {
            reason = "Social Widget";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        if (settings.BlockAnnoyances && MatchesDomainSet(reqHost, AnnoyanceDomains))
        {
            reason = "Annoyance";
            Interlocked.Increment(ref _totalBlockedLifetime);
            return true;
        }

        // Strict/Aggressive path heuristics (zero-allocation span search)
        if (settings.BlockerMode is "Strict" or "Aggressive")
        {
            var pathSpan = reqUri.AbsolutePath.AsSpan();
            foreach (var pattern in HeuristicTrackerPaths)
            {
                if (pathSpan.Contains(pattern.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Heuristic Tracker";
                    Interlocked.Increment(ref _totalBlockedLifetime);
                    return true;
                }
            }
        }

        return false;
    }

    public string CleanTrackingUrl(string url, BrowserSettings settings)
    {
        if (!settings.StripTrackingParameters)
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (string.IsNullOrEmpty(uri.Query))
            return url;

        var rawQuery = uri.Query.TrimStart('?');
        var parts = rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        var removedAny = false;

        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            var key = eqIdx >= 0 ? part[..eqIdx] : part;
            if (TrackingQueryParams.Contains(key))
            {
                removedAny = true;
                continue;
            }
            kept.Add(part);
        }

        if (!removedAny)
            return url;

        var builder = new UriBuilder(uri)
        {
            Query = kept.Count > 0 ? string.Join("&", kept) : ""
        };
        return builder.Uri.AbsoluteUri;
    }

    // Default (Open-Source Chromium) uses CEF's native Chromium User-Agent when empty string is returned
    public string ResolveUserAgent(BrowserSettings settings) => settings.UserAgentPreset switch
    {
        "MinimalChrome" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Firefox" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Safari" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
        "Custom" when !string.IsNullOrWhiteSpace(settings.CustomUserAgent) => settings.CustomUserAgent.Trim(),
        _ => ""
    };

    public string BuildPrivacyInjectionScript(BrowserSettings settings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("(function(){ 'use strict'; try {");
        sb.AppendLine("if (window.__aetherPrivacyInjected) return;");
        sb.AppendLine("Object.defineProperty(window, '__aetherPrivacyInjected', { value: true, enumerable: false });");

        // Global Privacy Control & DNT on navigator
        if (settings.SendGlobalPrivacyControl)
        {
            sb.AppendLine("Object.defineProperty(navigator, 'globalPrivacyControl', { get: () => true, configurable: true });");
        }
        if (settings.SendDoNotTrack)
        {
            sb.AppendLine("Object.defineProperty(navigator, 'doNotTrack', { get: () => '1', configurable: true });");
        }

        // Hardware concurrency & device memory spoofing
        if (settings.SpoofHardwareInfo)
        {
            sb.AppendLine("Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 4, configurable: true });");
            sb.AppendLine("Object.defineProperty(navigator, 'deviceMemory', { get: () => 8, configurable: true });");
            sb.AppendLine("if ('getBattery' in navigator) { navigator.getBattery = () => Promise.reject(new DOMException('Blocked by Aether Privacy')); }");
        }

        // Anti-fingerprint Canvas subtle per-session deterministic noise
        if (settings.AntiFingerprintCanvas)
        {
            sb.AppendLine(@"
const _seed = (Math.random() * 1e6) | 0;
const _origToDataURL = HTMLCanvasElement.prototype.toDataURL;
HTMLCanvasElement.prototype.toDataURL = function() {
  try {
    const ctx = this.getContext('2d');
    if (ctx && this.width > 0 && this.height > 0 && this.width * this.height < 262144) {
      const img = ctx.getImageData(0, 0, Math.min(this.width, 16), Math.min(this.height, 16));
      img.data[0] = (img.data[0] ^ (_seed & 1));
      ctx.putImageData(img, 0, 0);
    }
  } catch(e){}
  return _origToDataURL.apply(this, arguments);
};");
        }

        // Anti-fingerprint WebGL vendor/renderer normalization
        if (settings.AntiFingerprintWebGL)
        {
            sb.AppendLine(@"
const _patchWebGL = (proto) => {
  if (!proto || !proto.getParameter) return;
  const orig = proto.getParameter;
  proto.getParameter = function(p) {
    if (p === 37445) return 'Generic Vendor';
    if (p === 37446) return 'Generic Renderer';
    return orig.apply(this, arguments);
  };
};
if (typeof WebGLRenderingContext !== 'undefined') _patchWebGL(WebGLRenderingContext.prototype);
if (typeof WebGL2RenderingContext !== 'undefined') _patchWebGL(WebGL2RenderingContext.prototype);");
        }

        // Anti-fingerprint AudioContext
        if (settings.AntiFingerprintAudio)
        {
            sb.AppendLine(@"
if (typeof AudioBuffer !== 'undefined' && AudioBuffer.prototype.getChannelData) {
  const _origGetChannel = AudioBuffer.prototype.getChannelData;
  AudioBuffer.prototype.getChannelData = function() {
    const data = _origGetChannel.apply(this, arguments);
    if (data && data.length > 0) data[0] = data[0] + 1e-7;
    return data;
  };
}");
        }

        // WebRTC IP leak protection (disable mDNS/host candidate harvesting unless user explicitly opts out)
        if (settings.BlockWebRtcIpLeak)
        {
            sb.AppendLine(@"
if (typeof RTCPeerConnection !== 'undefined') {
  const _OrigRTC = window.RTCPeerConnection;
  window.RTCPeerConnection = function(cfg, constraints) {
    cfg = Object.assign({}, cfg, { iceTransportPolicy: 'relay' });
    return new _OrigRTC(cfg, constraints);
  };
  window.RTCPeerConnection.prototype = _OrigRTC.prototype;
}");
        }

        // Disable web animations for ultra-lightweight performance
        if (settings.DisableWebAnimations)
        {
            sb.AppendLine(@"
const _style = document.createElement('style');
_style.textContent = '*, *::before, *::after { animation-duration: 0.001ms !important; transition-duration: 0.001ms !important; scroll-behavior: auto !important; }';
(document.head || document.documentElement).appendChild(_style);");
        }

        sb.AppendLine("} catch(e){} })();");
        var cosmetic = AdBlockService.BuildCosmeticAdBlockScript(settings);
        if (!string.IsNullOrEmpty(cosmetic))
            sb.AppendLine(cosmetic);
        return sb.ToString();
    }

    private static bool MatchesDomainSet(string host, FrozenSet<string> set)
    {
        if (set.Count == 0 || string.IsNullOrEmpty(host)) return false;
        if (set.Contains(host)) return true;

        var span = host.AsSpan();
        var dotIdx = span.IndexOf('.');
        while (dotIdx >= 0 && dotIdx < span.Length - 1)
        {
            span = span[(dotIdx + 1)..];
            var nextDot = span.IndexOf('.');
            if (nextDot < 0)
                break;
            if (set.Contains(span.ToString()))
                return true;
            dotIdx = nextDot;
        }
        return false;
    }

    private static bool MatchesDomainList(string host, List<string> list)
    {
        if (list.Count == 0 || string.IsNullOrEmpty(host)) return false;
        var hostSpan = host.AsSpan();
        foreach (var raw in list)
        {
            var domain = raw.AsSpan().Trim();
            if (domain.IsEmpty) continue;
            if (hostSpan.Equals(domain, StringComparison.OrdinalIgnoreCase))
                return true;
            if (hostSpan.Length > domain.Length &&
                hostSpan[hostSpan.Length - domain.Length - 1] == '.' &&
                hostSpan.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
