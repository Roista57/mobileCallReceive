using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed class StorageInitializationException(string stage, string directory, Exception inner)
    : Exception($"{stage} 실패\n설정 위치: {directory}\n{inner.Message}", inner)
{
    public string Stage { get; } = stage;
    public string DirectoryPath { get; } = directory;
}

public sealed record InitializedStorage(SettingsService Settings, SettingsLoad Loaded, EventStore Store);

public static class StartupStorage
{
    public static async Task<InitializedStorage> OpenAsync(string directory, AppSettings defaults)
    {
        var stage = "설정 폴더 접근·쓰기 확인";
        try
        {
            await SettingsLocationService.ProbeWritableAsync(directory);
            stage = "설정 파일 읽기·최초 저장";
            var settings = new SettingsService(directory);
            var loaded = await settings.LoadAsync(defaults);
            if (loaded.FirstRun && loaded.Warning is null) await settings.SaveAsync(loaded.Settings);
            stage = "이벤트 DB 초기화";
            var store = new EventStore(Path.Combine(directory, "events.sqlite3"));
            await store.InitializeAsync();
            return new(settings, loaded, store);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or NotSupportedException)
        {
            throw new StorageInitializationException(stage, directory, error);
        }
    }
}
