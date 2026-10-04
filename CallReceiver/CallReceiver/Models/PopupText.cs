namespace CallReceiver.Models;

public static class PopupText
{
    public static string FormatTime(DateTimeOffset time, string format) =>
        time.ToLocalTime().ToString(format, System.Globalization.CultureInfo.CurrentCulture);
    public static bool ValidTimeFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 100 || format.Contains('\r') || format.Contains('\n')) return false;
        try { _ = FormatTime(DateTimeOffset.Now, format); return true; }
        catch (FormatException) { return false; }
    }
    public static string PreviewTime(string format) =>
        ValidTimeFormat(format) ? FormatTime(DateTimeOffset.Now, format) : "잘못된 시간 형식";
    public static readonly string[] TitleOrders = ["NoticeTimePhone", "NoticePhoneTime", "TimeNoticePhone",
        "TimePhoneNotice", "PhoneNoticeTime", "PhoneTimeNotice"];
    public static readonly string[] Alignments = ["Left", "Center", "Right"];
    public static string Prefix(string prefix, string value) =>
        string.IsNullOrWhiteSpace(prefix) ? value : $"{prefix.Trim()} {value}";
    public static string Title(CallEvent value, AppSettings settings)
    {
        var notice = settings.NotificationText;
        var time = FormatTime(value.ReceivedAt, settings.TitleTimeFormat);
        var phone = value.PhoneNumber;
        return settings.TitleOrder switch
        {
            "NoticeTimePhone" => $"{notice} {time} {phone}",
            "NoticePhoneTime" => $"{notice} {phone} {time}",
            "TimeNoticePhone" => $"{time} {notice} {phone}",
            "TimePhoneNotice" => $"{time} {phone} {notice}",
            "PhoneNoticeTime" => $"{phone} {notice} {time}",
            "PhoneTimeNotice" => $"{phone} {time} {notice}",
            _ => throw new ArgumentException("상단 순서를 선택하세요.")
        };
    }
}
