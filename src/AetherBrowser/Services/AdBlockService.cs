using System.Collections.Frozen;
using System.IO;
using System.Net.Http;
using AetherBrowser.Models;

namespace AetherBrowser.Services;

public sealed class AdBlockService
{
    private readonly string _filterCachePath;
    private volatile FrozenSet<string> _communityDomains =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public int CommunityRuleCount => _communityDomains.Count;
    public int BuiltInRuleCount => BuiltInAdDomains.Count + BuiltInPathPatterns.Length;
    public int TotalRuleCount => BuiltInRuleCount + CommunityRuleCount;

    // Curated high-impact ad networks, video ad servers, popunders, native ads, and RTB exchanges
    private static readonly FrozenSet<string> BuiltInAdDomains = new string[]
    {
        // Google / YouTube Ads
        "doubleclick.net", "g.doubleclick.net", "googleads.g.doubleclick.net", "ad.doubleclick.net",
        "static.doubleclick.net", "m.doubleclick.net", "mediavisor.doubleclick.net", "pubads.g.doubleclick.net",
        "securepubads.g.doubleclick.net", "pagead2.googlesyndication.com", "googlesyndication.com",
        "googleadservices.com", "adservice.google.com", "adservice.google.de", "tpc.googlesyndication.com",
        "afs.googlesyndication.com", "fundingchoicesmessages.google.com", "imasdk.googleapis.com",
        "ads.youtube.com", "s0.2mdn.net", "2mdn.net", "admob.com", "googleads4.g.doubleclick.net",

        // Major Ad Exchanges & Programmatic / RTB
        "adnxs.com", "ib.adnxs.com", "adsrvr.org", "amazon-adsystem.com", "aax.amazon-adsystem.com",
        "c.amazon-adsystem.com", "z-na.amazon-adsystem.com", "pubmatic.com", "ads.pubmatic.com",
        "rubiconproject.com", "fastlane.rubiconproject.com", "openx.net", "us-u.openx.net",
        "casalemedia.com", "as-sec.casalemedia.com", "indexww.com", "htlb.casalemedia.com",
        "criteo.com", "criteo.net", "bidder.criteo.com", "static.criteo.net", "gum.criteo.com",
        "adform.net", "track.adform.net", "adroll.com", "d.adroll.com", "bidswitch.net",
        "contextweb.com", "media.net", "contextual.media.net", "moatads.com", "z.moatads.com",
        "smartadserver.com", "ww251.smartadserver.com", "spotxchange.com", "spotx.tv",
        "teads.tv", "a.teads.tv", "triplelift.com", "tlx.3lift.com", "3lift.com",
        "yieldmo.com", "zemanta.com", "33across.com", "sovrn.com", "lijit.com", "ap.lijit.com",
        "sharethrough.com", "btlr.sharethrough.com", "sonobi.com", "apex.go.sonobi.com",
        "gumgum.com", "g2.gumgum.com", "kargo.com", "krk.kargo.com", "magnite.com",
        "prebid.org", "admantx.com", "advertising.com", "pixel.advertising.com",
        "serving-sys.com", "bs.serving-sys.com", "turn.com", "rlcdn.com", "bluekai.com",
        "exelator.com", "krxd.net", "mathtag.com", "mookie1.com", "bidr.io", "agkn.com",
        "adsafeprotected.com", "doubleverify.com", "cdn.doubleverify.com", "fwmrm.net",
        "innovid.com", "flashtalking.com", "sizmek.com", "celtra.com", "connatix.com",
        "vidazoo.com", "primis.tech", "anyclip.com", "unruly.co", "undertone.com",
        "medianet.com", "bidtellect.com", "nativo.com", "s.ntv.io", "ntv.io",
        "adthrive.com", "ads.adthrive.com", "mediavine.com", "scripts.mediavine.com",
        "ezoic.net", "g.ezoic.net", "ezodn.com", "freestar.io", "a.pub.network", "pub.network",
        "nitropay.com", "s.nitropay.com", "playwire.com", "cdn.intergient.com", "snigelweb.com",
        "buysellads.com", "srv.buysellads.com", "cdn4.buysellads.net", "carbonads.net", "srv.carbonads.net",

        // Native Ads & Clickbait Recommendations
        "taboola.com", "cdn.taboola.com", "trc.taboola.com", "outbrain.com", "widgets.outbrain.com",
        "revcontent.com", "trends.revcontent.com", "mgid.com", "jsc.mgid.com", "adblade.com",
        "content.ad", "dianomi.com", "plista.com", "ligatus.com", "adnow.com",

        // Popunders, Redirects & Aggressive Ads
        "popads.net", "c1.popads.net", "popcash.net", "propellerads.com", "adcash.com",
        "exoclick.com", "a.exoclick.com", "syndication.exoclick.com", "realsrv.com",
        "juicyads.com", "poweredby.jads.co", "trafficjunky.net", "ads.trafficjunky.net",
        "trafficfactory.biz", "hilltopads.net", "clickadu.com", "adsterra.com", "highcpmgate.com",
        "topcreativeformat.com", "effectivegatecpm.com", "profitabledisplaynetwork.com",
        "onclickads.net", "pushame.com", "monetag.com", "a-ads.com", "coinzilla.com",

        // Social & Platform Ad Endpoints
        "ads.yahoo.com", "gemini.yahoo.com", "ads.linkedin.com", "px.ads.linkedin.com",
        "ads.twitter.com", "ads-twitter.com", "ads-api.twitter.com", "an.facebook.com",
        "ads.reddit.com", "events.redditmedia.com", "ads.pinterest.com", "ads.tiktok.com",
        "business-api.tiktok.com", "ads.snapchat.com", "sc-static.net", "bingads.microsoft.com",
        "bat.bing.com", "adn.ebay.com", "ads.spotify.com", "audio-sp-tyo.spotifycdn.com",

        // Mobile / App / SDK Ad Networks
        "adcolony.com", "applovin.com", "chartboost.com", "flurry.com", "inmobi.com",
        "ironsrc.com", "unityads.unity3d.com", "vungle.com", "fyber.com", "tapjoy.com",
        "startappservice.com", "mopub.com", "smaato.net", "verve.com", "pubnative.net",

        // European / German Ad Servers
        "ivwbox.de", "ioam.de", "stroeerdigitalgroup.de", "adition.com", "imagesrv.adition.com",
        "yieldlab.net", "ad.yieldlab.net", "emsservice.de", "meetrics.net", "mxcdn.net",
        "guj.de", "ad.360yield.com", "360yield.com", "smartclip.net", "adscale.de"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] BuiltInPathPatterns =
    {
        "/pagead/", "/pcs/activeview", "/api/stats/ads", "/ptracking", "/get_midroll_info",
        "/ads/js/", "/adserver", "/adsystem", "/prebid", "/pubads_impl", "/gpt.js",
        "/adsbygoogle.js", "/show_ads.js", "/popunder", "/vast.xml", "/vpaid",
        "/pixel.gif", "/track.gif", "/beacon.js", "/fbevents.js", "/gtag/js",
        "/ad_status.js", "/adframe", "/adbanner", "/sponsored_content"
    };

