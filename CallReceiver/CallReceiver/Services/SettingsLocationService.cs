using System.Text.Json;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed record LocationConfig(string SettingsDirectory);

public sealed class SettingsLocationService(string executableDirectory, string? overrideDirectory = null)
{
    private readonly SemaphoreSlim migrationGate = new(1, 1);
    private static readonly string[] ReservedFiles = ["settings.json", "settings.json.bak", "settings.json.tmp",
        "events.sqlite3", "events.sqlite3-wal", "events.sqlite3-shm"];
    public string ExecutableDirectory { get; } = Path.GetFullPath(executableDirectory);
    public string BootstrapPath => Path.Combine(string.IsNullOrWhiteSpace(overrideDirectory)
        ? ExecutableDirectory : Path.GetFullPath(overrideDirectory), "config-location.json");
    public string? PendingDirectory { get; private set; }
    public bool HasDirectoryOverride => !string.IsNullOrWhiteSpace(overrideDirectory);

    public async Task<(string Directory, string? Warning)> ResolveAsync()
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return (Path.GetFullPath(overrideDirectory), null);
        if (!File.Exists(BootstrapPath)) return (ExecutableDirectory, null);
        try
        {
            var config = JsonSerializer.Deserialize<LocationConfig>(await File.ReadAllTextAsync(BootstrapPath), JsonDefaults.Options);
            if (string.IsNullOrWhiteSpace(config?.SettingsDirectory)) return (ExecutableDirectory, null);
            return (Path.GetFullPath(config.SettingsDirectory), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return (ExecutableDirectory, "config-location.json을 읽을 수 없어 실행 파일 위치를 사용합니다.");
        }
    }

    public async Task ChangeAsync(string currentDirectory, string targetDirectory)
    {
        var source = Path.GetFullPath(currentDirectory);
        var target = Path.GetFullPath(targetDirectory);
        await migrationGate.WaitAsync();
        try
        {
            if (SameDirectory(source, target)) { PendingDirectory = null; return; }
            EnsureEmptyTarget(target);
            await ProbeWritableAsync(target);
            await ProbeWritableAsync(Path.GetDirectoryName(BootstrapPath)!);
            PendingDirectory = target;
        }
        finally { migrationGate.Release(); }
    }

    // Called only after HTTP reception, settings commands and notification writes have stopped.
    public async Task CompletePendingAsync(AppSettings settings, EventStore store)
    {
        await migrationGate.WaitAsync();
        try
        {
            if (PendingDirectory is not { } target) return;
            EnsureEmptyTarget(target);
            var staging = Path.Combine(target, ".callreceiver-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            List<string> created = [];
            var committed = false;
            try
            {
                await new SettingsService(staging).SaveAsync(settings);
                await store.BackupToAsync(Path.Combine(staging, "events.sqlite3"));
                EnsureEmptyTarget(target);
                foreach (var name in new[] { "settings.json", "events.sqlite3" })
                {
                    var path = Path.Combine(target, name);
                    File.Move(Path.Combine(staging, name), path); // Never overwrite an existing file.
                    created.Add(path);
                }
                await ActivateDirectoryAsync(target);
                committed = true;
                PendingDirectory = null;
            }
            finally
            {
                if (!committed)
                    foreach (var path in created) TryDelete(path);
                // The unique staging folder contains only files owned by this migration.
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { migrationGate.Release(); }
    }

    public async Task ActivateDirectoryAsync(string directory)
    {
        var path = Path.GetFullPath(directory);
        var value = SameDirectory(path, ExecutableDirectory) ? "" : path;
        var temp = Path.Combine(Path.GetDirectoryName(BootstrapPath)!, ".config-location-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new LocationConfig(value), JsonDefaults.Options);
                await stream.FlushAsync();
                stream.Flush(true);
            }
            if (File.Exists(BootstrapPath)) File.Replace(temp, BootstrapPath, null);
            else File.Move(temp, BootstrapPath);
        }
        finally { TryDelete(temp); }
    }

    public static async Task ProbeWritableAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".callreceiver-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await stream.WriteAsync(new byte[] { 0 });
            await stream.FlushAsync();
        }
        finally { TryDelete(probe); }
    }
    private static void EnsureEmptyTarget(string target)
    {
        Directory.CreateDirectory(target);
        if (ReservedFiles.Any(name => File.Exists(Path.Combine(target, name)) || Directory.Exists(Path.Combine(target, name))))
            throw new InvalidOperationException("선택한 폴더에 기존 설정 또는 이벤트 파일이 있습니다. 빈 폴더를 선택하세요.");
    }
    private static bool SameDirectory(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
