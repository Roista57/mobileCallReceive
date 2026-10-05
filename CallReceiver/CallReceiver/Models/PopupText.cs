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
    public static readonly string[] TitleItems = ["Notice", "Time", "Phone", "None"];
    public static readonly string[] Alignments = ["Left", "Center", "Right"];
    public static string Prefix(string prefix, string value) =>
        string.IsNullOrWhiteSpace(prefix) ? value : $"{prefix.Trim()} {value}";
    public static string Title(CallEvent value, AppSettings settings)
    {
        return string.Join(" ", new[] { settings.TitleItem1, settings.TitleItem2, settings.TitleItem3 }
            .Where(item => item != "None").Select(item => item switch
            {
                "Notice" => settings.NotificationText,
                "Time" => FormatTime(value.ReceivedAt, settings.TitleTimeFormat),
                "Phone" => value.PhoneNumber,
                _ => throw new ArgumentException("상단 항목을 선택하세요.")
            }));
    }
}
