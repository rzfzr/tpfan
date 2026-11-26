using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

static class PawnNative {
    const string Dll = @"C:\Program Files\PawnIO\PawnIOLib.dll";
    [DllImport(Dll)] public static extern int pawnio_open(out IntPtr h);
    [DllImport(Dll)] public static extern int pawnio_load(IntPtr h, byte[] blob, UIntPtr size);
    [DllImport(Dll)] public static extern int pawnio_execute(IntPtr h, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returned);
    [DllImport(Dll)] public static extern int pawnio_close(IntPtr h);
}

sealed class PawnModule : IDisposable {
    IntPtr _h;
    public PawnModule(string resource) {
        int hr = PawnNative.pawnio_open(out _h);
        if (hr < 0) throw new IOException($"pawnio_open failed 0x{hr:X8} (needs admin)");
        byte[] blob;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        using (var ms = new MemoryStream()) { s.CopyTo(ms); blob = ms.ToArray(); }
        hr = PawnNative.pawnio_load(_h, blob, (UIntPtr)blob.Length);
        if (hr < 0) throw new IOException($"pawnio_load({resource}) failed 0x{hr:X8}");
    }
    public ulong[] Exec(string fn, ulong[] input, int outCount) {
        var o = new ulong[Math.Max(outCount, 1)];
        int hr = PawnNative.pawnio_execute(_h, fn, input, (UIntPtr)input.Length, o, (UIntPtr)outCount, out _);
        if (hr < 0) throw new IOException($"{fn} failed 0x{hr:X8}");
        return o;
    }
    public void Dispose() { if (_h != IntPtr.Zero) { PawnNative.pawnio_close(_h); _h = IntPtr.Zero; } }
}

/// ACPI embedded controller on ports 0x62/0x66, shared with other tools (HWiNFO, LHM) through Global\Access_EC.
sealed class Ec : IDisposable {
    const byte Data = 0x62, Cmd = 0x66;
    readonly PawnModule _m = new PawnModule("LpcACPIEC.bin");
    readonly Mutex _mutex = new Mutex(false, @"Global\Access_EC");

    byte In(byte port) => (byte)_m.Exec("ioctl_pio_read", new ulong[] { port }, 1)[0];
    /// The status register (port 0x66): reading it never takes anything from anyone. For the ec-sniff diagnostic.
    public byte Status() => In(Cmd);
    void Out(byte port, byte v) => _m.Exec("ioctl_pio_write", new ulong[] { port, v }, 0);
    string _op = "";
    void Wait(int mask, bool set, string what) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int st;
        while ((((st = In(Cmd)) & mask) != 0) != set) {
            if (sw.ElapsedMilliseconds > 50) throw new TimeoutException($"{_op}: EC {what} (status 0x{st:X2})");
            Thread.SpinWait(50);
        }
    }
    void WaitIbfClear() => Wait(2, false, "IBF");
    void WaitObfSet() => Wait(1, true, "OBF");

    // Windows' own ACPI EC driver uses these same ports and nothing here can lock against it
    // (Global\Access_EC only covers other user-mode tools). When two transactions interleave, the EC
    // pairs our bytes with Windows' ones: a read returns a byte meant for Windows, and a write can
    // land on another register. On 2026-11-20..23 that happened at wake-up and switched off the
    // TrackPoint. So: never take an output byte we did not ask for, never retry in a loop (one retry of a
    // dropped read, below, is not a loop), and let the caller double-check a value before acting on it.
    T Locked<T>(Func<T> f) {
        bool got = false;
        try {
            try { got = _mutex.WaitOne(500); } catch (AbandonedMutexException) { got = true; }
            return f();
        } finally { if (got) _mutex.ReleaseMutex(); }
    }
    // A pending output byte is someone else's answer: wait for its owner to collect it.
    void WaitIdle() { Wait(1, false, "busy (OBF)"); WaitIbfClear(); }

    // Dropped reads (measured 2026-11-26): about once a minute the EC takes a read command and its address and
    // never answers. That is not a collision: with tpfan stopped, `tpfan ec-sniff` saw no other port traffic in 90 s
    // but one 1.35 ms Windows event query, and each failure ended with tpfan's own address byte as the last write
    // (status 0x00, no command after it). The firmware simply drops the read, likely while busy with internal work
    // (the battery). Nobody else gets a wrong byte, so the read is tried once more after 5 ms.
    public int DroppedReads;   // dropped and retried; logged by the controller
    byte ReadOnce(byte addr) => Locked(() => {
        _op = $"read 0x{addr:X2}";
        WaitIdle(); Out(Cmd, 0x80); WaitIbfClear(); Out(Data, addr); WaitObfSet(); return In(Data);
    });
    public byte Read(byte addr) {
        try { return ReadOnce(addr); }
        catch (TimeoutException e) when (e.Message.Contains("EC OBF")) { DroppedReads++; Thread.Sleep(5); return ReadOnce(addr); }
    }
    public void Write(byte addr, byte v) => Locked(() => {
        _op = $"write 0x{addr:X2}";
        WaitIdle(); Out(Cmd, 0x81); WaitIbfClear(); Out(Data, addr); WaitIbfClear(); Out(Data, v); WaitIbfClear(); return 0;
    });
    /// Two reads that must agree; a collision shows up as a mismatch and throws instead of returning junk.
    public byte ReadStable(byte addr) {
        byte a = Read(addr); Thread.Sleep(5); byte b = Read(addr);
        if (a != b) throw new TimeoutException($"EC 0x{addr:X2} unstable (0x{a:X2}/0x{b:X2})");
        return a;
    }
    public void Dispose() { _m.Dispose(); _mutex.Dispose(); }
}

