using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Native;

/// The strip on the taskbar: a layered child window of Shell_TrayWnd, just left of the tray. Being a child of the
/// taskbar, it hides with it (full-screen apps, auto-hide) and never floats on top of other windows. On the classic
/// taskbar (StartAllBack) the task buttons (MSTaskSwWClass) are narrowed by the strip's width, so nothing is under it.
/// A hidden top-level "host" window owns the timers and recreates the strip when Explorer restarts.
sealed unsafe class Bar {
    static Bar _it;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string HostClass = "tpfanBarHost", StripClass = "tpfanBarStrip";
    const uint TimerLayout = 1, TimerData = 2;

    enum Kind { Mode, Cpu, Disk, Net, Ram, Power, Temp, Battery, Rate, Fan }
    /// Gap = space to the next item. Mode items are one per mode (a 3-way switch); Mode holds its name.
    sealed class Item { public Kind Kind; public int X, W, Gap; public string Mode, Tip = ""; }
    string _modeNames = "";   // the modes the switch was laid out for

    readonly Sensors _s = new Sensors();
    readonly Modes _modes = new Modes();
    readonly List<Item> _items = new List<Item>();
    IntPtr _host, _strip, _tip, _taskbar, _taskList;
    uint _taskbarCreated;
    int _dpi, _scale = 1, _w, _h, _hover = -1, _posX = int.MinValue, _posY, _cores;
    bool _light, _tracking, _shrunk;
    Canvas _canvas;
    IntPtr _memDc, _dib, _bits;

    public static int Run() {
        _it = new Bar();
        return _it.Loop();
    }

    int Loop() {
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = 0x8 /* ICC_TAB_CLASSES: tooltips */ };
        InitCommonControlsEx(icc);
        IntPtr inst = GetModuleHandle(null);
        Register(HostClass, inst, (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&HostProc);
        Register(StripClass, inst, (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&StripProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _host = CreateWindowEx(WS_EX_TOOLWINDOW, HostClass, "tpfan-bar", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetTimer(_host, (UIntPtr)TimerLayout, 250, IntPtr.Zero);
        SetTimer(_host, (UIntPtr)TimerData, 1000, IntPtr.Zero);
        EnsureStrip();
        Data();
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(m); DispatchMessage(m); }
        RestoreTaskList();
        return 0;
    }

    static void Register(string name, IntPtr inst, IntPtr proc) {
        fixed (char* n = name) {
            var wc = new WNDCLASSEXW {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = proc, hInstance = inst,
                hCursor = LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */), lpszClassName = (IntPtr)n,
            };
            RegisterClassEx(wc);
        }
    }

    // ---- window procedures --------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    static IntPtr HostProc(IntPtr h, uint msg, IntPtr w, IntPtr l) {
        try {
            if (msg == WM_TIMER) { if ((uint)w == TimerLayout) _it.Layout(); else _it.Data(); return IntPtr.Zero; }
            if (msg == _it?._taskbarCreated && msg != 0) { _it.Recreate(); return IntPtr.Zero; }
            if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
        } catch (Exception e) { Log(e); }
        return DefWindowProc(h, msg, w, l);
    }

    [UnmanagedCallersOnly]
    static IntPtr StripProc(IntPtr h, uint msg, IntPtr w, IntPtr l) {
        try {
            switch (msg) {
                case WM_MOUSEACTIVATE: return (IntPtr)MA_NOACTIVATE;
                case WM_MOUSEMOVE: _it.Hover(_it.Hit(LoWord(l))); return IntPtr.Zero;
                case WM_MOUSELEAVE: _it._tracking = false; _it.Hover(-1); return IntPtr.Zero;
                case WM_LBUTTONUP: _it.Click(_it.Hit(LoWord(l))); return IntPtr.Zero;
                case WM_RBUTTONUP: _it.Menu(); return IntPtr.Zero;
                case WM_DESTROY: if (_it._strip == h) _it._strip = IntPtr.Zero; break;
            }
        } catch (Exception e) { Log(e); }
        return DefWindowProc(h, msg, w, l);
    }

    // ---- strip lifetime and position -----------------------------------------------------------------------

