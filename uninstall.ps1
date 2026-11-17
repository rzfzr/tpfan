# Removes the tpfan service; the fan goes back to BIOS auto. Run elevated.
if (Get-Service tpfan -ErrorAction SilentlyContinue) {
    Stop-Service tpfan -Force -ErrorAction SilentlyContinue
    sc.exe delete tpfan | Out-Null
}
"tpfan removed (fan is in BIOS auto). C:\Program Files\tpfan and C:\ProgramData\tpfan are left in place; delete them by hand if wanted."
