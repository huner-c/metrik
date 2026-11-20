using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Metrik;

internal sealed class UsbTabletLink : IDisposable
{
    private const int TabletPort = 47938;
    private const int MaxPayload = 4096;
    private const string AdbServerSocket = "tcp:5037";
    private readonly string _adb;
    private readonly byte[] _key;
    private readonly TabletFeed _feed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private volatile string _status = "USB cihazı aranıyor…";

    public string Status => _status;

    public UsbTabletLink(string adb, string pin, TabletFeed feed)
    {
        _adb = adb;
        _key = Encoding.ASCII.GetBytes(pin);
        _feed = feed;
        _worker = Task.Run(() => Run(_stop.Token));
    }

    public static string? FindAdb(string? selected)
    {
        if (Usable(selected)) return selected;
        var locations = new List<string>
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "platform-tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools"),
        };
        foreach (string variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk)
                locations.Add(Path.Combine(sdk, "platform-tools"));
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Metrik.csproj"))) continue;
            locations.Add(Path.Combine(dir.FullName, "android", ".sdk", "platform-tools"));
            break;
        }
        locations.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(static path => path.Trim().Trim('"')).Where(Path.IsPathFullyQualified));
        return locations.Select(static path => Path.Combine(path, "adb.exe")).FirstOrDefault(Usable);
    }

    private static bool Usable(string? path) => path is not null && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), "adb.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    private async Task Run(CancellationToken stop)
    {
        Bridge? bridge = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    bridge ??= await Forward(stop).ConfigureAwait(false);
                    if (bridge is { } active)
                    {
                        using var connection = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                        await connection.ConnectAsync(IPAddress.Loopback, active.Port, stop).ConfigureAwait(false);
                        _status = "Tablette USB kablosu modunu ve bağlantı kodunu kontrol et.";
                        if (await Pair(connection.GetStream(), stop).ConfigureAwait(false))
                        {
                            _status = "USB bağlı · Canlı ölçümler gönderiliyor";
                            await Transmit(connection.GetStream(), stop).ConfigureAwait(false);
                        }
                    }
                }
                catch (SocketException)
                {
                    if (bridge is { } failed) await Remove(failed).ConfigureAwait(false);
                    bridge = null;
                    _status = "USB bağlantısı yeniden deneniyor…";
                }
                catch (IOException)
                {
                    _status = "Tablette USB kablosu modunu ve bağlantı kodunu kontrol et.";
                }
                catch (Win32Exception)
                {
                    _status = "ADB çalıştırılamadı. Android SDK Platform-Tools’u kontrol et.";
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    _status = "USB yanıtı bekleniyor…";
                }

                await Task.Delay(1500, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (bridge is { } active) await Remove(active).ConfigureAwait(false);
        }
    }

    private async Task<Bridge?> Forward(CancellationToken stop)
    {
        var device = await Adb(stop, "-d", "get-serialno").ConfigureAwait(false);
        if (device.ExitCode != 0 || string.IsNullOrWhiteSpace(device.Output) || device.Output == "unknown")
        {
            _status = device.Error switch
            {
                var error when error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                    => "Tabletin kilidini aç ve USB hata ayıklama iznini onayla.",
                var error when error.Contains("more than one", StringComparison.OrdinalIgnoreCase)
                    => "Metrik için yalnızca bir Android USB cihazı bağlı olmalı.",
                var error when error.Contains("device offline", StringComparison.OrdinalIgnoreCase)
                    => "Tablet USB bağlantısını hazırlıyor. Metrik yeniden deniyor.",
                var error when error.Contains("daemon", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("server", StringComparison.OrdinalIgnoreCase)
                    => "Yerel ADB hizmeti başlatılamadı. Metrik yeniden deniyor.",
                _ => "Tableti USB ile bağla ve USB hata ayıklamayı aç."
            };
            return null;
        }

        string serial = device.Output;
        var forward = await Adb(stop, "-s", serial, "forward", "tcp:0", $"tcp:{TabletPort}").ConfigureAwait(false);
        if (forward.ExitCode == 0 && int.TryParse(forward.Output, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is > 0 and <= 65535) return new(serial, port);

        _status = "ADB, USB bağlantısını açamadı. USB kablosunu ve Platform-Tools sürümünü kontrol et.";
        return null;
    }

    private async Task Remove(Bridge bridge)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Adb(timeout.Token, "-s", bridge.Serial, "forward", "--remove",
                $"tcp:{bridge.Port}").ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException or OperationCanceledException) { }
    }

    private async Task<bool> Pair(NetworkStream stream, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        byte[] challenge = new byte[17];
        await stream.ReadExactlyAsync(challenge.AsMemory(0, 16), timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(HMACSHA256.HashData(_key, challenge), timeout.Token).ConfigureAwait(false);
        challenge[16] = 1;
        byte[] proof = new byte[32];
        await stream.ReadExactlyAsync(proof, timeout.Token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(_key, challenge));
    }

    private async Task Transmit(NetworkStream stream, CancellationToken stop)
    {
        byte[] packet = new byte[4 + MaxPayload];
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (_feed.Read() is not { Length: <= MaxPayload } payload) continue;
            BinaryPrimitives.WriteInt32BigEndian(packet, payload.Length);
            payload.CopyTo(packet.AsSpan(4));
            await stream.WriteAsync(packet.AsMemory(0, 4 + payload.Length), stop).ConfigureAwait(false);
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    private async Task<CommandResult> Adb(CancellationToken stop, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(_adb)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Remove("ADB_SERVER_SOCKET");
        // ADB treats the default hostless socket as local and starts its daemon when needed.
        // Passing -H 127.0.0.1 instead fails to start a stopped daemon.
        start.ArgumentList.Add("-L");
        start.ArgumentList.Add(AdbServerSocket);
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            return new(process.ExitCode, output.Result.Trim(), error.Result.Trim());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            throw;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _worker.GetAwaiter().GetResult(); }
        finally { _stop.Dispose(); }
    }

    private readonly record struct Bridge(string Serial, int Port);
    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
}
