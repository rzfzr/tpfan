using System;
using System.Threading;

/// The EC chip's Super I/O config space (ports 0x2E/0x2F or 0x4E/0x4F) through PawnIO's signed LpcIO module, and on
/// ITE embedded controllers the I2EC window behind it: D2 registers via Super I/O registers 0x2E (index) / 0x2F
/// (data); D2 0x11/0x10 = EC address high/low, D2 0x12 = data. That reaches the EC's whole address space (SRAM and
/// its own peripherals, e.g. the PWM block at 0x1800), the way System76's ectool does. Diagnostics only: reads.
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

    public void Dispose() {
        try { if (_entered) Write(0x02, 0x02); } catch { }   // leave config mode
        _m.Dispose(); _mutex.Dispose();
    }
}
