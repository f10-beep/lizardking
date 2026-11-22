// rf_link.h -- the operational puck<->controller RF protocol (the "dongle" role).
//
// Built on the radio HW layer (radio.cpp), this is what makes the device act as a puck once a controller is
// bonded: it transmits the host frame the bonded controller reconnects on, runs the connected-mode poll loop
// (a bare E3 each cycle), and decodes the controller's 0xF1 input reply. The decode fills the shared g_in
// (triton.h) and dispatches to the active IController; it also handles USB remote-wakeup on a Steam
// short-press, adaptive channel hopping (QoS), and the host-frame keepalive beacons.
//
// RECIPE: a live air capture of a real puck<->controller RECONNECT (sniff1.json) shows the controller streams
// 0xF1 input in response to a BARE 0xE3 poll -- with NO 0xE7 awake-announce and NO GET-report-0x45 sub-TLV.
// So the only thing OpenPuck genuinely needs to (re)establish the stream is connstate==3, which the host frame
// (0xE1) sets up; once connected, bare E3 polls keep F1 flowing. (The earlier RE hypothesis that E7+GET-0x45
// were required is contradicted by the capture.) The real puck also sends no E1 on its session channel (its
// bonded controller resumes on the known per-bond address); this puck still beacons E1 only because it runs
// the SHARED "ibex" address, not the per-bond one.
// F1 reply = type 0xF1 then TLV type4(analog/buttons)+type6(per-module gyro/accel/sticks).
#pragma once
#include <stdint.h>
#include "bonds.h" // NSLOT

// poll RX-window (us): the full window is paid only on a no-reply poll (a reply returns early), so it must
// stay < g_pollUs to fit inside the 250 Hz cycle. FIXED, not configurable (like g_pollUs) -- raising it
// starves the poll cycle, so it's pinned rather than exposed as a footgun knob.
extern const uint32_t g_rxWin;

// set on 0xF2 disconnect; pauses beacon+poll so a powering-off controller can sleep
extern unsigned long g_connCooldown;

// connected-mode state
extern uint8_t g_connSt, g_connStep;
extern uint16_t g_connPoll;
// count of 0xF1 input reports seen (aggregate across all slots)
extern uint32_t g_connF1;

// last protocol version the controller reported in an F3 reply (0xFF=none) -- aggregate (last writer wins)
extern uint8_t g_connF3v;

// Slot the poll loop is currently driving (set by rfConnStep before each E7/relay/E3). -1 = idle. Used by
// the decode path (g_in[g_curSlot]), the haptic flush (per-slot session address), the wake detect, and the
// stat dump.
extern int g_curSlot;
// True if ANY bonded slot is currently link-up (heard an F-reply within 300 ms). Used by the haptic
// re-init gating (we only re-init on a real link) and the beacon pacing.
bool anySlotLinkUp();

// QoS adaptive channel hopping
extern uint8_t g_qos; // 0=off (static g_sessCh), 1=auto-hop on degradation
extern uint8_t g_hopIdx;
extern volatile uint16_t g_qosBad;
extern unsigned long g_qosCheckMs, g_qosLastHopMs;

// per-second rate readouts (console SS, flight recorder)
extern uint16_t g_f1ps; // last completed second's F1 rate
// genuine new-report rate (report 0x45 seq byte changes)
extern uint16_t g_newps;

// last second's TRUE poll-cycle count (one E3 GET per warm slot per cycle, ~250 for one controller) -- vs F1
// tells starvation from reply-loss.
extern uint16_t g_pollsps;
// last second's relay-frame TX count (host/haptic output reports forwarded to the controller). Each relay TX
// precedes a poll and consumes a reply window, so a high relay rate directly depresses the F1/delivered rate.
extern uint16_t g_relayps;
// last second's CRC-fail and no-reply counts: diagnose a wedge -- polls>0 + noRx high = RX dead / wrong
// channel / controller silent; crc high = corrupt replies (interference); polls==0 = poll loop stopped.
extern uint16_t g_crcps, g_norxps;
// Count of RF-stall self-heals (radio power-cycle + reconnect) performed since boot. Non-zero means the
// firmware caught and recovered from a full-link wedge that previously needed a manual puck replug.
extern uint16_t g_rfStallRecover;

