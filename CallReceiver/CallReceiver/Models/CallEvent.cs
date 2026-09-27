using System.Text.Json;
using System.Globalization;

namespace CallReceiver.Models;

public sealed record CallEvent(int SchemaVersion, string EventId, string PhoneNumber,
    DateTimeOffset ReceivedAt, DateTimeOffset SentAt, bool IsTest)
{
    public static CallEvent Test() =>
        new(1, Guid.NewGuid().ToString(), "01012345678", DateTimeOffset.Now, DateTimeOffset.Now, true);

    public static CallEvent Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("JSON 객체가 필요합니다.");
        string Text(string name, int limit)
        {
            if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(p.GetString()) || p.GetString()!.Length > limit)
                throw new FormatException($"필수 문자열 오류: {name}");
            return p.GetString()!;
        }
        DateTimeOffset Time(string name)
        {
            var value = Text(name, 64);
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"T.*(?:Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                throw new FormatException($"시간대가 포함된 ISO-8601 시각이 필요합니다: {name}");
            return time;
        }
        if (!root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var v) || v != 1)
            throw new FormatException("schemaVersion은 1이어야 합니다.");
        var id = Text("eventId", 36);
        if (!Guid.TryParseExact(id, "D", out _)) throw new FormatException("eventId는 UUID여야 합니다.");
        if (!root.TryGetProperty("isTest", out var test) || test.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("isTest는 boolean이어야 합니다.");
        return new(1, id, Text("phoneNumber", 128), Time("receivedAt"), Time("sentAt"), test.GetBoolean());
    }
}

public static class PhoneText
{
    public static string Format(string number) => number;
    public static string Mask(string number) => number is "UNKNOWN" or "PRIVATE" ? number :
        number.Length > 7 ? $"{number[..3]}-****-{number[^4..]}" : "****";
}
