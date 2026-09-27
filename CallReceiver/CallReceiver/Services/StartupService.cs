using Microsoft.Win32;

namespace CallReceiver.Services;

public sealed class StartupService
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string valueName;
    private readonly string? executablePath;
    public StartupService(string valueName = "MobileCallSendPC", string? executablePath = null)
    { this.valueName = valueName; this.executablePath = executablePath; }
    public string? Read() { using var key = Registry.CurrentUser.OpenSubKey(RunPath); return key?.GetValue(valueName) as string; }
    public void Restore(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        if (value is null) key.DeleteValue(valueName, false); else key.SetValue(valueName, value);
    }
    public void Apply(bool enabled)
    {
        if (!enabled) { Restore(null); return; }
        var executable = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("실행 경로를 확인할 수 없습니다.");
        if (!executable.EndsWith("CallReceiver.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("자동 실행은 배포 폴더의 CallReceiver.exe로 실행한 뒤 설정하세요.");
        Restore($"\"{executable}\" --tray");
    }
}
