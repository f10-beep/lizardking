# Open-PuckPort: the puck's serial console, opened after asking a running
# Guide (which holds it) to quit. Dot-source it.

function Find-PuckCom {
	# interface 2 is the CDC console (docs/DESIGN.md, USB)
	$dev = Get-CimInstance Win32_PnPEntity |
		Where-Object { $_.PNPDeviceID -like 'USB\VID_1209&PID_0001&MI_02*' }
	if ($dev -and $dev.Name -match '\((COM\d+)\)') {
		return $Matches[1]
	}
}

function Open-PuckPort {
	$com = Find-PuckCom
	if (-not $com) {
		# an unpaired puck is in Steam mode, which has no serial console
		throw 'No LizardKing puck found (an unpaired one is in Steam mode).'
	}
	& (Join-Path $PSScriptRoot 'quit-guide.ps1')
	$port = New-Object System.IO.Ports.SerialPort $com
	$port.DtrEnable = $true
	$deadline = (Get-Date).AddSeconds(5)
	while ($true) {
		try {
			$port.Open()
			return $port
		} catch {
			if ((Get-Date) -gt $deadline) {
				throw "$com is busy: quit whatever holds it and retry."
			}
			Start-Sleep -Milliseconds 250
		}
	}
}
