using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Metrik;

internal readonly record struct FrameReading(double Fps, double FrameMilliseconds, string Runtime, bool IsLastSample = false);

internal sealed class FrameCapture : IDisposable
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const uint RealTime = 0x100;
    private const ulong EventsKeyword = 0x2;
    private const ulong InvalidTrace = ulong.MaxValue;
    private static readonly long SampleWindow = Stopwatch.Frequency * 2;
    private static readonly long Expiry = Stopwatch.Frequency * 5;
    private readonly string _name = $"Metrik-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly object _gate = new();
    private readonly Dictionary<(uint Thread, ushort StartEvent), PendingFrame> _pending = [];
    private readonly Dictionary<Chain, FrameSeries> _chains = [];
    private readonly RecordCallback _callback;
    private readonly nint _properties;
    private ulong _session;
    private ulong _consumer = InvalidTrace;
    private Thread? _worker;
    private volatile uint _target;
    private long _targetStart;
    private long _latestPresent;
    private volatile string? _error;

    public string? Error => _error;
    public bool AccessDenied { get; }

    public FrameCapture()
    {
        _callback = OnRecord;
        int offset = Marshal.SizeOf<TraceProperties>();
        int length = offset + (_name.Length + 1) * sizeof(char);
        _properties = Marshal.AllocHGlobal(length);
        var properties = new TraceProperties
        {
            Wnode = new() { BufferSize = (uint)length, ClientContext = 1, Flags = 0x00020000 },
            BufferSize = 16,
            LogFileMode = RealTime,
            FlushTimer = 1,
            LoggerNameOffset = (uint)offset
        };
        Marshal.StructureToPtr(properties, _properties, false);
        Marshal.Copy(new char[_name.Length + 1], 0, _properties + offset, _name.Length + 1);

        string stage = "ETW session";
        uint status = StartTrace(out ulong session, _name, _properties);
        if (status == 0)
        {
            _session = session;
            stage = "DirectX permissions";
            uint dxgi = EnableTraceEx2(_session, in DxgiProvider, 1, 0, EventsKeyword, 0, 0, 0);
            uint d3d9 = EnableTraceEx2(_session, in D3d9Provider, 1, 0, EventsKeyword, 0, 0, 0);
            status = dxgi != 0 ? dxgi : d3d9;
        }

        if (status == 0)
        {
            stage = "ETW reader";
            nint loggerName = Marshal.StringToHGlobalUni(_name);
            try
            {
                var logfile = new TraceLogfile
                {
                    LoggerName = loggerName,
                    Mode = RealTime | 0x1000 | 0x10000000, // Real time, raw QPC, EVENT_RECORD callback.
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
                };
                _consumer = OpenTrace(ref logfile);
                if (_consumer == InvalidTrace)
                    status = (uint)Marshal.GetLastPInvokeError();
            }
            finally { Marshal.FreeHGlobal(loggerName); }
        }

        if (status != 0 || _consumer == InvalidTrace)
        {
            AccessDenied = status is 5 or 1314;
            _error = $"{stage} ({status}): {new Win32Exception((int)status).Message}";
            StopSession();
            return;
        }

        _worker = new Thread(Consume) { IsBackground = true, Name = "Metrik DirectX ETW" };
        _worker.Start();
    }

    private void Consume()
    {
        ulong handle = _consumer;
        uint status = ProcessTrace(ref handle, 1, 0, 0);
        if (status != 0 && status != 1223)
            _error = $"ETW stream ({status}): {new Win32Exception((int)status).Message}";
    }

    public void SetTarget(uint pid)
    {
        lock (_gate)
        {
            _targetStart = Stopwatch.GetTimestamp();
            _target = pid;
            _latestPresent = 0;
            _pending.Clear();
            _chains.Clear();
        }
    }

    public FrameReading? Read()
    {
        if (_error is not null) return null;
        lock (_gate)
        {
            // ETW delivers batches. Measure in present time; use the wall clock only for freshness.
            long now = Stopwatch.GetTimestamp();
            if (_latestPresent == 0 || now - _latestPresent >= Expiry) return null;
            long cutoff = _latestPresent - SampleWindow;
            FrameSeries? busiest = null;
            bool dxgi = false;
            bool lastSample = false;
            foreach (var (chain, series) in _chains)
            {
                while (series.Samples.TryPeek(out long stamp) && stamp < cutoff) series.Samples.Dequeue();
                if (series.Samples.Count < 2 || now - series.Latest >= Expiry) continue;
                bool delayed = now - series.Latest >= SampleWindow;
                if (busiest is null || (lastSample && !delayed) ||
                    (lastSample == delayed && series.Samples.Count > busiest.Samples.Count))
                {
                    busiest = series;
                    dxgi = chain.Dxgi;
                    lastSample = delayed;
                }
            }
            if (busiest is null) return null;
            double elapsed = (busiest.Latest - busiest.Samples.Peek()) / (double)Stopwatch.Frequency;
            if (elapsed <= 0) return null;
            double fps = (busiest.Samples.Count - 1) / elapsed;
            return new(fps, 1000 / fps, dxgi ? "DXGI" : "DIRECTX 9", lastSample);
        }
    }

    private void OnRecord(ref EventRecord record)
    {
        ref EventHeader header = ref record.Header;
        if (_target == 0 || header.ProcessId != _target || record.Data == 0) return;

        bool dxgi = header.Provider == DxgiProvider;
        ushort id = header.Descriptor.Id;
        if (dxgi ? id is not (42 or 43 or 55 or 56) : header.Provider != D3d9Provider || id is not (1 or 2))
            return;
        bool start = dxgi ? id is 42 or 55 : id == 1;
        ushort startEvent = start ? id : (ushort)(id - 1);
        var pair = (header.ThreadId, startEvent);

        lock (_gate)
        {
            if (header.ProcessId != _target || header.Timestamp < _targetStart) return;
            if (start)
            {
                _pending.Remove(pair);
                // The payload pointer uses the event writer's architecture, not the consumer's.
                int pointerSize = (header.Flags & 0x20) != 0 ? 4 : 8;
                if (record.DataLength < pointerSize + (dxgi ? 4 : 0)) return;
                if (dxgi && (Marshal.ReadInt32(record.Data, pointerSize) & 1) != 0) return; // DXGI_PRESENT_TEST
                ulong address = pointerSize == 4
                    ? (uint)Marshal.ReadInt32(record.Data)
                    : (ulong)Marshal.ReadInt64(record.Data);
                _pending[pair] = new(new(address, dxgi), header.Timestamp);
                return;
            }

            if (!_pending.Remove(pair, out PendingFrame frame) || record.DataLength < sizeof(int) ||
                Marshal.ReadInt32(record.Data) != 0) return;
            // Treat a gap between successful presents as idle rather than averaging it into a new burst.
            if (!_chains.TryGetValue(frame.Chain, out FrameSeries? series))
            {
                if (_chains.Count >= 64)
                {
                    var victim = _chains.MinBy(entry => (
                        entry.Value.Samples.Count >= 2 && entry.Value.Latest >= _latestPresent - SampleWindow,
                        entry.Value.Latest));
                    _chains.Remove(victim.Key);
                }
                _chains.Add(frame.Chain, series = new());
            }
            long delta = frame.Timestamp - series.Latest;
            if (delta <= 0) return;
            if (delta > SampleWindow) series.Samples.Clear();
            series.Latest = frame.Timestamp;
            _latestPresent = Math.Max(_latestPresent, frame.Timestamp);
            series.Samples.Enqueue(frame.Timestamp);
            if (series.Samples.Count > 2048) series.Samples.Dequeue();
        }
    }

    private void StopSession()
    {
        if (_session == 0) return;
        ControlTrace(_session, _name, _properties, 1);
        _session = 0;
    }

    public void Dispose()
    {
        StopSession();
        if (_consumer != InvalidTrace) CloseTrace(_consumer);
        _worker?.Join();
        Marshal.FreeHGlobal(_properties);
    }

    private readonly record struct Chain(ulong Address, bool Dxgi);
    private readonly record struct PendingFrame(Chain Chain, long Timestamp);
    private sealed class FrameSeries
    {
        public readonly Queue<long> Samples = new();
        public long Latest;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Wnode
    {
        public uint BufferSize, ProviderId;
        public ulong HistoricalContext;
        public long Timestamp;
        public Guid Guid;
        public uint ClientContext, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public Wnode Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer,
            EnableFlags, AgeLimit, NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }

    // EVENT_TRACE_LOGFILEW contains two inline SDK structs that OpenTrace only writes.
    // These offsets use the Windows x64 ABI, matching the project's x64 target.
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct TraceLogfile
    {
        [FieldOffset(8)] public nint LoggerName;
        [FieldOffset(28)] public uint Mode;
        [FieldOffset(424)] public nint EventRecordCallback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version, Channel, Level, Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size, HeaderType, Flags, EventProperty;
        public uint ThreadId, ProcessId;
        public long Timestamp;
        public Guid Provider;
        public EventDescriptor Descriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader Header;
        public uint BufferContext;
        public ushort ExtendedDataCount, DataLength;
        public nint ExtendedData, Data, UserContext;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RecordCallback(ref EventRecord record);

    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)]
    private static extern uint StartTrace(out ulong handle, string name, nint properties);
    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint controlCode, byte level,
        ulong anyKeyword, ulong allKeyword, uint timeout, nint parameters);
    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    private static extern ulong OpenTrace(ref TraceLogfile logfile);
    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ref ulong handle, uint count, nint startTime, nint endTime);
    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong handle);
    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong handle, string name, nint properties, uint controlCode);
}
