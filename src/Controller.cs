using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

/// One fan step: the EC value for register 0x2F and the temperatures that move into / out of it.
/// "80" = BIOS auto (the EC's own fine-grained curve), "0".."7" = manual levels, "40" = full speed.
sealed class Step {
    public string Ec { get; set; }
    public double Up { get; set; }        // enter this step when smoothed Tctl >= Up
    public double Down { get; set; }      // drop one step when smoothed Tctl < Down for DownHoldSec
    public string Note { get; set; }
    internal byte Value => Convert.ToByte(Ec, 16);
}

sealed class Config {
    public double PollMs { get; set; } = 500;
    public double SmoothSec { get; set; } = 6;       // EMA time constant (filters boost spikes)
    public double UpDwellSec { get; set; } = 4;      // min time between two up steps
    public double DownHoldSec { get; set; } = 25;    // must stay below Down this long before stepping down
    public double EmergencyTemp { get; set; } = 95;  // raw Tctl >= this for EmergencySec -> top step (capped at 97)
    public double EmergencySec { get; set; } = 5;
    public double VerifySec { get; set; } = 5;       // how often 0x2F is read back (every EC access risks a collision)
    public double QuietSec { get; set; } = 30;       // no EC access this long after display on / resume
    public List<Step> Steps { get; set; } = new List<Step>();
    public string Mode { get; set; }                 // the CPU mode in force (tpfan-bar switches it)
    public List<CpuMode> Modes { get; set; } = new List<CpuMode>();
    public double ReapplySec { get; set; } = 30;     // SMU limits are reset by the firmware, so send them again

    public CpuMode CurrentMode => Modes.Find(m => string.Equals(m.Name, Mode, StringComparison.OrdinalIgnoreCase));

    /// The steps with the current mode's FanUp/FanDown on the top step.
    public List<Step> EffectiveSteps() {
        var steps = Steps.ConvertAll(s => new Step { Ec = s.Ec, Up = s.Up, Down = s.Down, Note = s.Note });
        if (CurrentMode?.FanUp is double up) { steps[^1].Up = up; steps[^1].Down = CurrentMode.FanDown.Value; }
        return steps;
    }

    public static Config Load(string path) {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), opts);
        if (c.Steps.Count < 2) throw new InvalidDataException("need at least 2 steps");
        foreach (var s in c.Steps) _ = s.Value;
        foreach (var m in c.Modes) m.Validate();
        if (c.Modes.Count > 0 && c.CurrentMode == null) throw new InvalidDataException($"Mode \"{c.Mode}\" is not one of the Modes");
        var steps = c.EffectiveSteps();
        for (int i = 1; i < steps.Count; i++)
            if (steps[i].Up <= steps[i - 1].Up || steps[i].Down >= steps[i].Up)
                throw new InvalidDataException($"step {i}: Up must increase and Down must be below Up");
        if (c.PollMs < 100 || c.SmoothSec <= 0 || c.ReapplySec < 5) throw new InvalidDataException("PollMs >= 100, SmoothSec > 0, ReapplySec >= 5");
        return c;
    }
}

sealed class Controller {
    readonly string _cfgPath, _dir;
    Config _cfg; DateTime _cfgTime;
    int _idx = -1;
    double _ema = double.NaN;
    DateTime _last = DateTime.UtcNow, _lastStatus = DateTime.MinValue, _lastUp = DateTime.MinValue;
    DateTime _belowSince = DateTime.MaxValue, _hotSince = DateTime.MaxValue;
    volatile bool _stop;

    // EC safety (see Ec in Hw.cs): every EC transaction can collide with Windows' EC driver, so write
    // only on a real step change, verify rarely, and leave the EC alone while it is busiest (wake-up).
    byte? _applied;                       // last value written to 0x2F (null = unknown, write on next cycle)
    byte _odd; int _oddCount;             // unexpected 0x2F value and how many checks in a row saw it
    DateTime _lastVerify = DateTime.MinValue, _lastReassert = DateTime.MinValue, _lastRpm = DateTime.MinValue;
    int _rpm = -1;
    long _quietUntil;                     // UTC ticks; no EC access before this
    volatile bool _off, _handback;        // display off / suspended; hand the fan back to the BIOS once

    void Quiet(double secs) => Interlocked.Exchange(ref _quietUntil, Math.Max(Interlocked.Read(ref _quietUntil), DateTime.UtcNow.AddSeconds(secs).Ticks));
    void OnPower(bool off, string what) {
        if (off) { if (!_off) Log($"{what}: fan to BIOS auto, EC paused"); _off = true; _handback = true; }
        else { if (_off) Log($"{what}: EC resumes in {_cfg.QuietSec:F0} s"); _off = false; Quiet(_cfg.QuietSec); Kick(3); }
    }

