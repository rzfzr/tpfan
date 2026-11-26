using System;
using System.Runtime.InteropServices;

/// A CPU mode: power and temperature limits for the SMU, the Windows power mode and frequency cap, and the
/// temperature where the fan's top step starts. Replaces Universal x86 Tuning Utility's presets.
sealed class CpuMode {
    public string Name { get; set; }
    // SMU limits, in the units UXTU shows (the SMU takes mW and mA). 0 = leave as it is.
    public double TctlTemp { get; set; }          // °C: the CPU boosts until this temperature
    public double StapmLimit { get; set; }        // W, sustained (STAPM) power
    public double FastLimit { get; set; }         // W, short bursts
    public double SlowLimit { get; set; }         // W, average over SlowTime
    public double StapmTime { get; set; }         // s
    public double SlowTime { get; set; }          // s
    public double VrmCurrent { get; set; }        // A, CPU TDC
    public double VrmMaxCurrent { get; set; }     // A, CPU EDC
    public double VrmSocCurrent { get; set; }     // A, SoC TDC
    public double VrmSocMaxCurrent { get; set; }  // A, SoC EDC
    public string Boost { get; set; }             // "max-performance", "power-saving" or null
    // Windows
    public string PowerMode { get; set; }         // "best-performance", "balanced", "best-efficiency" or null
    public int? MaxFreqMHz { get; set; }          // plugged-in frequency cap of the active plan, 0 = none, null = leave
    // Fan: replaces Up/Down of the top step
    public double? FanUp { get; set; }
    public double? FanDown { get; set; }
    public FanBoost FanBoost { get; set; }        // a faster fan above the top step, for long all-core loads only

    /// SMU (message, value) pairs, in the order UXTU sends them.
    public (uint msg, uint value, string name)[] SmuMessages() {
        var list = new System.Collections.Generic.List<(uint, uint, string)>();
        void Add(uint msg, double v, double scale, string name) { if (v > 0) list.Add((msg, (uint)Math.Round(v * scale), name)); }
        Add(Smu.TctlTemp, TctlTemp, 1, "tctl");
        Add(Smu.StapmLimit, StapmLimit, 1000, "stapm");
        Add(Smu.FastLimit, FastLimit, 1000, "fast");
        Add(Smu.StapmTime, StapmTime, 1, "stapm time");
        Add(Smu.SlowLimit, SlowLimit, 1000, "slow");
        Add(Smu.SlowTime, SlowTime, 1, "slow time");
        Add(Smu.VrmCurrent, VrmCurrent, 1000, "tdc");
        Add(Smu.VrmMaxCurrent, VrmMaxCurrent, 1000, "edc");
        Add(Smu.VrmSocCurrent, VrmSocCurrent, 1000, "soc tdc");
        Add(Smu.VrmSocMaxCurrent, VrmSocMaxCurrent, 1000, "soc edc");
        if (Boost == "max-performance") list.Add((Smu.MaxPerformance, 0, "max performance"));
        else if (Boost == "power-saving") list.Add((Smu.PowerSaving, 0, "power saving"));
        return list.ToArray();
    }

    public void Validate() {
        if (string.IsNullOrWhiteSpace(Name)) throw new System.IO.InvalidDataException("every mode needs a Name");
        if (TctlTemp > 100) throw new System.IO.InvalidDataException($"mode {Name}: TctlTemp above 100 °C");
        if (Boost != null && Boost != "max-performance" && Boost != "power-saving") throw new System.IO.InvalidDataException($"mode {Name}: Boost must be max-performance or power-saving");
        if (PowerMode != null && WinPower.Overlay(PowerMode) == null) throw new System.IO.InvalidDataException($"mode {Name}: unknown PowerMode {PowerMode}");
        if ((FanUp == null) != (FanDown == null) || FanDown >= FanUp) throw new System.IO.InvalidDataException($"mode {Name}: FanUp and FanDown go together, Down below Up");
        FanBoost?.Validate(Name);
    }
}

/// The fan above the top step (4200 rpm), only while the CPU has sat at its temperature limit under a full load for
/// a long time: a long build, not a burst. Once on it stays on for a while, so the fan never jumps up for a few
/// seconds and back. It ends on the load, not the temperature: with more cooling the CPU only boosts higher and
/// stays at its limit, so the temperature drops only once the load does.
sealed class FanBoost {
    public string Ec { get; set; } = "40";        // 0x2F value; 0x40 = full speed (6300 rpm)
    public string Note { get; set; } = "6300 rpm";
    public double Temp { get; set; } = 97;        // smoothed Tctl at or above this
    public double Load { get; set; } = 70;        // and smoothed CPU load (% of all threads) at or above this
    public double AfterSec { get; set; } = 30;    // for this long (time below counts back down, not to zero)
    public double MinSec { get; set; } = 120;     // once on, at least this long
    public double ExitLoad { get; set; } = 60;    // off when the load is below this, or Tctl below ExitTemp,
    public double ExitTemp { get; set; } = 90;
    public double ExitSec { get; set; } = 30;     // for this long (counted the same way)
    internal byte Value => Convert.ToByte(Ec, 16);

    public void Validate(string mode) {
        _ = Value;
        if (Temp > 100 || ExitTemp >= Temp) throw new System.IO.InvalidDataException($"mode {mode}: FanBoost Temp at most 100, ExitTemp below it");
        if (Load > 100 || ExitLoad >= Load) throw new System.IO.InvalidDataException($"mode {mode}: FanBoost Load at most 100, ExitLoad below it");
        if (AfterSec < 0 || MinSec < 0 || ExitSec < 0) throw new System.IO.InvalidDataException($"mode {mode}: FanBoost times can't be negative");
    }
}

/// Windows power settings a mode sets: the power mode (overlay) and the plugged-in maximum CPU frequency.
static class WinPower {
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid overlay);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    static Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
    static Guid ProcFreqMax = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");

    public static Guid? Overlay(string mode) => mode switch {
        "best-performance" => new Guid("ded574b5-45a0-4f42-8737-46345c09c238"),
        "balanced" => Guid.Empty,
        "best-efficiency" => new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"),
        _ => null,
    };

    public static void Apply(CpuMode m) {
        if (m.MaxFreqMHz is int mhz && PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) == 0) {
            var scheme = Marshal.PtrToStructure<Guid>(p); LocalFree(p);
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref ProcFreqMax, (uint)mhz), "frequency cap");
            Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "activate plan");
        }
        if (m.PowerMode != null) Check(PowerSetActiveOverlayScheme(Overlay(m.PowerMode).Value), "power mode");
    }

    static void Check(uint err, string what) { if (err != 0) throw new System.ComponentModel.Win32Exception((int)err, what); }
}
