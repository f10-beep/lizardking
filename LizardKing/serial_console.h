// serial_console.h -- the CDC command line: the Guide's protocol and the
// tuning and diagnosis commands (serial_console.cpp lists them).
#pragma once

// drain and dispatch CDC commands; call every loop()
void serialConsolePoll();
// the boot line, with why the puck last reset (LizardKing.ino; console "UP")
void printBoot();
