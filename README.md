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
- **Full speed (6300 rpm) is not used** by the default config: it's far above anything the BIOS does.
- **Fail-safe:** service stop or shutdown, an implausible Tctl, or 10 EC errors in a row put the fan back in
  BIOS auto. A crash exits non-zero and the service restarts.

Tctl is read directly from the CPU (SMN `0x59800`), not the EC's slower copy. EC access goes through
[PawnIO](https://pawnio.eu) with the signed modules in [src/modules](src/modules), and shares the
`Global\Access_EC` mutex with HWiNFO and LibreHardwareMonitor.

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

## Config

```jsonc
{
  "SmoothSec": 6,      // Tctl averaging window (filters boost spikes)
  "UpDwellSec": 4,     // at most one step up every 4 s
  "DownHoldSec": 25,   // cooler than the step's Down for 25 s before stepping down
  "EmergencyTemp": 95, // raw Tctl for EmergencySec -> top step (capped at 97 in code)
  "EmergencySec": 5,
  "Steps": [           // Up must increase, Down < Up; Ec is the hex value for 0x2F
    { "Ec": "80", "Up": 0,  "Down": 0,  "Note": "BIOS auto" },
    { "Ec": "7",  "Up": 85, "Down": 78, "Note": "4200 rpm" }
  ]
}
```

Other ThinkPads use the same `0x2F` / `0x84` registers but different speed tables. Run `tpfan probe` first and
build the steps from what it measures.
