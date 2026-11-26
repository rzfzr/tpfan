# PawnIO modules

`LpcACPIEC.bin` (port I/O restricted to the ACPI EC ports 0x62/0x66) and `AMDFamily17.bin` (AMD SMN/MSR reads)
are the signed PawnIO modules that LibreHardwareMonitorLib 0.9.4 ships as embedded resources
(`LibreHardwareMonitor.Resources.PawnIo.*.bin`). They are built from
[namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules); see that project for source and license.
PawnIO only loads signed modules, which is why the binaries are kept here instead of being built.

`RyzenSMU.bin` (SMU mailbox registers, PM table) is the same project's signed module, copied from Universal x86
Tuning Utility 26.3.1's install (`Assets\AMD\PawnIO\RyzenSMU.bin`, SHA-256 `301d9ca3…3450`); tpfan uses it for the
CPU modes.

`LpcIO.bin` (Super I/O config registers at 0x2E/0x4E, used by `tpfan sio` / `tpfan i2ec`) is the same project's
signed module, embedded in FanControl's LibreHardwareMonitorLib 0.9.4 (`LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin`,
SHA-256 `3dcf8b2b…d101`).

They need the PawnIO driver installed (`C:\Program Files\PawnIO`, e.g. via FanControl, LibreHardwareMonitor or
the installer from [pawnio.eu](https://pawnio.eu)).
