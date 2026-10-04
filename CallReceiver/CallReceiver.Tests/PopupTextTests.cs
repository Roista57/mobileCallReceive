using System.Text.Json;
using CallReceiver.Models;
using Xunit;

namespace CallReceiver.Tests;

public class PopupTextTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HH\nmm")]
    [InlineData("HH\rmm")]
    [InlineData("%")]
    public void InvalidTimeFormatsAreRejected(string format)
    {
        Assert.False(PopupText.ValidTimeFormat(format));
        Assert.NotNull((new AppSettings { TitleTimeFormat = format }).Validate());
        Assert.NotNull((new AppSettings { BodyTimeFormat = format }).Validate());
    }
    [Fact] public async Task CustomTimeFormatsPersistAndUseReceivedTime()
    {
        using var directory = new TestDirectory();
        var service = new CallReceiver.Services.SettingsService(directory.Path);
        var settings = new AppSettings { TitleTimeFormat = "HH:mm", BodyTimeFormat = "yyyy/MM/dd tt h:mm:ss" };
        await service.SaveAsync(settings);
        Assert.Equal(settings, (await service.LoadAsync(new AppSettings())).Settings);
        var call = CallEvent.Test() with { ReceivedAt = new DateTimeOffset(new DateTime(2020, 2, 3, 14, 5, 6, DateTimeKind.Local)) };
        Assert.Equal("전화수신알림 14:05 01012345678", PopupText.Title(call, settings));
        Assert.Equal(call.ReceivedAt.ToLocalTime().ToString(settings.BodyTimeFormat), PopupText.FormatTime(call.ReceivedAt, settings.BodyTimeFormat));
        Assert.False(PopupText.ValidTimeFormat(new string('y', 101)));
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Options)!;
        Assert.Equal("HH:mm:ss", legacy.TitleTimeFormat);
        Assert.Equal("yyyy.MM.dd HH:mm:ss", legacy.BodyTimeFormat);
    }
    [Fact] public void AllOrdersAndPrefixRules()
    {
        var call = CallEvent.Test() with { ReceivedAt = new DateTimeOffset(DateTime.Today.AddHours(14).AddMinutes(30)) };
        var settings = new AppSettings { NotificationText = "알림" };
        var expected = new[] { "알림 14:30:00 01012345678", "알림 01012345678 14:30:00",
            "14:30:00 알림 01012345678", "14:30:00 01012345678 알림", "01012345678 알림 14:30:00", "01012345678 14:30:00 알림" };
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], PopupText.Title(call, settings with { TitleOrder = PopupText.TitleOrders[i] }));
        Assert.Equal("번호 → 123", PopupText.Prefix(" 번호 → ", "123"));
        Assert.Equal("123", PopupText.Prefix("", "123"));
        Assert.Equal("123", PopupText.Prefix("   ", "123"));
    }
    [Fact] public void SettingsValidateAndReadLegacyServerProperties()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"serverFontSize\":12,\"serverFontBold\":true}", JsonDefaults.Options)!;
        Assert.Equal("NoticeTimePhone", settings.TitleOrder);
        Assert.Equal("전화번호:", settings.PhonePrefix);
        Assert.Equal("Left", settings.TimeAlignment);
        Assert.DoesNotContain("serverFont", JsonSerializer.Serialize(settings, JsonDefaults.Options));
        foreach (var size in new[] { 0, 1, 100 })
            Assert.Null((settings with { PhoneFontSize = size, TimeFontSize = size, PhonePrefix = "" }).Validate());
        Assert.NotNull((settings with { TitleOrder = "invalid" }).Validate());
        Assert.NotNull((settings with { PhoneAlignment = "invalid" }).Validate());
        Assert.NotNull((settings with { PhonePrefix = new string('x', 101) }).Validate());
        Assert.NotNull((settings with { TimePrefix = "a\nb" }).Validate());
    }
}
