using System.IO.Pipes;
using System.Text;

namespace NetScope.App.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _name;
    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenerCts;

    public SingleInstanceCoordinator(string name)
    {
        _name = name;
        _mutex = new Mutex(false, $"Local\\{name}");
        try { IsPrimary = _mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public bool IsPrimary { get; }

    public async Task ForwardAsync(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(1200);
            await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync(string.Join('\t', args));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
    }

    public void StartListening(Action<string[]> callback)
    {
        if (!IsPrimary) return;
        _listenerCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_listenerCts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_listenerCts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(_listenerCts.Token) ?? string.Empty;
                    callback(line.Split('\t', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
            }
        });
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
