using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CallReceiver.Models;
using CallReceiver.Services;
using Xunit;

namespace CallReceiver.Tests;

public class RequestDiagnosticTests
{
    private static async Task<LogEntry> WaitFor(HttpFixture f, Func<LogEntry, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(5000);
        while (true)
        {
            var value = f.Log.Snapshot().FirstOrDefault(predicate);
            if (value is not null) return value;
            await Task.Delay(10, timeout.Token);
        }
    }
    [Fact] public async Task CapturesExactPayloadEndpointsAndDurableDuplicateResult()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        var value = CallEvent.Test();
        var body = JsonSerializer.Serialize(value, JsonDefaults.Options);
        using var response = await f.Client.PostAsync("/api/call", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await WaitFor(f, l => l.Detail?.EventId == value.EventId);
        var detail = entry.Detail!;
        Assert.Equal(body, detail.RequestBody);
        Assert.Equal(value, detail.Event);
        Assert.Equal("신규 저장", detail.Storage);
        Assert.Equal("성공", detail.Validation);
        Assert.Equal("127.0.0.1:" + f.Settings.ListenPort, detail.LocalAddress);
        Assert.StartsWith("127.0.0.1:", detail.RemoteAddress);
        Assert.True(detail.ElapsedMs >= 0);
        Assert.Contains(detail.Stages, s => s.Name == "저장 완료");
        Assert.Contains(value.EventId, detail.ResponseBody!);
        Assert.Contains("쓰기 완료", detail.ResponseState);
        Assert.DoesNotContain(value.EventId, entry.Message);
        f.Log.SetPopupState(value.EventId, "표시 시작");
        Assert.Equal("표시 시작", f.Log.Snapshot().Single(l => l.Id == entry.Id).Detail!.PopupState);
        // A retry after an unobserved acknowledgement has exactly the same event identity.
        using var duplicate = await f.Client.PostAsJsonAsync("/api/call", value);
        Assert.True(duplicate.IsSuccessStatusCode);
        var duplicateLog = await WaitFor(f, l => l.Detail?.Storage == "중복");
        Assert.Equal(value.EventId, duplicateLog.Detail!.EventId);
        Assert.NotEqual(detail.RequestId, duplicateLog.Detail.RequestId);
        Assert.Equal(1, await f.Store.PendingCountAsync());
    }
    [Fact] public async Task BadJsonAndUnknownPathRetainBodyWithoutQueryOrHeaders()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        using var bad = await f.Client.PostAsync("/api/call", new StringContent("{bad", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var log = await WaitFor(f, l => l.Detail?.Validation == "실패");
        Assert.Equal("{bad", log.Detail!.RequestBody);
        Assert.NotNull(log.Detail.Error);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/unknown?secret=not-collected")
        { Content = new StringContent("test-payload", Encoding.UTF8, "application/json") };
        request.Headers.Add("Authorization", "never-log-this");
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var unknown = (await WaitFor(f, l => l.Detail?.Path == "/unknown")).Detail!;
        Assert.Equal("test-payload", unknown.RequestBody);
        Assert.Equal(404, unknown.HttpStatus);
        Assert.DoesNotContain("not-collected", unknown.Describe());
        Assert.DoesNotContain("never-log-this", unknown.Describe());
    }
    [Fact] public async Task OversizedAndInvalidUtf8AreBoundedAndRejected()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        using var large = await f.Client.PostAsync("/api/call", new StringContent(new string('x', 20000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        var over = (await WaitFor(f, l => l.Detail?.HttpStatus == 413)).Detail!;
        Assert.True(over.Truncated); Assert.True(over.CapturedBytes <= 16384);
        using var content = new ByteArrayContent([0xff, 0xfe, 0xff]);
        content.Headers.ContentType = new("application/json");
        using var invalid = await f.Client.PostAsync("/api/call", content);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await WaitFor(f, l => l.Detail?.InvalidUtf8 == true)).Detail!.InvalidUtf8);
    }
    [Fact] public async Task ClientDisconnectDoesNotUndoDurableWriteOrCreateSecondEvent()
    {
        await using var f = new HttpFixture(); await f.StartAsync();
        var value = CallEvent.Test();
        var body = JsonSerializer.Serialize(value, JsonDefaults.Options);
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, f.Settings.ListenPort);
            var bytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes($"POST /api/call HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await tcp.GetStream().WriteAsync(headers);
            await tcp.GetStream().WriteAsync(bytes);
            // Do not read the acknowledgement. Wait for durable acceptance before disconnecting.
            using var timeout = new CancellationTokenSource(5000);
            while (await f.Store.PendingCountAsync() == 0) await Task.Delay(10, timeout.Token);
        }
        using var retried = await f.Client.PostAsJsonAsync("/api/call", value);
        Assert.True(retried.IsSuccessStatusCode);
        await WaitFor(f, l => l.Detail?.Storage == "중복");
        Assert.Equal(1, await f.Store.PendingCountAsync());
    }
    [Fact] public async Task LogSnapshotsStayBoundedAndTextExportUnchanged()
    {
        var log = new RequestLog();
        for (var i = 0; i < 250; i++) log.Add("request " + i, new RequestDiagnostic { EventId = i.ToString() });
        Assert.Equal(200, log.Snapshot().Count);
        Assert.EndsWith("request 249", log.Snapshot()[0].ExportDisplay);
        var snapshot = log.Snapshot()[0];
        await log.ClearAsync();
        Assert.Empty(log.Snapshot());
        Assert.Equal("request 249", snapshot.Message);
    }
}
