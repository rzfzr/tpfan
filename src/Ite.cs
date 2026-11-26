using System;
using System.Threading;

/// The EC chip's Super I/O config space (ports 0x2E/0x2F or 0x4E/0x4F) through PawnIO's signed LpcIO module, and on
/// ITE embedded controllers the I2EC window behind it: D2 registers via Super I/O registers 0x2E (index) / 0x2F
/// (data); D2 0x11/0x10 = EC address high/low, D2 0x12 = data. That reaches the EC's whole address space (SRAM and
/// its own peripherals, e.g. the PWM block at 0x1800), the way System76's ectool does. Reads, and checked writes
/// (I2ecWriteChecked) of the fan duty for DutyHold and the duty tests. This EC refuses I2EC reads of its SRAM (0x0000-0x0FFF).
sealed class SuperIo : IDisposable {
    readonly PawnModule _m = new PawnModule("LpcIO.bin");
    readonly Mutex _mutex = new Mutex(false, @"Global\Access_ISABUS.HTP.Method");   // LibreHardwareMonitor's ISA bus lock
    public readonly ushort Port;
    bool _entered;

    public SuperIo(int slot) {
        _m.Exec("ioctl_select_slot", new ulong[] { (ulong)slot }, 0);
        Port = (ushort)(slot == 0 ? 0x2E : 0x4E);
    }
    void Out(byte v) => _m.Exec("ioctl_pio_outb", new ulong[] { Port, v }, 0);
    public byte Read(byte reg) => (byte)_m.Exec("ioctl_superio_inb", new ulong[] { reg }, 1)[0];
    void Write(byte reg, byte v) => _m.Exec("ioctl_superio_outb", new ulong[] { reg, v }, 0);
    public ushort ChipId() => (ushort)(Read(0x20) << 8 | Read(0x21));

    /// ITE's config-mode key; the EC's own Super I/O usually answers without it.
    public void Enter() { Out(0x87); Out(0x01); Out(0x55); Out(Port == 0x2E ? (byte)0x55 : (byte)0xAA); _entered = true; }

    /// An ITE EC (IT55xx, IT82xx (this E14 Gen 5: IT8227 at 0x4E), IT85xx, IT89xx), whose Super I/O has the D2/I2EC registers.
    public static bool IsIteEc(ushort id) => (id >> 8) is 0x55 or 0x82 or 0x85 or 0x89;

    public T Locked<T>(Func<T> f) {
        bool got = false;
        try {
            try { got = _mutex.WaitOne(500); } catch (AbandonedMutexException) { got = true; }
            return f();
        } finally { if (got) _mutex.ReleaseMutex(); }
    }

    byte D2Read(byte r) { Write(0x2E, r); return Read(0x2F); }
    void D2Write(byte r, byte v) { Write(0x2E, r); Write(0x2F, v); }
    public byte I2ecRead(ushort addr) { D2Write(0x11, (byte)(addr >> 8)); D2Write(0x10, (byte)addr); return D2Read(0x12); }

    /// Writes one byte of the EC's address space, only after reading the address registers back as addr and the
    /// current value passing oldOk. Returns the old value, or throws without writing.
    public byte I2ecWriteChecked(ushort addr, Func<byte, bool> oldOk, byte value) {
        D2Write(0x11, (byte)(addr >> 8)); D2Write(0x10, (byte)addr);
        byte hi = D2Read(0x11), lo = D2Read(0x10);
        if (hi != (byte)(addr >> 8) || lo != (byte)addr) throw new InvalidOperationException($"I2EC address reads back 0x{hi:X2}{lo:X2}, not 0x{addr:X4}: not writing");
        byte old = D2Read(0x12);
        if (!oldOk(old)) throw new InvalidOperationException($"0x{addr:X4} holds 0x{old:X2}, not what was expected: not writing");
        D2Write(0x12, value);
        return old;
    }

    public void Dispose() {
        try { if (_entered) Write(0x02, 0x02); } catch { }   // leave config mode
        _m.Dispose(); _mutex.Dispose();
    }
}

/// Holds the fan's PWM duty (IT8227 register 0x1804) at a value between the EC's own levels. With 0x2F at a level
/// (3..7: 4200 rpm) the firmware steers the duty back towards its target by about one count every 50 ms, so it is
/// rewritten every 100 ms: measured 2026-11-26, the fan then holds within ±15 rpm (0x9E = 5320 rpm) and the
/// firmware never gets more than 3 counts back. If this stops (crash, service stop) the firmware returns to its own
/// target within ~3 s. Every write is checked: the I2EC address must read back as 0x1804 and the duty must lie
/// between 0x20 and the held one (above it = the EC is at a higher level), so nothing is written if the EC is in
/// another state. Low values are allowed: a write to 0x2F restarts the firmware's ramp from about 0x46.
sealed class DutyHold : IDisposable {
    const ushort Pwm = 0x1804;
    readonly SuperIo _sio;
    readonly Thread _thread;
    volatile int _want = -1;      // duty to hold, -1 = none
    volatile bool _stop;
    /// Why the last second of writes failed, or null. The controller then falls back to full speed.
    public volatile string Error;

    public DutyHold() {
        _sio = new SuperIo(1);
        ushort id = _sio.Locked(() => _sio.ChipId());
        if (id != 0x8227) { _sio.Dispose(); throw new System.IO.IOException($"no IT8227 EC at 0x4E (chip 0x{id:X4})"); }
        _thread = new Thread(Loop) { IsBackground = true, Name = "duty hold" };
        _thread.Start();
    }

    public void Hold(byte? duty) => _want = duty ?? -1;

    void Loop() {
        int fails = 0;
        while (!_stop) {
            int w = _want;
            if (w < 0) { fails = 0; Error = null; }
            else {
                byte d = (byte)w;
                try { _sio.Locked(() => _sio.I2ecWriteChecked(Pwm, v => v >= 0x20 && v <= Math.Max(d, (byte)0x90), d)); fails = 0; Error = null; }
                catch (Exception e) { if (++fails >= 10 && Error == null) Error = e.Message; }
            }
            Thread.Sleep(100);
        }
    }

    public void Dispose() { _stop = true; _thread.Join(1000); _sio.Dispose(); }
}
