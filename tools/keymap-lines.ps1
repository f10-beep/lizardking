# "~K pad zone k0..k7" for every page of KEYMAP, as the firmware would build
# it: K(NAME) is HID_KEY_NAME, KM_* the puck's own codes, 0 nothing
function Get-KeymapLines([string]$HidHeader) {
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
