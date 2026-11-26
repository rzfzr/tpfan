# tpfan

Hybrid fan control for the **ThinkPad E14 Gen 5 (AMD, Ryzen 5 7530U)** on Windows, as a service.
It keeps the BIOS fan curve for normal use and adds the one speed above it the BIOS never uses.

Written to replace TPFanControl, whose custom curves can't be made smooth on this machine.

## Why

Measured on BIOS R2CET49W (1.31) by writing EC register `0x2F` and reading the fan RPM (`0x84/0x85`):

| `0x2F` | Fan |
|---|---|
| `0` | off |
| `1` | 2200 rpm |
| `2` | 2500 rpm |
| `3` … `7` | **4200 rpm** (all identical) |
| `0x40` | 6300 rpm (full, "disengaged") |
| `0x80` | BIOS auto: finer internal steps (~2100 / 3300 / 3600 / 3800 rpm) but never above ~3800 |

So any manual curve jumps 2500 → 4200 rpm, and the in-between speeds exist only inside BIOS auto. No EC
register below `0x100` exposes the auto targets, so they can't be set directly.

## How it controls

- **Normal range:** fan stays in BIOS auto (smooth, fine steps).
- **Above it:** when the 6-second average of Tctl reaches 85 °C, the fan goes to 4200 rpm (level 7). It returns
  to BIOS auto after 25 s below 78 °C.
