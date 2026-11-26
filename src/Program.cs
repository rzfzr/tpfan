using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

static class Program {
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "tpfan");
    static string CfgPath => Path.Combine(DataDir, "config.json");

    static int Main(string[] a) {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        string cmd = a.Length > 0 ? a[0] : "status";
        try {
            switch (cmd) {
                case "service":   // started by the SCM
                    System.ServiceProcess.ServiceBase.Run(new FanService(new Controller(CfgPath, DataDir)));
                    return 0;
                case "run": {   // foreground; Ctrl+C hands the fan back to the BIOS
                    var c = new Controller(a.Length > 1 ? a[1] : CfgPath, DataDir);
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; c.Stop(); };
                    c.Run(); return 0;
                }
                case "status": Console.Write(File.ReadAllText(Path.Combine(DataDir, "status.txt"))); return 0;
                case "check": Config.Load(a.Length > 1 ? a[1] : CfgPath); Console.WriteLine("config ok"); return 0;
                case "dump": return Dump();
                case "monitor": return Monitor(a.Length > 1 ? int.Parse(a[1]) : 10);
                case "set": return Set(a[1]);
                case "probe": return Probe(a.Length > 1 ? a[1] : "probe.log");
                case "ec-sniff": return Sniff(a.Length > 1 ? int.Parse(a[1]) : 60);
                case "smu": {   // limits in force, from the PM table
                    using var smu = new Smu();
                    var t = smu.Table(out ulong ver);
                    Console.WriteLine($"code name {smu.CodeName}, PM table 0x{ver:X}");
                    Console.WriteLine(Smu.Describe(t, ver));
                    return 0;
                }
                case "smu-raw": {   // the limit section of the PM table, for comparing what two tools set
                    using var smu = new Smu();
                    var t = smu.Table(out ulong ver);
                    Console.WriteLine($"PM table 0x{ver:X}");
                    foreach (int off in Enumerable.Range(0, 0x70 / 4).Select(i => i * 4).Concat(Enumerable.Range(0x900 / 4, 12).Select(i => i * 4)))
                        Console.Write($"{off:X3}={t[off / 4]:0.##} ");
                    Console.WriteLine();
                    return 0;
                }
                case "smu-set": {   // smu-set <message hex> <value>: one MP1 message, then the limits in force
                    using var smu = new Smu();
                    uint msg = Convert.ToUInt32(a[1], 16), val = uint.Parse(a[2]);
                    Console.WriteLine($"0x{msg:X2} {val} -> answer 0x{smu.Send(msg, val):X2}");
                    Thread.Sleep(300);
                    var t = smu.Table(out ulong ver);
                    Console.WriteLine(Smu.Describe(t, ver));
                    return 0;
                }
                default:
                    Console.Error.WriteLine("usage: tpfan status | run [config] | check [config] | dump | monitor [s] | set <hex|auto> | probe [log]");
                    return 2;
            }
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static byte[] DumpEc(Ec ec) { var b = new byte[256]; for (int i = 0; i < 256; i++) b[i] = ec.Read((byte)i); return b; }
    static string Hex(byte[] b) {
        var sb = new StringBuilder("      " + string.Join(" ", Enumerable.Range(0, 16).Select(i => $" {i:X}")) + "\n");
        for (int r = 0; r < 16; r++) sb.Append($"{r * 16:X2}:  " + string.Join(" ", b.Skip(r * 16).Take(16).Select(x => x.ToString("X2"))) + "\n");
        return sb.ToString();
    }
    static int Dump() { using var ec = new Ec(); Console.Write(Hex(DumpEc(ec))); return 0; }

    static string Line(Ec ec, AmdTemp t) {
        var temps = string.Join(",", Enumerable.Range(0x78, 8).Select(i => ec.Read((byte)i)));
        return $"{DateTime.Now:HH:mm:ss} tctl={t.Tctl(),5:F1} ec78={temps} 2F=0x{ec.Read(Tp.FanCtl):X2} rpm={Tp.Rpm(ec)}";
    }
    static int Monitor(int secs) {
        using var ec = new Ec(); using var t = new AmdTemp();
        for (int i = 0; i < secs; i++) { Console.WriteLine(Line(ec, t)); Thread.Sleep(1000); }
        return 0;
    }
    /// Watches the EC status register (stop the service first, so all traffic seen is someone else's) and groups
    /// its changes into transactions: bits 0x01 output full, 0x02 input full, 0x08 last write was a command,
    /// 0x10 burst, 0x20 SCI event pending (Windows will query it), 0x40 SMI event pending (firmware).
    static int Sniff(int secs) {
        using var ec = new Ec();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var changes = new List<(double t, byte s)>();
        byte prev = ec.Status(); long samples = 0;
        changes.Add((0, prev));
        while (sw.ElapsedMilliseconds < secs * 1000L) {
            byte s = ec.Status(); samples++;
            if (s != prev) { changes.Add((sw.Elapsed.TotalMilliseconds, s)); prev = s; }
        }
        Console.WriteLine($"{samples / (double)secs:F0} reads/s, {changes.Count - 1} changes in {secs} s");
        // a transaction = changes less than 20 ms apart
        var bursts = new List<List<(double t, byte s)>>();
        for (int i = 1; i < changes.Count; i++) {
            if (bursts.Count == 0 || changes[i].t - bursts[^1][^1].t > 20) bursts.Add(new List<(double, byte)>());
            bursts[^1].Add(changes[i]);
        }
        int sci = 0, smi = 0, other = 0;
        double lastSci = -1; var sciGaps = new List<double>();
        foreach (var b in bursts) {
            bool hasSci = b.Any(c => (c.s & 0x20) != 0), hasSmi = b.Any(c => (c.s & 0x40) != 0);
            if (hasSci) { sci++; if (lastSci >= 0) sciGaps.Add((b[0].t - lastSci) / 1000); lastSci = b[0].t; }
            else if (hasSmi) smi++; else other++;
            string kind = hasSci ? "SCI event (Windows query)" : hasSmi ? "SMI event (firmware)" : "no event flag (someone's read/write)";
            Console.WriteLine($"{b[0].t / 1000,8:F3} s  {b[^1].t - b[0].t,6:F2} ms  {kind,-38} {string.Join(">", b.Select(c => c.s.ToString("X2")))}");
        }
        Console.WriteLine($"transactions: {bursts.Count} ({sci} SCI events, {smi} SMI events, {other} without an event flag)");
        if (sciGaps.Count > 0) Console.WriteLine($"SCI events every {sciGaps.Average():F1} s on average (min {sciGaps.Min():F1}, max {sciGaps.Max():F1})");
        return 0;
    }

    static int Set(string v) {
        using var ec = new Ec();
        byte b = v == "auto" ? (byte)0x80 : Convert.ToByte(v, 16);
        ec.Write(Tp.FanCtl, b); Thread.Sleep(100);
        Console.WriteLine($"2F=0x{ec.Read(Tp.FanCtl):X2}"); return 0;
    }

    /// Steps through every fan level, records steady RPM and an EC dump for each, then measures
    /// BIOS auto mode under full CPU load. Stop any other fan controller first.
    static int Probe(string logPath) {
        using var ec = new Ec(); using var t = new AmdTemp();
        using var log = new StreamWriter(logPath) { AutoFlush = true };
        void L(string s) { Console.WriteLine(s); log.WriteLine(s); }
        L($"start 2F=0x{ec.Read(Tp.FanCtl):X2}");
        L("== auto baseline"); L(Hex(DumpEc(ec)));
        var levels = new byte[] { 0x40, 7, 6, 5, 4, 3, 2, 1, 2, 3, 4, 5, 6, 7, 0x40, 0 };
        var summary = new List<string>();
        try {
            foreach (var lv in levels) {
                ec.Write(Tp.FanCtl, lv);
                var rpms = new List<int>();
                int secs = lv == 0 ? 6 : 14;
                for (int s = 0; s < secs; s++) {
                    Thread.Sleep(1000);
                    double tc = t.Tctl();
                    int rpm = Tp.Rpm(ec);
                    if (s >= secs - 5) rpms.Add(rpm);
                    L($"  lv=0x{lv:X2} s={s,2} tctl={tc:F1} rpm={rpm} 2F=0x{ec.Read(Tp.FanCtl):X2}");
                    if (tc > 99) throw new Exception("too hot, aborting");
                }
                var row = $"level 0x{lv:X2}: rpm avg={rpms.Average():F0} min={rpms.Min()} max={rpms.Max()}";
                summary.Add(row); L(row);
                L($"== dump at level 0x{lv:X2}"); L(Hex(DumpEc(ec)));
            }
        } finally {
            ec.Write(Tp.FanCtl, 0x80);
            L($"restored auto, 2F=0x{ec.Read(Tp.FanCtl):X2}");
        }
        L("== SUMMARY"); foreach (var s in summary) L(s);

        L("== AUTO UNDER LOAD (180s load, then 90s idle)");
        ec.Write(Tp.FanCtl, 0x80);
        bool stop = false;
        var threads = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new Thread(() => { double x = 1; while (!Volatile.Read(ref stop)) x = Math.Sqrt(x + 1.5); }) { IsBackground = true }).ToList();
        threads.ForEach(th => th.Start());
        try {
            for (int s = 0; s < 270; s++) {
                if (s == 180) Volatile.Write(ref stop, true);
                Thread.Sleep(1000);
                double tc = t.Tctl();
                L($"  auto s={s,3} load={(s < 180 ? 1 : 0)} " + Line(ec, t));
                if (tc > 99) { Volatile.Write(ref stop, true); L("load stopped: tctl > 99"); }
            }
        } finally { Volatile.Write(ref stop, true); }
        L("== auto end dump"); L(Hex(DumpEc(ec)));
        return 0;
    }
}
