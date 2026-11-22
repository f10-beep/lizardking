// LizardKing Guide for Windows (.NET Framework 4.x, which ships with Windows
// 10/11, so it runs without installing anything).
//
// The Guide teaches the controller; the controller's Guide button and its
// chords are the only interface, and nothing on screen is clickable. Two
// switches:
//   Guide    a window showing the controller three times (no pad, one pad,
//            both pads touched) with every button labelled, the key map,
//            and the chords; the layer your thumbs select lights up as you
//            use it. A medium press of the Guide button opens it, any
//            Guide press closes it.
//   Key map  the keyboard overlay, shown while the Guide is closed and both
//            thumbs are on the pads (Guide + right pad click).
//
// While the Guide window is up it is a practice space: the puck sends the
// PC nothing, everything goes to the Guide alone, held by a heartbeat ("~g"
// every 250 ms from the window thread; "~h" hands the controller back). The
// cursor is parked in a corner meanwhile. A key, click or wheel turn from
// another keyboard or mouse puts the Guide away.
//
// It reads the puck's serial port. The key table comes from the firmware
// ("~m" -> "KM" lines), so it always matches the flashed layout. "KS" lines
// report the layer, both thumbs' zones and Shift; "KK pad zone" arrives for
// every typed key; "KT" carries the status and the key map switches, which
// the puck owns; "KG" turns the Guide on or off; "KI" (Guide + Start) asks
// to install.
//
// Started from the puck's drive it never installs by itself: it copies
// itself to a temporary folder and runs from there until the user asks to
// install (Guide + Start).
//
// Build: tools/guide/build.cmd. --demo cycles fake states without a puck.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("LizardKing Guide")]
[assembly: AssemblyDescription("Shows what every button does on a LizardKing controller")]
[assembly: AssemblyProduct("LizardKing")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace LizardKing
{
	struct PuckState
	{
		public int Layer, ZoneR, ZoneL;
		public bool Shift;

		public PuckState(int layer, int zr, int zl, bool shift)
		{
			Layer = layer;
			ZoneR = zr;
			ZoneL = zl;
			Shift = shift;
		}
	}

	// live controller input ("KB"): buttons as the firmware's TB_* bits,
	// triggers 0..31, sticks and pad positions -32..32 (y up)
	struct InputState
	{
		public uint Buttons;
		public int LT, RT, LX, LY, RX, RY, LPX, LPY, RPX, RPY;

		public bool Has(uint mask)
		{
			return (Buttons & mask) != 0;
		}
	}

	// status ("KT"): charge as in the controller's report 0x43 (1
	// discharging, 2 charging, 4 charged, 0 unknown); signal in -dBm, 0 = no
	// sample yet; the key map switches, owned by the puck
	struct CtlStatus
	{
		public bool Connected, KeymapOn, KeymapLow;
		public int Battery, Charge, Rssi;

		// 1..4 bars, 0 unknown
		public int Bars
		{
			get {
				if (Rssi <= 0)
					return 0;
				return Rssi <= 55 ? 4 : Rssi <= 65 ? 3 : Rssi <= 75 ? 2 : 1;
			}
		}

		public string Text
		{
			get {
				if (!Connected)
					return "Controller asleep";
				string[] sig = { "signal unknown", "weak signal",
						 "fair signal", "good signal",
						 "strong signal" };
				string batt = Battery > 0 ? Battery + "%" : "battery unknown";
				if (Charge == 2)
					batt += " charging";
				return batt + ", " + sig[Bars];
			}
		}
	}

	// Which device a key or click came from. Raw input reaches the hidden
	// key map window even unfocused, and the puck's own keyboard and mouse
	// carry its USB ID in their device names.
	static class RawSource
	{
		const uint RidInput = 0x10000003, RidiDeviceName = 0x20000007;
		static readonly Dictionary<IntPtr, bool> puck =
			new Dictionary<IntPtr, bool>();

		public static void Register(IntPtr window)
		{
			var d = new Native.RawInputDevice[2];
			// keyboards and mice; INPUTSINK: even while unfocused
			d[0].UsagePage = 1;
			d[0].Usage = 6;
			d[1].UsagePage = 1;
			d[1].Usage = 2;
			for (int i = 0; i < 2; i++) {
				d[i].Flags = 0x100;
				d[i].Target = window;
			}
			Native.RegisterRawInputDevices(d, 2,
				(uint)Marshal.SizeOf(typeof(Native.RawInputDevice)));
		}

		static bool IsPuck(IntPtr dev)
		{
			bool p;
			if (puck.TryGetValue(dev, out p))
				return p;
			uint n = 0;
			Native.GetRawInputDeviceInfo(dev, RidiDeviceName, null, ref n);
			var sb = new StringBuilder((int)n + 1);
			Native.GetRawInputDeviceInfo(dev, RidiDeviceName, sb, ref n);
			p = sb.ToString().IndexOf("VID_1209&PID_0001",
						  StringComparison.OrdinalIgnoreCase) >= 0;
			puck[dev] = p;
			return p;
		}

		// Whether a WM_INPUT should dismiss the Guide: a key, click or
		// wheel turn from a device that is not the puck. The puck's own
		// keys (its Esc included) are what the Guide is there to try out;
		// pointer movement counts for nothing, and input with no device
		// (injected by software) is ignored.
		public static bool Dismisses(IntPtr input)
		{
			int hdr = 8 + 2 * IntPtr.Size;
			uint size = 0;
			Native.GetRawInputData(input, RidInput, IntPtr.Zero, ref size,
					       (uint)hdr);
			if (size == 0 || size > 1024)
				return false;
			IntPtr buf = Marshal.AllocHGlobal((int)size);
			try {
				if (Native.GetRawInputData(input, RidInput, buf, ref size,
							   (uint)hdr) != size)
					return false;
				int type = Marshal.ReadInt32(buf, 0);
				IntPtr dev = Marshal.ReadIntPtr(buf, 8);
				bool foreign = dev != IntPtr.Zero && !IsPuck(dev);
				if (type == 1) {
					// RAWKEYBOARD: Message at 8 (key down or sys key
					// down)
					int msg = Marshal.ReadInt32(buf, hdr + 8);
					return foreign && (msg == 0x100 || msg == 0x104);
				}
				if (type == 0) {
					// RAWMOUSE: button flags at 4; any button down or
					// either wheel
					int flags = (ushort)Marshal.ReadInt16(buf, hdr + 4);
					return foreign && (flags & 0xD55) != 0;
				}
				return false;
			} finally {
				Marshal.FreeHGlobal(buf);
			}
		}
	}

	// TB_* in the firmware's triton.h. Select and Start are the two small
	// buttons left and right of the Steam button.
	static class Btn
	{
		public const uint A = 0x1, B = 0x2, X = 0x4, Y = 0x8, Qam = 0x10;
		public const uint R3 = 0x20, Start = 0x40, R4 = 0x80, R5 = 0x100;
		public const uint RB = 0x200, Down = 0x400, Right = 0x800;
		public const uint Left = 0x1000, Up = 0x2000, Select = 0x4000;
		public const uint L3 = 0x8000, Steam = 0x10000, L4 = 0x20000;
		public const uint L5 = 0x40000, LB = 0x80000, RPadT = 0x200000;
		public const uint RPadC = 0x400000, RT = 0x800000;
		public const uint LPadT = 0x2000000, LPadC = 0x4000000;
		public const uint LT = 0x8000000;
		public const uint Dpad = Up | Down | Left | Right;
		public const uint Face = A | B | X | Y;
	}

	static class Hid
	{
		static readonly Dictionary<int, string> Names =
			new Dictionary<int, string> {
				{ 0x28, "Enter" }, { 0x29, "Esc" }, { 0x2A, "Bksp" },
				{ 0x2B, "Tab" }, { 0x2C, "Space" }, { 0x2D, "-" },
				{ 0x2E, "=" }, { 0x2F, "[" }, { 0x30, "]" },
				{ 0x31, "\\" }, { 0x33, ";" }, { 0x34, "'" },
				{ 0x35, "`" }, { 0x36, "," }, { 0x37, "." },
				{ 0x38, "/" }, { 0x49, "Ins" }, { 0x4A, "Home" },
				{ 0x4B, "PgUp" }, { 0x4C, "Del" }, { 0x4D, "End" },
				{ 0x4E, "PgDn" }, { 0x4F, "\u2192" }, { 0x50, "\u2190" },
				{ 0x51, "\u2193" }, { 0x52, "\u2191" },
				// numpad operators and decimal
				{ 0x54, "/" }, { 0x55, "*" }, { 0x56, "-" },
				{ 0x57, "+" }, { 0x63, "." },
			};
		// US layout: what Shift turns each main-block symbol into
		const string Plain = "1234567890-=[]\\;',./`";
		const string Shifted = "!@#$%^&*()_+{}|:\"<>?~";

		public static string Label(int code, bool shift)
		{
			if (code >= 0x04 && code <= 0x1D) {
				char c = (char)('a' + code - 0x04);
				return (shift ? char.ToUpper(c) : c).ToString();
			}
			// numpad keys ignore Shift
			if ((code >= 0x54 && code <= 0x57) || code == 0x63)
				return Names[code];
			string label;
			if (code >= 0x1E && code <= 0x26)
				label = (code - 0x1D).ToString();
			else if (code == 0x27)
				label = "0";
			else if (code >= 0x3A && code <= 0x45)
				label = "F" + (code - 0x39);
			else if (!Names.TryGetValue(code, out label))
				label = "";
			if (shift && label.Length == 1) {
				int i = Plain.IndexOf(label[0]);
				if (i >= 0)
					return Shifted[i].ToString();
			}
			return label;
		}

		// for --demo: a typed character back to its main-keyboard code
		public static int Code(char c)
		{
			if (c >= 'A' && c <= 'Z')
				return 0x04 + c - 'A';
			if (c == '0')
				return 0x27;
			if (c >= '1' && c <= '9')
				return 0x1D + c - '0';
			foreach (var kv in Names)
				if (kv.Key < 0x54 && kv.Value == c.ToString())
					return kv.Key;
			return 0;
		}
	}

	static class Native
	{
		[StructLayout(LayoutKind.Sequential)]
		public struct CommTimeouts
		{
			public uint ReadInterval, ReadMultiplier, ReadConstant;
			public uint WriteMultiplier, WriteConstant;
		}

		[DllImport("kernel32.dll", SetLastError = true,
			   CharSet = CharSet.Unicode)]
		public static extern SafeFileHandle CreateFile(string name,
							       uint access,
							       uint share,
							       IntPtr security,
							       uint disposition,
							       uint flags,
							       IntPtr template);
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool ReadFile(SafeFileHandle h, byte[] buf,
						   int n, out int read,
						   IntPtr overlapped);
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool WriteFile(SafeFileHandle h, byte[] buf,
						    int n, out int written,
						    IntPtr overlapped);
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool SetCommTimeouts(SafeFileHandle h,
							  ref CommTimeouts t);
		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool EscapeCommFunction(SafeFileHandle h,
							     uint func);
		[DllImport("winmm.dll")]
		public static extern uint timeBeginPeriod(uint ms);
		[DllImport("shcore.dll")]
		public static extern int SetProcessDpiAwareness(int value);
		[DllImport("user32.dll")]
		public static extern bool DestroyIcon(IntPtr icon);

		[StructLayout(LayoutKind.Sequential)]
		public struct RawInputDevice
		{
			public ushort UsagePage, Usage;
			public uint Flags;
			public IntPtr Target;
		}

		[DllImport("user32.dll", SetLastError = true)]
		public static extern bool RegisterRawInputDevices(
			RawInputDevice[] devices, uint count, uint size);
		[DllImport("user32.dll")]
		public static extern uint GetRawInputData(IntPtr input, uint command,
							  IntPtr data, ref uint size,
							  uint headerSize);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		public static extern uint GetRawInputDeviceInfo(IntPtr device,
								uint command,
								StringBuilder data,
								ref uint size);
	}

	static class Theme
	{
		public static readonly Color Bg = Hex("1b1e24");
		public static readonly Color Panel = Hex("20242b");
		public static readonly Color Cell = Hex("2a2f38");
		public static readonly Color Edge = Hex("3d4450");
		public static readonly Color Ink = Hex("e8eaee");
		public static readonly Color Dim = Hex("6c7380");
		public static readonly Color Accent = Hex("3f8cff");
		public static readonly Color AccentFill = Hex("24344f");
		public static readonly Color Mini = Hex("6a7180");
		public static readonly Color Flash = Hex("6fa4ff");

		public static Color Hex(string s)
		{
			return Color.FromArgb(Convert.ToInt32(s, 16) |
					      unchecked((int)0xFF000000));
		}

		public static Color Mix(Color a, Color b, double e)
		{
			return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * e),
					      (int)Math.Round(a.G + (b.G - a.G) * e),
					      (int)Math.Round(a.B + (b.B - a.B) * e));
		}
	}

	// Rounded outlines: big radii for panels and windows, a few pixels
	// for everything else, so nothing on screen has a knife-sharp corner.
	static class Shape
	{
		// windows get the small radius too, unlike the panels inside them
		public const float Window = 5, Small = 4;

		// corners clockwise from top left
		public static GraphicsPath Round(RectangleF r, float tl, float tr,
						 float br, float bl)
		{
			var p = new GraphicsPath();
			Corner(p, r.X, r.Y, tl, 180);
			Corner(p, r.Right - 2 * tr, r.Y, tr, 270);
			Corner(p, r.Right - 2 * br, r.Bottom - 2 * br, br, 0);
			Corner(p, r.X, r.Bottom - 2 * bl, bl, 90);
			p.CloseFigure();
			return p;
		}

		static void Corner(GraphicsPath p, float x, float y, float r,
				   float start)
		{
			if (r <= 0) {
				float cx = start == 180 || start == 90 ? x : x + 2 * r;
				float cy = start == 180 || start == 270 ? y : y + 2 * r;
				p.AddLine(cx, cy, cx, cy);
				return;
			}
			p.AddArc(x, y, 2 * r, 2 * r, start, 90);
		}

		public static GraphicsPath Round(RectangleF r, float rad)
		{
			return Round(r, rad, rad, rad, rad);
		}

		// a polygon with every corner rounded to radius r
		public static GraphicsPath RoundPoly(PointF[] pts, float r)
		{
			var p = new GraphicsPath();
			int n = pts.Length;
			for (int i = 0; i < n; i++) {
				PointF a = pts[(i + n - 1) % n], v = pts[i],
				       b = pts[(i + 1) % n];
				PointF s0 = Toward(v, a, r), s1 = Toward(v, b, r);
				p.AddBezier(s0, Mid(s0, v), Mid(s1, v), s1);
			}
			p.CloseFigure();
			return p;
		}

		static PointF Toward(PointF from, PointF to, float d)
		{
			float dx = to.X - from.X, dy = to.Y - from.Y;
			float len = (float)Math.Sqrt(dx * dx + dy * dy);
			return new PointF(from.X + dx / len * d, from.Y + dy / len * d);
		}

		static PointF Mid(PointF a, PointF b)
		{
			return new PointF(a.X + (b.X - a.X) * 0.55f,
					  a.Y + (b.Y - a.Y) * 0.55f);
		}

		// clip a borderless window to rounded corners
		public static Region WindowRegion(Size size)
		{
			using (var p = Round(new RectangleF(0, 0, size.Width,
							    size.Height),
					     Window))
				return new Region(p);
		}
	}

	// The puck's serial port. Win32 calls rather than SerialPort: on a
	// surprise unplug SerialPort's internal event thread can throw on a
	// thread we don't own and take the process down.
	class PuckLink
	{
		const uint GenericRW = 0xC0000000, OpenExisting = 3, SetDtr = 5;
		readonly App app;
		// set by the window thread, sent by the serial thread between
		// reads: a hung window stops asking, and the puck gives the
		// controller back to the PC
		volatile bool beat, release;

		// keep the controller's input to the Guide (practice mode)
		public void Beat()
		{
			beat = true;
		}

		// give it back to the PC at once
		public void Release()
		{
			release = true;
		}

		public PuckLink(App app)
		{
			this.app = app;
		}

		public void Start()
		{
			var t = new Thread(Run);
			t.IsBackground = true;
			t.Start();
		}

		// pid.codes test ID 1209:0001; the CDC function's port name is in
		// its "Device Parameters" key. Only ports present right now count.
		static string FindPort()
		{
			var present = new HashSet<string>(
				SerialPort.GetPortNames(),
				StringComparer.OrdinalIgnoreCase);
			using (var usb = Registry.LocalMachine.OpenSubKey(
				       @"SYSTEM\CurrentControlSet\Enum\USB")) {
				if (usb == null)
					return null;
				foreach (string dev in usb.GetSubKeyNames()) {
					if (!dev.StartsWith(
						    "VID_1209&PID_0001",
						    StringComparison.OrdinalIgnoreCase))
						continue;
					using (var dk = usb.OpenSubKey(dev)) {
						foreach (string inst in dk.GetSubKeyNames()) {
							using (var p = dk.OpenSubKey(
								       inst +
								       @"\Device Parameters")) {
								if (p == null)
									continue;
								var name = p.GetValue(
									"PortName") as string;
								if (name != null &&
								    present.Contains(name))
									return name;
							}
						}
					}
				}
			}
			return null;
		}

		void Run()
		{
			while (true) {
				try {
					string port = FindPort();
					if (port != null)
						Session(port);
				} catch (Exception) {
					// unplugged, or held by another program
				}
				app.Post(delegate { app.OnDisconnected(); });
				Thread.Sleep(1000);
			}
		}

		void Session(string port)
		{
			using (var h = Native.CreateFile(@"\\.\" + port, GenericRW,
							 0, IntPtr.Zero,
							 OpenExisting, 0,
							 IntPtr.Zero)) {
				if (h.IsInvalid)
					return;
				// a read returns as soon as anything arrives, or after
				// 100 ms with nothing (so beats go out promptly)
				var to = new Native.CommTimeouts();
				to.ReadInterval = uint.MaxValue;
				to.ReadMultiplier = uint.MaxValue;
				to.ReadConstant = 100;
				to.WriteConstant = 500;
				Native.SetCommTimeouts(h, ref to);
				// the firmware streams only while a host holds DTR
				Native.EscapeCommFunction(h, SetDtr);
				Write(h, "~m\n");
				app.Post(delegate { app.OnConnected(port); });

				var keymap = new int[2, 9, 8];
				var buf = new byte[512];
				var line = new StringBuilder();
				int read;
				while (Native.ReadFile(h, buf, buf.Length, out read,
						       IntPtr.Zero)) {
					if (release) {
						release = beat = false;
						Write(h, "~h\n");
					}
					if (beat) {
						beat = false;
						Write(h, "~g\n");
					}
					for (int i = 0; i < read; i++) {
						if (buf[i] != '\n') {
							line.Append((char)buf[i]);
							continue;
						}
						try {
							Handle(line.ToString().Trim(),
							       keymap);
						} catch (FormatException) {
							// a garbled line: skip it, keep the link
						} catch (IndexOutOfRangeException) {
						}
						line.Clear();
					}
				}
			}
		}

		static void Write(SafeFileHandle h, string s)
		{
			var b = Encoding.ASCII.GetBytes(s);
			int n;
			Native.WriteFile(h, b, b.Length, out n, IntPtr.Zero);
		}

		void Handle(string line, int[,,] keymap)
		{
			var f = line.Split(new[] { ' ' },
					   StringSplitOptions.RemoveEmptyEntries);
			if (f.Length == 0)
				return;
			if (f[0] == "KM" && f.Length == 11) {
				int pad = int.Parse(f[1]), zone = int.Parse(f[2]);
				for (int k = 0; k < 8; k++)
					keymap[pad, zone, k] = Convert.ToInt32(f[3 + k],
									       16);
			} else if (f[0] == "KM" && f.Length == 2 && f[1] == "end") {
				var copy = (int[,,])keymap.Clone();
				app.Post(delegate { app.OnKeymap(copy); });
			} else if (f[0] == "KS" && (f.Length == 4 || f.Length == 5)) {
				// older firmware sends no shift field
				var st = new PuckState(int.Parse(f[1]), int.Parse(f[2]),
						       int.Parse(f[3]),
						       f.Length == 5 && f[4] != "0");
				app.Post(delegate { app.OnState(st); });
			} else if (f[0] == "KK" && f.Length == 3) {
				int pad = int.Parse(f[1]), zone = int.Parse(f[2]);
				app.Post(delegate { app.OnPress(pad, zone); });
			} else if (f[0] == "KB" && f.Length == 12) {
				var inp = new InputState();
				inp.Buttons = Convert.ToUInt32(f[1], 16);
				inp.LT = int.Parse(f[2]);
				inp.RT = int.Parse(f[3]);
				inp.LX = int.Parse(f[4]);
				inp.LY = int.Parse(f[5]);
				inp.RX = int.Parse(f[6]);
				inp.RY = int.Parse(f[7]);
				inp.LPX = int.Parse(f[8]);
				inp.LPY = int.Parse(f[9]);
				inp.RPX = int.Parse(f[10]);
				inp.RPY = int.Parse(f[11]);
				app.Post(delegate { app.OnInput(inp); });
			} else if (f[0] == "KT" && f.Length == 7) {
				var st = new CtlStatus();
				st.Connected = f[1] == "1";
				st.Battery = int.Parse(f[2]);
				st.Charge = int.Parse(f[3]);
				st.Rssi = int.Parse(f[4]);
				st.KeymapOn = f[5] == "1";
				st.KeymapLow = f[6] == "1";
				app.Post(delegate { app.OnStatus(st); });
			} else if (f.Length == 1 && f[0] == "KG") {
				app.Post(delegate { app.ToggleGuide(); });
			} else if (f.Length == 1 && f[0] == "KI") {
				app.Post(delegate { app.InstallChord(); });
			}
		}
	}

	// --demo: each layer in turn; in the keyboard layer the left thumb
	// steps around while the right one types
	static class Demo
	{
		public static void Start(App app)
		{
			var t = new Thread(delegate() { Run(app); });
			t.IsBackground = true;
			t.Start();
		}

		// sticks circling, triggers pulling, buttons taking turns, a thumb
		// tracing the left pad
		static void FakeInput(App app)
		{
			uint[] order = { Btn.A, Btn.B, Btn.X, Btn.Y, Btn.LB, Btn.RB,
					 Btn.L4, Btn.R5, Btn.Up, Btn.Right, Btn.Steam,
					 Btn.Select, Btn.Start, Btn.Qam, Btn.L3 };
			var clock = Stopwatch.StartNew();
			while (true) {
				double t = clock.Elapsed.TotalSeconds;
				var inp = new InputState();
				inp.Buttons = order[(int)(t * 2.5) % order.Length] |
					      Btn.LPadT;
				inp.LX = (int)(28 * Math.Cos(t * 2));
				inp.LY = (int)(28 * Math.Sin(t * 2));
				inp.RX = (int)(20 * Math.Sin(t * 3));
				inp.LT = (int)(31 * (0.5 + 0.5 * Math.Sin(t * 2.2)));
				inp.RT = (int)(31 * (0.5 + 0.5 * Math.Cos(t * 1.7)));
				inp.LPX = (int)(24 * Math.Cos(t * 1.3));
				inp.LPY = (int)(24 * Math.Sin(t * 2.6));
				app.Post(delegate { app.OnInput(inp); });
				Thread.Sleep(16);
			}
		}

		static void Run(App app)
		{
			var km = new int[2, 9, 8];
			var rows = new Dictionary<int, string>[2];
			rows[KeyOverlay.PadR] = new Dictionary<int, string> {
				{ 4, "SPRBLGNC" }, { 1, "TFMZVJDW" },
				{ 7, "780./*96" }, { 3, "\\ ]     " },
			};
			rows[KeyOverlay.PadL] = new Dictionary<int, string> {
				{ 4, "EUO.Y,AI" }, { 1, "HQ'-/;KX" },
				{ 7, "235=+-41" }, { 5, "`     [ " },
			};
			var numpad = new Dictionary<char, int> {
				{ '/', 0x54 }, { '*', 0x55 }, { '-', 0x56 },
				{ '+', 0x57 }, { '.', 0x63 },
			};
			for (int pad = 0; pad < 2; pad++) {
				foreach (var page in rows[pad]) {
					for (int i = 0; i < 8; i++) {
						char c = page.Value[i];
						int code;
						if (page.Key != 7 ||
						    !numpad.TryGetValue(c, out code))
							code = Hid.Code(c);
						km[pad, page.Key, i] = code;
					}
				}
			}
			app.Post(delegate { app.OnKeymap(km); });
			app.Post(delegate { app.OnConnected("demo"); });
			var demoStatus = new CtlStatus();
			demoStatus.Connected = true;
			demoStatus.Battery = 72;
			demoStatus.Charge = 1;
			demoStatus.Rssi = 58;
			demoStatus.KeymapOn = demoStatus.KeymapLow = true;
			app.Post(delegate { app.OnStatus(demoStatus); });
			var fake = new Thread(delegate() { FakeInput(app); });
			fake.IsBackground = true;
			fake.Start();
			int[] steps = { 4, 1, 4, 7, 8, 7, 4, 3, 4, 5 };
			while (true) {
				for (int layer = 0; layer < 2; layer++) {
					var st = new PuckState(layer, 4, 4, false);
					app.Post(delegate { app.OnState(st); });
					Thread.Sleep(2500);
				}
				foreach (int lz in steps) {
					var st = new PuckState(KeyOverlay.LayerKbd, 4, lz,
							       false);
					app.Post(delegate {
						app.OnState(st);
						app.OnPress(KeyOverlay.PadR, 1);
					});
					Thread.Sleep(600);
				}
			}
		}
	}

	// The controller's status as one chip: battery gauge (a bolt while
	// charging, amber when low), percentage and signal bars on a subtle
	// backing, or "Controller asleep".
	static class StatusArt
	{
		static readonly StringFormat Left = new StringFormat {
			Alignment = StringAlignment.Near,
			LineAlignment = StringAlignment.Center,
		};

		public static void Paint(Graphics g, CtlStatus st, float x, float cy,
					 Font font)
		{
			const float h = 28;
			if (!st.Connected) {
				string t = "Controller asleep";
				float tw = g.MeasureString(t, font).Width;
				Chip(g, x, cy, tw + 16, h);
				using (var b = new SolidBrush(Theme.Dim))
					g.DrawString(t, font, b,
						     new RectangleF(x + 8, cy - h / 2, tw + 4,
								    h),
						     Left);
				return;
			}
			string pct = st.Battery > 0 ? st.Battery + "%" : "?";
			float pw = g.MeasureString(pct, font).Width;
			const float bw = 26, bh = 13;
			float bx = x + 10, px = bx + bw + 4, sx = px + pw + 2;
			Chip(g, x, cy, sx + 4 * 6 + 8 - x, h);

			bool lowBatt = st.Battery > 0 && st.Battery <= 15 &&
				       st.Charge != 2;
			Color ink = lowBatt ? Theme.Hex("e0a040") : Theme.Ink;
			var body = new RectangleF(bx, cy - bh / 2, bw, bh);
			using (var pen = new Pen(ink, 1.5f))
			using (var b = new SolidBrush(ink)) {
				using (var p = Shape.Round(body, 2.5f))
					g.DrawPath(pen, p);
				g.FillRectangle(b, body.Right + 0.5f, cy - 3, 2, 6);
				float w = (bw - 4) *
					  Math.Min(100, Math.Max(0, st.Battery)) / 100f;
				if (w > 0)
					g.FillRectangle(b, body.X + 2, body.Y + 2, w,
							bh - 4);
				if (st.Charge == 2) {
					// a bolt across the gauge
					var bolt = new PointF[] {
						new PointF(bx + 15, cy - 8),
						new PointF(bx + 8, cy + 1),
						new PointF(bx + 13, cy + 1),
						new PointF(bx + 10, cy + 8),
						new PointF(bx + 18, cy - 2),
						new PointF(bx + 13, cy - 2),
					};
					using (var outline = new Pen(Theme.Cell, 3))
						g.DrawPolygon(outline, bolt);
					using (var y = new SolidBrush(Theme.Flash))
						g.FillPolygon(y, bolt);
				}
				g.DrawString(pct, font, b,
					     new RectangleF(px, cy - h / 2, pw + 4, h),
					     Left);
			}
			for (int i = 0; i < 4; i++) {
				float bar = 4 + i * 3;
				bool lit = i < st.Bars;
				using (var b = new SolidBrush(lit ? Theme.Ink : Theme.Edge))
				using (var p = Shape.Round(
					       new RectangleF(sx + i * 6, cy + 6.5f - bar, 4,
							      bar),
					       1.5f))
					g.FillPath(b, p);
			}
		}

		static void Chip(Graphics g, float x, float cy, float w, float h)
		{
			using (var b = new SolidBrush(Theme.Cell))
			using (var p = Shape.Round(new RectangleF(x, cy - h / 2, w, h),
						   h / 2))
				g.FillPath(b, p);
		}
	}

	// One chord in a title bar's cheat sheet. Lit 0..1 while it fades after
	// use; a prompt (the install chord until installed) is outlined.
	struct Chord
	{
		public string Text;
		public double Lit;
		public bool Prompt;

		public Chord(string text, double lit, bool prompt)
		{
			Text = text;
			Lit = lit;
			Prompt = prompt;
		}
	}

	// The title bar shared by the Guide and the key map: title and status
	// chip on the left, the chords that act on the window on the right.
	static class TitleBar
	{
		public const int Height = 48;

		static readonly Font TitleFont = new Font("Segoe UI Semibold", 17f,
							  GraphicsUnit.Pixel);
		static readonly Font Small = new Font("Segoe UI Semibold", 14f,
						      GraphicsUnit.Pixel);
		static readonly StringFormat Left = new StringFormat {
			Alignment = StringAlignment.Near,
			LineAlignment = StringAlignment.Center,
		};
		static readonly StringFormat Center = new StringFormat {
			Alignment = StringAlignment.Center,
			LineAlignment = StringAlignment.Center,
		};

		public static void Paint(Graphics g, float width, string title,
					 CtlStatus st, Chord[] chords)
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			const float pad = 14, cy = Height / 2f;
			float tw = g.MeasureString(title, TitleFont).Width;
			using (var b = new SolidBrush(Theme.Ink))
				g.DrawString(title, TitleFont, b,
					     new RectangleF(pad, 0, tw + 4, Height), Left);
			StatusArt.Paint(g, st, pad + tw + 10, cy, Small);

			// right to left, so the first chord sits furthest left
			float x = width - pad;
			for (int i = chords.Length - 1; i >= 0; i--) {
				var c = chords[i];
				float w = g.MeasureString(c.Text, Small).Width + 24;
				x -= w;
				var r = new RectangleF(x, cy - 15, w, 30);
				x -= 8;
				Color fill = Theme.Mix(c.Prompt ? Theme.AccentFill : Theme.Cell,
						       Theme.Accent, c.Lit);
				using (var b = new SolidBrush(fill))
				using (var p = Shape.Round(r, 7))
					g.FillPath(b, p);
				if (c.Prompt) {
					using (var pen = new Pen(Theme.Accent, 1.5f))
					using (var p = Shape.Round(r, 7))
						g.DrawPath(pen, p);
				}
				using (var b = new SolidBrush(Theme.Mix(Theme.Ink, Color.White,
									c.Lit)))
					g.DrawString(c.Text, Small, b, r, Center);
			}
		}
	}

	// The keyboard key map: both pads' 3x3 grids for the zone the other
	// thumb rests in. Shown on its own as a small overlay, and painted into
	// the Guide window's key map panel.
	class KeyOverlay : Form
	{
		public const int LayerKbd = 2, Zones = 9, Center = 4;
		public const int PadR = 0, PadL = 1;
		// zone index (NW N NE / W C E / SW S SE) -> position in the
		// firmware's ring order (clockwise from N), -1 for the inert center
		static readonly int[] RingPos = { 7, 0, 1, 6, -1, 2, 5, 4, 3 };
		const int CellSize = 84, Gap = 6, PadGap = 60;
		const int HeaderH = TitleBar.Height;
		// hint glyphs sit this far from the cell center, toward their
		// direction
		const int MiniOff = 29;
		const int BigPt = 20, MiniPt = 9;
		// a step's glyph transition: just long enough to be seen
		const double AnimMs = 60;
		// a typed key's cell starts at full flash and fades back
		const double FlashMs = 75;
		// the zone a thumb just left fades back over LeaveMs instead of
		// snapping (the zone entered lights up at once)
		const double LeaveMs = 80;

		readonly int grid = 3 * CellSize + 2 * Gap;
		readonly Stopwatch clock = Stopwatch.StartNew();
		readonly System.Windows.Forms.Timer frames =
			new System.Windows.Forms.Timer();
		readonly Dictionary<int, Font> fonts = new Dictionary<int, Font>();
		readonly Dictionary<int, double> flashes =
			new Dictionary<int, double>();
		readonly StringFormat centered = new StringFormat {
			Alignment = StringAlignment.Center,
			LineAlignment = StringAlignment.Center,
		};

		int[,,] keymap;
		PuckState state = new PuckState(0, Center, Center, false);
		PuckState shown;
		bool hasShown, low = true, allowed;
		double animStart = -1;
		int[] animDr = new int[2], animDc = new int[2];
		bool[] animPad = new bool[2];
		int[] leftZone = { -1, -1 };
		double[] leftAt = new double[2];

		// raised whenever the key map's picture changes (the Guide window
		// repaints its copy)
		public event Action Changed;
		// a key, click or wheel turn from another keyboard or mouse
		public event Action Dismiss;

		CtlStatus status;

		public CtlStatus Status
		{
			set {
				status = value;
				if (Opacity > 0)
					Invalidate(new Rectangle(0, 0, ClientSize.Width,
								 HeaderH));
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			RawSource.Register(Handle);
		}

		protected override void WndProc(ref Message m)
		{
			const int WmInput = 0xFF;
			if (m.Msg == WmInput && Dismiss != null &&
			    RawSource.Dismisses(m.LParam))
				Dismiss();
			base.WndProc(ref m);
		}

		public KeyOverlay()
		{
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			TopMost = true;
			BackColor = Theme.Bg;
			Opacity = 0;
			ClientSize = new Size(2 * grid + PadGap + 40,
					      grid + 84 + HeaderH);
			SetStyle(ControlStyles.AllPaintingInWmPaint |
					 ControlStyles.OptimizedDoubleBuffer |
					 ControlStyles.UserPaint,
				 true);
				frames.Interval = 5;
			frames.Tick += delegate { FrameTick(); };
			Place();
		}

		// layered + transparent: clicks pass through; noactivate and
		// toolwindow: never takes focus, stays out of the taskbar and
		// Alt-Tab
		protected override CreateParams CreateParams
		{
			get {
				var cp = base.CreateParams;
				cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80 | 0x8;
				return cp;
			}
		}

		protected override bool ShowWithoutActivation
		{
			get { return true; }
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Region = Shape.WindowRegion(Size);
		}

		public bool HasKeymap
		{
			get { return keymap != null; }
		}

		// the key grids and legend, below this overlay's title bar
		public Size KeysSize
		{
			get {
				return new Size(ClientSize.Width,
						ClientSize.Height - HeaderH);
			}
		}

		// key map on and the Guide closed: the overlay shows itself while
		// both thumbs are down
		public bool Allowed
		{
			get { return allowed; }
			set {
				allowed = value;
				UpdateOpacity();
			}
		}

		void UpdateOpacity()
		{
			bool show = allowed && state.Layer == LayerKbd && keymap != null;
			Opacity = show ? 0.92 : 0;
		}

		void Repaint()
		{
			Invalidate();
			if (Changed != null)
				Changed();
		}

		Font FontOf(int pt, bool bold)
		{
			int key = pt * 2 + (bold ? 1 : 0);
			Font f;
			if (!fonts.TryGetValue(key, out f)) {
				f = new Font("Segoe UI", pt,
					     bold ? FontStyle.Bold : FontStyle.Regular,
					     GraphicsUnit.Point);
				fonts[key] = f;
			}
			return f;
		}

		public void Place()
		{
			var s = Screen.PrimaryScreen.Bounds;
			// centered on one or two thirds of the screen height: off
			// the middle, where text is typed, without straying far
			int y = (low ? 2 * s.Height : s.Height) / 3 - Height / 2;
			Location = new Point(s.X + (s.Width - Width) / 2, s.Y + y);
		}

		// the puck owns the position (Guide + left pad click)
		public bool Low
		{
			set {
				if (value == low)
					return;
				low = value;
				Place();
				Invalidate();
			}
		}

		public void OnKeymap(int[,,] km)
		{
			keymap = km;
			OnState(state);
		}

		public void OnState(PuckState st)
		{
			var prev = shown;
			bool hadPrev = hasShown;
			state = shown = st;
			hasShown = true;
			UpdateOpacity();
			animStart = -1;
			// The other thumb stepping one zone: each new glyph grows
			// out of the hint that announced it. Labels are final at
			// once, so the animation never holds back what a click
			// types.
			bool any = false;
			bool leaving = false;
			if (st.Layer == LayerKbd && hadPrev && prev.Layer == LayerKbd) {
				for (int pad = 0; pad < 2; pad++) {
					int o = OwnZone(prev, pad);
					if (o != OwnZone(st, pad)) {
						leftZone[pad] = o;
						leftAt[pad] = clock.Elapsed.TotalMilliseconds;
						leaving = true;
					}
				}
			}
			if (leaving)
				frames.Start();
			if (st.Layer == LayerKbd && hadPrev && prev.Layer == LayerKbd) {
				for (int pad = 0; pad < 2; pad++) {
					int o = OtherZone(prev, pad), n = OtherZone(st, pad);
					int dr = n / 3 - o / 3, dc = n % 3 - o % 3;
					animPad[pad] = o != n && Math.Abs(dr) <= 1 &&
						       Math.Abs(dc) <= 1;
					animDr[pad] = dr;
					animDc[pad] = dc;
					any |= animPad[pad];
				}
			}
			if (any) {
				animStart = clock.Elapsed.TotalMilliseconds;
				frames.Start();
			}
			Repaint();
		}

		public void OnPress(int pad, int zone)
		{
			if (pad < 0 || pad > 1 || zone < 0 || zone >= Zones)
				return;
			flashes[pad * Zones + zone] = clock.Elapsed.TotalMilliseconds;
			frames.Start();
			Repaint();
		}

		static int OtherZone(PuckState st, int pad)
		{
			return pad == PadR ? st.ZoneL : st.ZoneR;
		}

		static int OwnZone(PuckState st, int pad)
		{
			return pad == PadR ? st.ZoneR : st.ZoneL;
		}

		void FrameTick()
		{
			double now = clock.Elapsed.TotalMilliseconds;
			if (animStart >= 0 && now - animStart >= AnimMs)
				animStart = -1;
			var done = new List<int>();
			foreach (var kv in flashes)
				if (now - kv.Value >= FlashMs)
					done.Add(kv.Key);
			foreach (int k in done)
				flashes.Remove(k);
			bool fading = false;
			for (int pad = 0; pad < 2; pad++)
				fading |= leftZone[pad] >= 0 &&
					  now - leftAt[pad] < LeaveMs;
			if (animStart < 0 && flashes.Count == 0 && !fading)
				frames.Stop();
			Repaint();
		}

		protected override void OnPaint(PaintEventArgs pe)
		{
			var g = pe.Graphics;
			g.Clear(Theme.Bg);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen(Theme.Edge, 2))
			using (var bp = Shape.Round(new RectangleF(1, 1,
								  ClientSize.Width - 2,
								  ClientSize.Height - 2),
						    Shape.Window - 1))
				g.DrawPath(pen, bp);
			if (keymap == null || state.Layer != LayerKbd)
				return;
			TitleBar.Paint(g, ClientSize.Width, "Key map", status,
				       new[] { new Chord(low ? "Guide + left pad: move up" :
							       "Guide + left pad: move down",
							 0, false) });
			g.TranslateTransform(0, HeaderH);
			PaintKeys(g, true, true);
		}

		// The key grids in KeysSize coordinates; the Guide window scales
		// them into its panel. Without glyphs: just the empty grids (the
		// Guide shows them so while the thumbs are not both down).
		public void PaintKeys(Graphics g, bool legend, bool glyphs)
		{
			if (keymap == null)
				return;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			double now = clock.Elapsed.TotalMilliseconds;
			double e = 1;
			if (animStart >= 0) {
				double p = Math.Min(1, (now - animStart) / AnimMs);
				// ease out: most of the way in the first frame
				e = 1 - Math.Pow(1 - p, 3);
			}
			for (int pad = 0; pad < 2; pad++)
				PaintPad(g, pad, now, e, glyphs);
			if (!legend)
				return;
			using (var b = new SolidBrush(Theme.Dim))
				g.DrawString("LT Bksp   LB Tab   RB Enter   RT Space\n" +
						     "L4 Shift   L5 Ctrl   R4 Win   R5 Alt",
					     FontOf(10, false), b,
					     new RectangleF(0, KeysSize.Height - 60,
							    KeysSize.Width, 60),
					     centered);
		}

		void PaintPad(Graphics g, int pad, double now, double e,
			      bool glyphs)
		{
			int x0 = pad == PadL ? 20 : 20 + grid + PadGap;
			int other = OtherZone(state, pad), ro = other / 3,
			    co = other % 3;
			bool anim = animStart >= 0 && animPad[pad];
			bool live = glyphs && state.Layer == LayerKbd;
			for (int z = 0; z < Zones; z++) {
				int x = x0 + (z % 3) * (CellSize + Gap);
				int y = 20 + (z / 3) * (CellSize + Gap);
				float cx = x + CellSize / 2f, cy = y + CellSize / 2f;
				bool here = live && z == OwnZone(state, pad);
				// how lit the cell is: 1 under the thumb, easing out
				// from 1 for the zone it just left
				double lit = here ? 1 : 0;
				if (live && !here && z == leftZone[pad]) {
					double t = (now - leftAt[pad]) / LeaveMs;
					if (t < 1)
						lit = (1 - t) * (1 - t);
				}
				Color fill = Theme.Mix(Theme.Cell, Theme.AccentFill, lit);
				double t0;
				if (live && flashes.TryGetValue(pad * Zones + z, out t0))
					fill = Theme.Mix(Theme.Flash, fill,
							 Math.Min(1, (now - t0) / FlashMs));
				// the grid's outer corners are rounded like the pad itself
				const float big = 22;
				using (var cell = Shape.Round(
					       new RectangleF(x, y, CellSize, CellSize),
					       z == 0 ? big : Shape.Small,
					       z == 2 ? big : Shape.Small,
					       z == 8 ? big : Shape.Small,
					       z == 6 ? big : Shape.Small)) {
					using (var b = new SolidBrush(fill))
						g.FillPath(b, cell);
					using (var pen = new Pen(Theme.Mix(Theme.Edge,
									   Theme.Accent,
									   lit),
								 (float)(2 + 2 * lit)))
						g.DrawPath(pen, cell);
				}
				if (!glyphs)
					continue;

				int pos = RingPos[z];
				string label = pos >= 0 ?
					Hid.Label(keymap[pad, other, pos], state.Shift) :
					"";
				Color ink = label.Length > 0 ? Theme.Ink : Theme.Dim;
				if (label.Length == 0)
					label = "\u00b7";
				float bx = cx, by = cy;
				Font font = FontOf(BigPt, true);
				if (anim && z != Center) {
					float k = (float)((1 - e) * MiniOff);
					bx += animDc[pad] * k;
					by += animDr[pad] * k;
					int pt = (int)Math.Round(MiniPt +
								 (BigPt - MiniPt) * e);
					font = FontOf(pt, e > 0.5);
					ink = Theme.Mix(Theme.Mini, ink, e);
				}
				DrawCentered(g, label, font, ink, bx, by);

				if (pos < 0)
					continue;
				// one zone further from where the other thumb rests now
				for (int d = 0; d < Zones; d++) {
					int r2 = ro + d / 3 - 1, c2 = co + d % 3 - 1;
					if (d == Center || r2 < 0 || r2 > 2 || c2 < 0 ||
					    c2 > 2)
						continue;
					string alt = Hid.Label(keymap[pad, r2 * 3 + c2, pos],
							       state.Shift);
					if (alt.Length > 0)
						DrawCentered(g, alt, FontOf(MiniPt, false),
							     Theme.Mini,
							     cx + (d % 3 - 1) * MiniOff,
							     cy + (d / 3 - 1) * MiniOff);
				}
			}
		}

		void DrawCentered(Graphics g, string s, Font f, Color c, float x,
				  float y)
		{
			using (var b = new SolidBrush(c))
				g.DrawString(s, f, b,
					     new RectangleF(x - 40, y - 30, 80, 60),
					     centered);
		}
	}

	// The controller, drawn from measurements of the real one (top view,
	// triggers up). Units: 1 = 0.12 mm or so; the body spans x 30..1434,
	// y 68..1088, symmetric about x = 732. Bumper and trigger are drawn
	// lifted off each shoulder and turned toward the viewer; the back
	// buttons, on the underside, dashed and pulled out from under the pads.
	static class ControllerArt
	{
		const float MX = 1464;
		static readonly PointF Start = new PointF(732, 68);
		// left half of the outline, top centre to notch centre: cubic
		// segments (control, control, end) through measured landmarks
		static readonly float[][] Segs = {
			new float[] { 560, 64, 450, 70, 390, 72 },
			new float[] { 300, 58, 190, 70, 150, 150 },
			new float[] { 100, 250, 40, 480, 30, 700 },
			new float[] { 24, 950, 90, 1092, 230, 1088 },
			new float[] { 300, 1084, 330, 1010, 390, 950 },
			new float[] { 420, 915, 440, 884, 480, 880 },
			new float[] { 560, 877, 660, 875, 732, 875 },
		};

		// world box that holds the controller plus its label columns
		public static readonly RectangleF World =
			new RectangleF(-400, -175, 2264, 1290);

		static PointF P(float x, float y)
		{
			return new PointF(x, y);
		}

		static PointF M(PointF p)
		{
			return new PointF(MX - p.X, p.Y);
		}

		public static GraphicsPath Body()
		{
			var path = new GraphicsPath();
			var ends = new List<PointF> { Start };
			PointF cur = Start;
			foreach (var s in Segs) {
				var e = P(s[4], s[5]);
				path.AddBezier(cur, P(s[0], s[1]), P(s[2], s[3]), e);
				ends.Add(e);
				cur = e;
			}
			for (int i = Segs.Length - 1; i >= 0; i--) {
				var s = Segs[i];
				path.AddBezier(M(P(s[4], s[5])), M(P(s[2], s[3])),
					       M(P(s[0], s[1])), M(ends[i]));
			}
			path.CloseFigure();
			return path;
		}

		static GraphicsPath RoundRect(float cx, float cy, float w, float h,
					      float r)
		{
			var p = new GraphicsPath();
			float x = cx - w / 2, y = cy - h / 2, d = 2 * r;
			p.AddArc(x, y, d, d, 180, 90);
			p.AddArc(x + w - d, y, d, d, 270, 90);
			p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
			p.AddArc(x, y + h - d, d, d, 90, 90);
			p.CloseFigure();
			return p;
		}

		static void Quad(GraphicsPath p, PointF a, PointF c, PointF b)
		{
			p.AddBezier(a, P(a.X + 2f / 3 * (c.X - a.X),
					a.Y + 2f / 3 * (c.Y - a.Y)),
				    P(b.X + 2f / 3 * (c.X - b.X),
				      b.Y + 2f / 3 * (c.Y - b.Y)),
				    b);
		}

		// a tapered, round-topped tab, as the trigger looks from the top
		static GraphicsPath Trigger()
		{
			var p = new GraphicsPath();
			p.AddLine(-98, -32, -88, -100);
			Quad(p, P(-88, -100), P(-80, -150), P(0, -152));
			Quad(p, P(0, -152), P(80, -150), P(88, -100));
			p.AddLine(88, -100, 98, -32);
			Quad(p, P(98, -32), P(98, -24), P(88, -24));
			p.AddLine(88, -24, -88, -24);
			Quad(p, P(-88, -24), P(-98, -24), P(-98, -32));
			p.CloseFigure();
			return p;
		}

		static GraphicsPath Cross(float cx, float cy, float a)
		{
			var p = new GraphicsPath();
			int[] u = { -1, 1, 1, 3, 3, 1, 1, -1, -1, -3, -3, -1 };
			int[] v = { -3, -3, -1, -1, 1, 1, 3, 3, 1, 1, -1, -1 };
			var pts = new PointF[12];
			for (int i = 0; i < 12; i++)
				pts[i] = P(cx + u[i] * a, cy + v[i] * a);
			p.Dispose();
			return Shape.RoundPoly(pts, 9);
		}

		public static readonly PointF PadL = P(449, 628), PadR = P(1015, 628);

		// layer: 0 no pad, 1 one pad, 2 both pads (decides what the pads
		// show); colours are already faded for inactive panels. Pressed
		// controls fill with hiFill and outline with hiPen; sticks move,
		// triggers fill as they are pulled, a dot follows each thumb on
		// its pad.
		public static void Draw(Graphics g, int layer, Color ink,
					Color hidden, Color padFill, Font padFont,
					InputState inp, Color hiFill, Color hiLine)
		{
			using (var body = new Pen(ink, 5))
			using (var pen = new Pen(ink, 4))
			using (var thin = new Pen(ink, 2.5f))
			using (var dash = new Pen(hidden, 4))
			using (var hiPen = new Pen(hiLine, 5))
			using (var inkB = new SolidBrush(ink))
			using (var fillB = new SolidBrush(padFill))
			using (var hiB = new SolidBrush(hiFill)) {
				dash.DashPattern = new float[] { 3, 2.5f };
				body.LineJoin = LineJoin.Round;
				pen.LineJoin = LineJoin.Round;
				hiPen.LineJoin = LineJoin.Round;
				using (var p = Body())
					g.DrawPath(body, p);
				for (int s = -1; s <= 1; s += 2) {
					bool left = s < 0;
					var st = g.Save();
					var c = left ? P(285, 0) : M(P(285, 0));
					g.TranslateTransform(c.X, c.Y);
					g.RotateTransform(s * 6);
					using (var bp = RoundRect(0, 17, 230, 58, 24)) {
						bool on = inp.Has(left ? Btn.LB : Btn.RB);
						if (on)
							g.FillPath(hiB, bp);
						g.DrawPath(on ? hiPen : pen, bp);
					}
					using (var t = Trigger()) {
						int pull = Math.Min(left ? inp.LT : inp.RT, 31);
						if (pull > 0) {
							// fills from the base as it is pulled
							var clip = g.Save();
							g.SetClip(t, CombineMode.Intersect);
							float h = 130f * pull / 31;
							g.FillRectangle(hiB, -100, -22 - h, 200, h);
							g.Restore(clip);
						}
						g.DrawPath(inp.Has(left ? Btn.LT : Btn.RT) ?
								   hiPen :
								   pen,
							   t);
					}
					g.Restore(st);
				}

				float a = 236f / 6, dx = 277, dy = 252;
				var arms = new[] {
					new { M = Btn.Up, X = dx - a, Y = dy - 3 * a },
					new { M = Btn.Down, X = dx - a, Y = dy + a },
					new { M = Btn.Left, X = dx - 3 * a, Y = dy - a },
					new { M = Btn.Right, X = dx + a, Y = dy - a },
				};
				foreach (var arm in arms)
					if (inp.Has(arm.M))
						g.FillRectangle(hiB, arm.X, arm.Y, 2 * a, 2 * a);
				using (var d = Cross(dx, dy, a))
					g.DrawPath(inp.Has(Btn.Dpad) ? hiPen : pen, d);

				var pills = new[] {
					new { M = Btn.Select, X = 472f, Y = 148f, W = 92f,
					      H = 40f },
					new { M = Btn.Start, X = 992f, Y = 148f, W = 92f,
					      H = 40f },
					new { M = Btn.Qam, X = 732f, Y = 637f, W = 120f,
					      H = 46f },
				};
				foreach (var pl in pills)
					using (var r = RoundRect(pl.X, pl.Y, pl.W, pl.H,
								 pl.H / 2)) {
						bool on = inp.Has(pl.M);
						if (on)
							g.FillPath(hiB, r);
						g.DrawPath(on ? hiPen : pen, r);
					}

				var round = new[] {
					new { M = Btn.Steam, X = 732f, Y = 247f, R = 50f },
					new { M = Btn.Y, X = 1182f, Y = 162f, R = 40f },
					new { M = Btn.X, X = 1097f, Y = 240f, R = 40f },
					new { M = Btn.B, X = 1267f, Y = 240f, R = 40f },
					new { M = Btn.A, X = 1182f, Y = 320f, R = 40f },
				};
				foreach (var r in round) {
					bool on = inp.Has(r.M);
					if (on)
						g.FillEllipse(hiB, r.X - r.R, r.Y - r.R, 2 * r.R,
							      2 * r.R);
					g.DrawEllipse(on ? hiPen : pen, r.X - r.R, r.Y - r.R,
						      2 * r.R, 2 * r.R);
				}

				// sticks: the cap moves with the stick within its ring
				const float travel = 37f / 32;
				var sticks = new[] {
					new { X = 517f, Y = 342f, DX = inp.LX, DY = inp.LY,
					      M = Btn.L3 },
					new { X = 947f, Y = 342f, DX = inp.RX, DY = inp.RY,
					      M = Btn.R3 },
				};
				foreach (var sk in sticks) {
					g.DrawEllipse(pen, sk.X - 97, sk.Y - 97, 194, 194);
					float cx = sk.X + sk.DX * travel;
					float cy = sk.Y - sk.DY * travel;
					bool click = inp.Has(sk.M);
					bool moved = Math.Abs(sk.DX) > 2 || Math.Abs(sk.DY) > 2;
					if (click)
						g.FillEllipse(hiB, cx - 60, cy - 60, 120, 120);
					g.DrawEllipse(click || moved ? hiPen : thin, cx - 60,
						      cy - 60, 120, 120);
				}

				for (int i = 0; i < 2; i++) {
					var c = i == 0 ? PadL : PadR;
					bool touch = inp.Has(i == 0 ? Btn.LPadT : Btn.RPadT);
					bool click = inp.Has(i == 0 ? Btn.LPadC : Btn.RPadC);
					var st = g.Save();
					g.TranslateTransform(c.X, c.Y);
					g.RotateTransform(i == 0 ? 3 : -3);
					using (var r = RoundRect(0, 0, 312, 312, 34)) {
						// tinted only while a thumb is on it
						if (click)
							g.FillPath(hiB, r);
						else if (touch)
							g.FillPath(fillB, r);
						g.DrawPath(click ? hiPen : pen, r);
					}
					if (layer == 2) {
						for (int k = 1; k < 3; k++) {
							float o = -156 + k * 104;
							g.DrawLine(thin, o, -156, o, 156);
							g.DrawLine(thin, -156, o, 156, o);
						}
					}
					if (layer == 1) {
						var sf = new StringFormat {
							Alignment = StringAlignment.Center,
							LineAlignment = StringAlignment.Center,
						};
						g.DrawString(i == 0 ? "scroll" : "cursor", padFont,
							     inkB,
							     new RectangleF(-156, -40, 312, 80), sf);
					}
					if (touch) {
						float px = (i == 0 ? inp.LPX : inp.RPX) * 140f / 32;
						float py = -(i == 0 ? inp.LPY : inp.RPY) * 140f / 32;
						g.FillEllipse(hiB, px - 24, py - 24, 48, 48);
						g.DrawEllipse(hiPen, px - 24, py - 24, 48, 48);
					}
					g.Restore(st);
				}

				var backs = new[] {
					new { M = Btn.L4, P = P(195, 640) },
					new { M = Btn.L5, P = P(200, 836) },
					new { M = Btn.R4, P = M(P(195, 640)) },
					new { M = Btn.R5, P = M(P(200, 836)) },
				};
				foreach (var bb in backs) {
					bool on = inp.Has(bb.M);
					if (on)
						g.FillEllipse(hiB, bb.P.X - 60, bb.P.Y - 60, 120,
							      120);
					g.DrawEllipse(on ? hiPen : dash, bb.P.X - 60,
						      bb.P.Y - 60, 120, 120);
				}
			}
		}
	}

	// What each button does, per layer, and where its label goes.
	static class Labels
	{
		public enum Side { Left, Right, Top, Bottom }

		public class Slot
		{
			public string Id;
			public PointF Anchor;
			public Side Side;
			public float At;

			public Slot(string id, float ax, float ay, Side side, float at)
			{
				Id = id;
				Anchor = new PointF(ax, ay);
				Side = side;
				At = at;
			}
		}

		// label columns: left text ends at x -40, right starts at 1504;
		// top/bottom labels are centred on their x
		public static readonly Slot[] Slots = {
			new Slot("lt", 190, -88, Side.Left, -95),
			new Slot("lb", 172, 17, Side.Left, 25),
			new Slot("dpad", 159, 252, Side.Left, 252),
			new Slot("ls", 450, 410, Side.Left, 440),
			new Slot("l4", 135, 640, Side.Left, 640),
			new Slot("l5", 140, 836, Side.Left, 836),
			new Slot("rt", 1274, -88, Side.Right, -95),
			new Slot("rb", 1292, 17, Side.Right, 25),
			new Slot("face", 1307, 240, Side.Right, 241),
			new Slot("rs", 1014, 410, Side.Right, 440),
			new Slot("r4", 1329, 640, Side.Right, 640),
			new Slot("r5", 1324, 836, Side.Right, 836),
			new Slot("back", 472, 128, Side.Top, 520),
			new Slot("xbox", 732, 197, Side.Top, 732),
			new Slot("start", 992, 128, Side.Top, 944),
			new Slot("qam", 732, 660, Side.Bottom, 732),
		};

		static Dictionary<string, string> Desk(string qam, string ls,
						       string rs, string face)
		{
			var d = new Dictionary<string, string> {
				{ "lt", "Backspace" }, { "lb", "Tab" },
				{ "dpad", "Arrows" }, { "l4", "Shift" },
				{ "l5", "Ctrl" }, { "rt", "Space" },
				{ "rb", "Enter" }, { "r4", "Win" }, { "r5", "Alt" },
				{ "back", "Esc" }, { "start", "Menu key" },
			};
			if (qam != null)
				d["qam"] = qam;
			if (ls != null)
				d["ls"] = ls;
			if (rs != null)
				d["rs"] = rs;
			if (face != null)
				d["face"] = face;
			return d;
		}

		// whether the control a label names is in use right now
		public static bool Active(string id, InputState i)
		{
			switch (id) {
			case "lt": return i.Has(Btn.LT) || i.LT > 2;
			case "rt": return i.Has(Btn.RT) || i.RT > 2;
			case "lb": return i.Has(Btn.LB);
			case "rb": return i.Has(Btn.RB);
			case "dpad": return i.Has(Btn.Dpad);
			case "ls":
				return i.Has(Btn.L3) || Math.Abs(i.LX) > 2 ||
				       Math.Abs(i.LY) > 2;
			case "rs":
				return i.Has(Btn.R3) || Math.Abs(i.RX) > 2 ||
				       Math.Abs(i.RY) > 2;
			case "l4": return i.Has(Btn.L4);
			case "l5": return i.Has(Btn.L5);
			case "r4": return i.Has(Btn.R4);
			case "r5": return i.Has(Btn.R5);
			case "back": return i.Has(Btn.Select);
			case "start": return i.Has(Btn.Start);
			case "xbox": return i.Has(Btn.Steam);
			case "qam": return i.Has(Btn.Qam);
			}
			return false;
		}

		// face labels are "Y|X|B|A": drawn as a diamond like the buttons
		public static readonly Dictionary<string, string>[] Text = {
			new Dictionary<string, string> {
				{ "lt", "LT" }, { "lb", "LB" }, { "dpad", "D-pad" },
				{ "ls", "Stick · L3" }, { "l4", "B" }, { "l5", "Y" },
				{ "rt", "RT" }, { "rb", "RB" }, { "face", "Y|X|B|A" },
				{ "rs", "Stick · R3" }, { "r4", "A" }, { "r5", "X" },
				{ "back", "Back" }, { "xbox", "Guide" },
				{ "start", "Start" }, { "qam", "Print Screen" },
			},
			Desk("Play / pause", "Vol · track\nClick: middle",
			     "Click: back", "PgUp|Home|End|PgDn"),
			Keyboard(),
		};

		// with both thumbs on the pads the d-pad and the two small center
		// buttons are out of reach, so the keyboard layer leaves them out
		static Dictionary<string, string> Keyboard()
		{
			var d = Desk(null, null, null, null);
			d.Remove("dpad");
			d.Remove("back");
			d.Remove("start");
			return d;
		}
	}

	// The Guide: no pad and one pad on the left; both pads on the right,
	// with the key map under it. The layer your thumbs select is outlined;
	// the others stay readable but faded.
	class GuideWindow : Form
	{
		const int HeaderH = TitleBar.Height, Gap = 16;
		readonly KeyOverlay keys;
		readonly StringFormat center = new StringFormat {
			Alignment = StringAlignment.Center,
			LineAlignment = StringAlignment.Center,
		};
		readonly StringFormat right = new StringFormat {
			Alignment = StringAlignment.Far,
			LineAlignment = StringAlignment.Center,
		};
		readonly StringFormat left = new StringFormat {
			Alignment = StringAlignment.Near,
			LineAlignment = StringAlignment.Center,
		};
		readonly Font title = new Font("Segoe UI Semibold", 18f,
					       GraphicsUnit.Pixel);
		readonly Font head = new Font("Segoe UI Semibold", 16f,
					      GraphicsUnit.Pixel);
		readonly Font small = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
		readonly Font label = new Font("Segoe UI", 46f, GraphicsUnit.Pixel);
		readonly Font labelB = new Font("Segoe UI Semibold", 46f,
						GraphicsUnit.Pixel);
		PuckState state;
		InputState input;
		readonly bool installed;
		bool dirty;
		readonly System.Windows.Forms.Timer frames =
			new System.Windows.Forms.Timer();

		public GuideWindow(KeyOverlay keys, bool installed)
		{
			frames.Interval = 16;
			frames.Tick += delegate {
				if ((dirty || Fading || Lighting) && Opacity > 0)
					Invalidate();
				dirty = false;
			};
			frames.Start();
			this.keys = keys;
			this.installed = installed;
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			TopMost = true;
			BackColor = Theme.Bg;
			Opacity = 0;
			SetStyle(ControlStyles.AllPaintingInWmPaint |
					 ControlStyles.OptimizedDoubleBuffer |
					 ControlStyles.UserPaint |
					 ControlStyles.ResizeRedraw,
				 true);
			var s = Screen.PrimaryScreen.WorkingArea;
			int w = s.Width * 9 / 10, h = s.Height * 9 / 10;
			Bounds = new Rectangle(s.X + (s.Width - w) / 2,
					       s.Y + (s.Height - h) / 2, w, h);
			keys.Changed += delegate {
				if (Opacity > 0)
					Invalidate(KeyArea());
			};
		}

		protected override CreateParams CreateParams
		{
			get {
				var cp = base.CreateParams;
				cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80 | 0x8;
				return cp;
			}
		}

		protected override bool ShowWithoutActivation
		{
			get { return true; }
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Region = Shape.WindowRegion(Size);
		}

		public void OnInput(InputState inp)
		{
			input = inp;
			dirty = true;
		}

		CtlStatus status;

		public void OnStatus(CtlStatus st)
		{
			status = st;
			Invalidate(new Rectangle(0, 0, ClientSize.Width, HeaderH));
		}


		// The panel just left fades back over LeaveMs instead of snapping,
		// which looked jarring when switching layers quickly; the panel
		// entered lights up at once.
		const double LeaveMs = 80, Dim = 0.4;
		readonly Stopwatch fade = Stopwatch.StartNew();
		int leaving = -1;

		bool Fading
		{
			get {
				return leaving >= 0 &&
				       fade.Elapsed.TotalMilliseconds < LeaveMs;
			}
		}

		// how lit a panel is: 1 in use, Dim faded, in between while left
		double Level(int layer)
		{
			if (state.Layer == layer)
				return 1;
			if (layer != leaving || !Fading)
				return Dim;
			double t = fade.Elapsed.TotalMilliseconds / LeaveMs;
			// ease out: most of the fall early, a soft finish
			return Dim + (1 - Dim) * (1 - t) * (1 - t);
		}

		public void OnState(PuckState st)
		{
			if (st.Layer != state.Layer) {
				leaving = state.Layer;
				fade.Restart();
			}
			bool layerChanged = st.Layer != state.Layer;
			state = st;
			if (layerChanged && Opacity > 0)
				Invalidate();
		}

		// 0 no pad (top left), 1 one pad (bottom left), 2 both pads (the
		// whole right column)
		Rectangle Panel(int i)
		{
			int w = (ClientSize.Width - 3 * Gap) / 2;
			int h = (ClientSize.Height - HeaderH - 2 * Gap) / 2;
			if (i == 2)
				return new Rectangle(2 * Gap + w, HeaderH, w, 2 * h + Gap);
			return new Rectangle(Gap, HeaderH + i * (h + Gap), w, h);
		}

		// where a panel's controller drawing goes: below its title, the
		// height of a left-column panel
		Rectangle ArtArea(int i)
		{
			var q = Panel(i);
			return new Rectangle(q.X + 10, q.Y + 36, q.Width - 20,
					     Panel(0).Height - 44);
		}

		Rectangle KeyArea()
		{
			var q = Panel(2);
			var a = ArtArea(2);
			return new Rectangle(a.X, a.Bottom + 8, a.Width,
					     q.Bottom - a.Bottom - 20);
		}

		protected override void OnPaint(PaintEventArgs pe)
		{
			var g = pe.Graphics;
			g.Clear(Theme.Bg);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen(Theme.Edge, 2))
			using (var bp = Shape.Round(new RectangleF(1, 1,
								  ClientSize.Width - 2,
								  ClientSize.Height - 2),
						    Shape.Window - 1))
				g.DrawPath(pen, bp);
			PaintHeader(g);
			string[] names = { "No pad", "One pad", "Both pads" };
			for (int i = 0; i < 3; i++)
				if (pe.ClipRectangle.IntersectsWith(Panel(i)))
					PaintController(g, i, names[i]);
			PaintKeyMap(g, KeyArea());
		}

		void PaintHeader(Graphics g)
		{
			var chords = new List<Chord> {
				new Chord("Guide: close", 0, false),
				// each says what it will do now
				new Chord(status.KeymapOn ? "Guide + right pad: hide key map" :
							    "Guide + right pad: show key map",
					  LitLevel(ChordKeymap), false),
				new Chord(status.KeymapLow ? "Guide + left pad: key map up" :
							     "Guide + left pad: key map down",
					  LitLevel(ChordMove), false),
			};
			// until installed, the next step is outlined
			if (!installed)
				chords.Add(new Chord("Guide + Start: install",
						     LitLevel(ChordInstall), true));
			TitleBar.Paint(g, ClientSize.Width, "LizardKing Guide", status,
				       chords.ToArray());
		}

		// A chord's chip lights up when the puck reports its effect, then
		// fades over LitMs.
		public const int ChordKeymap = 0, ChordMove = 1, ChordInstall = 2;
		const double LitMs = 600;
		readonly Stopwatch clock = Stopwatch.StartNew();
		readonly double[] litAt = { -LitMs, -LitMs, -LitMs };

		public void Light(int chord)
		{
			litAt[chord] = clock.Elapsed.TotalMilliseconds;
			dirty = true;
		}

		double LitLevel(int chord)
		{
			double t = (clock.Elapsed.TotalMilliseconds - litAt[chord]) / LitMs;
			return t >= 1 ? 0 : (1 - t) * (1 - t);
		}

		bool Lighting
		{
			get {
				foreach (int c in new[] { ChordKeymap, ChordMove,
							  ChordInstall })
					if (LitLevel(c) > 0)
						return true;
				return false;
			}
		}

		static GraphicsPath Rounded(RectangleF r, float rad)
		{
			var p = new GraphicsPath();
			float d = 2 * rad;
			p.AddArc(r.X, r.Y, d, d, 180, 90);
			p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
			p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
			p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
			p.CloseFigure();
			return p;
		}

		void PaintFrame(Graphics g, Rectangle q, double level, int pads,
				string name)
		{
			double f = (level - Dim) / (1 - Dim);
			using (var p = Rounded(q, 12)) {
				using (var b = new SolidBrush(Theme.Panel))
					g.FillPath(b, p);
				using (var pen = new Pen(Theme.Mix(Theme.Edge, Theme.Accent,
								   f),
							 (float)(1.5 + 1.5 * f)))
					g.DrawPath(pen, p);
			}
			Color ink = Theme.Mix(Theme.Dim, Theme.Ink, f);
			// two small squares: which pads are touched
			for (int i = 0; i < 2; i++) {
				var r = new Rectangle(q.X + 14 + i * 20, q.Y + 14, 14, 14);
				bool filled = i < pads;
				using (var b = new SolidBrush(ink))
				using (var pen = new Pen(ink, 1.5f))
				using (var sq = Shape.Round(r, 3)) {
					if (filled)
						g.FillPath(b, sq);
					else
						g.DrawPath(pen, sq);
				}
			}
			using (var b = new SolidBrush(ink))
				g.DrawString(name, head, b,
					     new RectangleF(q.X + 60, q.Y + 6, 300, 30),
					     left);
		}

		void PaintController(Graphics g, int layer, string name)
		{
			var q = Panel(layer);
			bool on = state.Layer == layer;
			double k = Level(layer);
			PaintFrame(g, q, k, layer, name);
			// faded panels mix their ink toward the panel colour
			Color ink = Theme.Mix(Theme.Panel, Theme.Ink, k);
			Color hidden = Theme.Mix(Theme.Panel, Theme.Dim, k);
			Color padFill = Theme.Mix(Theme.Panel, Theme.AccentFill, k);
			Color lead = Theme.Mix(Theme.Panel, Theme.Mini, k);
			Color hiFill = Theme.Mix(Theme.Panel, Theme.AccentFill,
						 Math.Max(k, 0.8));
			Color hiLine = Theme.Mix(Theme.Panel, Theme.Flash, Math.Max(k, 0.7));

			RectangleF area = ArtArea(layer);
			var w = ControllerArt.World;
			float s = Math.Min(area.Width / w.Width, area.Height / w.Height);
			var st = g.Save();
			g.SetClip(q);
			g.TranslateTransform(area.X + (area.Width - w.Width * s) / 2,
					     area.Y + (area.Height - w.Height * s) / 2);
			g.ScaleTransform(s, s);
			g.TranslateTransform(-w.X, -w.Y);
			// only the panel for the layer in use follows the controller
			var inp = on ? input : new InputState();
			ControllerArt.Draw(g, layer, ink, hidden, padFill, label, inp,
					   hiFill, hiLine);
			PaintLabels(g, layer, ink, lead, hiLine, inp);
			g.Restore(st);
		}

		void PaintLabels(Graphics g, int layer, Color ink, Color lead,
				 Color hi, InputState inp)
		{
			var text = Labels.Text[layer];
			using (var normal = new SolidBrush(ink))
			using (var lit = new SolidBrush(hi))
			using (var pen = new Pen(lead, 2.5f)) {
				pen.DashPattern = new float[] { 3, 3 };
				foreach (var sl in Labels.Slots) {
					string t;
					if (!text.TryGetValue(sl.Id, out t))
						continue;
					var b = Labels.Active(sl.Id, inp) ? lit : normal;
					var a = sl.Anchor;
					switch (sl.Side) {
					case Labels.Side.Left:
						g.DrawLine(pen, -25, sl.At, a.X, a.Y);
						g.DrawString(t, label, b,
							     new RectangleF(-640, sl.At - 70,
									    600, 140),
							     right);
						break;
					case Labels.Side.Right:
						g.DrawLine(pen, 1489, sl.At, a.X, a.Y);
						if (sl.Id == "face")
							Diamond(g, t, normal, lit, inp, 1504,
								sl.At);
						else
							g.DrawString(t, label, b,
								     new RectangleF(1504,
										    sl.At - 70,
										    600, 140),
								     left);
						break;
					case Labels.Side.Top:
						g.DrawLine(pen, sl.At, -35, a.X, a.Y);
						g.DrawString(t, label, b,
							     new RectangleF(sl.At - 220, -110,
									    440, 70),
							     center);
						break;
					default:
						g.DrawLine(pen, sl.At, 955, a.X, a.Y);
						g.DrawString(t, label, b,
							     new RectangleF(sl.At - 300, 955,
									    600, 70),
							     center);
						break;
					}
				}
			}
		}

		// "Y|X|B|A": four words arranged like the face buttons, each lit
		// while its button is held
		void Diamond(Graphics g, string t, Brush normal, Brush lit,
			     InputState inp, float x0, float y)
		{
			var w = t.Split('|');
			Brush by = inp.Has(Btn.Y) ? lit : normal;
			Brush bx = inp.Has(Btn.X) ? lit : normal;
			Brush bb = inp.Has(Btn.B) ? lit : normal;
			Brush ba = inp.Has(Btn.A) ? lit : normal;
			bool letters = w[0].Length == 1;
			float cx = x0 + (letters ? 70 : 140);
			float dx = letters ? 50 : 18, dy = letters ? 48 : 52;
			g.DrawString(w[0], label, by,
				     new RectangleF(cx - 150, y - dy - 35, 300, 70),
				     center);
			g.DrawString(w[3], label, ba,
				     new RectangleF(cx - 150, y + dy - 35, 300, 70),
				     center);
			g.DrawString(w[1], label, bx,
				     new RectangleF(cx - dx - 300, y - 35, 300, 70),
				     right);
			g.DrawString(w[2], label, bb,
				     new RectangleF(cx + dx, y - 35, 300, 70), left);
		}

		void PaintKeyMap(Graphics g, Rectangle q)
		{
			bool on = state.Layer == KeyOverlay.LayerKbd;
			double f = (Level(KeyOverlay.LayerKbd) - Dim) / (1 - Dim);
			if (!keys.HasKeymap)
				return;
			RectangleF area = q;
			var ks = keys.KeysSize;
			float s = Math.Min(area.Width / ks.Width,
					   area.Height / ks.Height);
			var st = g.Save();
			g.SetClip(q);
			g.TranslateTransform(area.X + (area.Width - ks.Width * s) / 2,
					     area.Y + (area.Height - ks.Height * s) / 2);
			g.ScaleTransform(s, s);
			keys.PaintKeys(g, false, f > 0);
			g.Restore(st);
			if (!on) {
				// fade like the other inactive panels
				int veil = (int)Math.Round(150 * (1 - f));
				using (var b = new SolidBrush(Color.FromArgb(veil,
									    Theme.Panel)))
					g.FillRectangle(b, area);
			}
		}
	}

	// A brief word in the middle of the screen when a key map chord is used
	// with the Guide closed
	class Toast : Form
	{
		readonly System.Windows.Forms.Timer hide =
			new System.Windows.Forms.Timer();
		readonly Font font = new Font("Segoe UI Semibold", 30f,
					      GraphicsUnit.Pixel);
		string text = "";

		public Toast()
		{
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			TopMost = true;
			BackColor = Theme.AccentFill;
			Opacity = 0;
			Size = new Size(360, 84);
			var s = Screen.PrimaryScreen.WorkingArea;
			Location = new Point(s.X + (s.Width - Width) / 2,
					     s.Y + s.Height / 6);
			hide.Interval = 1100;
			hide.Tick += delegate {
				hide.Stop();
				Opacity = 0;
			};
		}

		protected override CreateParams CreateParams
		{
			get {
				var cp = base.CreateParams;
				cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80 | 0x8;
				return cp;
			}
		}

		protected override bool ShowWithoutActivation
		{
			get { return true; }
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Region = Shape.WindowRegion(Size);
		}

		public void Say(string t)
		{
			text = t;
			Invalidate();
			Opacity = 0.95;
			hide.Stop();
			hide.Start();
		}

		protected override void OnPaint(PaintEventArgs pe)
		{
			var g = pe.Graphics;
			g.Clear(Theme.AccentFill);
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen(Theme.Accent, 2))
			using (var bp = Shape.Round(new RectangleF(1, 1, Width - 2,
								  Height - 2),
						    Shape.Window - 1))
				g.DrawPath(pen, bp);
			using (var b = new SolidBrush(Theme.Ink))
				g.DrawString(text, font, b, ClientRectangle,
					     new StringFormat {
						     Alignment = StringAlignment.Center,
						     LineAlignment = StringAlignment.Center,
					     });
		}
	}

	// Per-user install: a copy in %LOCALAPPDATA%\Programs\LizardKing and an
	// ordinary HKCU Run entry, both visible in Settings > Apps > Startup and
	// removed by Uninstall. Nothing needs admin rights.
	static class Install
	{
		const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
		const string RunName = "LizardKing Guide";

		static string Programs
		{
			get {
				return Path.Combine(
					Environment.GetFolderPath(
						Environment.SpecialFolder.LocalApplicationData),
					"Programs");
			}
		}

		public static string Dir
		{
			get { return Path.Combine(Programs, "LizardKing"); }
		}

		public static string Exe
		{
			get { return Path.Combine(Dir, "LizardKing Guide.exe"); }
		}

		public static bool IsInstalledCopy
		{
			get {
				return string.Equals(Path.GetFullPath(Application.ExecutablePath),
						     Path.GetFullPath(Exe),
						     StringComparison.OrdinalIgnoreCase);
			}
		}

		public static void StartAtSignIn(bool on)
		{
			using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
				if (on)
					k.SetValue(RunName, "\"" + Exe + "\"");
				else
					k.DeleteValue(RunName, false);
			}
		}

		// The drive's files are read-only and a copy keeps that flag, so an
		// earlier copy would refuse to be overwritten: clear it on both
		// ends.
		public static void CopyTo(string path)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			if (File.Exists(path))
				File.SetAttributes(path, FileAttributes.Normal);
			File.Copy(Application.ExecutablePath, path, true);
			File.SetAttributes(path, FileAttributes.Normal);
		}

		public static void Uninstall()
		{
			StartAtSignIn(false);
			// the running exe can't delete itself: a short-lived cmd
			// removes the folder once this process has exited
			var psi = new ProcessStartInfo("cmd.exe",
				"/c timeout /t 2 /nobreak >nul & rd /s /q \"" + Dir + "\"");
			psi.CreateNoWindow = true;
			psi.UseShellExecute = false;
			Process.Start(psi);
		}
	}

	// Owns the windows and the two switches; everything here runs on the UI
	// thread.
	class App
	{
		public readonly KeyOverlay Keys = new KeyOverlay();
		readonly GuideWindow guide;
		readonly Toast toast = new Toast();
		readonly bool installed;
		readonly System.Windows.Forms.Timer beats =
			new System.Windows.Forms.Timer();
		bool guideUp;
		// the key map switches as last reported, once there is a report
		bool hasSwitches;
		CtlStatus last;
		// where the cursor was before the Guide parked it
		Point cursor;

		// the serial link, once started (none in --demo)
		public PuckLink Link;

		public event Action<string> StatusChanged;

		public App(bool guideUp, bool installed)
		{
			this.installed = installed;
			this.guideUp = guideUp;
			guide = new GuideWindow(Keys, installed);
			// reaching for another keyboard or mouse puts the Guide away,
			// a safety net beside the Guide button
			Keys.Dismiss += delegate {
				if (this.guideUp)
					ToggleGuide();
			};
			// practice mode: while the Guide is up the controller's input
			// goes to it, not to the apps behind it
			beats.Interval = 250;
			beats.Tick += delegate {
				if (this.guideUp && Link != null)
					Link.Beat();
			};
			beats.Start();
		}

		public bool Installed
		{
			get { return installed; }
		}

		public void ShowWindows()
		{
			guide.Show();
			toast.Show();
			if (guideUp)
				Park();
			Apply();
		}

		public void Post(MethodInvoker f)
		{
			try {
				if (Keys.IsHandleCreated && !Keys.IsDisposed)
					Keys.BeginInvoke(f);
			} catch (InvalidOperationException) {
				// the window closed between the check and the call
			}
		}

		void Apply()
		{
			Keys.Allowed = !guideUp && (!hasSwitches || last.KeymapOn);
			guide.Opacity = guideUp ? 0.995 : 0;
			if (Link != null) {
				if (guideUp)
					Link.Beat();
				else
					Link.Release();
			}
			if (guideUp)
				guide.Invalidate();
		}

		// The puck sends no cursor movement while the Guide is up, so the
		// cursor would sit on top of it: park it in a corner, outside the
		// window, and put it back afterwards.
		void Park()
		{
			cursor = Cursor.Position;
			var s = Screen.PrimaryScreen.Bounds;
			Cursor.Position = new Point(s.Right - 1, s.Bottom - 1);
		}

		public void ToggleGuide()
		{
			guideUp = !guideUp;
			if (guideUp)
				Park();
			else
				Cursor.Position = cursor;
			Apply();
		}

		public void InstallChord()
		{
			guide.Light(GuideWindow.ChordInstall);
			if (installed)
				toast.Say("Installed");
			else
				Program.InstallNow();
		}

		public void OnConnected(string port)
		{
			if (StatusChanged != null)
				StatusChanged("Connected (" + port + ")");
		}

		public void OnDisconnected()
		{
			OnState(new PuckState(0, KeyOverlay.Center, KeyOverlay.Center,
					      false));
			if (StatusChanged != null)
				StatusChanged("Waiting for the puck");
		}

		public void OnKeymap(int[,,] km)
		{
			Keys.OnKeymap(km);
		}

		public void OnState(PuckState st)
		{
			Keys.OnState(st);
			guide.OnState(st);
		}

		public void OnPress(int pad, int zone)
		{
			Keys.OnPress(pad, zone);
		}

		public void OnInput(InputState inp)
		{
			guide.OnInput(inp);
		}

		public void OnStatus(CtlStatus st)
		{
			if (hasSwitches && st.KeymapOn != last.KeymapOn) {
				guide.Light(GuideWindow.ChordKeymap);
				if (!guideUp)
					toast.Say(st.KeymapOn ? "Key map on" : "Key map off");
			}
			if (hasSwitches && st.KeymapLow != last.KeymapLow) {
				guide.Light(GuideWindow.ChordMove);
				if (!guideUp)
					toast.Say(st.KeymapLow ? "Key map bottom" :
								 "Key map top");
			}
			hasSwitches = true;
			last = st;
			Keys.Low = st.KeymapLow;
			Keys.Allowed = !guideUp && st.KeymapOn;
			guide.OnStatus(st);
			Keys.Status = st;
			if (StatusChanged != null)
				StatusChanged(st.Text);
		}
	}

	static class Program
	{
		const string MutexName = @"Local\LizardKingGuide";
		const string QuitName = @"Local\LizardKingGuide.Quit";

		[STAThread]
		static int Main(string[] args)
		{
			try {
				Native.SetProcessDpiAwareness(2);
			} catch (Exception) {
			}
			// Windows' default 15.6 ms timer tick would stretch the
			// animation frames
			Native.timeBeginPeriod(1);
			Application.EnableVisualStyles();
			bool demo = Array.IndexOf(args, "--demo") >= 0;
			bool fresh = Array.IndexOf(args, "--installed") >= 0;

			// Started from the puck's drive: run from a temporary copy,
			// since the drive can be hidden while the Guide is open.
			// Nothing is installed until the user asks.
			if (!demo && FromRemovableDrive()) {
				try {
					string tmp = Path.Combine(Path.GetTempPath(),
								  "LizardKing",
								  "LizardKing Guide.exe");
					Install.CopyTo(tmp);
					Process.Start(tmp);
				} catch (Exception ex) {
					MessageBox.Show("Could not start the Guide:\n" +
							ex.Message, "LizardKing Guide");
					return 1;
				}
				return 0;
			}

			// A newer launch (an update, a dev build) takes over from a
			// running copy instead of competing for the serial port.
			bool created;
			// the demo never opens the serial port, so it runs alongside
			// a real copy instead of taking over
			var mutex = new Mutex(true, demo ? MutexName + ".demo" :
							  MutexName, out created);
			var quit = new EventWaitHandle(false, EventResetMode.AutoReset,
						       QuitName);
			if (!created) {
				quit.Set();
				try {
					if (!mutex.WaitOne(5000))
						return 1;
				} catch (AbandonedMutexException) {
				}
			}
			bool installed = Install.IsInstalledCopy;
			// opened by hand (from the drive, or just installed) it starts
			// with the Guide up; started at sign-in, closed
			Run(demo, quit, installed, !installed || fresh);
			mutex.ReleaseMutex();
			return 0;
		}

		static bool FromRemovableDrive()
		{
			try {
				var root = Path.GetPathRoot(Application.ExecutablePath);
				return new DriveInfo(root).DriveType == DriveType.Removable;
			} catch (Exception) {
				return false;
			}
		}

		public static void InstallNow()
		{
			var r = MessageBox.Show(
				"Install the LizardKing Guide for this Windows user?\n\n" +
				"It is copied to your user folder and starts when you " +
				"sign in. Hold the Guide button to open the Guide. " +
				"Remove it any time from its tray icon (Uninstall) or " +
				"turn it off in Settings > Apps > Startup.",
				"LizardKing Guide", MessageBoxButtons.OKCancel,
				MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
				MessageBoxOptions.DefaultDesktopOnly);
			if (r != DialogResult.OK)
				return;
			try {
				Install.CopyTo(Install.Exe);
				Install.StartAtSignIn(true);
				// the installed copy takes over from this one
				Process.Start(Install.Exe, "--installed");
			} catch (Exception ex) {
				MessageBox.Show("Could not install the Guide:\n" +
						ex.Message, "LizardKing Guide",
						MessageBoxButtons.OK, MessageBoxIcon.Error,
						MessageBoxDefaultButton.Button1,
						MessageBoxOptions.DefaultDesktopOnly);
			}
		}

		static void Run(bool demo, EventWaitHandle quit, bool installed,
				bool guideUp)
		{
			var app = new App(guideUp, installed);
			var tray = new NotifyIcon();
			tray.Icon = MakeIcon();
			tray.ContextMenuStrip = Menu(app, tray);
			tray.Text = Tip("Waiting for the puck");
			// the tray is a signpost to the Guide button, not a second
			// way in
			tray.MouseClick += delegate(object o, MouseEventArgs e) {
				if (e.Button == MouseButtons.Left)
					tray.ShowBalloonTip(5000, "LizardKing Guide", Hint,
							    ToolTipIcon.Info);
			};
			tray.Visible = true;

			app.Keys.Shown += delegate {
				app.ShowWindows();
				if (demo) {
					Demo.Start(app);
				} else {
					app.Link = new PuckLink(app);
					app.Link.Start();
				}
				var t = new Thread(delegate() {
					quit.WaitOne();
					app.Post(delegate { Application.Exit(); });
				});
				t.IsBackground = true;
				t.Start();
			};
			Application.Run(app.Keys);
			tray.Visible = false;
			tray.Dispose();
		}

		const string Hint = "Hold the Guide button to open the Guide.";

		// NotifyIcon refuses a tooltip of 64 characters or more
		static string Tip(string status)
		{
			string t = status + "\n" + Hint;
			return t.Length < 64 ? t : t.Substring(0, 63);
		}

		// only what has no chord: Uninstall and Quit
		static ContextMenuStrip Menu(App app, NotifyIcon tray)
		{
			var m = new ContextMenuStrip();
			var status = new ToolStripMenuItem("Waiting for the puck");
			status.Enabled = false;
			app.StatusChanged += delegate(string s) {
				status.Text = s;
				tray.Text = Tip(s);
			};
			var uninstall = new ToolStripMenuItem("Uninstall...");
			uninstall.Click += delegate {
				var r = MessageBox.Show(
					"Remove the LizardKing Guide from this user account?\n\n" +
					"This deletes " + Install.Dir + " and its startup entry.",
					"LizardKing Guide", MessageBoxButtons.OKCancel,
					MessageBoxIcon.Question);
				if (r != DialogResult.OK)
					return;
				Install.Uninstall();
				Application.Exit();
			};
			uninstall.Visible = app.Installed;
			var quit = new ToolStripMenuItem("Quit");
			quit.Click += delegate { Application.Exit(); };
			m.Items.AddRange(new ToolStripItem[] {
				status, new ToolStripSeparator(), uninstall, quit,
			});
			return m;
		}

		// two 3x3 pads, drawn rather than shipped as a file
		static Icon MakeIcon()
		{
			using (var bmp = new Bitmap(32, 32))
			using (var g = Graphics.FromImage(bmp)) {
				g.Clear(Color.Transparent);
				using (var bg = new SolidBrush(Theme.Bg))
					g.FillRectangle(bg, 0, 6, 32, 20);
				using (var dot = new SolidBrush(Theme.Accent)) {
					for (int pad = 0; pad < 2; pad++)
						for (int i = 0; i < 9; i++)
							g.FillRectangle(dot,
									2 + pad * 16 + (i % 3) * 4,
									10 + (i / 3) * 4, 3, 3);
				}
				IntPtr h = bmp.GetHicon();
				var icon = (Icon)Icon.FromHandle(h).Clone();
				Native.DestroyIcon(h);
				return icon;
			}
		}
	}
}
