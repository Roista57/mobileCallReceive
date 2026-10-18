using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CallReceiver.Models;
using CallReceiver.Services;
using Xunit;
using Xunit.Abstractions;

namespace CallReceiver.Tests;

public class StabilityTests(ITestOutputHelper output)
{
    [Fact] public async Task MigrationKeepsLatestSettingsPendingAndDeduplicationWithoutMovingLogs()
    {
        using var source = new TestDirectory(); using var target = new TestDirectory();
        var settings = new SettingsService(source.Path);
        await settings.SaveAsync(new AppSettings());
        var events = new EventStore(Path.Combine(source.Path, "events.sqlite3")); await events.InitializeAsync();
        var location = new SettingsLocationService(source.Path);
        await location.ChangeAsync(source.Path, target.Path);
        Assert.Equal(source.Path, (await new SettingsLocationService(source.Path).ResolveAsync()).Directory);
        Assert.Empty(Directory.GetFiles(target.Path));
        var calls = Enumerable.Range(0, 24).Select(_ => CallEvent.Test()).ToArray();
        await Task.WhenAll(calls.Select(value => Task.Run(() => events.AcceptAsync(value))));
        await events.MarkShownAsync(calls[0].EventId);
        var latest = new AppSettings { ListenPort = 19001, PhonePrefix = "발신 >" };
        await settings.SaveAsync(latest);
        Directory.CreateDirectory(Path.Combine(source.Path, "logs"));
        await File.WriteAllTextAsync(Path.Combine(source.Path, "logs", "old.log"), "keep");
        await location.CompletePendingAsync(latest, events);
        Assert.Equal(target.Path, (await location.ResolveAsync()).Directory);
        Assert.Null(location.PendingDirectory);
        Assert.Equal(latest, (await new SettingsService(target.Path).LoadAsync(new())).Settings);
        var restored = new EventStore(Path.Combine(target.Path, "events.sqlite3")); await restored.InitializeAsync();
        Assert.Equal(23, await restored.PendingCountAsync());
        Assert.True(await restored.IsShownAsync(calls[0].EventId));
        foreach (var call in calls) Assert.False(await restored.AcceptAsync(call));
        Assert.True(File.Exists(settings.SettingsPath));
        Assert.True(File.Exists(Path.Combine(source.Path, "logs", "old.log")));
        Assert.False(Directory.Exists(Path.Combine(target.Path, "logs")));
        Assert.False(File.Exists(location.BootstrapPath + ".bak"));
    }

