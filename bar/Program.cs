using System.Threading;

// tpfan-bar: this laptop's readout on the taskbar, replacing HWiNFO's tray icons and XMeters. One strip, left to
// right: CPU mode switch (E P M: click a letter for that mode), CPU load per core, disk activity, network send/receive, memory, CPU package
// power, CPU temperature, battery charge and rate, fan speed. Hover an item for details. No hardware access of its own: Tctl,
// fan and the mode come from tpfan's status.txt, everything else from Windows. No admin needed.

static class Program {
    static int Main() {
        using var single = new Mutex(true, @"Local\tpfan-bar", out bool first);
        if (!first) return 0;
        return Bar.Run();
    }
}
