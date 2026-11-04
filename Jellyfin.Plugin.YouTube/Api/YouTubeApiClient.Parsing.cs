using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Models;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    private static void ParseDataApiV3VideoItems(JsonElement root, List<YouTubeVideoItem> list)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            var vid = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(vid) || !item.TryGetProperty("snippet", out var snippet))
            {
                continue;
            }

            long durationSec = 0;
            if (item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("duration", out var durProp))
            {
                try
                {
                    durationSec = (long)System.Xml.XmlConvert.ToTimeSpan(durProp.GetString() ?? "PT0S").TotalSeconds;
                }
                catch
                {
                    durationSec = 0;
                }
            }

            long views = 0;
            if (item.TryGetProperty("statistics", out var stats) && stats.TryGetProperty("viewCount", out var vc))
            {
                long.TryParse(vc.GetString(), out views);
            }

            DateTime? pubDate = snippet.TryGetProperty("publishedAt", out var pa) && DateTime.TryParse(pa.GetString(), out var dt)
                ? dt.ToUniversalTime()
                : null;

            list.Add(new YouTubeVideoItem
            {
                VideoId = vid,
                Title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? vid : vid,
                Description = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                ChannelTitle = snippet.TryGetProperty("channelTitle", out var ct) ? ct.GetString() ?? string.Empty : string.Empty,
                ChannelId = snippet.TryGetProperty("channelId", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                ThumbnailUrl = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
                DurationSeconds = durationSec,
                ViewCount = views,
                PublishedAt = pubDate,
                PublishedTimeText = pubDate?.ToString("yyyy-MM-dd") ?? string.Empty
            });
        }
    }

    public static void ExtractVideosFromInnertubeJson(JsonElement element, List<YouTubeVideoItem> results, int maxResults)
    {
        if (results.Count >= maxResults)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tileRenderer", out var tile))
            {
                var item = ParseTileRenderer(tile);
                if (item != null && !results.Any(r => r.VideoId == item.VideoId))
                {
                    results.Add(item);
                }
            }
            else if (element.TryGetProperty("videoRenderer", out var vr))
            {
                var item = ParseVideoRenderer(vr);
                if (item != null && !results.Any(r => r.VideoId == item.VideoId))
                {
                    results.Add(item);
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                ExtractVideosFromInnertubeJson(prop.Value, results, maxResults);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                ExtractVideosFromInnertubeJson(child, results, maxResults);
            }
        }
    }

    private static YouTubeVideoItem? ParseTileRenderer(JsonElement tile)
    {
        var vid = tile.TryGetProperty("contentId", out var cid) ? cid.GetString() : null;
        if (string.IsNullOrWhiteSpace(vid) &&
            tile.TryGetProperty("onSelectCommand", out var osc) &&
            osc.TryGetProperty("watchEndpoint", out var we) &&
            we.TryGetProperty("videoId", out var wid))
        {
            vid = wid.GetString();
        }

        if (string.IsNullOrWhiteSpace(vid) || vid.Length != 11)
        {
            return null;
        }

        var title = vid;
        var channelTitle = string.Empty;
        var viewsText = string.Empty;

        if (tile.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("tileMetadataRenderer", out var tmr))
        {
            if (tmr.TryGetProperty("title", out var tObj))
            {
                title = ExtractText(tObj) ?? vid;
            }

            if (tmr.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                var lineIdx = 0;
                foreach (var line in lines.EnumerateArray())
                {
                    if (line.TryGetProperty("lineRenderer", out var lr) &&
                        lr.TryGetProperty("items", out var items) &&
                        items.ValueKind == JsonValueKind.Array &&
                        items.GetArrayLength() > 0 &&
                        items[0].TryGetProperty("lineItemRenderer", out var lir) &&
                        lir.TryGetProperty("text", out var txtObj))
                    {
                        var textVal = ExtractText(txtObj) ?? string.Empty;
                        if (lineIdx == 0)
                        {
                            channelTitle = textVal;
                        }
                        else if (lineIdx == 1)
                        {
                            viewsText = textVal;
                        }

                        lineIdx++;
                    }
                }
            }
        }

        long durationSec = 0;
        var resumePct = 0;
        if (tile.TryGetProperty("header", out var hdr) &&
            hdr.TryGetProperty("tileHeaderRenderer", out var thr) &&
            thr.TryGetProperty("thumbnailOverlays", out var overlays) &&
            overlays.ValueKind == JsonValueKind.Array)
        {
            foreach (var ov in overlays.EnumerateArray())
            {
                if (ov.TryGetProperty("thumbnailOverlayTimeStatusRenderer", out var ts) &&
                    ts.TryGetProperty("text", out var durTxt))
                {
                    durationSec = ParseDurationText(ExtractText(durTxt));
                }
                else if (ov.TryGetProperty("thumbnailOverlayResumePlaybackRenderer", out var rp) &&
                         rp.TryGetProperty("percentDurationWatched", out var pctProp))
                {
                    resumePct = pctProp.GetInt32();
                }
            }
        }

        var chanId = string.Empty;
        if (tile.TryGetProperty("onLongPressCommand", out var lpc) &&
            lpc.TryGetProperty("showMenuCommand", out var smc) &&
            smc.TryGetProperty("menu", out var menu) &&
            menu.TryGetProperty("menuRenderer", out var mr) &&
            mr.TryGetProperty("items", out var mItems) &&
            mItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var mi in mItems.EnumerateArray())
            {
                if (mi.TryGetProperty("menuNavigationItemRenderer", out var mnir) &&
                    mnir.TryGetProperty("navigationEndpoint", out var ne) &&
                    ne.TryGetProperty("browseEndpoint", out var be) &&
                    be.TryGetProperty("browseId", out var bidProp))
                {
                    var bid = bidProp.GetString();
                    if (!string.IsNullOrWhiteSpace(bid) && bid.StartsWith("UC", StringComparison.Ordinal))
                    {
                        chanId = bid;
                        break;
                    }
                }
            }
        }

        var pubText = string.Empty;
        if (viewsText.Contains('•'))
        {
            var split = viewsText.Split('•');
            if (split.Length > 1)
            {
                pubText = split[1].Trim();
            }
        }

        var pubDate = ParseRelativeDate(pubText);
        var viewCount = ParseCountFromText(viewsText);

        return new YouTubeVideoItem
        {
            VideoId = vid,
            Title = title,
            ChannelTitle = channelTitle,
            ChannelId = chanId,
            Description = string.Empty,
            ThumbnailUrl = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
            DurationSeconds = durationSec,
            ResumePercent = resumePct,
            PublishedTimeText = pubText,
            PublishedAt = pubDate,
            ViewCount = viewCount
        };
    }

    private static YouTubeVideoItem? ParseVideoRenderer(JsonElement vr)
    {
        var vid = vr.TryGetProperty("videoId", out var vProp) ? vProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(vid))
        {
            return null;
        }

        var title = vr.TryGetProperty("title", out var tObj) ? ExtractText(tObj) ?? vid : vid;
        var chanTitle = vr.TryGetProperty("ownerText", out var ot) ? ExtractText(ot) ?? string.Empty : string.Empty;
        var durSec = vr.TryGetProperty("lengthText", out var lt) ? ParseDurationText(ExtractText(lt)) : 0;
        var pubText = vr.TryGetProperty("publishedTimeText", out var pt) ? ExtractText(pt) ?? string.Empty : string.Empty;

        var desc = vr.TryGetProperty("descriptionSnippet", out var ds) ? ExtractText(ds) ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(desc) && vr.TryGetProperty("detailedMetadataSnippets", out var dms) && dms.ValueKind == JsonValueKind.Array)
        {
            foreach (var snip in dms.EnumerateArray())
            {
                if (snip.TryGetProperty("snippetText", out var st))
                {
                    desc = ExtractText(st) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        break;
                    }
                }
            }
        }

        var viewText = vr.TryGetProperty("viewCountText", out var vt) ? ExtractText(vt) ?? string.Empty : string.Empty;
        var viewCount = ParseCountFromText(viewText);
        var pubDate = ParseRelativeDate(pubText);

        return new YouTubeVideoItem
        {
            VideoId = vid,
            Title = title,
            ChannelTitle = chanTitle,
            ThumbnailUrl = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
            DurationSeconds = durSec,
            PublishedTimeText = pubText,
            PublishedAt = pubDate,
            Description = desc,
            ViewCount = viewCount
        };
    }

    private static string? ExtractText(JsonElement el)
    {
        if (el.TryGetProperty("simpleText", out var st))
        {
            return st.GetString();
        }

        if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray())
            {
                if (r.TryGetProperty("text", out var t))
                {
                    sb.Append(t.GetString());
                }
            }

            return sb.ToString();
        }

        return null;
    }

    public static long ParseDurationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && int.TryParse(parts[1], out var s))
        {
            return (m * 60) + s;
        }

        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m2) && int.TryParse(parts[2], out var s2))
        {
            return (h * 3600) + (m2 * 60) + s2;
        }

        return 0;
    }

    public static DateTime? ParseRelativeDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lower = text.ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var n))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (lower.Contains("jahr") || lower.Contains("year"))
        {
            return now.AddYears(-n);
        }

        if (lower.Contains("monat") || lower.Contains("month"))
        {
            return now.AddMonths(-n);
        }

        if (lower.Contains("woche") || lower.Contains("week"))
        {
            return now.AddDays(-n * 7);
        }

        if (lower.Contains("tag") || lower.Contains("day"))
        {
            return now.AddDays(-n);
        }

        if (lower.Contains("stunde") || lower.Contains("hour"))
        {
            return now.AddHours(-n);
        }

        if (lower.Contains("minute"))
        {
            return now.AddMinutes(-n);
        }

        return now.AddDays(-1);
    }

    public static long ParseCountFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var cleaned = text.Replace(".", string.Empty).Replace(",", string.Empty).Trim();
        var match = System.Text.RegularExpressions.Regex.Match(cleaned, @"([\d\.]+)\s*([KMBkmb]?)");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num))
        {
            return 0;
        }

        var suffix = match.Groups[2].Value.ToUpperInvariant();
        return suffix switch
        {
            "B" => (long)(num * 1_000_000_000),
            "M" => (long)(num * 1_000_000),
            "K" => (long)(num * 1_000),
            _ => (long)num
        };
    }

    private static string ExtractBestThumbnailFromSnippet(JsonElement snippet)
    {
        if (snippet.TryGetProperty("thumbnails", out var thumbs))
        {
            foreach (var key in new[] { "maxres", "standard", "high", "medium", "default" })
            {
                if (thumbs.TryGetProperty(key, out var t) && t.TryGetProperty("url", out var u))
                {
                    return u.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }
}
