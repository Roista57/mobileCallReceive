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
    // Presentation only: captions, stored events and logs retain the received value.
    public static string Format(string number)
    {
        if (number.Length is < 8 or > 12 || number.Any(c => c is < '0' or > '9')) return number;
        if (number.Length == 8 && number[..2] is "15" or "16" or "18")
            return $"{number[..4]}-{number[4..]}";

        var prefixLength = number switch
        {
            _ when number.StartsWith("02", StringComparison.Ordinal) && number.Length is 9 or 10 => 2,
            _ => number[..3] switch
            {
                "010" or "070" when number.Length == 11 => 3,
                "011" or "016" or "017" or "018" or "019" or
                "031" or "032" or "033" or "041" or "042" or "043" or "044" or
                "051" or "052" or "053" or "054" or "055" or "061" or "062" or "063" or "064"
                    when number.Length is 10 or 11 => 3,
                "050" when number.Length == 11 => 3,
                "050" when number.Length == 12 => 4,
                "060" or "080" when number.Length == 10 => 3,
                _ => 0
            }
        };
        return prefixLength == 0 ? number : $"{number[..prefixLength]}-{number[prefixLength..^4]}-{number[^4..]}";
    }
    public static string Mask(string number) => number is "UNKNOWN" or "PRIVATE" ? number :
        number.Length > 7 ? $"{number[..3]}-****-{number[^4..]}" : "****";
}
