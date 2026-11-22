# Installs a UF2 the way a user does: reboot the puck into its UF2
# bootloader, then copy the file onto the drive that appears.
param([Parameter(Mandatory)][string]$Uf2)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'puck-port.ps1')

function Find-BootDrive {
	Get-PSDrive -PSProvider FileSystem |
		Where-Object { Test-Path (Join-Path $_.Root 'INFO_UF2.TXT') } |
		Select-Object -First 1
}

$drive = Find-BootDrive
if (-not $drive) {
	if (-not (Find-PuckCom)) {
		throw 'No LizardKing puck found (an unpaired one is in Steam mode). ' +
			'Double-tap reset for the bootloader drive, then retry.'
	}
	$port = Open-PuckPort
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
