using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// Everything the bar shows, read once a second. NaN (or -1 / empty) = not available.
sealed unsafe partial class Sensors {
    public static readonly string TpfanDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "tpfan");
    static readonly string StatusFile = Path.Combine(TpfanDir, "status.txt");
    static readonly Regex StatusRx = new Regex(@"tctl=(\S+) avg=\S+ (.*)$");

    public double CpuW = double.NaN, Tctl = double.NaN, RateW = double.NaN, HoursLeft = double.NaN, HoursToFull = double.NaN;
    public bool Ac, Charging, Discharging;
    public int Percent = -1;
    public string Fan = "";
    public int Rpm = -1;          // last fan speed tpfan read (it reads it every 5 s; not while paused)
    public bool FanForced;        // tpfan holds a step above BIOS auto (the 4200 rpm step)
    public bool FanPaused;        // tpfan isn't touching the EC right now (display off, after wake)
    public string ModeApplied;   // mode tpfan reports as in force; null if none / tpfan not running
    public bool SmuError;
    public double CpuTotal = double.NaN;
    public double[] CoreLoad = Array.Empty<double>();   // per physical core: the busier of its threads, 0..100
    public double DiskActive = double.NaN, DiskRead = double.NaN, DiskWrite = double.NaN;
    public double SendBps = double.NaN, RecvBps = double.NaN;
    public string NetNames = "";
    public double RamPercent = double.NaN, RamUsed, RamTotal, CommitUsed, CommitLimit;

    readonly Pdh _pdh = new Pdh();
    readonly IntPtr _power, _util, _idle, _read, _write;
    readonly int[] _coreOf = Cpu.CoreOfLogical();   // logical processor -> physical core
    readonly int _cores;
    readonly Stopwatch _netClock = new Stopwatch();
    ulong _inOctets, _outOctets;

    public Sensors() {
        // CPU package power: the RAPL energy meter Windows exposes as a counter (mW). CPU load: Task Manager's
        // "% Processor Utility" (counts boost, capped at 100). Disk: Task Manager's active time = 100 - idle.
        _power = _pdh.Add(@"\Energy Meter(RAPL_Package0_PKG)\Power");
        _util = _pdh.Add(@"\Processor Information(*)\% Processor Utility");
        _idle = _pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _read = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _write = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _cores = _coreOf.Length == 0 ? 0 : _coreOf.Max() + 1;
    }

    public void Read() {
        _pdh.Collect();
        CpuW = _pdh.Value(_power) / 1000;
        ReadCpuLoad();
        DiskActive = Math.Clamp(100 - _pdh.Value(_idle), 0, 100);
        DiskRead = _pdh.Value(_read); DiskWrite = _pdh.Value(_write);
        ReadNet();
        ReadRam();
        ReadTpfan();
        ReadBattery();
    }

    void ReadCpuLoad() {
        var all = _pdh.Values(_util);
        CpuTotal = all.TryGetValue("_Total", out var t) ? Math.Min(t, 100) : double.NaN;
        var cores = new double[_cores];
        foreach (var (name, v) in all) {
            // instances are "group,index" ("0,5"); skip the "0,_Total" and "_Total" sums
            int comma = name.IndexOf(','); if (comma < 0) continue;
            if (!int.TryParse(name.AsSpan(comma + 1), out int i) || i >= _coreOf.Length) continue;
            cores[_coreOf[i]] = Math.Max(cores[_coreOf[i]], Math.Min(v, 100));
        }
        CoreLoad = all.Count == 0 ? Array.Empty<double>() : cores;
    }

    void ReadNet() {
        // Physical adapters only (Wi-Fi, Ethernet): VPN tunnels (NordVPN, Tailscale) would count traffic twice.
        if (!Net.Totals(out ulong inO, out ulong outO, out string names)) { SendBps = RecvBps = double.NaN; return; }
        double dt = _netClock.Elapsed.TotalSeconds;
        if (_netClock.IsRunning && dt > 0.2 && inO >= _inOctets && outO >= _outOctets) {
            RecvBps = (inO - _inOctets) * 8 / dt;
            SendBps = (outO - _outOctets) * 8 / dt;
        }
        _inOctets = inO; _outOctets = outO; _netClock.Restart();
        NetNames = names;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalMemoryStatusEx(ref MemoryStatus m);

    void ReadRam() {
        var m = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
        if (!GlobalMemoryStatusEx(ref m) || m.TotalPhys == 0) { RamPercent = double.NaN; return; }
        RamTotal = m.TotalPhys; RamUsed = m.TotalPhys - m.AvailPhys;
        RamPercent = RamUsed * 100 / RamTotal;
        CommitLimit = m.TotalPageFile; CommitUsed = m.TotalPageFile - m.AvailPageFile;
    }

    void ReadTpfan() {
        try {
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(StatusFile)).TotalSeconds > 10) {
                Tctl = double.NaN; Fan = "Fan: tpfan is not running"; ModeApplied = null; Rpm = -1; FanForced = FanPaused = false; return;
            }
            string line;
            using (var f = new FileStream(StatusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(f)) line = r.ReadLine();
            var m = line == null ? null : StatusRx.Match(line);
            if (m == null || !m.Success) return;   // caught mid-write: keep the previous values
            Tctl = double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN;
            string rest = m.Groups[2].Value;
            var mode = Regex.Match(rest, @" mode=(\S+)( smu=error)?$");
            ModeApplied = mode.Success ? mode.Groups[1].Value : null;
            SmuError = mode.Success && mode.Groups[2].Success;
            if (mode.Success) rest = rest.Substring(0, mode.Index);
            var rpm = Regex.Match(rest, @"rpm=(\d+)");
            var note = Regex.Match(rest, @"\(([^)]*)\)");
            var step = Regex.Match(rest, @"step=(\d+)/");
            FanPaused = rest.StartsWith("paused");
            if (rpm.Success) Rpm = int.Parse(rpm.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!FanPaused) FanForced = step.Success && step.Groups[1].Value != "0";
            Fan = rest.StartsWith("paused") ? $"Fan: tpfan {rest}"
                : rpm.Success ? $"Fan {rpm.Groups[1].Value} rpm" + (note.Success && !note.Groups[1].Value.Contains("rpm") ? $" ({note.Groups[1].Value})" : "")
                : "Fan: " + rest;
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatus; public int LifeTime, FullLifeTime; }
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetSystemPowerStatus(out PowerStatus s);

    // SYSTEM_BATTERY_STATE (level 5). Rate in mW, positive while charging.
    [StructLayout(LayoutKind.Sequential)]
    struct BatteryState {
        public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag;
        public uint MaxCapacity, RemainingCapacity;
        public int Rate;
        public uint EstimatedTime, DefaultAlert1, DefaultAlert2;
    }
    [LibraryImport("powrprof.dll")] private static partial uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out BatteryState output, uint outputLength);

    void ReadBattery() {
        RateW = HoursLeft = HoursToFull = double.NaN; Charging = Discharging = false; Percent = -1;
        if (!GetSystemPowerStatus(out var ps)) return;
        Ac = ps.AcLine == 1;
        bool present = ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryPercent <= 100;
        if (!present) return;
        Percent = ps.BatteryPercent;
        if (CallNtPowerInformation(5, IntPtr.Zero, 0, out var b, (uint)sizeof(BatteryState)) != 0) return;
        Charging = b.Charging != 0; Discharging = b.Discharging != 0;
        if (b.Rate != int.MinValue) RateW = b.Rate / 1000.0;   // int.MinValue = BATTERY_UNKNOWN_RATE
        if (Charging && b.Rate > 0 && b.MaxCapacity > b.RemainingCapacity) HoursToFull = (b.MaxCapacity - b.RemainingCapacity) / (double)b.Rate;
        if (Discharging && b.EstimatedTime != uint.MaxValue && b.EstimatedTime > 0) HoursLeft = b.EstimatedTime / 3600.0;
    }
}

