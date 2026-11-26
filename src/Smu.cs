using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// The CPU's power-management firmware (SMU) on Cezanne / Barcelo (Ryzen 5 7530U), through PawnIO's signed
/// RyzenSMU module. This is what Universal x86 Tuning Utility and RyzenAdj do to set power and temperature limits.
///
/// The limits are MP1 mailbox messages (RyzenAdj lib/api.c, FAM_CEZANNE). The module's own send_smu_command uses
/// the other (PSMU) mailbox on this CPU, so MP1 messages are sent register by register with its
/// read/write_smu_register, which allow the 0x3B10xxx mailbox range. The PM table (read back the limits in force)
/// uses the module's own resolve/update/read calls. Global\Access_PCI is the lock other tools use for the same
/// PCI index/data registers.
sealed class Smu : IDisposable {
    // MP1 mailbox (RyzenAdj nb_smu_ops.c, MP1_C2PMSG_*_1)
    const uint MsgAddr = 0x3B10528, RspAddr = 0x3B10564, ArgAddr = 0x3B10998;
    public const uint Ok = 1, Failed = 0xFF, UnknownCmd = 0xFE, RejectedPrereq = 0xFD, RejectedBusy = 0xFC;

    // MP1 message ids for Cezanne; values are mW, mA, °C or seconds as given (RyzenAdj lib/api.c)
    public const uint MaxPerformance = 0x11, PowerSaving = 0x12, StapmLimit = 0x14, FastLimit = 0x15, SlowLimit = 0x16,
        SlowTime = 0x17, StapmTime = 0x18, TctlTemp = 0x19, VrmCurrent = 0x1A, VrmSocCurrent = 0x1B,
        VrmMaxCurrent = 0x1C, VrmSocMaxCurrent = 0x1D;

    readonly PawnModule _m = new PawnModule("RyzenSMU.bin");
    readonly Mutex _mutex = new Mutex(false, @"Global\Access_PCI");
    ulong _tableVersion;

    T Locked<T>(Func<T> f) {
        bool got = false;
        try {
            try { got = _mutex.WaitOne(500); } catch (AbandonedMutexException) { got = true; }
            return f();
        } finally { if (got) _mutex.ReleaseMutex(); }
    }

    uint Read(uint addr) => (uint)_m.Exec("ioctl_read_smu_register", new ulong[] { addr }, 1)[0];
    void Write(uint addr, uint v) => _m.Exec("ioctl_write_smu_register", new ulong[] { addr, v }, 0);

    public int CodeName => (int)_m.Exec("ioctl_get_code_name", Array.Empty<ulong>(), 1)[0];

    /// Sends one MP1 message (RyzenAdj's smu_service_req) and returns the SMU's answer: Ok, UnknownCmd, ...
    public uint Send(uint msg, uint arg) => Locked(() => {
        var sw = Stopwatch.StartNew();
        while (Read(RspAddr) == 0) {   // 0 = still busy with someone else's message
            if (sw.ElapsedMilliseconds > 200) throw new TimeoutException("SMU busy");
            Thread.Sleep(1);
        }
        Write(RspAddr, 0);
        Write(ArgAddr, arg);
        for (uint i = 1; i < 6; i++) Write(ArgAddr + 4 * i, 0);
        Write(MsgAddr, msg);
        sw.Restart();
        uint r;
        while ((r = Read(RspAddr)) == 0) {
            if (sw.ElapsedMilliseconds > 500) throw new TimeoutException($"SMU message 0x{msg:X2}: no answer");
            Thread.SpinWait(200);
        }
        return r;
    });

    /// The PM table as floats (the SMU refreshes it on request). Offsets for table version 0x400005 below.
    public float[] Table(out ulong version) {
        var t = Locked(() => {
            if (_tableVersion == 0) _tableVersion = _m.Exec("ioctl_resolve_pm_table", Array.Empty<ulong>(), 2)[0];
            _m.Exec("ioctl_update_pm_table", Array.Empty<ulong>(), 0);
            return _m.Exec("ioctl_read_pm_table", Array.Empty<ulong>(), 300);
        });
        version = _tableVersion;
        var f = new float[t.Length * 2];
        for (int i = 0; i < t.Length; i++) {
            f[2 * i] = BitConverter.Int32BitsToSingle((int)(t[i] & 0xFFFFFFFF));
            f[2 * i + 1] = BitConverter.Int32BitsToSingle((int)(t[i] >> 32));
        }
        return f;
    }

    /// Limits in force, from PM table 0x400004/0x400005 (RyzenAdj's getters): W, A, °C, s.
    public static string Describe(float[] t, ulong version) {
        if (version != 0x400005 && version != 0x400004) return $"PM table version 0x{version:X} not known";
        float F(int off) => t[off / 4];
        return $"stapm {F(0x0):F1} W (now {F(0x4):F1}), fast {F(0x8):F1} W (now {F(0xC):F1}), slow {F(0x10):F1} W (now {F(0x14):F1}), " +
               $"tdc {F(0x20):F1} A, edc {F(0x30):F1} A, soc tdc {F(0x28):F1} A, soc edc {F(0x38):F1} A, " +
               $"tctl {F(0x40):F1} °C (now {F(0x44):F1}), stapm time {F(0x918):F0} s, slow time {F(0x91C):F0} s";
    }

    public void Dispose() { _m.Dispose(); _mutex.Dispose(); }
}
