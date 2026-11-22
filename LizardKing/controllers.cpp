#include "controllers.h"
#include "config.h"
#include "puck_hid.h"
#include "mode_lizardking.h"

IController *g_active = nullptr;

IController *controllerFor(uint8_t mode)
{
	return mode == MODE_LIZARDKING ? (IController *)&g_lizardKingCtl :
					 &g_steamPuck;
}