- **Emergency:** raw Tctl ≥ 95 °C for 5 s jumps to the top step.
- **Long-load boost:** above 4200 rpm only in a mode with `FanBoost` (Maximum), and only while the CPU stays at its
  limit; see [Long-load boost](#long-load-boost-fanboost). Otherwise full speed (6300 rpm) is never used:
  it's far above anything the BIOS does.
- **Fail-safe:** service stop or shutdown and an implausible Tctl put the fan back in BIOS auto; display off
  and suspend do too (see below). A crash exits non-zero and the service restarts.

Tctl is read directly from the CPU (SMN `0x59800`), not the EC's slower copy. EC access goes through
[PawnIO](https://pawnio.eu) with the signed modules in [src/modules](src/modules), and shares the
`Global\Access_EC` mutex with HWiNFO and LibreHardwareMonitor.

### EC safety

Windows reads EC RAM through memory here (`ECOR` is SystemMemory `0xFE00DA00`), but it still uses ports
`0x62/0x66` for EC events: on an SCI it sends query `0x84` and reads the event number. Nothing in user mode can
lock against that, so a tpfan transaction can interleave with it. The first version wrote `0x2F` back whenever
one read disagreed, and drained "stale" output bytes; at wake-up the EC raises many events, so tpfan read
event numbers (`0x24`, `0x3F`, `0x87`, `0x96`: all real `_Qxx` handlers) as fan values and rewrote the register
in bursts. Windows lost or misread those events, and on 2026-11-20..23 the TrackPoint stopped working after
wake-ups (data reporting left disabled; restarting `EPDCtrl.exe` re-enables it). Now:

- it writes `0x2F` only on a step change; `0x2F` and RPM are read every `VerifySec` (5 s), not every poll;
- a read is done twice and must agree, an output byte it didn't ask for is never taken, errors back off 5 s;
- about once a minute the EC accepts a read and never answers it (measured 2026-11-26). That is not a collision:
  with tpfan stopped, `tpfan ec-sniff` saw no other port traffic in 90 s apart from one 1.35 ms Windows event
  query, and each such failure ended with tpfan's own address byte as the last write. The firmware drops the read,
  likely while busy with internal work, so a dropped read is retried once after 5 ms. Every 10 minutes the log
  counts them (`EC last 10 min: n reads dropped by the EC and retried`)
  instead of retrying;
- a different `0x2F` is corrected only when the same value is seen on 3 checks in a row, at most once a minute;
- display off / suspend hands the fan to BIOS auto and stops EC access; display on / resume (and any >5 s
  gap in the loop) waits `QuietSec` (30 s) before touching the EC again.

If the CPU still sits at its limit under load, check the CPU tuning, not the fan. For example, Universal x86
Tuning Utility's `--tctl-temp=97` tells the CPU to boost until 97 °C, so extra cooling only becomes extra
boost.

## Install

Needs the PawnIO driver (installed by FanControl / LibreHardwareMonitor, or from pawnio.eu) and the .NET 9 SDK
to build (it runs on any newer runtime). From an elevated PowerShell in the repo:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It builds into `publish\`, installs to `C:\Program Files\tpfan` as the auto-start service `tpfan` (depends on
PawnIO, restarts on failure) and creates `C:\ProgramData\tpfan\config.json` if missing. Close other fan tools
first (it stops TPFanControl if running). To update, rebuild (`dotnet publish src -c Release -o publish`) and
run it again; an existing config is kept.

Remove with `C:\Program Files\tpfan\uninstall.ps1` (elevated): the service goes away and the fan returns to
BIOS auto.

## Use

| | |
|---|---|
| `"C:\Program Files\tpfan\tpfan.exe" status` | current Tctl, average, step and RPM (no admin needed) |
| `C:\ProgramData\tpfan\tpfan.log` | every step change, config reload and EC error |
| `C:\ProgramData\tpfan\config.json` | the curve; editable without admin, reloaded on save, bad edits are logged and ignored |
| `tpfan check [config]` | validate a config |

Elevated, with the service stopped (`Stop-Service tpfan`):

| | |
|---|---|
| `tpfan run [config]` | run the controller in the console (Ctrl+C hands the fan back to the BIOS) |
| `tpfan monitor [s]` | print Tctl, EC temps, `0x2F` and RPM every second |
| `tpfan dump` | hex dump of EC RAM `0x00–0xFF` |
| `tpfan set <hex\|auto>` | write `0x2F` directly |
| `tpfan probe [log]` | measure RPM for every level, then BIOS auto under full CPU load (~8 min) |
| `tpfan sweep [hex,hex-hex,...] [s]` | hold each `0x2F` value for s seconds (default 12) and print the RPM it settles at |
| `tpfan dither <low> <high> <ms> [% high] [s]` | alternate `0x2F` between two values and print the RPM every 250 ms |
| `tpfan ec-sniff [s]` | watch the EC status register and list everyone else's transactions (default 60 s) |

`status.txt` is rewritten every second. Tctl comes from the CPU, so it is reported even while EC access is
paused (display off, after wake, after an EC error); only the fan part then says `paused (...)`.

## CPU modes

tpfan also applies CPU modes (replacing Universal x86 Tuning Utility on this laptop): `Modes` in the config, each
with power and temperature limits for the CPU's power-management firmware (SMU), the Windows power mode, the
plugged-in frequency cap and the fan's top-step thresholds. `"Mode"` picks one; tpfan-bar's mode letter
cycles it, and so does editing the config. The values for this laptop (Eco, Performance, Maximum) and how they
were measured are in the dotfiles repo, `workspaces/e14g5/cpu-tuning/README.md`.

- The SMU limits go through PawnIO's signed **RyzenSMU** module (from UXTU's install; see `src/modules`). They
  are MP1 mailbox messages (`0x3B10528` / `0x3B10564` / `0x3B10998`, ids from RyzenAdj for Cezanne: STAPM 0x14,
  fast 0x15, slow 0x16, slow time 0x17, STAPM time 0x18, Tctl 0x19, TDC/EDC 0x1A–0x1D, max performance 0x11,
  power saving 0x12). The module's own `send_smu_command` uses the other (PSMU) mailbox on this CPU, so tpfan
  writes the MP1 registers itself with `read/write_smu_register`, under the `Global\Access_PCI` mutex.
- They are sent at start, on a mode change, 2–3 s after wake, display-on and cable changes, and every
  `ReapplySec` (30 s): the firmware and Lenovo's thermal service reset them. That takes 13–27 ms.
- The Windows power mode (overlay) and the active plan's `PROCFREQMAX` are set only on a mode change.
- `status.txt` ends with ` mode=<name>` (and ` smu=error` if a message was refused; the log says which).

Only for Cezanne / Barcelo (Ryzen 5000U / 7030U): other CPUs use other mailboxes and message ids. With no
`Modes` in the config tpfan never talks to the SMU.

| Elevated, service running or not | |
|---|---|
| `tpfan smu` | limits in force, from the PM table (version 0x400005 offsets) |
| `tpfan smu-raw` | the PM table's limit section, raw, for comparing two tools |
| `tpfan smu-set <hex id> <value>` | send one MP1 message, then print the limits |

## Taskbar readout (tpfan-bar)

[`bar/`](bar) draws one strip on the taskbar, just left of the tray, replacing HWiNFO's tray icons and XMeters.
Left to right:

| Item | Shows | Source |
|---|---|---|
| Mode switch | one letter per mode in the config: **E P M**. The applied one is lit (Eco green, Performance white, Maximum red), the others dim; grey while a switch is pending. **Click a letter: that mode** | `mode=` in tpfan's `status.txt`; a click rewrites `"Mode"` in its config |
| CPU | one green bar per physical core: the busier of its two threads | `\Processor Information(*)\% Processor Utility`, Task Manager's CPU figure |
| Disk | orange pie: active time | `\PhysicalDisk(_Total)\% Idle Time` (active = 100 − idle, as in Task Manager) |
| Network | ▲ send (blue) and ▼ receive (green) in Task Manager's units: whole Kbps, Mbps and Gbps with one decimal below 100 | `GetIfTable2`, hardware adapters only, so VPN tunnels aren't counted twice |
| RAM | blue pie: memory in use | `GlobalMemoryStatusEx` |
| CPU power, W | blue | Windows' RAPL performance counter `\Energy Meter(RAPL_Package0_PKG)\Power` |
| CPU temperature and fan | Tctl in ° (white, yellow from 85, red from 95), then the fan in rpm (white on the BIOS curve, yellow while tpfan holds the 4200 rpm step, red while the long-load boost is on, grey when the reading is over 15 s old, i.e. tpfan has paused longer than its 5 s back-off after an EC error (display on, wake)). One tooltip. **Click: tpfan log** | tpfan's `status.txt`: `tctl=`, `rpm=` (read every 5 s) and `step=`; `--` when tpfan isn't running |
| Battery | the charge in % (white, amber at 30 % and red at 15 % on battery), then a full-height arrow for the rate: up while charging, down while discharging, its colour the watts (discharging: yellow, orange at 18 W, red from 35 W; charging: green to cyan at 45 W); a grey dash when plugged in and not charging. One tooltip: charge, exact watts, time to full or empty | `GetSystemPowerStatus`, `CallNtPowerInformation(SystemBatteryState)` |

Hover an item for the exact values (per-core loads, disk read/write speed, adapter names, memory and commit in GB,
fan speed and step, long-load boost progress, time to full or empty). Click a meter for Task Manager, the temperature and fan for the tpfan log; right-click for Task Manager, the
tpfan log, Exit. Everything is drawn in pixel fonts (7×14 digits, 5×11 for the network, 5×7 units), scaled by a whole number on larger
taskbars, so it stays sharp.

How it sits on the taskbar:

- It is a layered (per-pixel alpha) **child window of `Shell_TrayWnd`**, so it hides with the taskbar (full-screen
  apps, auto-hide) and never floats over other windows. It is a separate process: if it stops, the taskbar is
  unaffected (unlike XMeters' deskband, which runs inside Explorer).
- On the classic taskbar (StartAllBack, as here) it narrows the task buttons (`MSTaskSwWClass`) by its own width,
  so nothing is underneath it; Exit gives the width back. Every 250 ms it checks its place and the task buttons'
  width, so after a tray change it may be misplaced for up to a quarter second.
- A hidden top-level window recreates it when Explorer restarts (`TaskbarCreated`). Errors go to
  `%TEMP%\tpfan-bar.log`.
- Not supported: vertical taskbars, the stock Windows 11 taskbar (no `TrayNotifyWnd` to sit next to) and
  secondary monitors' taskbars.

Cost, measured on 2026-11-26: 0.47 % of one core and 17 MB private memory, against 2.3 % and 51 MB for HWiNFO
(sensors-only) alone. (tpfan's `EC OBF` errors, about one a minute, went on at the same rate without HWiNFO:
the EC firmware drops a read now and then; see EC safety above.) It is plain Win32 with source-generated P/Invoke, so it can be
built with NativeAOT for a smaller footprint once the "Desktop development with C++" build tools are installed.

```powershell
powershell -ExecutionPolicy Bypass -File .\bar\install.ps1              # build, install to %LOCALAPPDATA%\Programs\tpfan-bar, start at logon
powershell -ExecutionPolicy Bypass -File .\bar\install.ps1 -Aot         # the same, NativeAOT
powershell -ExecutionPolicy Bypass -File .\bar\install.ps1 -Uninstall
```

## Long-load boost (FanBoost)

A mode can have a `FanBoost`: a speed above the top step, for when the CPU stays at its temperature limit (a long
build, or a core held at the limit by a long single-threaded job), never for bursts. The default config gives it to
Maximum, at full speed (`0x40`, 6300 rpm):

- **On** after the smoothed Tctl has been ≥ 97 °C for 30 s. Time below counts back down instead of resetting, so a
  short dip delays the boost without starting over.
- **Stays on** at least 120 s, so it never comes on for a few seconds and goes away again.
- **Off** (back to 4200 rpm) after 30 s, counted the same way, below 90 °C. With more cooling the CPU boosts higher
  and stays at its limit (99 °C), so it lasts as long as the work does.
- Switching to a mode without `FanBoost` ends it at once; so do display off, suspend and sleep. The 5 s EC back-off
  after a dropped read (about once a minute under load) keeps it, or the fan would swing down and back up.
- Optional `Load` and `ExitLoad` (% of all threads busy, from `GetSystemTimes`) add a load condition: on only with
  the load ≥ `Load`, off also when it drops below `ExitLoad`. It was tried at 70/60 %: one thread held at 100 °C by
  Windows Update (23 % load) then got no boost, so the default config leaves them out.

`status.txt` shows ` boost=<s>/30s load=<%>` while it builds up and ` boost=on` while it runs (the step is then
one past the top, e.g. `step=2/2`); the log has `boost` and `unboost` lines. tpfan-bar draws the rpm red.

```jsonc
"FanBoost": { "Ec": "40", "Note": "6300 rpm", "Temp": 97, "AfterSec": 30, "MinSec": 120, "ExitTemp": 90, "ExitSec": 30 }
```

No speed between 4200 and 6300 rpm was found (2026-11-26, under a full load):

- Alternating `0x07` and `0x40` (`tpfan dither`, 0.5–2 s periods) doesn't average out: every switch restarts the
  EC's fan ramp, so the fan *slowed down* to about 2900 rpm.
- The EC reads only bits 0–2 of `0x2F` below `0x40` (`tpfan sweep`, `0x08`–`0x30`): `0x08`, `0x10`, `0x18`, `0x20`,
  `0x28`, `0x30` stop the fan like level 0, and `0x0B`, `0x0F`, `0x17`, `0x1F`, `0x27`, `0x2F` run at 4200 like 3–7.

## Config

```jsonc
{
  "SmoothSec": 6,      // Tctl averaging window (filters boost spikes)
  "UpDwellSec": 4,     // at most one step up every 4 s
  "DownHoldSec": 25,   // cooler than the step's Down for 25 s before stepping down
  "EmergencyTemp": 95, // raw Tctl for EmergencySec -> top step (capped at 97 in code)
  "EmergencySec": 5,
  "VerifySec": 5,      // how often 0x2F and RPM are read back (default)
  "QuietSec": 30,      // no EC access this long after display on / resume (default)
  "Steps": [           // Up must increase, Down < Up; Ec is the hex value for 0x2F
    { "Ec": "80", "Up": 0,  "Down": 0,  "Note": "BIOS auto" },
    { "Ec": "7",  "Up": 85, "Down": 78, "Note": "4200 rpm" }
  ]
}
```

Other ThinkPads use the same `0x2F` / `0x84` registers but different speed tables. Run `tpfan probe` first and
build the steps from what it measures.
