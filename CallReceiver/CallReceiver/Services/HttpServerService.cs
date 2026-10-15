using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed class HttpServerService(EventStore store, RequestLog log)
{
    private WebApplication? host;
    private readonly SemaphoreSlim gate = new(1, 1);
    public AppSettings? RunningSettings { get; private set; }
    public bool IsRunning => host is not null;
    public event Action? EventAccepted;

    public async Task StartAsync(AppSettings settings)
    {
        await gate.WaitAsync();
        try
        {
            if (host is not null) return;
            if (settings.Validate() is { } validation) throw new ArgumentException(validation);
            if (!NetworkAddresses.List().Contains(settings.ListenAddress))
                throw new InvalidOperationException("저장된 LAN IP가 현재 PC에 없습니다. 수신 주소를 다시 선택하세요.");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders(); // Never log headers or request bodies.
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 16 * 1024;
                options.Listen(IPAddress.Parse(settings.ListenAddress), settings.ListenPort);
            });
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var trace = new HttpTrace(context);
                context.Items[typeof(HttpTrace)] = trace;
                try
                {
                    await trace.ReadBodyAsync(context);
                    await next(context);
                    if (!trace.ResponseWritten && !context.Response.HasStarted)
                        await Reply(context, new { success = false, error = "등록되지 않은 경로 또는 메서드" }, context.Response.StatusCode);
                }
                catch (BadHttpRequestException e)
                {
                    trace.Detail = trace.Detail with { Error = $"{e.GetType().Name}: {e.Message}" };
                    await ErrorResponse(context, e.StatusCode, "요청 크기 또는 형식 오류");
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    trace.Detail = trace.Detail with { Error = $"{e.GetType().Name}: {e.Message}",
                        ResponseState = context.RequestAborted.IsCancellationRequested ? "클라이언트 연결 중단" : "통신/응답 쓰기 실패" };
                }
                catch (Exception e)
                {
                    trace.Detail = trace.Detail with { Error = $"{e.GetType().Name}: {e.Message}" };
                    await ErrorResponse(context, 500, "서버 처리 오류");
                }
                finally
                {
                    trace.Step("요청 처리 종료");
                    var aborted = context.RequestAborted.IsCancellationRequested;
                    var detail = trace.Detail with { FinishedAt = DateTimeOffset.Now,
                        ElapsedMs = trace.Clock.Elapsed.TotalMilliseconds, Stages = trace.Stages.ToArray(),
                        HttpStatus = aborted || !trace.ResponseWritten ? null : context.Response.StatusCode,
                        ResponseState = aborted ? "클라이언트 연결 중단 (접수 여부는 저장 결과 참조)" : trace.Detail.ResponseState };
                    var suffix = detail.Storage == "신규 저장" ? $" {detail.Event?.PhoneNumber}" : detail.Storage == "중복" ? " DUPLICATE" : "";
                    log.Add($"{detail.Method} {detail.Path} {detail.HttpStatus?.ToString() ?? detail.ResponseState}{suffix}", detail);
                }
            });
            app.MapGet("/api/health", (HttpContext context) => Reply(context, new
            {
                success = true, status = "ok", serverTime = DateTimeOffset.Now.ToString("O"), version = "1.0.0"
            }));
            app.MapPost(settings.ApiPath, async (HttpContext context) =>
            {
                var trace = (HttpTrace)context.Items[typeof(HttpTrace)]!;
                if (!context.Request.HasJsonContentType())
                {
                    trace.Detail = trace.Detail with { Validation = "Content-Type 오류" };
                    await Reply(context, new { success = false, error = "application/json 필요" }, 415); return;
                }
                CallEvent value;
                try
                {
                    trace.Step("JSON 검증");
                    if (trace.Detail.InvalidUtf8) throw new FormatException("UTF-8 본문 형식 오류");
                    using var document = JsonDocument.Parse(trace.Body);
                    value = CallEvent.Parse(document.RootElement);
                    trace.Detail = trace.Detail with { Event = value, EventId = value.EventId, Validation = "성공" };
                }
                catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
                {
                    trace.Detail = trace.Detail with { Validation = "실패", Error = $"{e.GetType().Name}: {e.Message}" };
                    await Reply(context, new { success = false, error = "필수 값 또는 JSON 형식 오류" }, 400); return;
                }
                try
                {
                    trace.Step("SQLite 저장/중복 확인");
                    // Finish durable acceptance even if the requesting phone has disconnected.
                    var created = await store.AcceptAsync(value);
                    var shown = false;
                    if (!created) { try { shown = await store.IsShownAsync(value.EventId); } catch { /* Ack remains valid after durable deduplication. */ } }
                    trace.Detail = trace.Detail with { Storage = created ? "신규 저장" : "중복",
                        PopupState = shown ? "표시 시작 (기존 이벤트)" : "미표시" };
                    trace.Step(created ? "저장 완료" : "중복 확인 완료");
                    if (created) EventAccepted?.Invoke();
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    trace.Detail = trace.Detail with { Storage = "저장 실패", Error = $"{e.GetType().Name}: {e.Message}" };
                    await Reply(context, new { success = false, error = "저장소 오류: 다시 시도하세요." }, 503); return;
                }
                await Reply(context, new { success = true, eventId = value.EventId });
            });
            try { await app.StartAsync(); }
            catch { await app.DisposeAsync(); throw; }
            host = app;
            RunningSettings = settings;
            log.Add($"HTTP 서버 시작: {settings.ListenAddress}:{settings.ListenPort}");
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            var app = host;
            if (app is null) return;
            await app.StopAsync();
            await app.DisposeAsync();
            host = null;
            RunningSettings = null;
            log.Add("HTTP 서버 중지");
        }
        finally { gate.Release(); }
    }
    private static async Task Reply(HttpContext context, object body, int status = 200)
    {
        var trace = (HttpTrace)context.Items[typeof(HttpTrace)]!;
        trace.Step("응답 생성");
        var json = JsonSerializer.Serialize(body, JsonDefaults.Options);
        trace.Detail = trace.Detail with { ResponseBody = json.Length <= 4096 ? json : json[..4096] };
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(json, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        trace.ResponseWritten = true;
        trace.Detail = trace.Detail with { ResponseState = "서버 응답 쓰기 완료 (모바일 수신 미확인)" };
        trace.Step("응답 쓰기 완료");
    }
    private static async Task ErrorResponse(HttpContext context, int status, string error)
    {
        var trace = (HttpTrace)context.Items[typeof(HttpTrace)]!;
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
        {
            trace.Detail = trace.Detail with { ResponseState = "응답 중단" }; return;
        }
        try { await Reply(context, new { success = false, error }, status); }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        { trace.Detail = trace.Detail with { ResponseState = "응답 쓰기 실패", Error = trace.Detail.Error + $" / {e.GetType().Name}: {e.Message}" }; }
    }
    private sealed class HttpTrace
    {
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public readonly List<RequestStage> Stages = [];
        public RequestDiagnostic Detail;
        public byte[] Body = [];
        public bool ResponseWritten;
        public HttpTrace(HttpContext context)
        {
            static string Endpoint(IPAddress? ip, int port) => ip is null ? "주소 미확인" :
                $"{(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)}:{port}";
            Detail = new RequestDiagnostic { Method = context.Request.Method, Path = context.Request.Path.Value ?? "/",
                ContentType = context.Request.ContentType, ContentLength = context.Request.ContentLength,
                RemoteAddress = Endpoint(context.Connection.RemoteIpAddress, context.Connection.RemotePort),
                LocalAddress = Endpoint(context.Connection.LocalIpAddress, context.Connection.LocalPort) };
            Step("요청 수신");
        }
        public void Step(string name) => Stages.Add(new(name, Clock.Elapsed.TotalMilliseconds));
        public async Task ReadBodyAsync(HttpContext context)
        {
            Step("요청 본문 읽기");
            using var memory = new MemoryStream();
            var complete = false;
            try
            {
                if (context.Request.ContentLength > 16384) throw new BadHttpRequestException("본문 크기 초과", 413);
                var chunk = new byte[4096];
                while (memory.Length <= 16384)
                {
                    var count = await context.Request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, 16385 - memory.Length)), context.RequestAborted);
                    if (count == 0) { complete = true; break; }
                    memory.Write(chunk, 0, count);
                }
                if (memory.Length > 16384) throw new BadHttpRequestException("본문 크기 초과", 413);
            }
            finally
            {
                Body = memory.ToArray();
                var captured = Body.AsSpan(0, Math.Min(16384, Body.Length)).ToArray();
                var invalid = false;
                string text;
                try { text = new System.Text.UTF8Encoding(false, true).GetString(captured); }
                catch (System.Text.DecoderFallbackException) { invalid = true; text = System.Text.Encoding.UTF8.GetString(captured); }
                Detail = Detail with { RequestBody = text, CapturedBytes = captured.Length,
                    Truncated = Body.Length > 16384 || context.Request.ContentLength > 16384,
                    Incomplete = !complete, InvalidUtf8 = invalid };
                Step(complete ? "요청 본문 읽기 완료" : "요청 본문 읽기 미완료");
            }
        }
    }
}