    void EnsureStrip() {
        IntPtr tb = FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero) return;
        if (_strip != IntPtr.Zero && IsWindow(_strip) && GetParent(_strip) == tb) return;
        Recreate();
    }

    void Recreate() {
        if (_strip != IntPtr.Zero && IsWindow(_strip)) DestroyWindow(_strip);
        if (_tip != IntPtr.Zero) { DestroyWindow(_tip); _tip = IntPtr.Zero; }
        _strip = IntPtr.Zero; _posX = int.MinValue; _dpi = 0; _shrunk = false;
        _taskbar = FindWindow("Shell_TrayWnd", null);
        if (_taskbar == IntPtr.Zero) return;
        _strip = CreateWindowEx(WS_EX_LAYERED, StripClass, "tpfan-bar", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                                0, 0, 1, 1, _taskbar, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_strip == IntPtr.Zero) { Log(new Exception($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}")); return; }
        _tip = CreateWindowEx(WS_EX_TOPMOST, "tooltips_class32", null, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                              0, 0, 0, 0, _host, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        SendMessage(_tip, TTM_SETMAXTIPWIDTH, IntPtr.Zero, (IntPtr)600);
        Layout();
        Draw();
    }

    /// Every 250 ms: keep the strip left of the tray, sized to the taskbar, and the task buttons out of its way.
    void Layout() {
        EnsureStrip();
        if (_strip == IntPtr.Zero) return;
        IntPtr tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(_taskbar, out var tb) || !GetWindowRect(tray, out var tr)) return;
        if (tb.Height > tb.Width) return;   // vertical taskbar: not supported

        int dpi = (int)GetDpiForWindow(_taskbar); if (dpi == 0) dpi = 96;
        if (dpi != _dpi || tr.Height != _h || _s.CoreLoad.Length != _cores) { _dpi = dpi; _h = tr.Height; Measure(); Draw(); }

        int gap = 2 * _scale, x = tr.Left - _w - gap;
        var p = new POINT { X = x, Y = tr.Top };
        ScreenToClient(_taskbar, ref p);
        if (p.X != _posX || p.Y != _posY) {
            SetWindowPos(_strip, HWND_TOP, p.X, p.Y, _w, _h, SWP_NOACTIVATE);
            _posX = p.X; _posY = p.Y;
        }
        ShrinkTaskList(x - gap);
    }

    void ShrinkTaskList(int limit) {
        IntPtr rebar = FindWindowEx(_taskbar, IntPtr.Zero, "ReBarWindow32", null);
        IntPtr task = rebar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (task == IntPtr.Zero || !GetWindowRect(task, out var ts) || ts.Right <= limit || limit <= ts.Left) return;
        var q = new POINT { X = ts.Left, Y = ts.Top };
        ScreenToClient(rebar, ref q);
        MoveWindow(task, q.X, q.Y, limit - ts.Left, ts.Height, true);
        _taskList = task; _shrunk = true;
    }

    /// On exit: give the task buttons their full width back (Explorer would also do it at its next layout).
    void RestoreTaskList() {
        if (!_shrunk || !IsWindow(_taskList)) return;
        IntPtr rebar = GetParent(_taskList);
        if (!GetWindowRect(rebar, out var rb) || !GetWindowRect(_taskList, out var ts)) return;
        var q = new POINT { X = ts.Left, Y = ts.Top };
        ScreenToClient(rebar, ref q);
        MoveWindow(_taskList, q.X, q.Y, rb.Right - ts.Left, ts.Height, true);
    }

    // ---- layout of the items ---------------------------------------------------------------------------------

    int S(int v) => v * _scale;
    int BigY => (_h - S(PixelFont.Big.Height)) / 2;
    int Dia => Math.Min(S(18), _h - S(6));
    int NumBox(string widest) => S(PixelFont.Big.Width(widest));
    int NetText => S(Math.Max(PixelFont.Small.Width("99.9 Kbps"), PixelFont.Small.Width("999 Kbps")));

    void Measure() {
        _scale = Math.Max(1, (int)Math.Round(_dpi / 96.0));
        _cores = Math.Max(1, _s.CoreLoad.Length == 0 ? 6 : _s.CoreLoad.Length);
        int unitGap = S(2), small = S(PixelFont.Small.Width("W"));
        var widths = new (Kind, int)[] {
            (Kind.Cpu, _cores * S(3) + (_cores - 1) * S(1)),
            (Kind.Disk, Dia),
            (Kind.Net, S(5) + S(3) + NetText),
            (Kind.Ram, Dia),
            (Kind.Power, NumBox("8.8") + unitGap + small),
            (Kind.Temp, NumBox("100") + unitGap + S(PixelFont.Small.Width("o"))),
            (Kind.Battery, NumBox("100") + unitGap + S(PixelFont.Small.Width("%"))),
            (Kind.Rate, 2 * S(3) + 1),   // an arrow, odd width so it has a centre column
            (Kind.Fan, NumBox("8888") + unitGap + S(PixelFont.Small.Width("rpm"))),
        };
        _items.Clear();
        int x = S(6);
        // The mode switch: one letter per mode in tpfan's config, close together.
        var names = _modes.All.Count > 0 ? _modes.All.Select(m => m.Name).ToList() : new List<string> { null };
        _modeNames = string.Join(",", names);
        for (int i = 0; i < names.Count; i++) {
            int w = S(PixelFont.Big.Width(Letter(names[i])));
            int gap = i == names.Count - 1 ? S(10) : S(6);
            _items.Add(new Item { Kind = Kind.Mode, Mode = names[i], X = x, W = w, Gap = gap });
            x += w + gap;
        }
        foreach (var (k, w) in widths) { _items.Add(new Item { Kind = k, X = x, W = w, Gap = S(10) }); x += w + S(10); }
        _w = x - S(10) + S(6);
        if (_canvas == null || _canvas.W != _w || _canvas.H != _h) {
            _canvas = new Canvas(_w, _h);
            if (_memDc != IntPtr.Zero) { DeleteObject(_dib); DeleteDC(_memDc); }
            _memDc = CreateCompatibleDC(IntPtr.Zero);
            var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32 };
            _dib = CreateDIBSection(_memDc, bmi, 0, out _bits, IntPtr.Zero, 0);
            SelectObject(_memDc, _dib);
        }
        SyncTools(true);
    }

    /// An item's mouse area: half the gap on each side (the strip's margin at the ends), so neighbours never overlap.
    (int Left, int Right) Area(int i) {
        var it = _items[i];
        int left = i == 0 ? 0 : it.X - _items[i - 1].Gap / 2;
        int right = i == _items.Count - 1 ? _w : it.X + it.W + (it.Gap + 1) / 2;
        return (left, right);
    }

    int Hit(int x) {
        for (int i = 0; i < _items.Count; i++) { var (a, b) = Area(i); if (x >= a && x < b) return i; }
        return -1;
    }

    static string Letter(string mode) =>
        mode != null && mode.Length > 0 && PixelFont.Big.Has(char.ToUpperInvariant(mode[0])) && char.IsLetter(mode[0]) ? char.ToUpperInvariant(mode[0]).ToString() : "?";

    // ---- data and drawing ------------------------------------------------------------------------------------

    void Data() {
        _s.Read();
        _modes.Refresh();
        _light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1;
        if (_strip == IntPtr.Zero) return;
        string names = _modes.All.Count > 0 ? string.Join(",", _modes.All.Select(m => m.Name)) : "";
        if ((_s.CoreLoad.Length != 0 && _s.CoreLoad.Length != _cores) || names != _modeNames) Measure();
        Tooltips();
        Draw();
    }

    void Draw() {
        if (_strip == IntPtr.Zero || _canvas == null) return;
        var c = _canvas; bool l = _light;
        c.Clear();
        if (_hover >= 0 && _hover < _items.Count) {
            var hi = _items[_hover];
            int pad = Math.Min(S(4), hi.Gap / 2);
            c.Fill(hi.X - pad, S(2), hi.W + 2 * pad, _h - S(4), Palette.Hover(l));
        }
        foreach (var it in _items) {
            int x = it.X;
            switch (it.Kind) {
                case Kind.Mode: {
                    // lit: the mode tpfan has applied; grey: the one asked for, still being applied; dim: the others
                    string applied = _s.ModeApplied, requested = _modes.Requested;
                    var color = it.Mode != null && it.Mode == applied && applied == requested ? Palette.Mode(it.Mode, l)
                              : it.Mode != null && it.Mode == requested ? Palette.Grey(l)
                              : Palette.Dim(l);
                    c.Text(PixelFont.Big, Letter(it.Mode), x, BigY, _scale, color);
                    break;
                }
                case Kind.Cpu: {
                    int bh = Math.Min(S(18), _h - S(6));
                    var loads = _s.CoreLoad.Length == _cores ? _s.CoreLoad : new double[_cores];
                    c.Bars(loads, x, (_h - bh) / 2, S(3), S(1), bh, Palette.CpuBars(l), Palette.Track(l));
                    break;
                }
                case Kind.Disk: c.Pie(_s.DiskActive, x, (_h - Dia) / 2, Dia, S(1), Palette.Disk(l), Palette.Disk(l)); break;
                case Kind.Ram: c.Pie(_s.RamPercent, x, (_h - Dia) / 2, Dia, S(1), Palette.Ram(l), Palette.Ram(l)); break;
                case Kind.Net: {
                    int top = (_h - S(16)) / 2, tx = x + S(8);
                    c.Text(PixelFont.Small, "^", x, top, _scale, Palette.Send(l));
                    c.Text(PixelFont.Small, "v", x, top + S(8), _scale, Palette.Recv(l));
                    string up = NetBar(_s.SendBps), down = NetBar(_s.RecvBps);
                    c.Text(PixelFont.Small, up, tx + NetText - S(PixelFont.Small.Width(up)), top, _scale, Palette.Send(l));
                    c.Text(PixelFont.Small, down, tx + NetText - S(PixelFont.Small.Width(down)), top + S(8), _scale, Palette.Recv(l));
                    break;
                }
                case Kind.Power: Number(x, NumBox("8.8"), double.IsNaN(_s.CpuW) ? "--" : Num(_s.CpuW), "W", Palette.Cpu(l)); break;
                case Kind.Temp:
                    Number(x, NumBox("100"), double.IsNaN(_s.Tctl) ? "--" : Math.Round(_s.Tctl).ToString("0", Inv), "o",
                           double.IsNaN(_s.Tctl) ? Palette.Grey(l) : _s.Tctl >= 95 ? Palette.Red(l) : _s.Tctl >= 85 ? Palette.Yellow(l) : Palette.Text(l));
                    break;
                case Kind.Rate: {
                    // up (green) while charging, down while discharging (amber, red from 25 W); length = watts,
                    // full height at 45 W charging / 35 W discharging; a grey dash when plugged in and idle
                    double w = Math.Abs(_s.RateW);
                    int aw = it.W, ah = Math.Min(S(18), _h - S(6)), ay = (_h - ah) / 2;
                    bool idle = double.IsNaN(w) || w < 0.05 || (!_s.Charging && !_s.Discharging);
                    if (idle) { c.Fill(x, ay + ah / 2 - _scale / 2, aw, _scale, Palette.Grey(l)); break; }
                    int sh = Math.Max(1, aw / 6);
                    c.Fill(x + aw / 2 - sh, ay, 2 * sh + 1, ah, Palette.Track(l));   // full-scale track behind the arrow
                    double full = _s.Charging ? 45 : 35;
                    int len = Math.Max(S(5), (int)Math.Round(Math.Min(w / full, 1) * ah));
                    var color = _s.Charging ? Palette.Green(l) : w >= 25 ? Palette.Red(l) : Palette.Amber(l);
                    c.Arrow(x, ay, aw, ah, _s.Charging, len, color);
                    break;
                }
                case Kind.Battery: {
                    int p = _s.Percent;
                    Number(x, NumBox("100"), p < 0 ? "--" : p.ToString(Inv), "%",
                           p < 0 ? Palette.Grey(l) : p <= 15 && !_s.Ac ? Palette.Red(l) : p <= 30 && !_s.Ac ? Palette.Amber(l) : Palette.Text(l));
                    break;
                }
                case Kind.Fan:
                    // white: BIOS curve; yellow: tpfan holds the 4200 rpm step; grey: tpfan paused (last reading)
                    Number(x, NumBox("8888"), _s.Rpm < 0 ? "--" : _s.Rpm.ToString(Inv), "rpm",
                           _s.Rpm < 0 || _s.FanPaused ? Palette.Grey(l) : _s.FanForced ? Palette.Yellow(l) : Palette.Text(l));
                    break;
            }
        }
        Present();
    }

    /// A big number right-aligned in its box, then a small unit on the same baseline.
    void Number(int x, int box, string text, string unit, Rgba color) {
        int w = S(PixelFont.Big.Width(text));
        _canvas.Text(PixelFont.Big, text, x + box - w, BigY, _scale, color);
        int uy = unit == "o" ? BigY : BigY + S(PixelFont.Big.Height - 7);   // small font baseline = its row 6
        _canvas.Text(PixelFont.Small, unit, x + box + S(2), uy, _scale, color);
    }

    void Present() {
        fixed (uint* src = _canvas.Px) Buffer.MemoryCopy(src, (void*)_bits, (long)_w * _h * 4, (long)_w * _h * 4);
        var size = new SIZE { cx = _w, cy = _h };
        var zero = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(_strip, IntPtr.Zero, IntPtr.Zero, size, _memDc, zero, 0, blend, ULW_ALPHA);
    }

    // ---- mouse ---------------------------------------------------------------------------------------------

    void Hover(int i) {
        if (!_tracking && i >= 0) {
            var t = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = _strip };
            _tracking = TrackMouseEvent(ref t);
        }
        if (i == _hover) return;
        _hover = i; Draw();
    }

    void Click(int i) {
        if (i < 0) return;
        if (_items[i].Kind == Kind.Mode) {
            _modes.Refresh();
            string mode = _items[i].Mode;
            if (mode != null && mode != _modes.Requested && _modes.Request(mode)) { Tooltips(); Draw(); }
        } else if (_items[i].Kind is Kind.Cpu or Kind.Disk or Kind.Net or Kind.Ram) {
            ShellExecute(IntPtr.Zero, "open", "taskmgr.exe", null, null, 1);
        } else if (_items[i].Kind == Kind.Fan) {
            ShellExecute(IntPtr.Zero, "open", Path.Combine(Sensors.TpfanDir, "tpfan.log"), null, null, 1);
        }
    }

    void Menu() {
        IntPtr m = CreatePopupMenu();
        AppendMenu(m, MF_STRING, (UIntPtr)1, "Task Manager");
        AppendMenu(m, MF_STRING, (UIntPtr)2, "tpfan log");
        AppendMenu(m, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(m, MF_STRING, (UIntPtr)3, "Exit");
        GetCursorPos(out var p);
        SetForegroundWindow(_host);
        int cmd = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.X, p.Y, 0, _host, IntPtr.Zero);
        DestroyMenu(m);
        if (cmd == 1) ShellExecute(IntPtr.Zero, "open", "taskmgr.exe", null, null, 1);
        else if (cmd == 2) ShellExecute(IntPtr.Zero, "open", Path.Combine(Sensors.TpfanDir, "tpfan.log"), null, null, 1);
        else if (cmd == 3) { RestoreTaskList(); if (_strip != IntPtr.Zero) DestroyWindow(_strip); DestroyWindow(_host); }
    }

    // ---- tooltips ------------------------------------------------------------------------------------------

    void Tooltips() {
        foreach (var it in _items) it.Tip = TipFor(it);
        SyncTools(false);
    }

    /// Adds the tools (one per item) when the layout changes, else only updates their text.
    void SyncTools(bool rects) {
        if (_tip == IntPtr.Zero || _strip == IntPtr.Zero) return;
        for (int i = 0; i < _items.Count; i++) {
            var it = _items[i];
            IntPtr text = Marshal.StringToHGlobalUni(it.Tip.Length == 0 ? " " : it.Tip);
            try {
                var ti = new TOOLINFOW {
                    cbSize = (uint)sizeof(TOOLINFOW), uFlags = TTF_SUBCLASS, hwnd = _strip, uId = (IntPtr)(i + 1),
                    rect = new RECT { Left = Area(i).Left, Top = 0, Right = Area(i).Right, Bottom = _h }, lpszText = text,
                };
                if (rects) { SendMessage(_tip, TTM_DELTOOLW, IntPtr.Zero, (IntPtr)(&ti)); SendMessage(_tip, TTM_ADDTOOLW, IntPtr.Zero, (IntPtr)(&ti)); }
                else SendMessage(_tip, TTM_UPDATETIPTEXTW, IntPtr.Zero, (IntPtr)(&ti));
            } finally { Marshal.FreeHGlobal(text); }
        }
    }

    string TipFor(Item item) {
        switch (item.Kind) {
            case Kind.Mode: {
                if (item.Mode == null) return "No CPU modes in tpfan's config";
                string applied = _s.ModeApplied, requested = _modes.Requested;
                var summary = _modes.All.Find(m => m.Name == item.Mode).Summary;
                string state = item.Mode == applied && applied == requested ? "active"
                             : item.Mode == requested ? (applied == null ? "selected, but tpfan is not applying it" : "switching…")
                             : "click to switch";
                return $"{item.Mode} ({state})" + (summary != null ? "\n" + summary : "")
                       + (item.Mode == applied && _s.SmuError ? "\nSMU error: see the tpfan log" : "");
            }
            case Kind.Cpu:
                return $"CPU {Pct(_s.CpuTotal)}" + (_s.CoreLoad.Length == 0 ? "" : "\nCores " + string.Join(" ", _s.CoreLoad.Select(c => c.ToString("0", Inv))) + " %") + "\nClick: Task Manager";
            case Kind.Disk: return $"Disk active {Pct(_s.DiskActive)}\nRead {Bytes(_s.DiskRead)}, write {Bytes(_s.DiskWrite)}";
            case Kind.Net: return $"Network{(_s.NetNames.Length > 0 ? $" ({_s.NetNames})" : "")}\nSend {NetLong(_s.SendBps)}\nReceive {NetLong(_s.RecvBps)}";
            case Kind.Ram: return $"Memory {Pct(_s.RamPercent)} in use\n{Gb(_s.RamUsed)} of {Gb(_s.RamTotal)} GB\nCommitted {Gb(_s.CommitUsed)} of {Gb(_s.CommitLimit)} GB";
            case Kind.Power: return (double.IsNaN(_s.CpuW) ? "CPU power n/a" : $"CPU package power {_s.CpuW.ToString("0.0", Inv)} W") + "\n" + _s.Fan;
            case Kind.Temp: return (double.IsNaN(_s.Tctl) ? "CPU temperature n/a" : $"CPU temperature (Tctl) {_s.Tctl.ToString("0.0", Inv)} °C") + "\n" + _s.Fan;
            case Kind.Rate: case Kind.Battery: return BatteryTip();
            case Kind.Fan:
                return _s.Fan + (_s.FanPaused && _s.Rpm >= 0 ? $"\nLast reading {_s.Rpm} rpm" : "")
                       + (_s.FanForced && !_s.FanPaused ? "\ntpfan holds the 4200 rpm step" : _s.Rpm >= 0 && !_s.FanPaused ? "\nBIOS fan curve" : "")
                       + "\nClick: tpfan log";
        }
        return "";
    }

    string BatteryTip() {
        if (_s.Percent < 0) return "No battery";
        string t = $"Battery {_s.Percent} %";
        double w = Math.Abs(_s.RateW);
        if (_s.Charging && !double.IsNaN(w)) t += $"\nCharging {w.ToString("0.0", Inv)} W" + (double.IsNaN(_s.HoursToFull) ? "" : $", full in {Dur(_s.HoursToFull)}");
        else if (_s.Discharging && !double.IsNaN(w)) t += $"\nUsing {w.ToString("0.0", Inv)} W" + (double.IsNaN(_s.HoursLeft) ? "" : $", {Dur(_s.HoursLeft)} left");
        else t += _s.Ac ? "\nPlugged in, not charging" : "";
        return t;
    }

    // ---- formatting ----------------------------------------------------------------------------------------

    /// One decimal under 10, whole numbers above (capped at 99): "8.7", "19".
    static string Num(double v) => v < 9.95 ? v.ToString("0.0", Inv) : Math.Min(Math.Round(v, MidpointRounding.AwayFromZero), 99).ToString("0", Inv);
    static string Pct(double v) => double.IsNaN(v) ? "n/a" : v.ToString("0", Inv) + " %";
    static string Gb(double bytes) => (bytes / (1L << 30)).ToString("0.0", Inv);
    static string Bytes(double b) =>
        double.IsNaN(b) ? "n/a" : b < 1024 * 1024 ? $"{(b / 1024).ToString("0", Inv)} KB/s" : $"{(b / 1024 / 1024).ToString("0.0", Inv)} MB/s";

    /// Task Manager's network units: bits per second, decimal.
    static (double, string) NetUnit(double bps) => bps >= 999.5e6 ? (bps / 1e9, "Gbps") : bps >= 999.5e3 ? (bps / 1e6, "Mbps") : (bps / 1e3, "Kbps");
    static string NetLong(double bps) { if (double.IsNaN(bps)) return "n/a"; var (v, u) = NetUnit(bps); return $"{v.ToString(v < 100 ? "0.0" : "0", Inv)} {u}"; }
    static string NetBar(double bps) => double.IsNaN(bps) ? "--" : NetLong(bps);

    static string Dur(double h) { int m = (int)Math.Round(h * 60); return m >= 60 ? $"{m / 60} h {m % 60:00} min" : $"{m} min"; }

    static void Log(Exception e) {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "tpfan-bar.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}\n"); } catch { }
    }
}
