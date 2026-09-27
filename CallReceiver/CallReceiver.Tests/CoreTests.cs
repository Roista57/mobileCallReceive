using System.Text.Json;
using CallReceiver.Models;
using CallReceiver.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CallReceiver.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcs-tests-" + Guid.NewGuid());
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}

public class CoreTests
{
    [Fact] public void SettingsRejectUnsafeAddressesAndInvalidDimensions()
    {
        var s = new AppSettings();
        Assert.Null(s.Validate());
        Assert.NotNull((s with { ListenAddress = "0.0.0.0" }).Validate());
        Assert.NotNull((s with { ListenPort = 0 }).Validate());
        Assert.NotNull((s with { ApiPath = "api/call" }).Validate());
        Assert.NotNull((s with { ApiPath = "//api/call" }).Validate());
        Assert.NotNull((s with { TimeFormat = "" }).Validate());
        Assert.NotNull((s with { TimeFormat = new string('y', 101) }).Validate());
        Assert.NotNull((s with { PopupWidth = double.NaN }).Validate());
        Assert.NotNull((s with { DisplayDurationSeconds = 0 }).Validate());
    }
    [Fact] public async Task SettingsRoundtripAndCorruptPrimaryRecovery()
    {
        using var dir = new TestDirectory();
        var service = new SettingsService(dir.Path);
        var first = new AppSettings { TimeFormat = "HH:mm:ss" };
        Assert.True((await service.LoadAsync(first)).FirstRun);
        await service.SaveAsync(first);
        await service.SaveAsync(first with { ListenPort = 19000 });
        Assert.Equal(19000, (await service.LoadAsync(first)).Settings.ListenPort);
        await File.WriteAllTextAsync(service.SettingsPath, "{broken");
        var recovered = await service.LoadAsync(first);
        Assert.Equal(first, recovered.Settings);
        Assert.NotNull(recovered.Warning);
        await service.SaveAsync(first with { ListenPort = 19001 });
        Assert.Equal(19001, (await service.LoadAsync(first)).Settings.ListenPort);
    }
    [Fact] public async Task SimultaneousDuplicatePersistsOnceAndSurvivesReopen()
    {
        using var dir = new TestDirectory();
        var path = System.IO.Path.Combine(dir.Path, "events.db");
        var store = new EventStore(path); await store.InitializeAsync();
        var value = CallEvent.Test();
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => store.AcceptAsync(value))));
        Assert.Equal(1, results.Count(x => x));
        var reopened = new EventStore(path); await reopened.InitializeAsync();
        Assert.Equal(value, await reopened.NextAsync());
        await reopened.MarkShownAsync(value.EventId);
        Assert.False(await reopened.AcceptAsync(value with { EventId = value.EventId.ToUpperInvariant() }));
        Assert.Null(await reopened.NextAsync());
    }
    [Fact] public async Task RetentionOnlyRemovesOldDisplayedEvents()
    {
        using var dir = new TestDirectory();
        var path = System.IO.Path.Combine(dir.Path, "events.db");
        var store = new EventStore(path); await store.InitializeAsync();
        var first = CallEvent.Test(); var pending = CallEvent.Test();
        await store.AcceptAsync(first); await store.AcceptAsync(pending);
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE events SET shown_at=0 WHERE event_id=$id";
            command.Parameters.AddWithValue("$id", first.EventId);
            await command.ExecuteNonQueryAsync();
        }
        await store.PruneAsync();
        Assert.Equal(pending, await store.NextAsync());
        Assert.True(await store.AcceptAsync(first));
    }
    [Theory]
    [InlineData("01012345678", "01012345678")]
    [InlineData("0212345678", "0212345678")]
    [InlineData("+821012345678", "+821012345678")]
    [InlineData("UNKNOWN", "UNKNOWN")]
    [InlineData("PRIVATE", "PRIVATE")]
    [InlineData("07012345678", "07012345678")]
    [InlineData("15881588", "15881588")]
    public void PhonePresentation(string input, string expected)
    {
        Assert.Equal(expected, PhoneText.Format(input));
        if (input == "01012345678") Assert.Equal("010-****-5678", PhoneText.Mask(input));
    }
    [Fact] public void GeometryClampsOnNegativeOriginAndScaledMonitor()
    {
        var monitor = new MonitorInfo("test", "test", -2560, -100, 2560, 1400, 1.5, 1.5);
        var bounds = MonitorService.Clamp(monitor, 5000, -50, 350, 140);
        Assert.Equal(new PopupBounds(-525, -100, 525, 210), bounds);
        var huge = MonitorService.Clamp(monitor, 0, 0, 5000, 5000);
        Assert.Equal(new PopupBounds(-2560, -100, 2560, 1400), huge);
    }
    [Fact] public void EventRequiresOffsetAndBoolean()
    {
        var json = JsonSerializer.Serialize(CallEvent.Test(), JsonDefaults.Options);
        using var good = JsonDocument.Parse(json);
        Assert.True(CallEvent.Parse(good.RootElement).IsTest);
        using var invalid = JsonDocument.Parse(json.Replace("\"isTest\": true", "\"isTest\": \"true\""));
        Assert.Throws<FormatException>(() => CallEvent.Parse(invalid.RootElement));
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        root["receivedAt"] = "2026-09-26T12:00:00";
        using var missingOffset = JsonDocument.Parse(root.ToJsonString());
        Assert.Throws<FormatException>(() => CallEvent.Parse(missingOffset.RootElement));
    }
    [Fact] public void LegacyDeviceIdIsIgnored()
    {
        var json = JsonSerializer.Serialize(CallEvent.Test(), JsonDefaults.Options);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["deviceId"] = "OLD_PHONE";
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.DoesNotContain("OLD_PHONE", JsonSerializer.Serialize(CallEvent.Parse(document.RootElement), JsonDefaults.Options));
    }
    [Fact] public async Task SettingsLocationDefaultsAndMovesWithoutOverwrite()
    {
        using var exe = new TestDirectory();
        using var target = new TestDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(exe.Path, "settings.json"), "{}");
        var locations = new SettingsLocationService(exe.Path);
        Assert.Equal(System.IO.Path.GetFullPath(exe.Path), (await locations.ResolveAsync()).Directory);
        await locations.ChangeAsync(exe.Path, target.Path);
        Assert.True(File.Exists(System.IO.Path.Combine(target.Path, "settings.json")));
        Assert.Equal(System.IO.Path.GetFullPath(target.Path), (await locations.ResolveAsync()).Directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => locations.ChangeAsync(exe.Path, target.Path));
    }
    [Fact] public void LogsAreBoundedAndSingleLine()
    {
        var log = new RequestLog();
        for (var i = 0; i < 220; i++) log.Add($"line\n{i}");
        Assert.Equal(200, log.Entries.Count);
        Assert.DoesNotContain('\n', log.Entries[0].Message);
    }
    [Fact] public void StartupRegistryRegistrationCanBeRestored()
    {
        var service = new StartupService("MCS_Test_" + Guid.NewGuid(), @"C:\Test Folder\CallReceiver.exe");
        var original = service.Read();
        try
        {
            service.Apply(true);
            Assert.Equal("\"C:\\Test Folder\\CallReceiver.exe\" --tray", service.Read());
            service.Apply(false);
            Assert.Null(service.Read());
        }
        finally { service.Restore(original); }
    }
}
