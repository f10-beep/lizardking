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

# "~K pad zone k0..k7" for every page of KEYMAP, as the firmware would build
# it: K(NAME) is HID_KEY_NAME, KM_* the puck's own codes, 0 nothing
function Get-KeymapLines {
	$src = Get-Content (Join-Path $PSScriptRoot '..\LizardKing\mode_lizardking.cpp') -Raw
	$codes = @{}
	foreach ($m in [regex]::Matches((Get-Content $HidHeader -Raw),
			'#define\s+HID_KEY_(\w+)\s+0x([0-9A-Fa-f]+)')) {
		$codes['K(' + $m.Groups[1].Value + ')'] =
			[Convert]::ToInt32($m.Groups[2].Value, 16)
	}
	foreach ($m in [regex]::Matches($src, 'enum \{ (KM_\w+ = 0x[0-9A-Fa-f]+[^}]*)\}')) {
		$names = $m.Groups[1].Value -split ',' | ForEach-Object { $_.Trim() }
		$first = [Convert]::ToInt32(($names[0] -split '=')[1].Trim(), 16)
		for ($i = 0; $i -lt $names.Count; $i++) {
			$codes[($names[$i] -split '=')[0].Trim()] = $first + $i
		}
	}
	$i = $src.IndexOf('KEYMAP[2][Z_COUNT][8] = {')
	$body = $src.Substring($i, $src.IndexOf('};', $i) - $i)
	$body = [regex]::Replace($body.Substring($body.IndexOf('{')), '//[^\n]*', '')
	$depth = 0; $pad = -1; $zone = -1; $row = @()
	foreach ($t in [regex]::Matches($body, '\{|\}|K\(\w+\)|KM_\w+|\b0\b')) {
		switch ($t.Value) {
			'{' {
				$depth++
				if ($depth -eq 2) { $pad++; $zone = -1 }
				elseif ($depth -eq 3) { $zone++; $row = @() }
			}
			'}' {
				if ($depth -eq 3) {
					while ($row.Count -lt 8) { $row += 0 }
					'~K {0:X} {1:X} {2}' -f $pad, $zone,
						(($row | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
				}
				$depth--
			}
			'0' { $row += 0 }
			default {
				if (-not $codes.ContainsKey($t.Value)) {
					throw "Unknown key $($t.Value) in KEYMAP"
				}
				$row += $codes[$t.Value]
			}
		}
	}
}

if ($Keymap) {
	$Lines = @(Get-KeymapLines) + $Lines
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
		Write-Host "Key map pushed ($((@(Get-KeymapLines)).Count) pages)."
	}
} finally {
	$port.Close()
}
if ($guide) {
	Start-Process $guide
}
