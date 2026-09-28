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
                var route = context.Request.Path.Value ?? "/";
                try
                {
                    await next(context);
                }
                catch (BadHttpRequestException e)
                {
                    context.Response.StatusCode = e.StatusCode;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "요청 크기 또는 형식 오류" });
                }
                finally
                {
                    var detail = context.Items.TryGetValue("detail", out var value) ? $" {value}" : "";
                    log.Add($"{context.Request.Method} {route} {context.Response.StatusCode}{detail}");
                }
            });
            app.MapGet("/api/health", () => Results.Json(new
            {
                success = true, status = "ok", serverTime = DateTimeOffset.Now.ToString("O"), version = "1.0.0"
            }));
            app.MapPost(settings.ApiPath, async (HttpContext context) =>
            {
                if (!context.Request.HasJsonContentType())
                    return Results.Json(new { success = false, error = "application/json 필요" }, statusCode: 415);
                CallEvent value;
                try
                {
                    using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    value = CallEvent.Parse(document.RootElement);
                }
                catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
                {
                    return Results.Json(new { success = false, error = "필수 값 또는 JSON 형식 오류" }, statusCode: 400);
                }
                try
                {
                    // Deliberately finish the durable write even if the client disconnects.
                    var created = await store.AcceptAsync(value);
                    context.Items["detail"] = created
                        ? value.PhoneNumber
                        : "DUPLICATE";
                    if (created) EventAccepted?.Invoke();
                    return Results.Json(new { success = true, eventId = value.EventId });
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    return Results.Json(new { success = false, error = "저장소 오류: 다시 시도하세요." }, statusCode: 503);
                }
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
}
