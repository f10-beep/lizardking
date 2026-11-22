#include "usb_app_drivers.h"
#include "config.h"
#include <Adafruit_TinyUSB.h>

extern "C" const usbd_class_driver_t *usbd_app_driver_get_cb(uint8_t *count)
{
	static const usbd_class_driver_t drivers[] = {
		*xinputClassDriver(),
	};

	*count = sizeof(drivers) / sizeof(drivers[0]);
	return drivers;
}

// The Steam pairing presentation keeps a WebUSB interface ahead of the slot
// HIDs: SDL's puck driver only binds slots on interfaces 2..5, as on the real
// puck.
Adafruit_USBD_WebUSB usb_web;

// Vendor-type control requests and the vendor interface's class requests all
// land on one global TinyUSB callback, which the Adafruit core defines for
// WebUSB. Linker wrapping (-Wl,--wrap in the Makefile) lets each mode answer
// its own requests and pass the rest on.
bool lkVendorControl(uint8_t rhport, uint8_t stage,
		     const tusb_control_request_t *request);
extern "C" bool
__real_tud_vendor_control_xfer_cb(uint8_t rhport, uint8_t stage,
				  const tusb_control_request_t *request);
extern "C" bool
__wrap_tud_vendor_control_xfer_cb(uint8_t rhport, uint8_t stage,
				  const tusb_control_request_t *request)
{
	if (g_usbMode == MODE_LIZARDKING)
		return lkVendorControl(rhport, stage, request);
	return __real_tud_vendor_control_xfer_cb(rhport, stage, request);
}

// LizardKing answers the MS OS 1.0 string (0xEE) that binds the Xbox driver.
const uint16_t *lkStringDescriptor(uint8_t index);
extern "C" const uint16_t *__real_tud_descriptor_string_cb(uint8_t index,
							   uint16_t langid);
extern "C" const uint16_t *__wrap_tud_descriptor_string_cb(uint8_t index,
							   uint16_t langid)
{
	const uint16_t *d = g_usbMode == MODE_LIZARDKING ?
				    lkStringDescriptor(index) :
				    nullptr;
	return d ? d : __real_tud_descriptor_string_cb(index, langid);
}
