using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using optimizerDuck.Domain.Diagnostics;
using optimizerDuck.Domain.Measurements;

namespace optimizerDuck.Services.Measurements;

/// <summary>Boot time result: median, how many boots it used, where it came from, culprits.</summary>
public sealed record BootTimeResult(
    Measured<int> MedianMs,
    Measured<BootTimeSource> Source,
    int Samples,
    IReadOnlyList<BootCulprit> Culprits
);

/// <summary>
///     Reads boot times from the event log (plan phase 3). First choice: Diagnostics-Performance
///     event 100. Plan B when Windows does not log it: kernel start (Kernel-General 12) to user
///     logon (Winlogon 7001). Median of the last 3 boots in 30 days. Read-only.
/// </summary>
public static class BootTimeProbe
{
    private const string PerformanceLog = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private const long ThirtyDaysMs = 30L * 24 * 60 * 60 * 1000;
    private const int MaxSamples = 3;

    public static BootTimeResult Read()
    {
        var event100 = ReadEvent100();
        var culprits = ReadCulprits();
        if (event100.Count > 0)
            return new BootTimeResult(
                Measured<int>.Of(Median(event100)),
                Measured<BootTimeSource>.Of(BootTimeSource.DiagnosticsPerformance),
                event100.Count,
                culprits
            );

        var planB = ReadKernelToLogon(out var reason);
        return planB.Count > 0
            ? new BootTimeResult(
                Measured<int>.Of(Median(planB)),
                Measured<BootTimeSource>.Of(BootTimeSource.KernelToLogon),
                planB.Count,
                culprits
            )
            : new BootTimeResult(
                Measured<int>.Missing(reason),
                Measured<BootTimeSource>.Missing(reason),
                0,
                culprits
            );
    }

    /// <summary>Median of the values (the middle one, or the mean of the two middle ones).</summary>
    public static int Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("No values.", nameof(values));
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    ///     Pairs each kernel start with the first logon after it (and before the next start).
    ///     Returns the durations in ms of the most recent boots, newest first.
    /// </summary>
    public static IReadOnlyList<int> PairBoots(
        IReadOnlyList<DateTime> kernelStarts,
        IReadOnlyList<DateTime> logons,
        int max = MaxSamples
    )
    {
        var starts = kernelStarts.Order().ToList();
        var result = new List<int>();
        for (var i = starts.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var start = starts[i];
            var next = i + 1 < starts.Count ? starts[i + 1] : DateTime.MaxValue;
            var logon = logons.Where(l => l > start && l < next).Order().FirstOrDefault();
            if (logon == default)
                continue;
            var ms = (logon - start).TotalMilliseconds;
            if (ms is > 0 and < 30 * 60 * 1000) // ignore absurd pairs (a logon hours later)
                result.Add((int)ms);
        }
        return result;
    }

    private static List<int> ReadEvent100()
    {
        var values = new List<int>();
        foreach (var data in ReadEvents(PerformanceLog, "EventID=100", MaxSamples, out _))
            if (int.TryParse(data.GetValueOrDefault("BootTime"), out var ms) && ms > 0)
                values.Add(ms);
        return values;
    }

    private static List<BootCulprit> ReadCulprits()
    {
        var culprits = new List<BootCulprit>();
        foreach (
            var (data, id) in ReadEventsWithId(PerformanceLog, "EventID>=101 and EventID<=110", 20)
        )
        {
            var name = data.GetValueOrDefault("FriendlyName") is { Length: > 0 } friendly
                ? friendly
                : data.GetValueOrDefault("Name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var delay =
                int.TryParse(data.GetValueOrDefault("DegradationTime"), out var d) ? d
                : int.TryParse(data.GetValueOrDefault("TotalTime"), out var t) ? t
                : 0;
            culprits.Add(new BootCulprit(name.Trim(), id, delay));
        }

        return culprits
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.DelayMs).First())
            .OrderByDescending(c => c.DelayMs)
            .Take(10)
            .ToList();
    }

    private static List<int> ReadKernelToLogon(out NotMeasuredReason reason)
    {
        var starts = new List<DateTime>();
        foreach (
            var (data, time) in ReadSystemEvents(
                "Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12",
                out reason
            )
        )
        {
            // StartTime is when the kernel really started; TimeCreated is a moment later.
            starts.Add(
                DateTime.TryParse(
                    data.GetValueOrDefault("StartTime"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var start
                )
                    ? start
                    : time
            );
        }

        var logons = ReadSystemEvents(
                "Provider[@Name='Microsoft-Windows-Winlogon'] and EventID=7001",
                out _
            )
            .Select(e => e.Time)
            .ToList();

        return PairBoots(starts, logons).ToList();
    }

    private static List<(Dictionary<string, string> Data, DateTime Time)> ReadSystemEvents(
        string filter,
        out NotMeasuredReason reason
    )
    {
        reason = NotMeasuredReason.NotReported;
        var list = new List<(Dictionary<string, string>, DateTime)>();
        try
        {
            var xpath =
                $"*[System[{filter} and TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]";
            using var reader = new EventLogReader(
                new EventLogQuery("System", PathType.LogName, xpath)
            );
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                using (record)
                    list.Add(
                        (
                            ParseData(record.ToXml()),
                            record.TimeCreated?.ToUniversalTime() ?? default
                        )
                    );
        }
        catch (UnauthorizedAccessException)
        {
            reason = NotMeasuredReason.AccessDenied;
        }
        catch (EventLogException)
        {
            reason = NotMeasuredReason.QueryFailed;
        }
        return list;
    }

    private static IEnumerable<Dictionary<string, string>> ReadEvents(
        string log,
        string filter,
        int max,
        out NotMeasuredReason reason
    )
    {
        reason = NotMeasuredReason.NotReported;
        return ReadEventsWithId(log, filter, max).Select(e => e.Data).ToList();
    }

    private static List<(Dictionary<string, string> Data, int Id)> ReadEventsWithId(
        string log,
        string filter,
        int max
    )
    {
        var list = new List<(Dictionary<string, string>, int)>();
        try
        {
            var query = new EventLogQuery(
                log,
                PathType.LogName,
                $"*[System[({filter}) and TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]]]"
            )
            {
                ReverseDirection = true, // newest first
            };
            using var reader = new EventLogReader(query);
            for (
                var record = reader.ReadEvent();
                record is not null && list.Count < max;
                record = reader.ReadEvent()
            )
                using (record)
                    list.Add((ParseData(record.ToXml()), record.Id));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // Missing or unreadable log: no samples, the caller falls back or reports why.
        }
        return list;
    }

    /// <summary>Reads the EventData name/value pairs of an event's XML.</summary>
    public static Dictionary<string, string> ParseData(string eventXml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var element in XDocument.Parse(eventXml).Descendants())
            {
                if (element.Name.LocalName != "Data")
                    continue;
                var name = element.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                    result[name] = element.Value;
            }
        }
        catch (XmlException)
        {
            // Malformed XML: no data.
        }
        return result;
    }
}
