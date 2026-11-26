# Tunes a running puck without a reflash: sends console lines, and with
# -Keymap the key map as LizardKing/mode_lizardking.cpp has it now, then
# restarts the Guide if one was running. Changes last until the puck
# reboots; the Guide's title says "dev overrides" meanwhile. `make keymap`
# and `make tune SET="LK_GYRO_SENS_SLOW 10"` call it.
param(
	[string[]]$Lines = @(),
	[switch]$Keymap,
	# TinyUSB's hid.h, for the HID_KEY_* codes the key map names
	[string]$HidHeader
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'puck-port.ps1')
. (Join-Path $PSScriptRoot 'keymap-lines.ps1')

if ($Keymap) {
	$Lines = @(Get-KeymapLines $HidHeader) + $Lines
}
# the Guide that holds the port, from wherever it runs (installed, the
# guide drive, the build), to start again afterwards
$guide = Get-CimInstance Win32_Process |
	Where-Object { $_.Name -in 'LizardKing Guide.exe', 'LizardKingGuide.exe' } |
	Select-Object -First 1 -ExpandProperty ExecutablePath
$port = Open-PuckPort
try {
	foreach ($line in $Lines) {
		$port.Write("$line`n")
		Start-Sleep -Milliseconds 20
	}
	Start-Sleep -Milliseconds 300
	# the puck's replies; its live feed for the Guide is left out
	$port.ReadExisting() -split "`n" | Where-Object { $_ -like '# *' } |
		ForEach-Object { $_.TrimEnd() }
	if ($Keymap) {
		Write-Host "Key map pushed ($((@(Get-KeymapLines $HidHeader)).Count) pages)."
	}
} finally {
	$port.Close()
}
if ($guide) {
	Start-Process $guide
}