/// The CPU modes in tpfan's config, and switching between them by rewriting its "Mode" (tpfan reloads on save).
sealed class Modes {
    static readonly string ConfigFile = Path.Combine(Sensors.TpfanDir, "config.json");
    static readonly Regex ModeRx = new Regex("(\"Mode\"\\s*:\\s*\")([^\"]*)(\")");
    DateTime _stamp;
    public List<(string Name, string Summary)> All = new List<(string, string)>();
    public string Requested;

    public void Refresh() {
        try {
            var stamp = File.GetLastWriteTimeUtc(ConfigFile);
            if (stamp == _stamp) return;
            _stamp = stamp;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ConfigFile),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            Requested = root.TryGetProperty("Mode", out var mo) ? mo.GetString() : null;
            All.Clear();
            if (!root.TryGetProperty("Modes", out var list)) return;
            foreach (var m in list.EnumerateArray()) {
                double N(string p) => m.TryGetProperty(p, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : 0;
                string cap = m.TryGetProperty("MaxFreqMHz", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? (f.GetInt32() == 0 ? "no frequency cap" : $"{f.GetInt32() / 1000.0:0.0} GHz cap") : "";
                string fan = N("FanUp") > 0 ? $", fan 4200 rpm from {N("FanUp"):0} °C" : "";
                All.Add((m.GetProperty("Name").GetString(),
                         $"{N("TctlTemp"):0} °C, {N("StapmLimit"):0}/{N("SlowLimit"):0}/{N("FastLimit"):0} W\n{cap}{fan}"));
            }
        } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Text.Json.JsonException || e is KeyNotFoundException || e is InvalidOperationException) { }
    }

