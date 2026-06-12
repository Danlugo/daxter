using System.Text.Json;

namespace Daxter.Core.Rest;

/// <summary>
/// Builds + validates the PATCH body for the Power BI Service "Scheduled refresh" configuration
/// (<c>PATCH .../datasets/{id}/refreshSchedule</c>) for <b>import</b> models. Pure + HTTP-free so it
/// can be unit-tested directly. Only the fields the caller sets are emitted, so flipping just
/// <c>enabled</c> (or just the time zone) never clobbers the other settings — Power BI does a partial
/// update on the <c>value</c> object.
/// </summary>
public static class RefreshScheduleRequest
{
    /// <summary>Canonical weekday names Power BI accepts in <c>days</c>.</summary>
    public static readonly IReadOnlyList<string> ValidDays =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>Accepted <c>notifyOption</c> values.</summary>
    public static readonly IReadOnlyList<string> ValidNotify = ["MailOnFailure", "NoNotification"];

    /// <summary>
    /// Builds the JSON body <c>{ "value": { ... } }</c> for the PATCH. Every argument is optional;
    /// only non-null / non-empty ones are included. Throws <see cref="DaxterException"/> on invalid
    /// days, time slots (must be HH:mm on the hour or half-hour), or notify option, and when nothing
    /// at all would be changed.
    /// </summary>
    public static string Build(
        bool? enabled,
        IReadOnlyList<string>? days,
        IReadOnlyList<string>? times,
        string? localTimeZoneId,
        string? notifyOption)
    {
        var value = new Dictionary<string, object>();

        if (enabled is { } e)
        {
            value["enabled"] = e;
        }

        if (days is { Count: > 0 })
        {
            value["days"] = NormalizeDays(days);
        }

        if (times is { Count: > 0 })
        {
            value["times"] = NormalizeTimes(times);
        }

        if (!string.IsNullOrWhiteSpace(localTimeZoneId))
        {
            value["localTimeZoneId"] = localTimeZoneId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(notifyOption))
        {
            value["notifyOption"] = NormalizeNotify(notifyOption);
        }

        if (value.Count == 0)
        {
            throw new DaxterException(
                "Nothing to update — set at least one of: enabled, days, times, timezone, notify.");
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = value });
    }

    /// <summary>Maps caller-supplied weekday names (case-insensitive; full names) to Power BI's
    /// canonical casing, in calendar order, de-duplicated.</summary>
    public static IReadOnlyList<string> NormalizeDays(IReadOnlyList<string> days)
    {
        var canonical = new List<string>();
        foreach (var raw in days)
        {
            var name = raw?.Trim() ?? "";
            var match = ValidDays.FirstOrDefault(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new DaxterException(
                    $"Invalid day '{raw}'. Use full weekday names: {string.Join(", ", ValidDays)}.");
            }

            if (!canonical.Contains(match))
            {
                canonical.Add(match);
            }
        }

        // Return in calendar order so the body is stable regardless of input order.
        return ValidDays.Where(canonical.Contains).ToList();
    }

    /// <summary>Validates + normalizes time slots to <c>HH:mm</c>. Power BI only accepts slots on the
    /// hour or half-hour (minutes 00 or 30); anything else is rejected with an actionable message.</summary>
    public static IReadOnlyList<string> NormalizeTimes(IReadOnlyList<string> times)
    {
        var slots = new List<string>();
        foreach (var raw in times)
        {
            var t = raw?.Trim() ?? "";
            var parts = t.Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var hh)
                || !int.TryParse(parts[1], out var mm)
                || hh is < 0 or > 23
                || (mm != 0 && mm != 30))
            {
                throw new DaxterException(
                    $"Invalid time '{raw}'. Use HH:mm on the hour or half-hour (e.g. 06:00, 13:30); minutes must be 00 or 30.");
            }

            var slot = $"{hh:D2}:{mm:D2}";
            if (!slots.Contains(slot))
            {
                slots.Add(slot);
            }
        }

        slots.Sort(StringComparer.Ordinal);
        return slots;
    }

    /// <summary>Maps friendly aliases (on/mail, off/none) to Power BI's <c>notifyOption</c> values.</summary>
    public static string NormalizeNotify(string notifyOption)
    {
        var v = notifyOption.Trim();
        return v.ToLowerInvariant() switch
        {
            "mailonfailure" or "mail" or "on" or "email" => "MailOnFailure",
            "nonotification" or "none" or "off" or "no" => "NoNotification",
            _ => ValidNotify.FirstOrDefault(n => n.Equals(v, StringComparison.OrdinalIgnoreCase))
                 ?? throw new DaxterException(
                     $"Invalid notify '{notifyOption}'. Use MailOnFailure or NoNotification (aliases: on/off)."),
        };
    }
}
