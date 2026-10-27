using System.IO;
using System.Text;
using System.Text.Json;
using ToolkitLauncher.Core;

internal static partial class Program
{
    private static async Task LifecycleReadSharingTestsAsync()
    {
        var root = Path.Combine(Temporary, "atomic-result-reader");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "result.json");
        var next = Path.Combine(root, "replacement.json");
        var id = Guid.NewGuid();
        var even = new string('A', 128 * 1024);
        var odd = new string('B', even.Length);
        var value = new LifecycleResult(1, id, "environment", "install", "running", DateTimeOffset.UtcNow, even) { Attempt = 0 };
        OwnedPaths.Write(path, value);
        File.WriteAllText(next, JsonSerializer.Serialize(value with { Attempt = 1, Message = odd }, OwnedPaths.Json));
        IOException? contention = null;
        using (var legacyReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { File.Replace(next, path, null); }
            catch (IOException error) { contention = error; }
        }
        Check(contention is not null, "Windows atomic replacement is blocked by a reader that omits delete sharing");
        Console.WriteLine($"No-delete-share control: HResult=0x{contention!.HResult:X8}; {contention.Message.Trim()}");
        using (var sharedReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            File.Replace(next, path, null);
            using var text = new StreamReader(sharedReader);
            Check(JsonSerializer.Deserialize<LifecycleResult>(text.ReadToEnd(), OwnedPaths.Json)?.Attempt == 0 &&
                OwnedPaths.Read<LifecycleResult>(path).Attempt == 1, "Delete sharing preserves the open old snapshot while new readers see the replacement");
        }

        using var stop = new CancellationTokenSource();
        using var started = new CountdownEvent(4);
        var readCount = 0;
        var readerFailures = 0;
        var unavailableSnapshots = 0;
        var writerFailures = 0;
        string? firstReaderFailure = null;
        string? firstWriterFailure = null;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            var announced = false;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var snapshot = OwnedPaths.Read<LifecycleResult>(path);
                        if (snapshot.OperationId != id || snapshot.Message != (snapshot.Attempt % 2 == 0 ? even : odd))
                            throw new InvalidDataException("Reader combined different result snapshots.");
                        Interlocked.Increment(ref readCount);
                    }
                    catch (IOException error) when ((error.HResult & 0xFFFF) is 2 or 3 or 32 or 33)
                    {
                        // The producer may hold an exclusive rename handle or momentarily
                        // remove the path. Reconcile rejects that read and polls again; it
                        // must never cause the producer's replacement to fail in return.
                        Interlocked.Increment(ref unavailableSnapshots);
                    }
                    catch (Exception error)
                    {
                        Interlocked.Increment(ref readerFailures);
                        Interlocked.CompareExchange(ref firstReaderFailure, error.GetType().Name + ": " + error.Message, null);
                    }
                    if (!announced) { announced = true; started.Signal(); }
                }
            }
            finally { if (!announced) started.Signal(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            if (!started.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent lifecycle readers did not start.");
            for (var i = 2; i < 122; i++)
            {
                File.WriteAllText(next, JsonSerializer.Serialize(value with { Attempt = i, Message = i % 2 == 0 ? even : odd }, OwnedPaths.Json));
                try { File.Replace(next, path, null); }
                catch (IOException error) { writerFailures++; firstWriterFailure ??= $"0x{error.HResult:X8}: {error.Message.Trim()}"; }
            }
        }
        finally { stop.Cancel(); await Task.WhenAll(readers); }
        Console.WriteLine($"Concurrent result polling: reads={readCount}; replacement attempts=120; writer failures={writerFailures}; unexpected reader failures={readerFailures}; unavailable snapshots={unavailableSnapshots}; first writer={firstWriterFailure}; first reader={firstReaderFailure}");
        Check(writerFailures == 0 && readerFailures == 0 && readCount >= 4, "Actual lifecycle metadata reads coexist with atomic producer replacement without torn snapshots");
        Check(OwnedPaths.Read<LifecycleResult>(path).Attempt == 121, "A recheck after replacements stop reads the latest complete result");

        File.WriteAllText(path, "\"Unicode é 测试\"", new UTF8Encoding(true));
        Check(OwnedPaths.Read<string>(path) == "Unicode é 测试", "Snapshot reader retains UTF-8 BOM and Unicode compatibility");
        File.WriteAllText(path, JsonSerializer.Serialize(new string('x', 1024 * 1024 - 2)));
        Check(OwnedPaths.Read<string>(path).Length == 1024 * 1024 - 2, "Exactly one MiB of metadata remains accepted");
        File.WriteAllText(path, JsonSerializer.Serialize(new string('x', 1024 * 1024)));
        await ThrowsAsync<InvalidDataException>(() => Task.Run(() => OwnedPaths.Read<string>(path)), "Opened metadata snapshots exceeding one MiB are rejected");
    }
}
