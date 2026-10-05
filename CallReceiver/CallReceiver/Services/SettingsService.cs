using System.Text.Json;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed record SettingsLoad(AppSettings Settings, bool FirstRun, string? Warning);

public sealed class SettingsService(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public async Task<SettingsLoad> LoadAsync(AppSettings defaults)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(SettingsPath) && !File.Exists(SettingsPath + ".bak")) return new(defaults, true, null);
        foreach (var path in new[] { SettingsPath, SettingsPath + ".bak" })
        {
            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path));
                if (json is System.Text.Json.Nodes.JsonObject obj &&
                    !obj.ContainsKey("titleItem1") && !obj.ContainsKey("titleItem2") && !obj.ContainsKey("titleItem3") &&
                    obj["titleOrder"] is { } legacy)
                {
                    if (legacy is not System.Text.Json.Nodes.JsonValue legacyValue || !legacyValue.TryGetValue<string>(out var order))
                        throw new JsonException("잘못된 상단 순서입니다.");
                    var items = System.Text.RegularExpressions.Regex.Matches(order, "Notice|Time|Phone")
                        .Select(match => match.Value).ToArray();
                    if (items.Length != 3 || string.Concat(items) != order || items.Distinct().Count() != 3)
                        throw new JsonException("잘못된 상단 순서입니다.");
                    for (var i = 0; i < 3; i++) obj[$"titleItem{i + 1}"] = items[i];
                }
                var settings = json?.Deserialize<AppSettings>(JsonDefaults.Options);
                if (settings is not null && settings.Validate() is null)
                    return new(settings, false, path.EndsWith(".bak") ? "설정 파일 오류로 이전 정상 백업을 불러왔습니다." : null);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return new(defaults, true, "설정과 백업을 읽을 수 없어 기본값을 사용합니다. 저장 전에 설정을 확인하세요.");
    }
    public async Task SaveAsync(AppSettings settings)
    {
        if (settings.Validate() is { } error) throw new ArgumentException(error);
        Directory.CreateDirectory(DirectoryPath);
        var temp = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonDefaults.Options);
            await stream.FlushAsync();
            stream.Flush(true);
        }
        if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, null);
        else File.Move(temp, SettingsPath);
    }
}