    // CPU mode (SMU limits + Windows settings). The firmware resets the SMU limits on wake, on a power-cable change
    // and when Lenovo's thermal service decides to, so they are sent again soon after those and every ReapplySec.
    Smu _smu;
    string _modeKey, _modeApplied, _smuError;
    List<Step> _steps;
    long _smuDue;                         // UTC ticks; send the SMU limits again at this time
    void Kick(double secs) {
        long t = DateTime.UtcNow.AddSeconds(secs).Ticks;
        long cur; do { cur = Interlocked.Read(ref _smuDue); if (cur <= t) return; } while (Interlocked.CompareExchange(ref _smuDue, t, cur) != cur);
    }

    void ApplyMode(DateTime now) {
        var mode = _cfg.CurrentMode;
        if (mode == null) { _modeApplied = null; return; }
        string key = JsonSerializer.Serialize(mode);
        bool changed = key != _modeKey;
        if (!changed && now.Ticks < Interlocked.Read(ref _smuDue)) return;
        Interlocked.Exchange(ref _smuDue, now.AddSeconds(_cfg.ReapplySec).Ticks);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long winMs = 0;
        if (changed) {
            _modeKey = key;
            try { WinPower.Apply(mode); } catch (Exception e) { Log($"mode {mode.Name}: Windows settings failed: {e.Message}"); }
            winMs = sw.ElapsedMilliseconds;
        }
        string err = _smu == null ? "SMU not available" : null;
        if (_smu != null)
            foreach (var (msg, val, name) in mode.SmuMessages()) {
                try { uint r = _smu.Send(msg, val); if (r != Smu.Ok) err = $"{name} answer 0x{r:X2}"; }
                catch (Exception e) when (e is TimeoutException || e is IOException) { err = $"{name}: {e.Message}"; }
            }
        long smuMs = sw.ElapsedMilliseconds - winMs;
        if (changed || err != _smuError || smuMs > 1000)
            Log((err == null ? $"mode {mode.Name} applied" : $"mode {mode.Name}: {err}") + $" (Windows {winMs} ms, SMU {smuMs} ms)");
        _smuError = err;
        _modeApplied = mode.Name;
    }

    public Controller(string cfgPath, string dir) { _cfgPath = cfgPath; _dir = dir; Directory.CreateDirectory(dir); }
    public void Stop() => _stop = true;

