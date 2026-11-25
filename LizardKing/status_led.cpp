#include "status_led.h"
#include "rf_link.h"
#include <Arduino.h>

#if defined(OPK_BOARD_MDBT50Q_CX_40)
#include <nrf_gpio.h>

// The borrowed RX variant does not map the CX-40's active-low P0.08 LED.
#define WAKE_LED_PIN NRF_GPIO_PIN_MAP(0, 8)
#undef WAKE_LED_ON
#define WAKE_LED_ON LOW
#endif

#define PULSE_MS 500u // wake flash duration
// out of 255: a glow that shows which puck has the controller without
// lighting up the desk
#define LINK_LEVEL 6

static unsigned long g_pulseMs = 0;
static bool g_pulsing = false;
static int g_level = -1;

// 0 (off) .. 255 (full)
static void ledLevel(int level)
{
	if (level == g_level)
		return;
	g_level = level;
#if defined(OPK_BOARD_MDBT50Q_CX_40)
	nrf_gpio_pin_write(WAKE_LED_PIN, level ? WAKE_LED_ON : !WAKE_LED_ON);
#else
	int duty = WAKE_LED_ON == HIGH ? level : 255 - level;
	analogWrite(WAKE_LED_PIN_A, duty);
	analogWrite(WAKE_LED_PIN_B, duty);
#endif
}

void ledInit()
{
#if defined(OPK_BOARD_MDBT50Q_CX_40)
	nrf_gpio_cfg_output(WAKE_LED_PIN);
#endif
	ledLevel(0);
}

void ledWakePulse()
{
	g_pulseMs = millis();
	g_pulsing = true;

	// light immediately at the remoteWakeup() call site, not on the next loop
	ledLevel(255);
}

void ledTask()
{
	if (g_pulsing && millis() - g_pulseMs >= PULSE_MS)
		g_pulsing = false;
	if (!g_pulsing)
		ledLevel(anySlotLinkUp() ? LINK_LEVEL : 0);
}
