using System.Text;
using System.Text.Json;
using CallReceiver.Models;
using CallReceiver.Services;
using Xunit;

namespace CallReceiver.Tests;

public class PersistentLogTests
{
    [Fact] public async Task RealHttpDetailsAndPopupStateSurviveRestart()
    {
        using var directory = new TestDirectory();
        var call = CallEvent.Test();
        await using (var log = new RequestLog(directory: directory.Path))
        {
            await log.InitializeAsync();
            await using var fixture = new HttpFixture(log);
            await fixture.StartAsync();
            await new HttpSelfTest().SendAsync(fixture.Settings, call);
            using var deadline = new CancellationTokenSource(5000);
            while (!log.Snapshot().Any(e => e.Detail?.EventId == call.EventId)) await Task.Delay(10, deadline.Token);
            log.SetPopupState(call.EventId, "표시 시작");
        }
        await using var restored = new RequestLog(directory: directory.Path);
        await restored.InitializeAsync();
        var detail = restored.Snapshot().Single(e => e.Detail?.EventId == call.EventId).Detail!;
        Assert.Equal(call, detail.Event);
        Assert.Equal("표시 시작", detail.PopupState);
        Assert.Contains(detail.Stages, s => s.Name == "저장 완료");
        Assert.Contains(call.EventId, detail.ResponseBody!);
        Assert.Contains(call.PhoneNumber, detail.Describe());
    }

