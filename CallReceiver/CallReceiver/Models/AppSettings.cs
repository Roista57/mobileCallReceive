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
    public string NotificationText { get; init; } = "전화수신알림";
    public double PhoneFontSize { get; init; } = 22;
    public bool PhoneFontBold { get; init; } = true;
    public double TimeFontSize { get; init; } = 22;
    public bool TimeFontBold { get; init; } = true;
    public string TestPhoneNumber { get; init; } = "01012345678";
    public string TitleTimeFormat { get; init; } = "HH:mm:ss";
    public string BodyTimeFormat { get; init; } = "yyyy.MM.dd HH:mm:ss";
    public string TitleItem1 { get; init; } = "Notice";
    public string TitleItem2 { get; init; } = "Time";
    public string TitleItem3 { get; init; } = "Phone";
    public string PhonePrefix { get; init; } = "전화번호:";
    public string TimePrefix { get; init; } = "수신시간:";
    public string PhoneAlignment { get; init; } = "Left";
    public string TimeAlignment { get; init; } = "Left";
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
        if (!double.IsFinite(PopupWidth) || PopupWidth is < 1 or > 2000 ||
            !double.IsFinite(PopupHeight) || PopupHeight is < 1 or > 1200)
            return "팝업 크기는 Width 1~2000, Height 1~1200 DIP 범위입니다.";
        if (!double.IsFinite(DisplayDurationSeconds) || DisplayDurationSeconds is < 1 or > 9999)
            return "표시 시간은 1~9999초입니다.";
        if (string.IsNullOrWhiteSpace(NotificationText) || NotificationText.Length > 100)
            return "안내 문구는 1~100자로 입력하세요.";
        if (!double.IsFinite(PhoneFontSize) || PhoneFontSize is < 0 or > 100)
            return "전화번호 글꼴 크기는 0~100이어야 합니다.";
        if (!double.IsFinite(TimeFontSize) || TimeFontSize is < 0 or > 100)
            return "수신시간 글꼴 크기는 0~100이어야 합니다.";
        if (string.IsNullOrWhiteSpace(TestPhoneNumber) || TestPhoneNumber.Length > 128)
            return "테스트 전화번호는 1~128자로 입력하세요.";
        var titleItems = new[] { TitleItem1, TitleItem2, TitleItem3 };
        if (titleItems.Any(item => !PopupText.TitleItems.Contains(item))) return "상단 항목을 선택하세요.";
        var visibleItems = titleItems.Where(item => item != "None").ToArray();
        if (visibleItems.Distinct().Count() != visibleItems.Length) return "상단 항목은 중복 선택할 수 없습니다.";
        if (!PopupText.Alignments.Contains(PhoneAlignment) || !PopupText.Alignments.Contains(TimeAlignment))
            return "정렬을 선택하세요.";
        if (PhonePrefix is null || TimePrefix is null || PhonePrefix.Length > 100 || TimePrefix.Length > 100 ||
            PhonePrefix.Contains('\r') || PhonePrefix.Contains('\n') || TimePrefix.Contains('\r') || TimePrefix.Contains('\n'))
            return "접두 문구는 0~100자의 한 줄로 입력하세요.";
        if (!PopupText.ValidTimeFormat(TitleTimeFormat) || !PopupText.ValidTimeFormat(BodyTimeFormat))
            return "시간 형식은 유효한 1~100자의 한 줄로 입력하세요.";
        return null;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
