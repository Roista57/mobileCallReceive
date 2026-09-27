using System.Text.Json;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed record LocationConfig(string SettingsDirectory);

public sealed class SettingsLocationService(string executableDirectory, string? overrideDirectory = null)
{
    public string ExecutableDirectory { get; } = Path.GetFullPath(executableDirectory);
    public string BootstrapPath => Path.Combine(ExecutableDirectory, "config-location.json");

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
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or NotSupportedException)
        {
            return (ExecutableDirectory, "config-location.json을 읽을 수 없어 실행 파일 위치를 사용합니다.");
        }
    }

    public async Task ChangeAsync(string currentDirectory, string targetDirectory)
    {
        var source = Path.GetFullPath(currentDirectory);
        var target = Path.GetFullPath(targetDirectory);
        if (source.TrimEnd(Path.DirectorySeparatorChar).Equals(
            target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(target);
        foreach (var name in new[] { "settings.json", "settings.json.bak", "events.sqlite3", "events.sqlite3-wal", "events.sqlite3-shm" })
            if (File.Exists(Path.Combine(target, name)))
                throw new InvalidOperationException("선택한 폴더에 기존 설정 또는 이벤트 파일이 있습니다. 빈 폴더를 선택하세요.");
        foreach (var name in new[] { "settings.json", "settings.json.bak", "events.sqlite3", "events.sqlite3-wal", "events.sqlite3-shm" })
        {
            var from = Path.Combine(source, name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(target, name), false);
        }
        var value = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(ExecutableDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? "" : target;
        var temp = BootstrapPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new LocationConfig(value), JsonDefaults.Options));
        if (File.Exists(BootstrapPath)) File.Replace(temp, BootstrapPath, BootstrapPath + ".bak");
        else File.Move(temp, BootstrapPath);
    }
}