    void Log(string s) {
        try {
            var p = Path.Combine(_dir, "tpfan.log");
            if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) File.Move(p, p + ".1", true);
            File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}\n");
        } catch { }
    }

    void ReloadIfChanged() {
        var t = File.GetLastWriteTimeUtc(_cfgPath);
        if (t == _cfgTime) return;
        _cfgTime = t;
        try { _cfg = Config.Load(_cfgPath); Log($"config loaded: {_cfg.Steps.Count} steps, mode {_cfg.CurrentMode?.Name ?? "none"}"); }
        catch (Exception e) { Log("config error, keeping previous: " + e.Message); if (_cfg == null) throw; }
        _steps = _cfg.EffectiveSteps();
        if (_idx >= _steps.Count) _idx = _steps.Count - 1;
    }

    public void Run() {
        using var ec = new Ec();
        using var amd = new AmdTemp();
        try { _smu = new Smu(); } catch (Exception e) { Log("SMU not available, modes only set Windows and the fan: " + e.Message); }
        ReloadIfChanged();
        Log("start");
        PowerWatch.Start(OnPower, () => Kick(2));
        int errors = 0;
        try {
            while (!_stop) {
                Thread.Sleep((int)_cfg.PollMs);
                try { if (Cycle(ec, amd)) errors = 0; }
                catch (Exception e) when (e is TimeoutException || e is IOException) {
                    errors++;
                    if (errors <= 3 || errors % 100 == 0) Log($"EC/sensor error #{errors}: {e.Message}");
                    _applied = null;   // a write may have been cut short: rewrite once the EC is calm
                    Quiet(5);          // back off instead of hammering a busy EC
                }
            }
        } finally {
            try { ec.Write(Tp.FanCtl, 0x80); } catch { }
            _smu?.Dispose();
            Log("stop: fan handed back to BIOS (0x80)");
        }
    }

    /// Returns true when it talked to the EC successfully (false while paused).
    bool Cycle(Ec ec, AmdTemp amd) {
        var now = DateTime.UtcNow;
        double dt = (now - _last).TotalSeconds; _last = now;
        ReloadIfChanged();
        var steps = _steps;

        if (dt >= 5) { Quiet(_cfg.QuietSec); Kick(3); Log($"{dt:F0} s gap (sleep?): EC resumes in {_cfg.QuietSec:F0} s"); }
        ApplyMode(now);   // SMU and Windows only, no EC: also while the EC is paused
        if (_off) {
            if (_handback) {
                _handback = false;
                if (_applied != 0x80) { ec.Write(Tp.FanCtl, 0x80); _applied = 0x80; }
            }
            _idx = -1; _ema = double.NaN; Status(now, TctlOrNaN(amd), "paused (display off)"); return false;
        }
        if (now.Ticks < Interlocked.Read(ref _quietUntil)) { _idx = -1; _ema = double.NaN; Status(now, TctlOrNaN(amd), "paused (EC quiet)"); return false; }

        double t = amd.Tctl();
        if (t < 20 || t > 115) {   // implausible reading: let the BIOS drive the fan
            if (_applied != 0x80) { ec.Write(Tp.FanCtl, 0x80); _applied = 0x80; Log($"bad tctl {t:F1}, BIOS auto"); }
            _idx = -1; return true;
        }
        // after a long gap (sleep/resume) restart the average from the current reading
        _ema = double.IsNaN(_ema) || dt >= 5 ? t : _ema + (t - _ema) * (1 - Math.Exp(-dt / _cfg.SmoothSec));
        if (_idx < 0) _idx = Math.Max(0, steps.FindLastIndex(s => s.Up <= _ema));

        if (t >= Math.Min(_cfg.EmergencyTemp, 97)) { if (_hotSince == DateTime.MaxValue) _hotSince = now; }
        else _hotSince = DateTime.MaxValue;

        string why = null;
        if (_hotSince != DateTime.MaxValue && (now - _hotSince).TotalSeconds >= _cfg.EmergencySec && _idx < steps.Count - 1) {
            _idx = steps.Count - 1; _lastUp = now; why = "emergency";
        } else if (_idx + 1 < steps.Count && _ema >= steps[_idx + 1].Up && (now - _lastUp).TotalSeconds >= _cfg.UpDwellSec) {
            _idx++; _lastUp = now; why = "up";
        }

        if (why != null || _idx == 0 || _ema >= steps[_idx].Down) _belowSince = DateTime.MaxValue;
        else {
            if (_belowSince == DateTime.MaxValue) _belowSince = now;
            if ((now - _belowSince).TotalSeconds >= _cfg.DownHoldSec) { _idx--; why = "down"; _belowSince = DateTime.MaxValue; }
        }

        byte want = steps[_idx].Value;
        if (_applied != want) {
            ec.Write(Tp.FanCtl, want); _applied = want; _lastVerify = now; _oddCount = 0;
            if (why == null) why = "apply";
        } else if ((now - _lastVerify).TotalSeconds >= _cfg.VerifySec) {
            _lastVerify = now;
            byte cur = ec.ReadStable(Tp.FanCtl);
            if (cur == want) _oddCount = 0;
            else {
                // The BIOS does reset 0x2F sometimes, but junk from a collision looks the same in one read.
                // Act only on a value seen on 3 checks in a row (>= 10 s), and at most once a minute.
                _oddCount = cur == _odd ? _oddCount + 1 : 1; _odd = cur;
                if (_oddCount >= 3 && (now - _lastReassert).TotalSeconds >= 60) {
                    ec.Write(Tp.FanCtl, want); _lastReassert = now; _oddCount = 0;
                    if (why == null) why = $"reassert (EC had 0x{cur:X2})";
                }
            }
        }
        if (why != null) Log($"{why,-9} step={_idx} ec=0x{want:X2} tctl={t:F1} ema={_ema:F1}");

        if ((now - _lastRpm).TotalSeconds >= _cfg.VerifySec) { _lastRpm = now; _rpm = Tp.Rpm(ec); }
        Status(now, t, $"step={_idx}/{steps.Count - 1} ({steps[_idx].Note}) ec=0x{want:X2} rpm={_rpm}");
        return true;
    }

    // Tctl comes from the CPU (SMN), not the EC, so it is still read while EC access is paused: status.txt
    // keeps a temperature for readers such as tpfan-bar. Errors only cost the reading.
    static double TctlOrNaN(AmdTemp amd) { try { return amd.Tctl(); } catch { return double.NaN; } }

    void Status(DateTime now, double t, string s) {
        if ((now - _lastStatus).TotalSeconds < 1) return;
        _lastStatus = now;
        string mode = _modeApplied == null ? "" : $" mode={_modeApplied}" + (_smuError != null ? " smu=error" : "");
        try { File.WriteAllText(Path.Combine(_dir, "status.txt"), $"{DateTime.Now:HH:mm:ss} tctl={t:F1} avg={_ema:F1} {s}{mode}\n"); } catch { }
    }
}
