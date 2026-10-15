using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CallReceiver.Models;

public sealed record RequestStage(string Name, double ElapsedMs);
public sealed record RequestDiagnostic
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public string? EventId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; init; }
    public double ElapsedMs { get; init; }
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public int CapturedBytes { get; init; }
    public string? RemoteAddress { get; init; }
    public string? LocalAddress { get; init; }
    public string? RequestBody { get; init; }
    public bool Truncated { get; init; }
    public bool Incomplete { get; init; }
    public bool InvalidUtf8 { get; init; }
    public int? HttpStatus { get; init; }
    public string? ResponseBody { get; init; }
    public string Validation { get; init; } = "미검증";
    public string Storage { get; init; } = "해당 없음";
    public string ResponseState { get; init; } = "처리 중";
    public string PopupState { get; init; } = "해당 없음";
    public string? Error { get; init; }
    public CallEvent? Event { get; init; }
    public IReadOnlyList<RequestStage> Stages { get; init; } = [];

    public static string Time(DateTimeOffset? time) => time is null ? "기록 없음" :
        TimeZoneInfo.ConvertTime(time.Value, TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"))
            .ToString("yyyy.MM.dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    private static string Json(string? value)
    {
        if (value is null) return "본문 없음/기록 없음";
        try { using var doc = JsonDocument.Parse(value); return JsonSerializer.Serialize(doc.RootElement, JsonDefaults.Options); }
        catch (JsonException) { return value; }
    }
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"요청 ID: {RequestId}\neventId: {EventId ?? "기록 없음"}");
        text.AppendLine($"수신: {Time(StartedAt)}\n종료: {Time(FinishedAt)}\n경과: {ElapsedMs:F1}ms");
        text.AppendLine($"\n{RemoteAddress ?? "주소 미확인"} → {LocalAddress ?? "주소 미확인"}");
        text.AppendLine($"{Method} {Path}\nContent-Type: {ContentType ?? "없음"}\n본문 크기: {ContentLength?.ToString() ?? "미확인"} bytes / 기록 {CapturedBytes} bytes");
        if (Event is { } call) text.AppendLine($"\n전화번호: {call.PhoneNumber}\n수신시간: {Time(call.ReceivedAt)}\nsentAt: {call.SentAt:O}\nisTest: {call.IsTest}\nschemaVersion: {call.SchemaVersion}");
        text.AppendLine($"\n검증: {Validation}\n저장: {Storage}\nHTTP: {HttpStatus?.ToString() ?? "응답 미완료"}\n응답: {ResponseState}\n팝업: {PopupState}");
        text.AppendLine("서버의 응답 쓰기 완료는 모바일 수신 확인이 아닙니다.");
        text.AppendLine("\n처리 흐름: " + string.Join(" → ", Stages.Select(s => $"{s.Name} (+{s.ElapsedMs:F1}ms)")));
        if (Error is not null) text.AppendLine($"\n오류: {Error}");
        text.AppendLine($"\n요청 본문\n{Json(RequestBody)}");
        if (Truncated) text.AppendLine("요청 기록은 최대 16KB로 잘렸습니다.");
        if (Incomplete) text.AppendLine("요청 본문 읽기가 완료되지 않았습니다.");
        if (InvalidUtf8) text.AppendLine("요청에 UTF-8 해석 오류가 있습니다.");
        text.AppendLine($"\n응답 본문\n{Json(ResponseBody)}");
        return text.ToString();
    }
}
