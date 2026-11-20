using System.Runtime.InteropServices;

namespace Metrik;

internal sealed record GpuReading(string Name, int? Load = null, int? Temperature = null,
    int? Hotspot = null, int? Clock = null, double? UsedGiB = null, double? TotalGiB = null,
    int? MemoryClock = null, int? MemoryTemperature = null, int? FanRpm = null, int? AsicPower = null,
    int? BoardPower = null)
{
    public bool Available => Load.HasValue || Temperature.HasValue || Hotspot.HasValue || Clock.HasValue || UsedGiB.HasValue;
}

internal sealed unsafe class GpuTelemetry : IDisposable
{
    private const double BytesPerGiB = 1024d * 1024 * 1024;
    private const int RadeonVendor = 0x1002;
    private const int AdlRadeonVendor = 1002;
    private static readonly DriverAllocate Allocate = size => size > 0 ? Marshal.AllocHGlobal(size) : 0;

    private readonly string _name = "No GPU sensor found";
    private readonly long _capacity;
    private readonly int _index = -1;
    private nint _context;
    private nint _memoryQuery, _memoryCounter;

    public GpuTelemetry()
    {
        var adapters = EnumerateDxgi();
        var primary = adapters.FirstOrDefault();
        var (selected, index, name) = SelectRadeon(adapters, primary);
        _index = index;
        _name = name ?? selected.Name ?? _name;
        _capacity = selected.Bytes;
        if (selected.Bytes > 0) OpenMemoryCounter(selected.Luid);
    }

