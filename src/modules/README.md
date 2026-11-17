# PawnIO modules

`LpcACPIEC.bin` (port I/O restricted to the ACPI EC ports 0x62/0x66) and `AMDFamily17.bin` (AMD SMN/MSR reads)
are the signed PawnIO modules that LibreHardwareMonitorLib 0.9.4 ships as embedded resources
(`LibreHardwareMonitor.Resources.PawnIo.*.bin`). They are built from
[namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules); see that project for source and license.
PawnIO only loads signed modules, which is why the binaries are kept here instead of being built.

They need the PawnIO driver installed (`C:\Program Files\PawnIO`, e.g. via FanControl, LibreHardwareMonitor or
the installer from [pawnio.eu](https://pawnio.eu)).
