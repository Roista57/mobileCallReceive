using System.IO.Pipes;
using System.Security.Principal;

namespace CallReceiver.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string name;
    private readonly CancellationTokenSource stop = new();
    public bool IsPrimary { get; }
    public SingleInstance(string suffix = "")
    {
        name = "MobileCallSendPC-" + WindowsIdentity.GetCurrent().User!.Value + suffix;
        mutex = new Mutex(true, @"Local\" + name, out var created);
        IsPrimary = created;
    }
    public async Task NotifyPrimaryAsync()
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
        await pipe.ConnectAsync(3000);
        await pipe.WriteAsync(new byte[] { 1 });
    }
    public void Listen(Action show) => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                var data = new byte[1];
                if (await pipe.ReadAsync(data, stop.Token) > 0) show();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { await Task.Delay(200, stop.Token); }
        }
    });
    public void Dispose()
    {
        stop.Cancel();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
        // Pending pipe operations observe cancellation before disposing the source.
    }
}