    private (Adapter Adapter, int Index, string? Name) SelectRadeon(List<Adapter> adapters, Adapter primary)
    {
        var fallback = (primary, -1, (string?)null);
        try
        {
            if (AdlCreate(Allocate, 1, out _context) != 0 || _context == 0) return fallback;
            if (AdlCount(_context, out int count) != 0 || count is < 1 or > 256) return fallback;
            var entries = new AdapterInfo[count];
            fixed (AdapterInfo* buffer = entries)
            {
                for (int i = 0; i < entries.Length; i++) buffer[i].Size = sizeof(AdapterInfo);
                if (AdlAdapterInfo(_context, buffer, count * sizeof(AdapterInfo)) != 0) return fallback;
                var best = fallback;
                int bestScore = -1;
                for (int i = 0; i < entries.Length; i++)
                {
                    ref AdapterInfo entry = ref buffer[i];
                    if (entry.VendorId is not (AdlRadeonVendor or RadeonVendor) || entry.Bus < 0) continue;
                    string name = Marshal.PtrToStringAnsi((nint)buffer[i].Name)?.Trim() ?? "AMD Radeon";
                    bool matchesPrimary = primary.Vendor == RadeonVendor && SameName(primary.Name, name);
                    bool active = AdlActive(_context, entry.AdapterIndex, out int state) == 0 && state == 1;
                    int score = (matchesPrimary ? 2 : 0) + (active ? 1 : 0);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    Adapter match = adapters.FirstOrDefault(adapter => adapter.Vendor == RadeonVendor && SameName(adapter.Name, name));
                    best = (match, entry.AdapterIndex, name);
                }
                return best;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Keep FPS available on systems without a compatible Radeon driver.
        }
        return fallback;
    }

    private static bool SameName(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public GpuReading Read()
    {
        double? total = _capacity > 0 ? _capacity / BytesPerGiB : null;
        if (_context == 0 || _index < 0) return new(_name, UsedGiB: ReadMemoryCounter(), TotalGiB: total);

        int? load = null, temperature = null, hotspot = null, clock = null;
        int? memoryClock = null, memoryTemperature = null, fanRpm = null, asicPower = null, boardPower = null;
        try
        {
            SensorLog log = new() { Size = sizeof(SensorLog) };
            if (AdlReadLog(_context, _index, ref log) == 0)
            {
                load = ReadSensor(log.Values, 19, 0, 100);
                temperature = ReadSensor(log.Values, 8, 1, 130) ?? ReadSensor(log.Values, 28, 1, 130);
                hotspot = ReadSensor(log.Values, 27, 1, 140);
                clock = ReadSensor(log.Values, 1, 0, 8000);
                memoryClock = ReadSensor(log.Values, 2, 0, 15000);
                memoryTemperature = ReadSensor(log.Values, 9, 1, 140);
                fanRpm = ReadSensor(log.Values, 14, 0, 20000);
                asicPower = ReadSensor(log.Values, 23, 0, 1500);
                boardPower = ReadSensor(log.Values, 73, 0, 1500);
            }
        }
        catch (EntryPointNotFoundException) { }

        double? used = null;
        try
        {
            if (AdlDedicatedVram(_context, _index, out int usedMiB) == 0 && usedMiB >= 0 &&
                (total is null || usedMiB / 1024d <= total))
                used = usedMiB / 1024d;
        }
        catch (EntryPointNotFoundException) { }
        used ??= ReadMemoryCounter();

        return new(_name, load, temperature, hotspot, clock, used, total, memoryClock, memoryTemperature, fanRpm, asicPower,
            boardPower);
    }

    private static int? ReadSensor(int* values, int sensor, int minimum, int maximum)
    {
        int index = sensor * 2;
        int value = values[index + 1];
        return values[index] == 1 && value >= minimum && value <= maximum ? value : null;
    }

    public void Dispose()
    {
        if (_memoryQuery != 0)
        {
            PdhCloseQuery(_memoryQuery);
            _memoryQuery = 0;
        }
        if (_context != 0)
        {
            AdlDestroy(_context);
            _context = 0;
        }
    }

    private void OpenMemoryCounter(long luid)
    {
        if (PdhOpenQuery(null, 0, out _memoryQuery) != 0) return;
        string path = $@"\GPU Adapter Memory(luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}_phys_0)\Dedicated Usage";
        if (PdhAddEnglishCounter(_memoryQuery, path, 0, out _memoryCounter) == 0) return;
        PdhCloseQuery(_memoryQuery);
        _memoryQuery = 0;
    }

    private double? ReadMemoryCounter()
    {
        if (_memoryQuery == 0 || PdhCollectQueryData(_memoryQuery) != 0 ||
            PdhGetFormattedCounterValue(_memoryCounter, 0x400, 0, out CounterValue value) != 0 ||
            value.Status > 1 || value.Value < 0 || value.Value > _capacity)
            return null;
        return value.Value / BytesPerGiB;
    }

    private readonly record struct Adapter(string? Name, uint Vendor, long Bytes, long Luid);

    private static List<Adapter> EnumerateDxgi()
    {
        var result = new List<Adapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(in iid, out nint factory) < 0) return result;
        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint i = 0; i < 32; i++)
            {
                nint adapter = 0;
                if (enumerate(factory, i, &adapter) < 0) break;
                try
                {
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, DxgiDesc*, int>)(*(nint**)adapter)[10];
                    DxgiDesc desc = default;
                    if (getDescription(adapter, &desc) >= 0 && (desc.Flags & 2) == 0)
                        result.Add(new(new string(desc.Name), desc.Vendor, (long)desc.DedicatedVideoMemory, desc.Luid));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiDesc
    {
        public fixed char Name[128];
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public int Size, AdapterIndex;
        public fixed byte Udid[256];
        public int Bus, Device, Function, VendorId;
        public fixed byte Name[256], DisplayName[256];
        public int Present, Exists;
        public fixed byte DriverPath[256], DriverPathExt[256], Pnp[256];
        public int DisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SensorLog
    {
        public int Size;
        public fixed int Values[256 * 2];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public long Value; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint DriverAllocate(int size);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);

    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_Main_Control_Create")]
    private static extern int AdlCreate(DriverAllocate allocate, int connected, out nint context);
    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_Main_Control_Destroy")]
    private static extern int AdlDestroy(nint context);
    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_Adapter_NumberOfAdapters_Get")]
    private static extern int AdlCount(nint context, out int count);
    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_Adapter_AdapterInfo_Get")]
    private static extern int AdlAdapterInfo(nint context, AdapterInfo* adapters, int size);
    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_Adapter_Active_Get")]
    private static extern int AdlActive(nint context, int adapter, out int active);
    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_New_QueryPMLogData_Get")]
    private static extern int AdlReadLog(nint context, int adapter, ref SensorLog log);
    [DllImport("atiadlxx.dll", EntryPoint = "ADL2_Adapter_DedicatedVRAMUsage_Get")]
    private static extern int AdlDedicatedVram(nint context, int adapter, out int usedMiB);
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, nuint userData, out nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out CounterValue value);
}