    [Fact] public async Task RestartRestoresRecentTwoHundredAndFullDetailAcrossDays()
    {
        using var dir = new TestDirectory();
        var now = DateTimeOffset.Parse("2026-10-15T23:59:59+09:00");
        var eventId = Guid.NewGuid().ToString();
        string latestId;
        await using (var log = new RequestLog(directory: dir.Path, now: () => now))
        {
            await log.InitializeAsync();
            for (var i = 0; i < 210; i++) log.Add("before " + i);
            now = now.AddSeconds(2);
            log.Add("POST /api/call 200 01012345678", new RequestDiagnostic
            { EventId = eventId, RequestBody = "{\"phoneNumber\":\"01012345678\"}", PopupState = "미표시" });
            log.SetPopupState(eventId, "표시 시작");
            latestId = log.Snapshot()[0].Id;
        } // Dispose must drain pending writes without an explicit Flush.
        var files = Directory.GetFiles(Path.Combine(dir.Path, "logs"));
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => f.EndsWith("CallReceiver-20261015.log"));
        Assert.Contains(files, f => f.EndsWith("CallReceiver-20261016.log"));
        Assert.Equal(212, files.Sum(f => File.ReadLines(f).Count()));
        await using var restored = new RequestLog(directory: dir.Path);
        await restored.InitializeAsync();
        Assert.Equal(200, restored.Snapshot().Count);
        Assert.Equal(latestId, restored.Snapshot()[0].Id);
        Assert.Contains("01012345678", restored.Snapshot()[0].Detail!.RequestBody);
        Assert.Equal("표시 시작", restored.Snapshot()[0].Detail!.PopupState);
        Assert.Equal("before 11", restored.Snapshot()[^1].Message);
        foreach (var file in files)
            foreach (var line in File.ReadLines(file)) { using var json = JsonDocument.Parse(line); Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32()); }
    }
    [Fact] public async Task ClearIsOrderedWithWritesAndDeletesOnlyOwnedFiles()
    {
        using var dir = new TestDirectory();
        await using (var log = new RequestLog(directory: dir.Path))
        {
            await log.InitializeAsync();
            for (var i = 0; i < 100; i++) log.Add("before " + i);
            await log.FlushAsync();
            var logs = log.LogDirectory!;
            await File.WriteAllTextAsync(Path.Combine(logs, "notes.log"), "keep");
            await File.WriteAllTextAsync(Path.Combine(logs, "CallReceiver-not-a-date.log"), "keep");
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "settings.json"), "keep");
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "events.sqlite3"), "keep");
            var clear = log.ClearAsync();
            Parallel.For(0, 25, i => log.Add("after " + i));
            await clear;
            await log.FlushAsync();
            Assert.Equal(25, log.Snapshot().Count);
            Assert.All(log.Snapshot(), e => Assert.StartsWith("after ", e.Message));
            Assert.True(File.Exists(Path.Combine(logs, "notes.log")));
            Assert.True(File.Exists(Path.Combine(logs, "CallReceiver-not-a-date.log")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(dir.Path, "settings.json")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(dir.Path, "events.sqlite3")));
        }
        await using var restored = new RequestLog(directory: dir.Path);
        await restored.InitializeAsync();
        Assert.Equal(25, restored.Snapshot().Count);
        Assert.All(restored.Snapshot(), e => Assert.StartsWith("after ", e.Message));
        await restored.ClearAsync();
        Assert.Empty(restored.Snapshot());
        Assert.Empty(Directory.GetFiles(restored.LogDirectory!, "CallReceiver-????????.log"));
    }
    [Fact] public async Task InterruptedLastLineIsSkippedAndNextAppendIsSeparated()
    {
        using var dir = new TestDirectory();
        string file;
        await using (var log = new RequestLog(directory: dir.Path))
        {
            await log.InitializeAsync(); log.Add("first"); await log.FlushAsync();
            file = Directory.GetFiles(log.LogDirectory!).Single();
        }
        await File.AppendAllTextAsync(file, "{\"version\":1", new UTF8Encoding(false));
        await using (var log = new RequestLog(directory: dir.Path))
        {
            await log.InitializeAsync();
            Assert.Equal("first", log.Snapshot().Single().Message);
            Assert.NotNull(log.PersistenceWarning);
            log.Add("second");
        }
        await using var restored = new RequestLog(directory: dir.Path);
        await restored.InitializeAsync();
        Assert.Equal(new[] { "second", "first" }, restored.Snapshot().Select(e => e.Message));
    }
    [Fact] public async Task WriteFailureKeepsMemoryAndWriterRecoversWithoutRecursion()
    {
        using var dir = new TestDirectory();
        var blockingPath = Path.Combine(dir.Path, "logs");
        await File.WriteAllTextAsync(blockingPath, "blocks directory creation");
        await using var log = new RequestLog(directory: dir.Path);
        await log.InitializeAsync();
        var warnings = 0;
        log.PersistenceFailed += _ => Interlocked.Increment(ref warnings);
        log.Add("memory only"); await log.FlushAsync();
        Assert.Equal("memory only", log.Snapshot().Single().Message);
        Assert.NotNull(log.PersistenceWarning); Assert.Equal(1, warnings);
        File.Delete(blockingPath);
        log.Add("recovered"); await log.FlushAsync();
        Assert.Single(Directory.GetFiles(blockingPath));
        await using var stream = new FileStream(Directory.GetFiles(blockingPath).Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains("recovered", await reader.ReadToEndAsync());
    }
    [Fact] public async Task LockedFileMakesClearFailExplicitlyAndAllowsRetry()
    {
        using var dir = new TestDirectory();
        string file;
        await using (var first = new RequestLog(directory: dir.Path))
        {
            await first.InitializeAsync(); first.Add("old"); await first.FlushAsync();
            file = Directory.GetFiles(first.LogDirectory!).Single();
        }
        await using var log = new RequestLog(directory: dir.Path);
        await log.InitializeAsync();
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => log.ClearAsync());
        Assert.NotNull(log.PersistenceWarning);
        await log.ClearAsync();
        Assert.False(File.Exists(file));
    }
    [Fact] public async Task ConcurrentWritesRestoreWithoutDuplicatesOrLostEntries()
    {
        using var dir = new TestDirectory();
        IReadOnlyList<LogEntry> snapshot;
        await using (var log = new RequestLog(directory: dir.Path))
        {
            await log.InitializeAsync();
            Parallel.For(0, 180, i => log.Add("동시 요청 " + i));
            snapshot = log.Snapshot();
        }
        await using var restored = new RequestLog(directory: dir.Path);
        await restored.InitializeAsync();
        Assert.Equal(180, restored.Snapshot().Count);
        Assert.Equal(snapshot.Select(e => e.Id), restored.Snapshot().Select(e => e.Id));
    }
}
