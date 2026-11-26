# Builds and installs (or updates) tpfan-bar for the current user and starts it at logon. No admin needed:
#   powershell -ExecutionPolicy Bypass -File .\bar\install.ps1
#   powershell -ExecutionPolicy Bypass -File .\bar\install.ps1 -Aot        # NativeAOT (needs the C++ build tools)
#   powershell -ExecutionPolicy Bypass -File .\bar\install.ps1 -Uninstall
# It also removes tpfan-tray (the earlier tray-icon version) if it is installed.
param([switch]$Uninstall, [switch]$Aot)
$ErrorActionPreference = 'Stop'
$dest = "$env:LOCALAPPDATA\Programs\tpfan-bar"
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# Exit asks the bar to give the task buttons their width back; a killed bar leaves that to Explorer's next layout.
Get-Process tpfan-bar, tpfan-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-ItemProperty $run -Name tpfan-tray -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\Programs\tpfan-tray" -Recurse -Force -ErrorAction SilentlyContinue

if ($Uninstall) {
    Remove-ItemProperty $run -Name tpfan-bar -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    'tpfan-bar removed'
    return
}

$props = if ($Aot) { @('-p:PublishAot=true', '-r', 'win-x64') } else { @() }
dotnet publish $PSScriptRoot -c Release -o $dest --nologo -v quiet @props
if ($LASTEXITCODE) { throw "dotnet publish exit $LASTEXITCODE" }
Set-ItemProperty $run -Name tpfan-bar -Value "`"$dest\tpfan-bar.exe`""
Start-Process "$dest\tpfan-bar.exe"
"tpfan-bar installed in $dest, starts at logon"
