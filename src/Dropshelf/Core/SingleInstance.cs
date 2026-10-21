using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;

namespace Dropshelf.Core;

/// <summary>
/// Keeps one Dropshelf per user session. Later launches (Send To, Explorer context menu,
/// shortcuts) forward their command line to the running instance over a named pipe.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = $"Dropshelf-{Environment.UserName}-{Environment.UserDomainName}".Replace('\\', '-');
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public event Action<string[]>? ArgumentsReceived;

    /// <summary>Returns the instance owner, or null after forwarding <paramref name="args"/> to the running instance.</summary>
    public static SingleInstance? Acquire(string[] args)
    {
        var mutex = new Mutex(true, $"Local\\{Id}", out var created);
        if (created) return new SingleInstance(mutex);

        mutex.Dispose();
        Forward(args);
        return null;
    }

    private static void Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(args));
            writer.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not reach the running instance: {ex.Message}");
        }
    }

    public void StartListening(Dispatcher dispatcher)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server);
                    var json = await reader.ReadToEndAsync(_cts.Token);
                    var args = JsonSerializer.Deserialize<string[]>(json) ?? [];
                    _ = dispatcher.BeginInvoke(() => ArgumentsReceived?.Invoke(args));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Pipe error: {ex.Message}");
                    await Task.Delay(500);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // not owned on this thread
        }
        _mutex.Dispose();
    }
}