    public bool Request(string name) {
        try {
            string text = File.ReadAllText(ConfigFile);
            if (!ModeRx.IsMatch(text)) return false;
            File.WriteAllText(ConfigFile, ModeRx.Replace(text, m => m.Groups[1].Value + name + m.Groups[3].Value, 1), new System.Text.UTF8Encoding(false));
            Requested = name;
            return true;
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
}

/// Performance counters through PDH, one query collected once per tick.
sealed unsafe partial class Pdh {
    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [LibraryImport("pdh.dll")] private static partial uint PdhCollectQueryData(IntPtr query);
    [LibraryImport("pdh.dll")] private static partial uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FmtValue value);
    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
    [StructLayout(LayoutKind.Explicit)] struct FmtValue { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Double; }
    const uint FmtDouble = 0x200, FmtNoCap100 = 0x8000, MoreData = 0x800007D2;
    readonly IntPtr _query;
    IntPtr _buf; int _bufSize;

    public Pdh() { if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero; }

    public IntPtr Add(string path) =>
        _query != IntPtr.Zero && PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    public void Collect() { if (_query != IntPtr.Zero) PdhCollectQueryData(_query); }

    /// NaN until a counter has two samples (rate counters) or when it doesn't exist.
    public double Value(IntPtr counter) =>
        counter != IntPtr.Zero && PdhGetFormattedCounterValue(counter, FmtDouble | FmtNoCap100, IntPtr.Zero, out var v) == 0 && v.CStatus <= 1 ? v.Double : double.NaN;

    /// All instances of a wildcard counter, by instance name. The buffer is kept between calls.
    public Dictionary<string, double> Values(IntPtr counter) {
        var result = new Dictionary<string, double>();
        if (counter == IntPtr.Zero) return result;
        uint bytes = 0;
        if (PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref bytes, out _, IntPtr.Zero) != MoreData || bytes == 0) return result;
        if (bytes > _bufSize) { if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf); _buf = Marshal.AllocHGlobal((int)bytes); _bufSize = (int)bytes; }
        if (PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref bytes, out uint count, _buf) != 0) return result;
        for (int i = 0; i < count; i++) {   // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then the value (status, pad, double)
            byte* item = (byte*)_buf + i * (IntPtr.Size + 16);
            string name = Marshal.PtrToStringUni(*(IntPtr*)item);
            uint status = *(uint*)(item + IntPtr.Size);
            if (name != null && status <= 1) result[name] = *(double*)(item + IntPtr.Size + 8);
        }
        return result;
    }
}

static unsafe partial class Cpu {
    [StructLayout(LayoutKind.Sequential)] struct Info { public UIntPtr Mask; public int Relationship; public ulong Reserved0, Reserved1; }
    [LibraryImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    /// For each logical processor, the index of its physical core (SMT threads share one).
    public static int[] CoreOfLogical() {
        uint len = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref len);
        if (len == 0) return Enumerable.Range(0, Environment.ProcessorCount).ToArray();
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try {
            if (!GetLogicalProcessorInformation(buf, ref len)) return Enumerable.Range(0, Environment.ProcessorCount).ToArray();
            var map = Enumerable.Repeat(-1, Environment.ProcessorCount).ToArray();
            int size = sizeof(Info), core = 0;
            for (int off = 0; off + size <= len; off += size) {
                var info = *(Info*)((byte*)buf + off);
                if (info.Relationship != 0) continue;   // RelationProcessorCore
                ulong mask = info.Mask.ToUInt64();
                for (int i = 0; i < map.Length && i < 64; i++) if ((mask & (1UL << i)) != 0) map[i] = core;
                core++;
            }
            for (int i = 0; i < map.Length; i++) if (map[i] < 0) map[i] = core++;
            return map;
        } finally { Marshal.FreeHGlobal(buf); }
    }
}

static unsafe partial class Net {
    // MIB_IF_ROW2, 1352 bytes on x64; offsets checked against Marshal.OffsetOf of the full struct.
    const int RowSize = 1352, AliasOff = 28, FlagsOff = 1152, OperStatusOff = 1156, InOctetsOff = 1208, OutOctetsOff = 1280;
    [LibraryImport("iphlpapi.dll")] private static partial uint GetIfTable2(out IntPtr table);
    [LibraryImport("iphlpapi.dll")] private static partial void FreeMibTable(IntPtr table);

    /// Byte totals over the hardware interfaces that are up, and their names.
    public static bool Totals(out ulong inOctets, out ulong outOctets, out string names) {
        inOctets = outOctets = 0; names = "";
        if (IntPtr.Size != 8 || GetIfTable2(out IntPtr table) != 0) return false;
        try {
            int n = *(int*)table;
            var up = new List<string>();
            for (int i = 0; i < n; i++) {
                byte* r = (byte*)table + 8 + i * RowSize;
                if ((r[FlagsOff] & 1) == 0 || *(uint*)(r + OperStatusOff) != 1) continue;   // HardwareInterface, IfOperStatusUp
                inOctets += *(ulong*)(r + InOctetsOff); outOctets += *(ulong*)(r + OutOctetsOff);
                string alias = new string((char*)(r + AliasOff));
                if (!up.Contains(alias)) up.Add(alias);
            }
            names = string.Join(", ", up);
            return true;
        } finally { FreeMibTable(table); }
    }
}