    public AdBlockService(string dataDirectory)
    {
        _filterCachePath = Path.Combine(dataDirectory, "adblock_filters.txt");
        LoadCachedCommunityList();
    }

    public bool IsBlockedAdRequest(string requestUrl, Uri reqUri, BrowserSettings settings)
    {
        var host = reqUri.Host;
        var community = _communityDomains;
        if (MatchesDomainSet(host, BuiltInAdDomains) || MatchesDomainSet(host, community))
            return true;

        var pathAndQuery = reqUri.PathAndQuery.AsSpan();
        foreach (var pattern in BuiltInPathPatterns)
        {
            if (pathAndQuery.Contains(pattern.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // YouTube specific ad API endpoints (never blocks actual video playback!)
        if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            if (pathAndQuery.Contains("/youtubei/v1/log_event".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                (pathAndQuery.Contains("/api/stats/qoe".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                 pathAndQuery.Contains("adformat".AsSpan(), StringComparison.OrdinalIgnoreCase)) ||
                pathAndQuery.Contains("/pagead/".AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public async Task<(bool Ok, string Message)> UpdateCommunityFilterListsAsync()
    {
        // Lightweight, reputable, hosts-formatted community adblock lists (Peter Lowe + AdGuard DNS filter domains)
        var sources = new[]
        {
            "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext",
            "https://adguardteam.github.io/AdGuardSDNSFilter/Filters/filter.txt"
        };

        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AetherBrowser/{UpdateService.CurrentVersion}");

        int okSources = 0;
        foreach (var url in sources)
        {
            try
            {
                var text = await http.GetStringAsync(url);
                ParseFilterLines(text, collected);
                okSources++;
            }
            catch { }
        }

        if (okSources == 0 || collected.Count == 0)
            return (false, "Could not download community filter lists. Check your internet connection.");

        _communityDomains = collected.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            await File.WriteAllLinesAsync(_filterCachePath, collected);
        }
        catch { }

        return (true, $"Updated AdBlocker! Loaded {TotalRuleCount:N0} active ad & tracker rules.");
    }

    private void LoadCachedCommunityList()
    {
        try
        {
            if (!File.Exists(_filterCachePath)) return;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(_filterCachePath))
            {
                var trimmed = line.AsSpan().Trim();
                if (trimmed.Length > 2 && trimmed[0] != '#')
                    set.Add(trimmed.ToString());
            }
            if (set.Count > 0)
                _communityDomains = set.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private static void ParseFilterLines(string content, HashSet<string> target)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var s = line.AsSpan().Trim();
            if (s.Length < 4 || s[0] == '#' || s[0] == '!' || s.StartsWith("@@".AsSpan(), StringComparison.Ordinal))
                continue;

            // AdBlock syntax: ||example.com^
            if (s.StartsWith("||".AsSpan(), StringComparison.Ordinal) && s[^1] == '^')
            {
                var dom = s[2..^1];
                if (IsValidDomain(dom))
                    target.Add(dom.ToString());
                continue;
            }

            // Hosts file syntax: 127.0.0.1 example.com OR 0.0.0.0 example.com
            if (s.StartsWith("127.0.0.1 ".AsSpan(), StringComparison.Ordinal) ||
                s.StartsWith("0.0.0.0 ".AsSpan(), StringComparison.Ordinal) ||
                s.StartsWith("127.0.0.1\t".AsSpan(), StringComparison.Ordinal) ||
                s.StartsWith("0.0.0.0\t".AsSpan(), StringComparison.Ordinal))
            {
                var rest = s[(s.IndexOfAny(' ', '\t') + 1)..].TrimStart();
                var end = rest.IndexOfAny(' ', '\t', '#');
                var dom = end >= 0 ? rest[..end] : rest;
                if (IsValidDomain(dom))
                    target.Add(dom.ToString());
            }
        }
    }

    private static bool IsValidDomain(ReadOnlySpan<char> d)
    {
        if (d.Length < 4 || d.Length > 120 || !d.Contains('.') || d.IndexOfAny('/', '*', '$') >= 0)
            return false;
        if (d.Equals("localhost".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static string BuildCosmeticAdBlockScript(BrowserSettings settings)
    {
        if (!settings.AdTrackerBlockerEnabled || !settings.BlockAds || settings.BlockerMode == "Off")
            return "";

        // Pass allowed domains so cosmetic CSS hiding respects per-site pause!
        var allowedJson = System.Text.Json.JsonSerializer.Serialize(settings.CustomAllowedDomains ?? new List<string>());

        return $$"""
(function(){
  'use strict';
  try {
    if (window.__aetherCosmeticInjected) return;
    Object.defineProperty(window, '__aetherCosmeticInjected', { value: true, enumerable: false });
    const allowed = {{allowedJson}};
    const host = (location.hostname || '').toLowerCase();
    if (!host || allowed.some(d => { d = (d||'').toLowerCase(); return d && (host === d || host.endsWith('.' + d)); })) return;

    const css = `
      ins.adsbygoogle,
      .adsbygoogle,
      [id^="google_ads_iframe"],
      [id^="div-gpt-ad"],
      [class*="google-auto-placed"],
      iframe[src*="doubleclick.net"],
      iframe[src*="googlesyndication.com"],
      iframe[src*="adnxs.com"],
      iframe[src*="amazon-adsystem.com"],
      iframe[src*="taboola.com"],
      iframe[src*="outbrain.com"],
      iframe[src*="criteo.com"],
      .OUTBRAIN, .ob-widget, .trc_related_container, [id^="taboola-"],
      .ad-slot, .ad-banner, .ad-wrapper, .ad-container, .ad-unit, .adsbox,
      [data-ad-slot], [data-google-query-id], [aria-label="Advertisement"],
      ytd-ad-slot-renderer,
      ytd-promoted-sparkles-web-renderer,
      ytd-promoted-video-renderer,
      ytd-display-ad-renderer,
      ytd-companion-slot-renderer,
      ytd-in-feed-ad-layout-renderer,
      ytd-banner-promo-renderer,
      #masthead-ad,
      #player-ads,
      .ytp-ad-overlay-container,
      .ytp-ad-text-overlay,
      .ytp-ad-message-container {
        display: none !important;
        visibility: hidden !important;
        height: 0 !important;
        min-height: 0 !important;
        overflow: hidden !important;
      }
    `;

    const inject = () => {
      if (document.getElementById('aether-adblock-css')) return;
      const st = document.createElement('style');
      st.id = 'aether-adblock-css';
      st.textContent = css;
      (document.head || document.documentElement).appendChild(st);
    };
    inject();
    document.addEventListener('DOMContentLoaded', inject, { once: true });

    // Event-driven YouTube video-ad auto-skipper (zero idle CPU burn — no polling timers)
    if (host.endsWith('youtube.com')) {
      const SKIP_SEL = '.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern';
      let observedPlayer = null;
      let classObserver = null;
      let adDomObserver = null;
      let bootObserver = null;

      const handleAdState = (player) => {
        try {
          const isAd = player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting');
          if (!isAd) {
            if (adDomObserver) { adDomObserver.disconnect(); adDomObserver = null; }
            return;
          }

          const clickSkip = () => {
            const skip = player.querySelector(SKIP_SEL);
            if (skip) skip.click();
          };
          const fastForward = () => {
            const vid = player.querySelector('video');
            if (!vid) return;
            vid.muted = true;
            if (isFinite(vid.duration) && vid.duration > 0) {
              vid.currentTime = vid.duration;
            } else {
              vid.addEventListener('durationchange', () => {
                if (player.classList.contains('ad-showing') && isFinite(vid.duration) && vid.duration > 0) {
                  vid.muted = true;
                  vid.currentTime = vid.duration;
                }
              }, { once: true });
            }
          };

          clickSkip();
          fastForward();

          // Only observe player subtree while an ad is actively showing
          if (!adDomObserver) {
            const adHost = player.querySelector('.ytp-ad-module') || player;
            adDomObserver = new MutationObserver(() => {
              if (!player.classList.contains('ad-showing') && !player.classList.contains('ad-interrupting')) {
                if (adDomObserver) { adDomObserver.disconnect(); adDomObserver = null; }
                return;
              }
              clickSkip();
              fastForward();
            });
            adDomObserver.observe(adHost, { childList: true, subtree: true });
          }
        } catch(e){}
      };

      const attachToPlayer = () => {
        const player = document.querySelector('#movie_player, .html5-video-player');
        if (!player) return false;
        if (player === observedPlayer) {
          handleAdState(player);
          return true;
        }
        if (classObserver) { classObserver.disconnect(); classObserver = null; }
        if (adDomObserver) { adDomObserver.disconnect(); adDomObserver = null; }
        observedPlayer = player;
        classObserver = new MutationObserver(() => handleAdState(player));
        classObserver.observe(player, { attributes: true, attributeFilter: ['class'] });
        handleAdState(player);
        return true;
      };

      const ensureAttached = () => {
        if (attachToPlayer()) {
          if (bootObserver) { bootObserver.disconnect(); bootObserver = null; }
          return;
        }
        if (!bootObserver && document.documentElement) {
          bootObserver = new MutationObserver(() => {
            if (attachToPlayer() && bootObserver) {
              bootObserver.disconnect();
              bootObserver = null;
            }
          });
          bootObserver.observe(document.documentElement, { childList: true, subtree: true });
        }
      };

      if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', ensureAttached, { once: true });
      } else {
        ensureAttached();
      }
      window.addEventListener('yt-navigate-finish', ensureAttached, { passive: true });
    }
  } catch(e){}
})();
""";
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
}
