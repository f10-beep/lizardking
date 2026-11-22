# Asks a running LizardKing Guide to quit, the way a newer copy of itself
# would (its named quit event), and waits for it to go: it holds the puck's
# serial port and, run from build/guide, the exe the build overwrites.
try {
	[System.Threading.EventWaitHandle]::OpenExisting(
		'Local\LizardKingGuide.Quit').Set() | Out-Null
} catch {
	# none running
	exit 0
}
# installed and drive copies are "LizardKing Guide.exe", the build's
# "LizardKingGuide.exe"
$deadline = (Get-Date).AddSeconds(5)
while (Get-Process -Name 'LizardKing Guide', 'LizardKingGuide' -ErrorAction SilentlyContinue) {
	if ((Get-Date) -gt $deadline) {
		throw 'The Guide did not quit.'
	}
	Start-Sleep -Milliseconds 100
}
