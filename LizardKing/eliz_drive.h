// eliz_drive.h -- LizardKing's read-only "guide drive" (USB mass storage).
//
// The puck carries its Windows Guide (tools/guide) so it can be opened on any
// PC: a small read-only FAT volume with the Guide's exe and a README. Nothing
// runs by itself; the user opens the drive and starts the program. The drive
// stays "empty" (like a card reader without a card) until the user asks for
// it (a medium press of the Guide button), so no host is ever shown it
// uninvited. It goes away on the same press, when the Guide connects, or when
// the puck is unplugged. Only built when the Guide's exe was embedded at
// build time (see Makefile, LIZARD_DRIVE).
#pragma once

// add the mass-storage interface; call after the other USB interfaces
void elizDriveBegin();
// hides the drive on a new host session (plug-in, wake) and while the Guide
// holds the serial port
void elizDriveTask();
// show / hide the drive; returns whether it is shown now
bool elizDriveToggle();
