using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CallReceiver.Models;
using CallReceiver.Services;
using Xunit;

namespace CallReceiver.Tests;

public sealed class HttpFixture : IAsyncDisposable
{
    public readonly TestDirectory Directory = new();
    public readonly EventStore Store;
    public readonly RequestLog Log = new();
    public readonly HttpServerService Server;
    public readonly HttpClient Client = new();
    public readonly AppSettings Settings;
    public HttpFixture()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Settings = new AppSettings { ListenPort = port };
        Store = new EventStore(System.IO.Path.Combine(Directory.Path, "events.db"));
        Server = new HttpServerService(Store, Log);
        Client.BaseAddress = new Uri($"http://127.0.0.1:{port}");
    }
    public async Task StartAsync() { await Store.InitializeAsync(); await Server.StartAsync(Settings); }
    public async ValueTask DisposeAsync() { await Server.StopAsync(); Client.Dispose(); Directory.Dispose(); }
}

public class HttpTests
{
    [Fact] public async Task CustomApiPathIsUsed()
    {
        await using var f = new HttpFixture();
        await f.Store.InitializeAsync();
        var settings = f.Settings with { ApiPath = "/custom/call" };
        await f.Server.StartAsync(settings);
        var value = CallEvent.Test();
        Assert.Equal(value.EventId, await new HttpSelfTest().SendAsync(settings, value));
        Assert.Equal(1, await f.Store.PendingCountAsync());
    }
    [Fact] public async Task RealHealthAndPostWithDurableAckAndDuplicate()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        var self = new HttpSelfTest();
        Assert.Contains("Status: OK", await self.HealthAsync(f.Settings));
        var value = CallEvent.Test();
        Assert.Equal(value.EventId, await self.SendAsync(f.Settings, value));
        Assert.Equal(value.EventId, await self.SendAsync(f.Settings, value));
        Assert.Equal(1, await f.Store.PendingCountAsync());
        Assert.Equal(value, await f.Store.NextAsync());
        Assert.Contains(f.Log.Entries, l => l.Message.Contains("DUPLICATE"));
        Assert.Contains(f.Log.Entries, l => l.Message == "POST /api/call 200 01012345678");
        Assert.DoesNotContain(f.Log.Entries, l => l.Message.Contains("eventId", StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public async Task UnknownRouteLogContainsTheActualPathWithoutQuery()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        using var response = await f.Client.PostAsync("/wrong/path?secret=value", new StringContent(""));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(f.Log.Entries, entry => entry.Message == "POST /wrong/path 404");
        Assert.DoesNotContain(f.Log.Entries, entry => entry.Message.Contains("secret=value"));
    }
    [Fact] public async Task AcceptsWithoutAuthenticationAndRejectsMalformedRequests()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        using var unauthenticated = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await unauthenticated.GetAsync(f.Client.BaseAddress + "api/health")).StatusCode);
        foreach (var body in new[] { "not-json", "{}", "[]", "{\"schemaVersion\":\"one\"}" })
        {
            using var result = await f.Client.PostAsync("/api/call", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }
        using var wrongType = await f.Client.PostAsync("/api/call", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using var tooLarge = await f.Client.PostAsync("/api/call", new StringContent(new string('x', 17000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(0, await f.Store.PendingCountAsync());
    }
    [Fact] public async Task ConcurrentRequestsReturnSameAckAndOneRow()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        var value = CallEvent.Test();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => f.Client.PostAsJsonAsync("/api/call", value)));
        foreach (var response in responses) { Assert.True(response.IsSuccessStatusCode); response.Dispose(); }
        Assert.Equal(1, await f.Store.PendingCountAsync());
    }
    [Fact] public async Task PortCollisionIsReportedWithoutReplacingServer()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        var second = new HttpServerService(f.Store, f.Log);
        await Assert.ThrowsAnyAsync<IOException>(() => second.StartAsync(f.Settings));
        Assert.False(second.IsRunning);
        Assert.True(f.Server.IsRunning);
        Assert.Contains("정상", await new HttpSelfTest().HealthAsync(f.Settings));
    }
    [Fact] public async Task NonlocalAddressDoesNotFallBackToWildcard()
    {
        await using var f = new HttpFixture();
        await f.Store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Server.StartAsync(f.Settings with { ListenAddress = "203.0.113.1" }));
        Assert.False(f.Server.IsRunning);
    }
}
