using System.Text.Encodings.Web;
using System.Text.Json;

namespace Metrik;

internal sealed record TabletReading(FrameReading? Frame, string? Game, string? Runtime, string FrameStatus, GpuReading Gpu)
{
    public long SampledAt { get; } = Environment.TickCount64;
}

internal sealed class TabletFeed : IDisposable
{
    public const string Protocol = "METRIK/1 ";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Lock _serialization = new();
    private volatile TabletReading? _reading;
    private volatile CpuReading? _cpu;
    private TabletReading? _lastReading;
    private CpuReading? _lastCpu;
    private byte[]? _payload;

    public TabletFeed() => _worker = Task.Run(() => Sample(_stop.Token));

    public void Publish(TabletReading reading) => _reading = reading;

    public byte[]? Read()
    {
        lock (_serialization)
        {
            if (_reading is not { } reading || Environment.TickCount64 - reading.SampledAt > 5000) return null;

            CpuReading? cpu = _cpu;
            if (!ReferenceEquals(_lastReading, reading) || !ReferenceEquals(_lastCpu, cpu))
            {
                _payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 1, reading.Frame, reading.Game, reading.Runtime, reading.FrameStatus, reading.Gpu, cpu,
                }, JsonOptions);
                _lastReading = reading;
                _lastCpu = cpu;
            }
            return _payload;
        }
    }

    private async Task Sample(CancellationToken stop)
    {
        using var cpu = new CpuTelemetry();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                _cpu = await cpu.Read(stop).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _ = _worker.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _stop.Dispose();
        }, TaskScheduler.Default);
    }
}
