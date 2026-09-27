using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed class HttpSelfTest
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(5) };
    public async Task<string> HealthAsync(AppSettings settings)
    {
        var time = Stopwatch.StartNew();
        using var request = Request(settings, HttpMethod.Get, "/api/health");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!json.RootElement.GetProperty("success").GetBoolean() ||
            json.RootElement.GetProperty("status").GetString() != "ok") throw new FormatException("잘못된 health 응답");
        return $"HTTP 서버 정상 · Status: OK · {time.ElapsedMilliseconds}ms";
    }
    public async Task<string> SendAsync(AppSettings settings, CallEvent value)
    {
        using var request = Request(settings, HttpMethod.Post, settings.ApiPath);
        request.Content = JsonContent.Create(value, options: JsonDefaults.Options);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!json.RootElement.GetProperty("success").GetBoolean() ||
            json.RootElement.GetProperty("eventId").GetString() != value.EventId) throw new FormatException("잘못된 eventId 응답");
        return value.EventId;
    }
    private static HttpRequestMessage Request(AppSettings settings, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://{settings.ListenAddress}:{settings.ListenPort}{path}");
        return request;
    }
}
