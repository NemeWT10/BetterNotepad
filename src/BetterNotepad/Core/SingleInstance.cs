using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BetterNotepad.Core;

/// <summary>Message from a second launch (e.g. a file double-clicked in Explorer).</summary>
public sealed record InstanceMessage(string[] Args, string WorkingDirectory);

/// <summary>
/// One process per user (and per data folder — a test instance never talks to the user's app).
/// A second launch hands its arguments to the first over a named pipe and exits right away.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    public bool IsPrimary { get; }

    public SingleInstance(string key)
    {
        _pipeName = key + "-pipe";
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + key, out var created);
        IsPrimary = created;
    }

    /// <summary>Sends args to the primary instance. Returns false if it did not answer.</summary>
    public bool SendToPrimary(string[] args)
    {
        try
        {
            // the primary may bring its window to the front (we are the foreground process right now)
            UI.Native.AllowSetForegroundWindow(UI.Native.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(3000);
            var json = JsonSerializer.Serialize(new InstanceMessage(args, Environment.CurrentDirectory));
            var bytes = Encoding.UTF8.GetBytes(json);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"single instance: handoff failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Listens for messages; <paramref name="onMessage"/> runs on a pool thread.</summary>
    public void Listen(Action<InstanceMessage> onMessage)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, token);
                    var msg = JsonSerializer.Deserialize<InstanceMessage>(ms.ToArray());
                    if (msg is not null) onMessage(msg);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"single instance: listener error: {ex.Message}");
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        }
        _mutex.Dispose();
    }
}
