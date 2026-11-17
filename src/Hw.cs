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
    void Out(byte port, byte v) => _m.Exec("ioctl_pio_write", new ulong[] { port, v }, 0);
    void Wait(int mask, bool set, string what) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (((In(Cmd) & mask) != 0) != set) {
            if (sw.ElapsedMilliseconds > 50) throw new TimeoutException("EC " + what);
            Thread.SpinWait(50);
        }
    }
    void WaitIbfClear() => Wait(2, false, "IBF");
    void WaitObfSet() => Wait(1, true, "OBF");

    T Locked<T>(Func<T> f) {
        bool got = false;
        try {
            try { got = _mutex.WaitOne(500); } catch (AbandonedMutexException) { got = true; }
            for (int attempt = 0; ; attempt++) {
                try { return f(); }
                catch (TimeoutException) when (attempt < 5) {
                    if ((In(Cmd) & 1) != 0) In(Data);   // drain a stale output byte left by a broken transaction
                    Thread.Sleep(10);
                }
            }
        } finally { if (got) _mutex.ReleaseMutex(); }
    }
    public byte Read(byte addr) => Locked(() => {
        WaitIbfClear(); Out(Cmd, 0x80); WaitIbfClear(); Out(Data, addr); WaitObfSet(); return In(Data);
    });
    public void Write(byte addr, byte v) => Locked(() => {
        WaitIbfClear(); Out(Cmd, 0x81); WaitIbfClear(); Out(Data, addr); WaitIbfClear(); Out(Data, v); WaitIbfClear(); return 0;
    });
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

/// ThinkPad EC fan registers.
static class Tp {
    public const byte FanCtl = 0x2F;   // bits0-2 level, 0x40 disengaged (max), 0x80 BIOS auto
    public static int Rpm(Ec ec) => ec.Read(0x84) | (ec.Read(0x85) << 8);
}
