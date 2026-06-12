using System.Text.Json;
using Daxter.Core;
using Daxter.Core.Rest;

namespace Daxter.Core.Tests;

/// <summary>The v1.42.0 scheduled-refresh PATCH-body builder. The value lives in the partial-update
/// contract: only the fields the caller sets may appear in the body (so flipping just `enabled` never
/// clobbers days/times), and bad days/times/notify must fail fast with an actionable message before any
/// HTTP call.</summary>
public sealed class RefreshScheduleRequestTests
{
    private static JsonElement Value(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("value");

    [Fact]
    public void Build_only_includes_fields_that_were_set()
    {
        var body = RefreshScheduleRequest.Build(enabled: true, days: null, times: null, localTimeZoneId: null, notifyOption: null);
        var value = Value(body);

        Assert.True(value.GetProperty("enabled").GetBoolean());
        Assert.False(value.TryGetProperty("days", out _));
        Assert.False(value.TryGetProperty("times", out _));
        Assert.False(value.TryGetProperty("localTimeZoneId", out _));
        Assert.False(value.TryGetProperty("notifyOption", out _));
    }

    [Fact]
    public void Build_emits_all_fields_when_all_set()
    {
        var body = RefreshScheduleRequest.Build(
            enabled: false,
            days: ["Monday", "Friday"],
            times: ["06:00", "12:30"],
            localTimeZoneId: "Pacific Standard Time",
            notifyOption: "off");
        var value = Value(body);

        Assert.False(value.GetProperty("enabled").GetBoolean());
        Assert.Equal("Pacific Standard Time", value.GetProperty("localTimeZoneId").GetString());
        Assert.Equal("NoNotification", value.GetProperty("notifyOption").GetString());
        Assert.Equal(2, value.GetProperty("days").GetArrayLength());
        Assert.Equal(2, value.GetProperty("times").GetArrayLength());
    }

    [Fact]
    public void Build_throws_when_nothing_to_change()
        => Assert.Throws<DaxterException>(() =>
            RefreshScheduleRequest.Build(null, null, null, null, null));

    [Fact]
    public void Days_are_canonicalised_to_calendar_order_and_deduped()
    {
        var days = RefreshScheduleRequest.NormalizeDays(["friday", "monday", "MONDAY"]);
        Assert.Equal(new[] { "Monday", "Friday" }, days);
    }

    [Theory]
    [InlineData("Funday")]
    [InlineData("Mon")]
    public void Invalid_day_throws(string bad)
        => Assert.Throws<DaxterException>(() => RefreshScheduleRequest.NormalizeDays([bad]));

    [Theory]
    [InlineData("06:00")]
    [InlineData("13:30")]
    [InlineData("00:00")]
    [InlineData("23:30")]
    public void Valid_times_are_accepted(string ok)
        => Assert.Single(RefreshScheduleRequest.NormalizeTimes([ok]));

    [Theory]
    [InlineData("06:15")]   // not on the half-hour
    [InlineData("24:00")]   // hour out of range
    [InlineData("6")]       // no minutes
    [InlineData("noon")]
    public void Invalid_times_throw(string bad)
        => Assert.Throws<DaxterException>(() => RefreshScheduleRequest.NormalizeTimes([bad]));

    [Fact]
    public void Times_are_sorted_and_deduped()
    {
        var times = RefreshScheduleRequest.NormalizeTimes(["12:00", "06:00", "12:00"]);
        Assert.Equal(new[] { "06:00", "12:00" }, times);
    }

    [Theory]
    [InlineData("on", "MailOnFailure")]
    [InlineData("mail", "MailOnFailure")]
    [InlineData("MailOnFailure", "MailOnFailure")]
    [InlineData("off", "NoNotification")]
    [InlineData("none", "NoNotification")]
    [InlineData("NoNotification", "NoNotification")]
    public void Notify_aliases_map_to_api_values(string input, string expected)
        => Assert.Equal(expected, RefreshScheduleRequest.NormalizeNotify(input));

    [Fact]
    public void Invalid_notify_throws()
        => Assert.Throws<DaxterException>(() => RefreshScheduleRequest.NormalizeNotify("maybe"));
}
