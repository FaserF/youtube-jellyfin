using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.YouTube.Models;

namespace Jellyfin.Plugin.YouTube.Helpers;

public static class SubtitleHelper
{
    public static string ConvertSrv1XmlToWebVtt(string xmlContent)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return sb.ToString();
        }

        try
        {
            var doc = XDocument.Parse(xmlContent);
            foreach (var node in doc.Descendants("text"))
            {
                var startStr = node.Attribute("start")?.Value;
                var durStr = node.Attribute("dur")?.Value;
                if (!double.TryParse(startStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var startSec))
                {
                    continue;
                }

                var durSec = double.TryParse(durStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 3.0;
                var endSec = startSec + durSec;
                var text = WebUtility.HtmlDecode(node.Value)?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(FormatVttTimestamp(startSec))
                      .Append(" --> ")
                      .Append(FormatVttTimestamp(endSec))
                      .Append('\n')
                      .Append(text)
                      .Append("\n\n");
                }
            }
        }
        catch
        {
            // Return minimal header on malformed XML
        }

        return sb.ToString();
    }

    public static string ConvertJson3ToWebVtt(string json3Content)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        if (string.IsNullOrWhiteSpace(json3Content))
        {
            return sb.ToString();
        }

        try
        {
            using var doc = JsonDocument.Parse(json3Content);
            if (doc.RootElement.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in events.EnumerateArray())
                {
                    if (!ev.TryGetProperty("segs", out var segs) || segs.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var startMs = ev.TryGetProperty("tStartMs", out var tProp) ? tProp.GetDouble() : 0;
                    var durMs = ev.TryGetProperty("dDurationMs", out var dProp) ? dProp.GetDouble() : 3000;
                    var textBuilder = new StringBuilder();
                    foreach (var seg in segs.EnumerateArray())
                    {
                        if (seg.TryGetProperty("utf8", out var uProp))
                        {
                            textBuilder.Append(uProp.GetString());
                        }
                    }

                    var text = textBuilder.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(FormatVttTimestamp(startMs / 1000.0))
                          .Append(" --> ")
                          .Append(FormatVttTimestamp((startMs + durMs) / 1000.0))
                          .Append('\n')
                          .Append(text)
                          .Append("\n\n");
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed JSON3
        }

        return sb.ToString();
    }

    public static string FormatVttTimestamp(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    public static List<(string Title, long StartTicks)> BuildSponsorBlockChapters(IEnumerable<SponsorBlockSegmentDto> segments)
    {
        var list = new List<(string Title, long StartTicks)>();
        foreach (var seg in segments.OrderBy(s => s.StartSeconds))
        {
            var ticks = TimeSpan.FromSeconds(Math.Max(0, seg.StartSeconds)).Ticks;
            list.Add(($"[SponsorBlock: {seg.Category}]", ticks));
        }

        return list;
    }
}
