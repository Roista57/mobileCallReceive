using System.Net;
using System.Text.Json;

namespace CallReceiver.Models;

public sealed record AppSettings
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; } = 18080;
    public string ApiPath { get; init; } = "/api/call";
    public string Monitor { get; init; } = "";
    public double PopupX { get; init; } = 0;
    public double PopupY { get; init; } = 0;
    public double PopupWidth { get; init; } = 350;
    public double PopupHeight { get; init; } = 140;
    public double DisplayDurationSeconds { get; init; } = 5;
    public string TimeFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";
    public bool TopMost { get; init; } = true;
    public bool PlaySound { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public bool MinimizeToTray { get; init; } = true;

    public string? Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
            return "수신 주소는 선택한 PC IPv4 또는 127.0.0.1이어야 합니다.";
        if (ListenPort is < 1 or > 65535) return "Port는 1~65535여야 합니다.";
        if (string.IsNullOrWhiteSpace(ApiPath) || ApiPath.Length > 200 || !ApiPath.StartsWith('/') ||
            ApiPath.StartsWith("//") || ApiPath.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-')))
            return "API Path는 /로 시작하고 영문, 숫자, /, _, -만 사용할 수 있습니다.";
        if (!double.IsFinite(PopupX) || !double.IsFinite(PopupY)) return "X/Y는 유효한 숫자여야 합니다.";
        if (!double.IsFinite(PopupWidth) || PopupWidth is < 240 or > 2000 ||
            !double.IsFinite(PopupHeight) || PopupHeight is < 120 or > 1200)
            return "팝업 크기는 Width 240~2000, Height 120~1200 DIP 범위입니다.";
        if (!double.IsFinite(DisplayDurationSeconds) || DisplayDurationSeconds is < 1 or > 60)
            return "표시 시간은 1~60초입니다.";
        if (string.IsNullOrWhiteSpace(TimeFormat) || TimeFormat.Length > 100)
            return "시간 형식은 1~100자로 입력하세요.";
        try { _ = DateTimeOffset.Now.ToString(TimeFormat); }
        catch (FormatException) { return "올바른 .NET 날짜/시간 형식을 입력하세요."; }
        return null;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