// Smoothed controller->puck signal strength, sampled by the radio (RSSISAMPLE) on each CRC-good controller
// reply during the poll. Stored as the dBm MAGNITUDE (35 = -35dBm); 0 = no sample yet. Per-slot: each
// controller's RSSI is reported on its own 0x7B byte 8 (puck_hid reports it to Steam, after subtracting
// RSSI_DBM_OFFSET).
extern volatile uint8_t g_linkRssi[NSLOT];
// dB to subtract from our raw RSSI magnitude before reporting, to match the real puck's close-range -35dBm
// (compensates the Pro Micro antenna vs Valve's front-end). Tune against one known-distance reading.
#define RSSI_DBM_OFFSET 20
// Battery percent from the controller's periodic report 0x43 (body[1]); 0 = none yet. Per-slot: each
// controller's battery is read on its own 0x43 reply and stored against its slot index. The Guide's
// status line reads the active slot's. The raw 0x43 report is also forwarded to
// Steam verbatim (puck_hid onAuxReport) so Steam reads it itself.
extern volatile uint8_t g_battery[NSLOT];
// charge state from report 0x43 body[0] (EChargeState: 1=discharging, 2=charging, 4=charging-done; 0=unknown)
extern volatile uint8_t g_batteryState[NSLOT];

#define RF_RECOVERY_STATUS_CHANNELS 14u

enum RfChannelDesignation : uint8_t {
	RF_CHANNEL_UNEXPLORED = 0,
	RF_CHANNEL_GOOD = 1,
	RF_CHANNEL_POOR = 2,
	RF_CHANNEL_MIXED = 3,
};

struct RfChannelStatusEntry {
	uint8_t channel;
	uint8_t ambientRssi;
	uint8_t designation;
	uint8_t worstPct;
	uint8_t meanPct;
	uint8_t confidence;
	uint8_t trials;
	uint8_t penalty;
	uint8_t recentOrder;
};

enum RfRecoveryHandoffPhase : uint8_t {
	RF_RECOVERY_HANDOFF_IDLE = 0,
	RF_RECOVERY_HANDOFF_WAIT_NEUTRAL = 1,
	RF_RECOVERY_HANDOFF_AUTHORIZE = 2,
	RF_RECOVERY_HANDOFF_SWITCH = 3,
	RF_RECOVERY_HANDOFF_ACQUIRE_TARGET = 4,
	RF_RECOVERY_HANDOFF_RECONCILE = 5,
	RF_RECOVERY_HANDOFF_ROLLBACK_SWITCH = 6,
	RF_RECOVERY_HANDOFF_ACQUIRE_OLD = 7,
};

enum RfRecoveryWaitReason : uint8_t {
	RF_RECOVERY_WAIT_NONE = 0,
	RF_RECOVERY_WAIT_FRESH_REPORT = 1,
	RF_RECOVERY_WAIT_INPUT_ACTIVE = 2,
	RF_RECOVERY_WAIT_NEUTRAL_DWELL = 3,
};

struct RfRecoveryStatus {
	uint8_t version;
	uint8_t flags;
	uint8_t currentChannel;
	uint8_t targetChannel;
	uint8_t startupChannel;
	uint8_t channelCount;
	uint8_t journalWrites;
	uint16_t ambientGeneration;
	uint8_t ambientSurveyChannel;
	uint32_t journalSequence;
	RfChannelStatusEntry channel[RF_RECOVERY_STATUS_CHANNELS];
	uint8_t handoffPhase;
	uint64_t handoffElapsedMs;
	uint8_t handoffOldChannel;
	uint8_t journalBuilderPhase;
	uint8_t journalBuilderIndex;
	uint8_t journalBuilderChannel;
	uint8_t journalBuilderProgress;
	uint8_t journalBuilderParticipantMask;
	uint8_t journalBuilderBestChannel;
	uint8_t journalBuilderFailure;
	uint8_t handoffWaitReason;
	uint16_t handoffNeutralMs;
	uint8_t recoveryCooldownSeconds;
	uint8_t recoveryFailedTarget;
	uint8_t ambientSurveyRetry;
	uint8_t ambientSurveyFailure;
	uint8_t ambientSurveyFailureChannel;
};

// TX one connected packet [LEN][S1][payload] on channel ch, then RX the reply into rfrx; decodes 0xF1.
// rxWinUs overrides the reply-wait window (0 = use g_rxWin). Pass a tiny value for NO-ACK relays that expect
// no reply, so they don't burn a full ~1.2ms window of dead air per haptic.
uint8_t rfConnTx(uint8_t ch, uint8_t s1, const uint8_t *payload, uint8_t plen,
		 uint16_t rxWinUs = 0);
// Mid-session channel hop: advertise newCh on the current channel a few times, then move the poll to newCh.
void rfHopTo(uint8_t newCh);
bool rfChannelHandoffHostGrace(int slot);
// Per-loop: host-frame beacons + connected-mode poll + remote-wakeup + QoS hop + per-second stats.
void rfLinkTask();
