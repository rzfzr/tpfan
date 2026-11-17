# Builds (if needed) and installs or updates the tpfan service. Run elevated from the repo root:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = 'C:\Program Files\tpfan'
$data = 'C:\ProgramData\tpfan'
Start-Transcript "$root\install.log" -Force | Out-Null
trap { "FAILED: $_"; Stop-Transcript | Out-Null; exit 1 }

if (-not (Test-Path 'C:\Program Files\PawnIO\PawnIOLib.dll')) { throw 'PawnIO is not installed (https://pawnio.eu)' }
if (-not (Test-Path "$root\publish\tpfan.exe")) {
    dotnet publish "$root\src" -c Release -o "$root\publish"
    if ($LASTEXITCODE) { throw "dotnet publish exit $LASTEXITCODE" }
}

# Another fan controller fighting over EC register 0x2F would make both useless
Get-Process TPFanControl -ErrorAction SilentlyContinue | ForEach-Object { "stopping $($_.Name)"; $_ | Stop-Process -Force }

if (Get-Service tpfan -ErrorAction SilentlyContinue) { Stop-Service tpfan -Force; Start-Sleep 2 }
New-Item -ItemType Directory -Force $dest, $data | Out-Null
Copy-Item "$root\publish\*" $dest -Recurse -Force
Copy-Item "$root\uninstall.ps1" $dest -Force

# Config: user-editable (Users: Modify), never overwritten once it exists
if (-not (Test-Path "$data\config.json")) { Copy-Item "$root\config.json" "$data\config.json" }
icacls "$data\config.json" /grant '*S-1-5-32-545:M' | Out-Null

if (-not (Get-Service tpfan -ErrorAction SilentlyContinue)) {
    New-Service -Name tpfan -BinaryPathName "`"$dest\tpfan.exe`" service" -StartupType Automatic -DependsOn PawnIO `
        -DisplayName 'tpfan (ThinkPad fan control)' `
        -Description "Hybrid fan control: BIOS auto in the normal range, 4200 rpm above it. Config: $data\config.json" | Out-Null
} else {
    sc.exe config tpfan depend= PawnIO | Out-Null
}
sc.exe failure tpfan reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Start-Service tpfan
Start-Sleep 3
Get-Service tpfan | Format-Table -AutoSize Name, Status, StartType
Get-Content "$data\tpfan.log" -Tail 3
Get-Content "$data\status.txt"
Stop-Transcript | Out-Null
