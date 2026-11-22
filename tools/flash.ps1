# Installs a UF2 the way a user does: reboot the puck into its UF2
# bootloader, then copy the file onto the drive that appears.
param([Parameter(Mandatory)][string]$Uf2)
$ErrorActionPreference = 'Stop'

function Find-BootDrive {
	Get-PSDrive -PSProvider FileSystem |
		Where-Object { Test-Path (Join-Path $_.Root 'INFO_UF2.TXT') } |
		Select-Object -First 1
}

$drive = Find-BootDrive
if (-not $drive) {
	# interface 2 is the CDC console (docs/DESIGN.md, USB)
	$dev = Get-CimInstance Win32_PnPEntity |
		Where-Object { $_.PNPDeviceID -like 'USB\VID_1209&PID_0001&MI_02*' }
	if (-not $dev -or $dev.Name -notmatch '\((COM\d+)\)') {
		# an unpaired puck is in Steam mode, which has no serial console
		throw 'No LizardKing puck found (an unpaired one is in Steam mode). ' +
			'Double-tap reset for the bootloader drive, then retry.'
	}
	$com = $Matches[1]
	& (Join-Path $PSScriptRoot 'quit-guide.ps1')
	$port = New-Object System.IO.Ports.SerialPort $com
	$port.DtrEnable = $true
	$deadline = (Get-Date).AddSeconds(5)
	while ($true) {
		try {
			$port.Open()
			break
		} catch {
			if ((Get-Date) -gt $deadline) {
				throw "$com is busy: quit whatever holds it and retry."
			}
			Start-Sleep -Milliseconds 250
		}
	}
	$port.Write("~b`n")
	Start-Sleep -Milliseconds 100
	$port.Close()
	$deadline = (Get-Date).AddSeconds(15)
	while (-not ($drive = Find-BootDrive)) {
		if ((Get-Date) -gt $deadline) {
			throw 'The bootloader drive did not appear.'
		}
		Start-Sleep -Milliseconds 250
	}
}

Write-Host "Copying $Uf2 to $($drive.Root)"
# the bootloader resets the board as soon as the last block is written, so
# the copy can end with the drive gone; that is success
try {
	Copy-Item $Uf2 $drive.Root
} catch {
	if (Test-Path $drive.Root) {
		throw
	}
}
Write-Host 'Flashed.'
