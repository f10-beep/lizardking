#include "controllers.h"
#include "config.h"
#include "puck_hid.h"
#include "mode_eliz.h"

IController *g_active = nullptr;

IController *controllerFor(uint8_t mode)
{
	return mode == MODE_ELIZ ? (IController *)&g_elizCtl : &g_steamPuck;
}
