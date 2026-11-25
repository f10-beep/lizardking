// status_led.h -- which puck has the controller, and wake activity.
//
// The LED glows faintly while this puck has the controller (with a puck in each room, that's the one
// in use), and is dark otherwise -- including while wake is armed (host suspended). It flashes full for
// half a second when a wake is actually sent (USBDevice.remoteWakeup()), a wake debugger: flash + PC
// stays asleep = resume signal was sent and the HOST ignored it (fix host-side: powercfg /deviceenablewake);
// no flash = firmware never fired (didn't see the gesture, or didn't consider the bus suspended).
//
// Board note: built with the Feather nRF52840 variant, but the usual hardware is a SuperMini "Pro Micro"
// clone. The Feather's user LED is P1.15 (D3, active high); the SuperMini's red user LED is P0.15 (= D24 in
// the Feather pin map -- SPI MISO, unused here). The SuperMini's bright blinking blue LED is its charger's,
// flickering with no battery attached: wired to the charger alone, out of firmware's reach. We drive BOTH pins so the indicator works on either board.
// Override the pins/polarity below if your board differs.
#pragma once

#ifndef WAKE_LED_PIN_A

// Feather: P1.15 user LED (harmless unconnected pad on SuperMini clones)
#define WAKE_LED_PIN_A LED_BUILTIN
#endif
#ifndef WAKE_LED_PIN_B

// SuperMini "Pro Micro" clone: P0.15 red user LED (D24 in the Feather map)
#define WAKE_LED_PIN_B 24
#endif
#ifndef WAKE_LED_ON
#define WAKE_LED_ON HIGH // set LOW if your board's LED is wired active-low
#endif

void ledInit(); // call once from setup(): pins to output, LED off

// call at each USBDevice.remoteWakeup() site: LED on now, off after 500ms
void ledWakePulse();
void ledTask(); // call every loop(): times out the pulse, follows the link