/// AMD Zen Tctl via SMN THM_TCON_CUR_TMP.
sealed class AmdTemp : IDisposable {
    readonly PawnModule _m = new PawnModule("AMDFamily17.bin");
    public double Tctl() {
        uint v = (uint)_m.Exec("ioctl_read_smn", new ulong[] { 0x00059800 }, 1)[0];
        double t = (v >> 21) * 0.125;
        if ((v & (1u << 19)) != 0) t -= 49;
        return t;
    }
    public void Dispose() => _m.Dispose();
}

/// Share of all logical processors busy since the last sample, from GetSystemTimes (kernel time includes idle).
sealed class CpuLoad {
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    long _idle, _total;
    public CpuLoad() => Sample();
    /// Percent, 0..100; NaN if nothing elapsed.
    public double Sample() {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return double.NaN;
        long total = kernel + user, di = idle - _idle, dt = total - _total;
        _idle = idle; _total = total;
        return dt <= 0 ? double.NaN : Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
    }
}

/// ThinkPad EC fan registers.
static class Tp {
    public const byte FanCtl = 0x2F;   // bits0-2 level, 0x40 disengaged (max), 0x80 BIOS auto
    public static int Rpm(Ec ec) => ec.Read(0x84) | (ec.Read(0x85) << 8);
}

/// Display on/off and suspend/resume notifications (also fire for Modern Standby, which never stops the service).
/// The EC is busiest right after wake-up, which is when collisions with Windows' EC driver happened.
static class PowerWatch {
    delegate uint Callback(IntPtr context, uint type, IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] struct Subscribe { public IntPtr Cb; public IntPtr Ctx; }
    [DllImport("powrprof.dll")] static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, IntPtr recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr handle);
    const uint DeviceNotifyCallback = 2, PbtSuspend = 4, PbtResumeSuspend = 7, PbtResumeAutomatic = 0x12, PbtSetting = 0x8013;
    static Guid ConsoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
    static Guid AcDcPowerSource = new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    static Callback _keep;   // must outlive the registration

    /// onEvent(true) = display off / suspending; onEvent(false) = display on / resumed. onAcChange: cable in or out.
    public static void Start(Action<bool, string> onEvent, Action onAcChange) {
        _keep = (ctx, type, setting) => {
            try {
                if (type == PbtSuspend) onEvent(true, "suspend");
                else if (type == PbtResumeSuspend || type == PbtResumeAutomatic) onEvent(false, "resume");
                else if (type == PbtSetting && setting != IntPtr.Zero) {
                    var which = Marshal.PtrToStructure<Guid>(setting);   // POWERBROADCAST_SETTING: Guid(16) + DataLength(4) + Data
                    int state = Marshal.ReadInt32(setting, 20);
                    if (which == AcDcPowerSource) onAcChange();
                    else if (state == 0) onEvent(true, "display off"); else if (state == 1) onEvent(false, "display on");
                }
            } catch { }
            return 0;
        };
        var sub = Marshal.AllocHGlobal(Marshal.SizeOf<Subscribe>());   // never freed: lives as long as the process
        Marshal.StructureToPtr(new Subscribe { Cb = Marshal.GetFunctionPointerForDelegate(_keep) }, sub, false);
        PowerSettingRegisterNotification(ref ConsoleDisplayState, DeviceNotifyCallback, sub, out _);
        PowerSettingRegisterNotification(ref AcDcPowerSource, DeviceNotifyCallback, sub, out _);
        PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, sub, out _);
    }
}