    [Fact] public async Task MigrationFailureKeepsSourceAndBootstrapAndCanRetry()
    {
        using var source = new TestDirectory(); using var target = new TestDirectory();
        var events = new EventStore(Path.Combine(source.Path, "events.sqlite3")); await events.InitializeAsync();
        var call = CallEvent.Test(); await events.AcceptAsync(call);
        var location = new SettingsLocationService(source.Path);
        await location.ActivateDirectoryAsync(source.Path);
        await location.ChangeAsync(source.Path, target.Path);
        using (var locked = new FileStream(location.BootstrapPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => location.CompletePendingAsync(new(), events));
        Assert.Equal(source.Path, (await location.ResolveAsync()).Directory);
        Assert.Empty(Directory.GetFileSystemEntries(target.Path));
        Assert.Equal(call, await events.NextAsync());
        await location.CompletePendingAsync(new(), events);
        Assert.Equal(target.Path, (await location.ResolveAsync()).Directory);
    }

    [Fact] public async Task CollisionAfterReservationDoesNotOverwriteAndReservationCanBeCancelled()
    {
        using var source = new TestDirectory(); using var target = new TestDirectory();
        var events = new EventStore(Path.Combine(source.Path, "events.sqlite3")); await events.InitializeAsync();
        var location = new SettingsLocationService(source.Path);
        await location.ChangeAsync(source.Path, target.Path);
        var foreign = Path.Combine(target.Path, "settings.json"); await File.WriteAllTextAsync(foreign, "do not touch");
        await Assert.ThrowsAsync<InvalidOperationException>(() => location.CompletePendingAsync(new(), events));
        Assert.Equal("do not touch", await File.ReadAllTextAsync(foreign));
        Assert.False(File.Exists(location.BootstrapPath));
        await location.ChangeAsync(source.Path, source.Path);
        Assert.Null(location.PendingDirectory);
    }

    [Fact] public async Task BackupDuringAcceptanceProducesValidDatabase()
    {
        using var source = new TestDirectory(); using var target = new TestDirectory();
        var store = new EventStore(Path.Combine(source.Path, "events.sqlite3")); await store.InitializeAsync();
        var writes = Task.Run(async () => { for (var i = 0; i < 30; i++) await store.AcceptAsync(CallEvent.Test()); });
        await store.BackupToAsync(Path.Combine(target.Path, "events.sqlite3"));
        await writes;
        var copy = new EventStore(Path.Combine(target.Path, "events.sqlite3")); await copy.InitializeAsync();
        Assert.InRange(await copy.PendingCountAsync(), 0, 30);
        Assert.Equal(30, await store.PendingCountAsync());
    }

    [Fact] public async Task StartupFailuresIdentifyStageAndPreserveDatabase()
    {
        using var dir = new TestDirectory();
        var file = Path.Combine(dir.Path, "not-a-directory"); await File.WriteAllTextAsync(file, "keep");
        var access = await Assert.ThrowsAsync<StorageInitializationException>(() => StartupStorage.OpenAsync(file, new()));
        Assert.Contains("설정 폴더", access.Stage);
        var db = Path.Combine(dir.Path, "events.sqlite3"); await File.WriteAllTextAsync(db, "not a database");
        var invalid = await Assert.ThrowsAsync<StorageInitializationException>(() => StartupStorage.OpenAsync(dir.Path, new()));
        Assert.Equal("이벤트 DB 초기화", invalid.Stage);
        Assert.Equal("not a database", await File.ReadAllTextAsync(db));
    }

    [Fact] public async Task LockedSettingsFileIsReportedAsReadFailureInsteadOfResetting()
    {
        using var dir = new TestDirectory();
        var service = new SettingsService(dir.Path); await service.SaveAsync(new AppSettings { ListenPort = 19002 });
        using (var locked = new FileStream(service.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<StorageInitializationException>(() => StartupStorage.OpenAsync(dir.Path, new()));
            Assert.Equal("설정 파일 읽기·최초 저장", error.Stage);
        }
        Assert.Equal(19002, (await service.LoadAsync(new())).Settings.ListenPort);
    }

    [Fact] public async Task DataDirectoryOverrideDoesNotWriteExecutableBootstrap()
    {
        using var exe = new TestDirectory(); using var isolated = new TestDirectory(); using var target = new TestDirectory();
        var service = new SettingsLocationService(exe.Path, isolated.Path);
        await service.ActivateDirectoryAsync(target.Path);
        Assert.False(File.Exists(Path.Combine(exe.Path, "config-location.json")));
        Assert.True(File.Exists(Path.Combine(isolated.Path, "config-location.json")));
        Assert.Equal(isolated.Path, (await service.ResolveAsync()).Directory);
    }

    [Fact] public async Task NullAndMalformedNestedLogFieldsDoNotBreakRestoration()
    {
        using var dir = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, "logs"));
        var entry = new LogEntry(DateTimeOffset.Now, "good summary") { Detail = new RequestDiagnostic() };
        var record = JsonSerializer.SerializeToNode(new StoredLog(1, "entry", DateTimeOffset.Now, entry), JsonDefaults.Options)!;
        var detail = record["entry"]!["detail"]!;
        detail["stages"] = new JsonArray(null, new JsonObject { ["name"] = null, ["elapsedMs"] = 1 },
            new JsonObject { ["name"] = "ok", ["elapsedMs"] = 2 });
        detail["storage"] = null; detail["method"] = null;
        var noStages = record.DeepClone(); noStages["entry"]!["id"] = "second"; noStages["entry"]!["detail"]!["stages"] = null;
        var wrongType = record.DeepClone(); wrongType["entry"]!["detail"]!["stages"] = "wrong type";
        var compact = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
        var path = Path.Combine(dir.Path, "logs", "CallReceiver-20261017.log");
        await File.WriteAllLinesAsync(path, [record.ToJsonString(compact), wrongType.ToJsonString(compact), noStages.ToJsonString(compact), "{broken"]);
        await using var restored = new RequestLog(directory: dir.Path); await restored.InitializeAsync();
        Assert.Equal(2, restored.Snapshot().Count);
        foreach (var log in restored.Snapshot()) Assert.Contains("저장:", log.Detail!.Describe());
        Assert.Single(restored.Snapshot().Single(l => l.Id == entry.Id).Detail!.Stages);
        Assert.Empty(restored.Snapshot().Single(l => l.Id == "second").Detail!.Stages);
        Assert.NotNull(restored.PersistenceWarning);
    }

    [Fact] public async Task SlowWriterHasBoundedPayloadQueueAndReportsOverflow()
    {
        async Task<(int Peak, long Drops, long EnqueueMs)> Measure(int capacity)
        {
            using var dir = new TestDirectory();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var warnings = new ConcurrentQueue<string>();
            await using var files = new LogFileStore(dir.Path, warnings.Enqueue, () => DateTimeOffset.Now,
                capacity, _ => release.Task);
            await files.Restored;
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 2000; i++) files.Append(new LogEntry(DateTimeOffset.Now, "request " + i)
                { Detail = new RequestDiagnostic { RequestBody = new string('x', 16384) + i } });
            timer.Stop();
            var result = (files.MaxPendingRecords, files.DroppedRecords, timer.ElapsedMilliseconds);
            release.SetResult(); await files.FlushAsync();
            Assert.Equal(0, files.PendingRecords);
            if (capacity == LogFileStore.DefaultCapacity) Assert.NotEmpty(warnings);
            return result;
        }
        var before = await Measure(int.MaxValue);
        var after = await Measure(LogFileStore.DefaultCapacity);
        output.WriteLine($"2000 records / 16KB body; baseline peak={before.Peak}, drops={before.Drops}, enqueue={before.EnqueueMs}ms; bounded peak={after.Peak}, drops={after.Drops}, enqueue={after.EnqueueMs}ms");
        Assert.Equal(2000, before.Peak);
        Assert.Equal(256, after.Peak);
        Assert.Equal(1744, after.Drops);
    }

    [Fact] public async Task BlockedLogWriterDoesNotBlockHttpAcceptance()
    {
        using var dir = new TestDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new LogFileStore(dir.Path, _ => { }, () => DateTimeOffset.Now, 2, _ => release.Task);
        await using var log = new RequestLog(files);
        try
        {
            await log.InitializeAsync();
            await using var fixture = new HttpFixture(log); await fixture.StartAsync();
            for (var i = 0; i < 12; i++)
                await new HttpSelfTest().SendAsync(fixture.Settings, CallEvent.Test()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(12, await fixture.Store.PendingCountAsync());
            Assert.True(log.DroppedFileRecords > 0);
            using var deadline = new CancellationTokenSource(3000);
            while (log.Snapshot().Count < 13) await Task.Delay(10, deadline.Token);
            Assert.Equal(13, log.Snapshot().Count);
        }
        finally { release.TrySetResult(); }
    }
}
