// LizardKing Guide for Windows (.NET Framework 4.x, which ships with Windows
// 10/11, so it runs without installing anything).
//
// The Guide teaches the controller; the controller's Guide button and its
// chords are the only interface, and nothing on screen is clickable. Two
// switches:
//   Guide    a window showing the controller twice (gamepad, keyboard)
//            with every button labelled, the key map,
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
// another keyboard or mouse (software-made included), the controller
// disconnecting, or a click on the tray icon puts the Guide away.
//
// It reads the puck's serial port. The key table comes from the firmware
// ("~m" -> "KM" lines), so it always matches the flashed layout. "KS" lines
// report the layer, both thumbs' zones and Shift; "KT" carries the status
// and the key map switches, which
// the puck owns; "KG" turns the Guide on or off; "KI" (Guide + Start) asks
// to install.
//
// Started from the puck's drive it never installs by itself: it copies
// itself to a temporary folder and runs from there until the user asks to
// install (Guide + Start).
//
// Build: guide/build.cmd (with Gpu.cs). --demo cycles fake states without
// a puck.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
		// a bit per pad (right 1, left 2) holding Sleep or Power
		public int Hold;

		public PuckState(int layer, int zr, int zl, bool shift)
		{
			Layer = layer;
			ZoneR = zr;
			ZoneL = zl;
			Shift = shift;
			Hold = 0;
		}
	}

	// live controller input ("KB"): buttons as the firmware's TB_* bits,
	// triggers 0..31, sticks and pad positions -32..32 (y up); Own0..2 the
	// held buttons each layer owns, by where they were pressed; MX, MY the
	// pointer's travel in counts (y up), MW the wheel's in sixteenths of a
	// notch, both wrapping at 16 bits. Ball and Wheel are the pads' surfaces
	// as drawn (see MouseMotion).
	struct InputState
	{
		public uint Buttons, Own0, Own1;
		public int LT, RT, LX, LY, RX, RY, LPX, LPY, RPX, RPY, MX, MY, MW;
		public MouseMotion.Surface Ball, Wheel;

		public uint Own(int layer)
		{
			return layer == 0 ? Own0 : Own1;
		}

		public bool Has(uint mask)
		{
			return (Buttons & mask) != 0;
		}
	}

	// status ("KT"): charge as in the controller's report 0x43 (1
	// discharging, 2 charging, 4 charged, 0 unknown); signal in -dBm, 0 = no
	// sample yet; the key map switches, owned by the puck; Dev, the puck's
	// key map or feel constants changed from the console since it booted
	struct CtlStatus
	{
		public bool Connected, KeymapLow, Dev;
		// the controller's serial, from its bond: the connected one's, else
		// the paired one's ("-" if none)
		public string Serial;
		// the key map: 0 off, 1 large, 2 small
		public int KeymapSize;
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

		// Only while the Guide is up, the one time a dismiss matters:
		// whenever the app in front doesn't take raw input itself (a
		// browser, unlike a game), every mouse report would otherwise
		// wake this window.
		public static void Listen(IntPtr window, bool on)
		{
			var d = new Native.RawInputDevice[2];
			// keyboards and mice; INPUTSINK (0x100): even while
			// unfocused; REMOVE (1) takes no window
			d[0].UsagePage = 1;
			d[0].Usage = 6;
			d[1].UsagePage = 1;
			d[1].Usage = 2;
			for (int i = 0; i < 2; i++) {
				d[i].Flags = on ? 0x100u : 1u;
				d[i].Target = on ? window : IntPtr.Zero;
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
		// wheel turn from anything but the puck. The puck's own keys (its
		// Esc included) are what the Guide is there to try out; pointer
		// movement counts for nothing. Input with no device, injected by
		// software, counts: Steam Input's desktop mouse and keyboard
		// arrive that way.
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
				bool foreign = dev == IntPtr.Zero || !IsPuck(dev);
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
	}

	static class Hid
	{
		static readonly Dictionary<int, string> Names =
			new Dictionary<int, string> {
				// ISO 9995-7 symbols where they read as well as
				// the word (Esc, Del and Ins read better as
				// words); the arrows as triangles, all head; the
				// Copilot ("AI") key as the sparkle AI features
				// share, no brand
				{ 0x28, "\u23CE" }, { 0x29, "Esc" },
				{ 0x2A, "\u232B" }, { 0x2B, "\u21E5" },
				{ 0x2C, "Space" }, { 0x2D, "-" },
				{ 0x2E, "=" }, { 0x2F, "[" }, { 0x30, "]" },
				{ 0x31, "\\" }, { 0x33, ";" }, { 0x34, "'" },
				{ 0x35, "`" }, { 0x36, "," }, { 0x37, "." },
				{ 0x38, "/" }, { 0x49, "Ins" }, { 0x4A, "\u21F1" },
				{ 0x4B, "\u21DE" }, { 0x4C, "Del" },
				{ 0x4D, "\u21F2" }, { 0x4E, "\u21DF" },
				{ 0x4F, "\u25B6" }, { 0x50, "\u25C0" },
				{ 0x51, "\u25BC" }, { 0x52, "\u25B2" },
				// numpad operators and decimal
				{ 0x54, "/" }, { 0x55, "*" }, { 0x56, "-" },
				{ 0x57, "+" }, { 0x63, "." },
				// the keys nothing else needs
				{ 0x39, "Caps" }, { 0x46, "PrtSc" },
				{ 0x47, "ScrLk" }, { 0x48, "Pause" },
				{ 0x53, "NumLk" }, { 0x58, "⏎" },
				// media keys: the puck's own codes, past the
				// keyboard page
				{ 0xF0, "\uD83D\uDD09" }, { 0xF1, "\uD83D\uDD0A" },
				{ 0xF2, "\u23EE" }, { 0xF3, "\u23ED" },
				{ 0xF4, "\u23EF" }, { 0xF5, "\uD83D\uDD07" },
				// Sleep and Power as IEC 5009 draws them: the
				// moon and the power symbol
				{ 0xEB, "\u23fe" }, { 0xEC, "\u23fb" },
				{ 0xED, "\u2726" },
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
			if ((code >= 0x54 && code <= 0x58) || code == 0x63)
				return Names[code];
			if (code >= 0x59 && code <= 0x62)
				return ((code - 0x58) % 10).ToString();
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

		[StructLayout(LayoutKind.Sequential)]
		public struct AppBarData
		{
			public uint Size;
			public IntPtr Window;
			public uint Message, Edge;
			public Rect Rect;
			public IntPtr Param;
		}

		[DllImport("shell32.dll")]
		public static extern IntPtr SHAppBarMessage(uint message,
							    ref AppBarData data);

		// Where a popup by the tray should end: above the taskbar where it
		// shows, even auto-hidden (then the working area is the whole
		// screen, and the taskbar slides up over anything at its foot).
		public static int AboveTaskbar()
		{
			const uint GetTaskbarPos = 5, EdgeBottom = 3;
			var d = new AppBarData();
			d.Size = (uint)Marshal.SizeOf(typeof(AppBarData));
			var work = Screen.PrimaryScreen.WorkingArea;
			if (SHAppBarMessage(GetTaskbarPos, ref d) == IntPtr.Zero ||
			    d.Edge != EdgeBottom)
				return work.Bottom;
			int height = d.Rect.Bottom - d.Rect.Top;
			return Math.Min(work.Bottom,
					Screen.PrimaryScreen.Bounds.Bottom - height);
		}

		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
		public static extern int CM_Locate_DevNodeW(out uint node, string id,
							    uint flags);

		// whether the device with this instance ID is present now
		public static bool DevicePresent(string id)
		{
			uint node;
			return CM_Locate_DevNodeW(out node, id, 0) == 0;
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct Rect
		{
			public int Left, Top, Right, Bottom;
		}

	}

	// Lizard greens: olive-tinted darks, an anole-green accent, a lime
	// highlight. Low battery stays amber so it never reads as "fine".
	static class Theme
	{
		public static readonly Color Bg = Hex("191e18");
		public static readonly Color Panel = Hex("1f251d");
		public static readonly Color Cell = Hex("283022");
		public static readonly Color Edge = Hex("3c4734");
		public static readonly Color Ink = Hex("e6ebde");
		public static readonly Color Dim = Hex("74806a");
		public static readonly Color Accent = Hex("6cbf3d");
		public static readonly Color AccentFill = Hex("253a1c");
		public static readonly Color Flash = Hex("b4e36f");

		// A faint scale pattern for the Guide's panels: staggered rows
		// of rounded beads, as on a lizard's back, each a shade lighter
		// than the seams around it and lit along its top, a few levels off
		// the surface, so it reads as finish and never as content. Text,
		// keys and chips sit on plain fills.
		static TextureBrush scales;

		public static void Scales(Graphics g, GraphicsPath area)
		{
			if (scales == null)
				scales = new TextureBrush(ScaleTile(), WrapMode.Tile);
			g.FillPath(scales, area);
		}

		static Bitmap ScaleTile()
		{
			// 6 x 4 scales, rows half a scale apart, a little irregular
			// so the repeat doesn't show
			const int W = 12, H = 10, Cols = 8, Rows = 6;
			var bmp = new Bitmap(W * Cols, H * Rows);
			var rnd = new Random(7);
			using (var g = Graphics.FromImage(bmp))
			using (var bead = new SolidBrush(Color.FromArgb(4, Ink)))
			using (var lit = new Pen(Color.FromArgb(6, Ink), 1f))
			using (var seam = new Pen(Color.FromArgb(17, Color.Black), 1.2f)) {
				g.SmoothingMode = SmoothingMode.AntiAlias;
				for (int r = 0; r < Rows; r++)
					for (int c = 0; c < Cols; c++) {
						float cx = c * W + (r % 2 == 1 ? W / 2f : 0);
						float cy = r * H;
						float w = W - 1 + (float)rnd.NextDouble() * 1.5f;
						float h = H + (float)rnd.NextDouble() * 1.5f;
						// drawn at each wrap so the tile joins seamlessly
						for (int dx = -1; dx <= 1; dx++)
							for (int dy = -1; dy <= 1; dy++) {
								var e = new RectangleF(
									cx + dx * bmp.Width - w / 2,
									cy + dy * bmp.Height - h / 2, w, h);
								g.FillEllipse(bead, e);
								g.DrawEllipse(seam, e);
								e.Inflate(-2.5f, -2.5f);
								g.DrawArc(lit, e, 200, 140);
							}
					}
			}
			return bmp;
		}

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

		// The latest input and state only, handed over by one posted
		// message at a time: when the window thread falls behind, stale
		// ones are dropped instead of queueing, so a Guide press is never
		// stuck behind seconds of them.
		readonly object latest = new object();
		InputState input;
		PuckState state;
		bool hasInput, hasState;
		int posted;

		void Hand(InputState? inp, PuckState? st)
		{
			lock (latest) {
				if (inp.HasValue) {
					input = inp.Value;
					hasInput = true;
				}
				if (st.HasValue) {
					state = st.Value;
					hasState = true;
				}
			}
			if (Interlocked.Exchange(ref posted, 1) == 0)
				app.Post(Deliver);
		}

		void Deliver()
		{
			posted = 0;
			bool i, s;
			InputState inp;
			PuckState st;
			lock (latest) {
				i = hasInput;
				s = hasState;
				inp = input;
				st = state;
				hasInput = hasState = false;
			}
			if (s)
				app.OnState(st);
			if (i)
				app.OnInput(inp);
		}

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
		// its "Device Parameters" key. Windows can go on listing a port
		// after its puck is unplugged, so every candidate is tried, not
		// just the first.
		static List<string> FindPorts()
		{
			var found = new List<string>();
			var present = new HashSet<string>(
				SerialPort.GetPortNames(),
				StringComparer.OrdinalIgnoreCase);
			using (var usb = Registry.LocalMachine.OpenSubKey(
				       @"SYSTEM\CurrentControlSet\Enum\USB")) {
				if (usb == null)
					return found;
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
									found.Add(name);
							}
						}
					}
				}
			}
			return found;
		}

		void Run()
		{
			while (true) {
				foreach (string port in FindPorts()) {
					try {
						Session(port);
					} catch (Exception) {
						// unplugged, or held by another program
					}
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
			} else if (f[0] == "KS" && f.Length >= 4 && f.Length <= 6) {
				// older firmware sends no shift or hold field
				var st = new PuckState(int.Parse(f[1]), int.Parse(f[2]),
						       int.Parse(f[3]),
						       f.Length >= 5 && f[4] != "0");
				st.Hold = f.Length == 6 ? int.Parse(f[5]) : 0;
				Hand(null, st);
			} else if (f[0] == "KB" && f.Length >= 14) {
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
				inp.Own0 = Convert.ToUInt32(f[12], 16);
				inp.Own1 = Convert.ToUInt32(f[13], 16);
				// a firmware of another build may send other mouse
				// fields: the buttons and sticks still light
				if (f.Length == 17) {
					inp.MX = int.Parse(f[14]);
					inp.MY = int.Parse(f[15]);
					inp.MW = int.Parse(f[16]);
				}
				Hand(inp, null);
			} else if (f[0] == "KT" && f.Length == 9) {
				var st = new CtlStatus();
				st.Connected = f[1] == "1";
				st.Battery = int.Parse(f[2]);
				st.Charge = int.Parse(f[3]);
				st.Rssi = int.Parse(f[4]);
				st.KeymapSize = int.Parse(f[5]);
				st.KeymapLow = f[6] == "1";
				st.Dev = f[7] == "1";
				st.Serial = f[8];
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
				inp.Own0 = inp.Own1 = inp.Buttons & ~Btn.LPadT;
				inp.LX = (int)(28 * Math.Cos(t * 2));
				inp.LY = (int)(28 * Math.Sin(t * 2));
				inp.RX = (int)(20 * Math.Sin(t * 3));
				inp.LT = (int)(31 * (0.5 + 0.5 * Math.Sin(t * 2.2)));
				inp.RT = (int)(31 * (0.5 + 0.5 * Math.Cos(t * 1.7)));
				inp.LPX = (int)(24 * Math.Cos(t * 1.3));
				inp.LPY = (int)(24 * Math.Sin(t * 2.6));
				// the wheel turning back and forth, and the pointer
				// swept by a thumb on the right pad, slow then fast
				inp.MW = (int)(400 * Math.Sin(t * 0.8));
				if (t % 6 < 4) {
					inp.Buttons |= Btn.RPadT;
					double r = t % 6 < 2 ? 30 : 900;
					inp.MX = (int)(r * Math.Cos(t * 3));
					inp.MY = (int)(r * Math.Sin(t * 3));
				}
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
			demoStatus.KeymapSize = 1;
			demoStatus.KeymapLow = true;
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
					app.Post(delegate { app.OnState(st); });
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
			float bx = x + 10, px = bx + BatteryW + 4, sx = px + pw + 2;
			Chip(g, x, cy, sx + 4 * 6 + 8 - x, h);
			Battery(g, st, bx, cy);
			using (var b = new SolidBrush(BatteryInk(st)))
				g.DrawString(pct, font, b,
					     new RectangleF(px, cy - h / 2, pw + 4, h),
					     Left);
			Signal(g, st, sx, cy);
		}

		// The small key map's: the battery over the signal, no percentage,
		// in a tile centred on (cx, cy)
		public static void PaintStacked(Graphics g, CtlStatus st, float cx,
						float cy)
		{
			using (var b = new SolidBrush(Theme.Cell))
			using (var p = Shape.Round(new RectangleF(cx - 22, cy - 24, 44,
								   48),
						   10))
				g.FillPath(b, p);
			if (!st.Connected)
				return;
			Battery(g, st, cx - BatteryW / 2 - 1, cy - 9);
			Signal(g, st, cx - 11, cy + 10);
		}

		const float BatteryW = 26;

		static Color BatteryInk(CtlStatus st)
		{
			bool low = st.Battery > 0 && st.Battery <= 15 && st.Charge != 2;
			return low ? Theme.Hex("e0a040") : Theme.Ink;
		}

		// the gauge, its left edge at bx, centred on cy
		static void Battery(Graphics g, CtlStatus st, float bx, float cy)
		{
			const float bw = BatteryW, bh = 13;
			Color ink = BatteryInk(st);
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
			}
		}

		// four bars from sx, their feet just below cy
		static void Signal(Graphics g, CtlStatus st, float sx, float cy)
		{
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

	// One chord in a title bar's cheat sheet; a prompt (the install chord
	// until installed) is outlined.
	struct Chord
	{
		public string Text;
		public bool Prompt;

		public Chord(string text, bool prompt)
		{
			Text = text;
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
		public static readonly Font Small = new Font("Segoe UI Semibold", 14f,
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
				using (var b = new SolidBrush(c.Prompt ? Theme.AccentFill :
									 Theme.Cell))
				using (var p = Shape.Round(r, 7))
					g.FillPath(b, p);
				if (c.Prompt) {
					using (var pen = new Pen(Theme.Accent, 1.5f))
					using (var p = Shape.Round(r, 7))
						g.DrawPath(pen, p);
				}
				using (var b = new SolidBrush(Theme.Ink))
					g.DrawString(c.Text, Small, b, r, Center);
			}
		}
	}

	// A window that shows only what it draws, on top of everything, and
	// never takes a click or the focus. Its picture goes to the compositor
	// through a Surface rather than the window's own GDI surface (see
	// Gpu.cs), so it keeps up over a game holding the GPU. Redrawing is a
	// posted message of its own rather than WM_PAINT, which has nothing to
	// paint here (it even arrives with an empty clip).
	abstract class Overlay : Form
	{
		readonly double opacity;
		Surface surface;
		// the picture, transparent outside the rounded outline
		Bitmap canvas;
		Region outline;
		// what needs redrawing, and whether a redraw is already posted
		Rectangle dirty;
		bool posted, up;
		// once the GPU can be had again, after Gpu.Open's wait
		readonly System.Windows.Forms.Timer retry =
			new System.Windows.Forms.Timer { Interval = 2100 };

		protected Overlay(Color bg, double opacity)
		{
			retry.Tick += delegate {
				retry.Stop();
				Post();
			};
			FormBorderStyle = FormBorderStyle.None;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.Manual;
			TopMost = true;
			BackColor = bg;
			this.opacity = opacity;
		}

		// layered + transparent: clicks pass through; noactivate and
		// toolwindow: never takes focus, stays out of the taskbar and
		// Alt-Tab; no redirection bitmap: Windows keeps no GDI surface
		// for it, the Surface is all there is
		protected override CreateParams CreateParams
		{
			get {
				var cp = base.CreateParams;
				cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80 | 0x8 |
					      0x200000;
				return cp;
			}
		}

		protected override bool ShowWithoutActivation
		{
			get { return true; }
		}

		public bool Up
		{
			get { return up; }
			set {
				if (value == up)
					return;
				up = value;
				if (up)
					Redraw();
				else
					TakeDown();
			}
		}

		void TakeDown()
		{
			if (surface == null)
				return;
			// a surface on a lost device goes, and its picture with it
			if (surface.Stale) {
				Drop();
				return;
			}
			try {
				surface.Hide();
			} catch (Exception) {
				Lose();
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			Connect();
			if (up)
				Post();
		}

		// A surface on the current device, made if need be; false when
		// the GPU can't be had just now. A lost device is let go here
		// and reopened, so a driver reset costs a frame, not the window.
		bool Connect()
		{
			if (surface != null && !surface.Stale)
				return true;
			Drop();
			if (!Gpu.Open()) {
				retry.Start();
				return false;
			}
			try {
				surface = new Surface(Handle, opacity);
				return true;
			} catch (Exception) {
				Lose();
				return false;
			}
		}

		void Drop()
		{
			if (surface != null)
				surface.Dispose();
			surface = null;
		}

		void Lose()
		{
			Drop();
			Gpu.Lost();
			// the new surface needs the whole picture
			dirty = ClientRectangle;
			retry.Start();
		}

		// clip: what changed; g is clipped to it, and the rest of the
		// picture is kept from before
		protected abstract void Draw(Graphics g, Rectangle clip);

		// Marks part of the picture changed, to be redrawn once the
		// messages already queued are handled; what changes meanwhile
		// joins that redraw, so a stream of input draws as fast as
		// drawing goes (the Guide about 60 a second) and no faster. Down,
		// nothing happens: the puck streams input and status while a game
		// is played, and coming up redraws it all anyway.
		protected void Redraw(Rectangle r)
		{
			if (!up)
				return;
			dirty = dirty.IsEmpty ? r : Rectangle.Union(dirty, r);
			Post();
		}

		protected void Redraw()
		{
			Redraw(ClientRectangle);
		}

		void Post()
		{
			// while the GPU is away, the retry brings the redraw
			if (posted || retry.Enabled || !IsHandleCreated)
				return;
			posted = true;
			BeginInvoke(new MethodInvoker(Present));
		}

		void Present()
		{
			posted = false;
			if (!up || ClientSize.Width == 0 || ClientSize.Height == 0 ||
			    !Connect())
				return;
			var all = new Rectangle(Point.Empty, ClientSize);
			var clip = Rectangle.Intersect(dirty, all);
			dirty = Rectangle.Empty;
			if (canvas == null || canvas.Size != ClientSize) {
				if (canvas != null) {
					canvas.Dispose();
					outline.Dispose();
				}
				canvas = new Bitmap(ClientSize.Width, ClientSize.Height,
						    PixelFormat.Format32bppPArgb);
				using (var p = Shape.Round(all, Shape.Window))
					outline = new Region(p);
				clip = all;
			}
			if (clip.IsEmpty)
				return;
			using (var g = Graphics.FromImage(canvas)) {
				g.SetClip(outline, CombineMode.Replace);
				g.SetClip(clip, CombineMode.Intersect);
				g.Clear(BackColor);
				Draw(g, clip);
			}
			try {
				surface.Show(canvas);
			} catch (Exception) {
				Lose();
			}
		}
	}

	// The keyboard key map: both pads' 3x3 grids for the zone the other
	// thumb rests in. Shown on its own as a small overlay, and painted into
	// the Guide window's key map panel.
	class KeyOverlay : Overlay
	{
		public const int LayerKbd = 1, Zones = 9, Center = 4;
		public const int PadR = 0, PadL = 1;
		// zone index (NW N NE / W C E / SW S SE) -> position in the
		// firmware's ring order (clockwise from N), -1 for the inert center
		static readonly int[] RingPos = { 7, 0, 1, 6, -1, 2, 5, 4, 3 };
		// The grids' geometry. Large has the hints round each key; small,
		// the expert's, each zone's own key alone, half the padding round
		// it, and the battery and signal stacked between the pads.
		struct Geo
		{
			public int Cell, Gap, PadGap, Margin, Pt;
			public float Corner;
			public bool Hints;

			public int Grid
			{
				get { return 3 * Cell + 2 * Gap; }
			}
		}
		static readonly Geo Large = new Geo {
			Cell = 84, Gap = 6, PadGap = 60, Margin = 20, Pt = 20,
			Corner = 22, Hints = true,
		};
		static readonly Geo Compact = new Geo {
			Cell = 44, Gap = 4, PadGap = 64, Margin = 12, Pt = 15,
			Corner = 12, Hints = false,
		};
		const int HeaderH = TitleBar.Height;
		// hint glyphs sit this far from the cell center, toward their
		// direction
		const int MiniOff = 29;
		const int MiniPt = 9;

		readonly Dictionary<string, Font> fonts =
			new Dictionary<string, Font>();
		readonly StringFormat centered = new StringFormat {
			Alignment = StringAlignment.Center,
			LineAlignment = StringAlignment.Center,
			FormatFlags = StringFormatFlags.NoWrap,
		};

		int[,,] keymap;
		PuckState state = new PuckState(0, Center, Center, false);
		// Sleep and Power act only once held as long as the firmware's
		// SYS_HOLD_MS: their cell fills meanwhile, so a key that seems to
		// do nothing shows it is on its way
		const int HoldMs = 1000;
		readonly Stopwatch held = new Stopwatch();
		readonly System.Windows.Forms.Timer holding =
			new System.Windows.Forms.Timer { Interval = 15 };
		bool low = true, allowed;

		// raised whenever the key map's picture changes (the Guide window
		// repaints its copy)
		public event Action Changed;
		// a key, click or wheel turn from another keyboard or mouse
		public event Action Dismiss;
		// a device arrived or left (WM_DEVICECHANGE)
		public event Action DevicesChanged;

		CtlStatus status;

		public CtlStatus Status
		{
			set {
				status = value;
				Redraw(new Rectangle(0, 0, ClientSize.Width,
						     HeaderH));
			}
		}

		protected override void WndProc(ref Message m)
		{
			const int WmInput = 0xFF, WmDeviceChange = 0x219;
			if (m.Msg == WmInput && Dismiss != null &&
			    RawSource.Dismisses(m.LParam))
				Dismiss();
			if (m.Msg == WmDeviceChange && DevicesChanged != null)
				DevicesChanged();
			base.WndProc(ref m);
		}

		public KeyOverlay() : base(Theme.Bg, 0.92)
		{
			FitSize();
			Place();
			holding.Tick += delegate {
				if (held.ElapsedMilliseconds >= HoldMs)
					holding.Stop();
				Redraw();
				if (Changed != null)
					Changed();
			};
		}

		public bool HasKeymap
		{
			get { return keymap != null; }
		}

		// the key grids alone, with their margin: what the Guide centers
		public Size GridSize
		{
			get {
				return new Size(2 * Large.Grid + Large.PadGap +
							2 * Large.Margin,
						Large.Grid + 2 * Large.Margin);
			}
		}

		// the key grids and legend, below this overlay's title bar
		public Size KeysSize
		{
			get { return new Size(GridSize.Width, GridSize.Height + 44); }
		}

		// key map on and the Guide closed: the overlay shows itself while
		// both thumbs are down
		public bool Allowed
		{
			get { return allowed; }
			set {
				allowed = value;
				UpdateShown();
			}
		}

		void UpdateShown()
		{
			Up = allowed && state.Layer == LayerKbd && keymap != null;
		}

		Font FontOf(int pt, bool bold)
		{
			return FontOf("Segoe UI", pt, bold);
		}

		// GDI+ doesn't fall back to another font for a missing glyph, so
		// the key symbols name the font that has them
		static string FamilyOf(string s)
		{
			foreach (char c in s) {
				if (char.IsSurrogate(c))
					return "Segoe UI Emoji";
				if (c >= 0x21D0)
					return "Segoe UI Symbol";
			}
			return "Segoe UI";
		}

		Font FontOf(string family, int pt, bool bold)
		{
			string key = family + pt + (bold ? "b" : "");
			Font f;
			if (!fonts.TryGetValue(key, out f)) {
				f = new Font(family, pt,
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

		bool small;
		// the large grids sit this much into the title bar's own margin, so
		// they read as under the title rather than apart from it
		const int TitleTuck = 14;

		void FitSize()
		{
			var c = Compact;
			ClientSize = small ?
				new Size(2 * c.Grid + c.PadGap + 2 * c.Margin,
					 c.Grid + 2 * c.Margin) :
				new Size(GridSize.Width,
					 GridSize.Height + 44 + HeaderH - TitleTuck);
		}

		// the puck owns the size (Guide + left pad click)
		public bool Small
		{
			set {
				if (value == small)
					return;
				small = value;
				FitSize();
				Place();
				Redraw();
			}
		}

		// the puck owns the position (Guide + right pad click)
		public bool Low
		{
			set {
				if (value == low)
					return;
				low = value;
				Place();
				Redraw();
			}
		}

		public void OnKeymap(int[,,] km)
		{
			keymap = km;
			OnState(state);
		}

		public void OnState(PuckState st)
		{
			if (st.Hold == 0) {
				holding.Stop();
			} else if (state.Hold == 0) {
				held.Restart();
				holding.Start();
			}
			state = st;
			UpdateShown();
			Redraw();
			if (Changed != null)
				Changed();
		}

		static int OtherZone(PuckState st, int pad)
		{
			return pad == PadR ? st.ZoneL : st.ZoneR;
		}

		static int OwnZone(PuckState st, int pad)
		{
			return pad == PadR ? st.ZoneR : st.ZoneL;
		}

		protected override void Draw(Graphics g, Rectangle clip)
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var pen = new Pen(Theme.Edge, 2))
			using (var bp = Shape.Round(new RectangleF(1, 1,
								  ClientSize.Width - 2,
								  ClientSize.Height - 2),
						    Shape.Window - 1))
				g.DrawPath(pen, bp);
			if (keymap == null || state.Layer != LayerKbd)
				return;
			if (small) {
				var c = Compact;
				PaintKeys(g, c, false, true, 1);
				StatusArt.PaintStacked(g, status,
						       c.Margin + c.Grid + c.PadGap / 2f,
						       c.Margin + c.Grid / 2f);
				return;
			}
			TitleBar.Paint(g, ClientSize.Width, "Key map", status,
				       new[] { new Chord(low ? "Guide + right pad: move up" :
							       "Guide + right pad: move down",
							 false) });
			g.TranslateTransform(0, HeaderH - TitleTuck);
			PaintKeys(g, Large, true, true, 1);
		}

		// The key grids in KeysSize coordinates; the Guide window scales
		// them into its panel. Without glyphs: just the empty grids (the
		// Guide shows them so while the thumbs are not both down).
		// level 1 full strength, lower fades toward the panel colour
		public void PaintKeys(Graphics g, bool legend, bool glyphs,
				      double level)
		{
			PaintKeys(g, Large, legend, glyphs, level);
		}

		void PaintKeys(Graphics g, Geo geo, bool legend, bool glyphs,
			       double level)
		{
			if (keymap == null)
				return;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			for (int pad = 0; pad < 2; pad++)
				PaintPad(g, geo, pad, glyphs, level);
			if (!legend)
				return;
			using (var b = new SolidBrush(Theme.Dim))
				g.DrawString("LT Bksp   LB Tab   RB Del   RT Space\n" +
						     "L4 Shift   L5 Ctrl   R4 Win   R5 Alt",
					     FontOf(10, false), b,
					     new RectangleF(0, KeysSize.Height - 60,
							    KeysSize.Width, 60),
					     centered);
		}

		void PaintPad(Graphics g, Geo geo, int pad, bool glyphs, double level)
		{
			Func<Color, Color> fade = c => Theme.Mix(Theme.Panel, c, level);
			int cs = geo.Cell, m = geo.Margin;
			int x0 = pad == PadL ? m : m + geo.Grid + geo.PadGap;
			int other = OtherZone(state, pad), ro = other / 3,
			    co = other % 3;
			bool live = glyphs && state.Layer == LayerKbd;
			for (int z = 0; z < Zones; z++) {
				int x = x0 + (z % 3) * (cs + geo.Gap);
				int y = m + (z / 3) * (cs + geo.Gap);
				float cx = x + cs / 2f, cy = y + cs / 2f;
				// lit under the thumb
				double lit = live && z == OwnZone(state, pad) ? 1 : 0;
				Color fill = Theme.Mix(Theme.Cell, Theme.AccentFill, lit);
				// the grid's outer corners are rounded like the pad itself
				float big = geo.Corner;
				using (var cell = Shape.Round(
					       new RectangleF(x, y, cs, cs),
					       z == 0 ? big : Shape.Small,
					       z == 2 ? big : Shape.Small,
					       z == 8 ? big : Shape.Small,
					       z == 6 ? big : Shape.Small)) {
					using (var b = new SolidBrush(fade(fill)))
						g.FillPath(b, cell);
					if (lit > 0 && (state.Hold & (1 << pad)) != 0)
						FillHold(g, x, y, cs, fade);
					using (var pen = new Pen(fade(Theme.Mix(Theme.Edge,
										Theme.Accent,
										lit)),
								 (float)(2 + 2 * lit)))
						g.DrawPath(pen, cell);
				}
				if (!glyphs)
					continue;

				int pos = RingPos[z];
				string label = pos >= 0 ?
					Hid.Label(keymap[pad, other, pos], state.Shift) :
					"";
				// names shrink to the letters' span, clear of the hints
				DrawFit(g, label, FontOf(geo.Pt, true), fade(Theme.Ink), cx, cy,
					geo.Hints ? cs / 2 : cs - 8, x, x + cs);

				if (pos < 0 || !geo.Hints)
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
						DrawFit(g, alt, FontOf(MiniPt, false),
							fade(Theme.Dim),
							cx + (d % 3 - 1) * MiniOff,
							cy + (d / 3 - 1) * MiniOff,
							cs / 2 - 6, x, x + cs);
				}
			}
		}

		// a pie filling clockwise from the top as the hold runs
		void FillHold(Graphics g, int x, int y, int cs,
			      Func<Color, Color> fade)
		{
			float p = Math.Min(1f, held.ElapsedMilliseconds / (float)HoldMs);
			float inset = cs / 8f, d = cs - 2 * inset;
			var c = Theme.Mix(Theme.AccentFill, Theme.Accent, 0.5);
			using (var b = new SolidBrush(fade(c)))
				g.FillPie(b, x + inset, y + inset, d, d, -90, 360 * p);
		}

		// Draws s centred on (x, y), shrunk to fit maxW and slid inside the
		// cell's span lo..hi, so names (Esc, PgUp) sit where letters do.
		void DrawFit(Graphics g, string s, Font f, Color c, float x, float y,
			     float maxW, float lo, float hi)
		{
			string family = FamilyOf(s);
			if (family != f.Name)
				f = FontOf(family, (int)f.SizeInPoints, f.Bold);
			float w = g.MeasureString(s, f).Width;
			if (w > maxW) {
				f = FontOf(family,
					   Math.Max(6, (int)(f.SizeInPoints * maxW / w)),
					   f.Bold);
				w = g.MeasureString(s, f).Width;
			}
			x = Math.Max(lo + w / 2, Math.Min(hi - w / 2, x));
			using (var b = new SolidBrush(c))
				g.DrawString(s, f, b,
					     new RectangleF(x - w, y - 30, 2 * w, 60),
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

		// A pad as the mouse, in the pad's own frame: a surface that moves
		// with what the PC gets, so the trackball's roll and glide and the
		// gyro show on the right pad as the one motion they are, and the
		// wheel on the left (see MouseMotion). Dots for the pointer, ribs
		// for the wheel; lit while the thumb is on the pad, since the
		// output's own bursts flicker too fast to light by.
		static void MouseSurface(Graphics g, bool right, InputState inp,
					 Color ink, Color hidden, Color hiLine,
					 Font padFont)
		{
			var m = right ? inp.Ball : inp.Wheel;
			var c = inp.Has(right ? Btn.RPadT : Btn.LPadT) ? hiLine : hidden;
			var st = g.Save();
			using (var pad = RoundRect(0, 0, 312, 312, 34))
			using (var inner = RoundRect(0, 0, 192, 192, 20))
			using (var edge = new Pen(hidden, 2.5f)) {
				g.SetClip(pad, CombineMode.Intersect);
				var all = g.Save();
				g.SetClip(inner, CombineMode.Exclude);
				Marks(g, right, m, 1 / MouseMotion.Zoom, c);
				g.Restore(all);
				g.SetClip(inner, CombineMode.Intersect);
				Marks(g, right, m, 1, c);
				g.Restore(all);
				g.DrawPath(edge, inner);
			}
			g.Restore(st);
			// what the pad does, and the mouse button its click is,
			// haloed so it reads over the moving marks
			var sf = new StringFormat {
				Alignment = StringAlignment.Center,
				LineAlignment = StringAlignment.Center,
			};
			using (var word = new GraphicsPath())
			using (var halo = new Pen(Theme.Panel, 14))
			using (var b = new SolidBrush(ink)) {
				word.AddString(right ? "pointer\nM1" : "scroll\nM3",
					       padFont.FontFamily, (int)padFont.Style,
					       padFont.Size,
					       new RectangleF(-156, -80, 312, 160), sf);
				halo.LineJoin = LineJoin.Round;
				g.DrawPath(halo, word);
				g.FillPath(b, word);
			}
		}

		// One zone's marks at scale k, each blurred by its step since the
		// last frame: dots for the pointer, ribs for the wheel.
		static void Marks(Graphics g, bool right, MouseMotion.Surface m,
				  float k, Color c)
		{
			const float cell = MouseMotion.Cell;
			float ox = m.X * k % cell, oy = m.Y * k % cell;
			// where each mark was a frame ago, relative to it
			float bx = -m.DX * k, by = m.DY * k;
			bool still = Math.Abs(bx) + Math.Abs(by) < 0.5f;
			using (var b = new SolidBrush(c))
			using (var pen = new Pen(c, 8)) {
				pen.StartCap = pen.EndCap = LineCap.Round;
				for (float y = -4 * cell - oy; y < 4 * cell; y += cell) {
					if (!right) {
						g.FillRectangle(b, -156, Math.Min(y, y + by) - 2,
								312, Math.Abs(by) + 4);
						continue;
					}
					for (float x = -4 * cell + ox; x < 4 * cell; x += cell) {
						if (still)
							g.FillEllipse(b, x - 4, y - 4, 8, 8);
						else
							g.DrawLine(pen, x + bx, y + by, x, y);
					}
				}
			}
		}

		// layer: 0 controller, 1 keyboard (decides what the pads
		// show); colours are already faded for inactive panels. Pressed
		// controls fill with hiFill and outline with hiPen; sticks move,
		// triggers fill as they are pulled, a dot follows each thumb on
		// its pad.
		public static void Draw(Graphics g, int layer, Color ink,
					Color hidden, Font padFont,
					InputState inp, Color hiFill, Color hiLine)
		{
			using (var body = new Pen(ink, 5))
			using (var pen = new Pen(ink, 4))
			using (var thin = new Pen(ink, 2.5f))
			using (var dash = new Pen(hidden, 4))
			using (var hiPen = new Pen(hiLine, 5))
			using (var inkB = new SolidBrush(ink))
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
						// lit only while clicked: the dot shows the
						// touch
						if (click)
							g.FillPath(hiB, r);
						g.DrawPath(click ? hiPen : pen, r);
					}
					if (layer == 1) {
						for (int k = 1; k < 3; k++) {
							float o = -156 + k * 104;
							g.DrawLine(thin, o, -156, o, 156);
							g.DrawLine(thin, -156, o, 156, o);
						}
					}
					if (layer == 0)
						MouseSurface(g, i == 1, inp, ink, hidden, hiLine,
							     padFont);
					if (touch) {
						float px = (i == 0 ? inp.LPX : inp.RPX) * 140f / 32;
						float py = -(i == 0 ? inp.LPY : inp.RPY) * 140f / 32;
						// in the keyboard the thumb picks the key; as
						// the mouse it only drives the surface, a
						// faint ring
						if (layer == 1) {
							g.FillEllipse(hiB, px - 24, py - 24, 48, 48);
							g.DrawEllipse(hiPen, px - 24, py - 24, 48, 48);
						} else {
							g.DrawEllipse(thin, px - 24, py - 24, 48, 48);
						}
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

	// The pads' surfaces as the Guide draws them, moved exactly as far as
	// the mouse output (the puck's travel, by difference, so a dropped line
	// costs nothing). Each pad has an inner zone at 1:1 and a band around
	// it ZOOM times zoomed out, which still reads when the inner one moves
	// too fast to; each mark is blurred by its step since the last frame,
	// as a camera's shutter would, so fast motion streaks instead of
	// strobing (a cell's step in one frame reads as no step).
	class MouseMotion
	{
		// world units: a mark every Cell, a pointer count PerCount (the
		// thumb's travel on the drawn pad, at slow speed), a notch a Cell
		public const float Cell = 48, Zoom = 4, PerCount = 0.75f;

		public struct Surface
		{
			// offset, wrapping at a band cell, and the last step (y up)
			public float X, Y, DX, DY;
		}

		Surface ball, wheel;
		int lastX, lastY, lastW;
		bool started;

		public void Step(ref InputState inp)
		{
			if (started) {
				Move(ref ball, (short)(inp.MX - lastX) * PerCount,
				     (short)(inp.MY - lastY) * PerCount);
				Move(ref wheel, 0, (short)(inp.MW - lastW) * Cell / 16);
			}
			started = true;
			lastX = inp.MX;
			lastY = inp.MY;
			lastW = inp.MW;
			inp.Ball = ball;
			inp.Wheel = wheel;
		}

		// no travel since the last frame: the puck sends nothing once the
		// mouse stops, so the blur of its last step is cleared here
		public void Settle(ref InputState inp)
		{
			ball.DX = ball.DY = wheel.DX = wheel.DY = 0;
			inp.Ball = ball;
			inp.Wheel = wheel;
		}

		static void Move(ref Surface s, float dx, float dy)
		{
			s.DX = dx;
			s.DY = dy;
			s.X = Wrap(s.X + dx);
			s.Y = Wrap(s.Y + dy);
		}

		static float Wrap(float f)
		{
			f %= Cell * Zoom;
			return f < 0 ? f + Cell * Zoom : f;
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
				{ "rt", "RT" }, { "rb", "RB" },
				{ "face", "M5|M4|Esc|Enter" },
				{ "rs", "Stick · R3" }, { "r4", "A" }, { "r5", "X" },
				{ "back", "Back" }, { "xbox", "Guide" },
				{ "start", "Start" }, { "qam", "M2" },
			},
			// with both thumbs on the pads the d-pad, face and center
			// buttons are out of reach, so they have no keys
			new Dictionary<string, string> {
				{ "lt", "Backspace" }, { "lb", "Tab" },
				{ "l4", "Shift" }, { "l5", "Ctrl" },
				{ "rt", "Space" }, { "rb", "Delete" },
				{ "r4", "Win" }, { "r5", "Alt" },
			},
		};
	}

	// The Guide: the gamepad on the left; the keyboard on the right,
	// with the key map under it. The layer your thumbs select is outlined;
	// the others stay readable but faded.
	class GuideWindow : Overlay
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
		PuckState state;
		InputState input;
		readonly bool installed;
		// the window edge, panels and scale texture, which never change:
		// drawn once, copied under every repaint
		Bitmap backdrop;

		public GuideWindow(KeyOverlay keys, bool installed)
			: base(Theme.Bg, 1)
		{
			this.keys = keys;
			this.installed = installed;
			var s = Screen.PrimaryScreen.WorkingArea;
			int w = s.Width * 9 / 10, h = s.Height * 9 / 10;
			Bounds = new Rectangle(s.X + (s.Width - w) / 2,
					       s.Y + (s.Height - h) / 2, w, h);
			keys.Changed += delegate { Redraw(KeyArea()); };
		}

		// the panel in use, and the other one when a button held across
		// a layer change (lit there) changed
		public void OnInput(InputState inp)
		{
			int other = 1 - state.Layer;
			Redraw(Panel(state.Layer));
			if (inp.Own(other) != input.Own(other))
				Redraw(Panel(other));
			input = inp;
		}

		CtlStatus status;

		public void OnStatus(CtlStatus st)
		{
			status = st;
			Redraw(new Rectangle(0, 0, ClientSize.Width, HeaderH));
		}

		// how lit a panel is: 1 in use, Dim the other
		const double Dim = 0.4;

		double Level(int layer)
		{
			return state.Layer == layer ? 1 : Dim;
		}

		public void OnState(PuckState st)
		{
			bool layerChanged = st.Layer != state.Layer;
			state = st;
			if (layerChanged)
				Redraw();
		}

		// 0 controller (the left column), 1 keyboard (the right column)
		Rectangle Panel(int i)
		{
			int w = (ClientSize.Width - 3 * Gap) / 2;
			return new Rectangle(Gap + i * (w + Gap), HeaderH, w,
					     ClientSize.Height - HeaderH - Gap);
		}

		// where a panel's controller drawing goes: below its title, the
		// whole controller panel, the top half of the keyboard's (the key map
		// goes under it)
		Rectangle ArtArea(int i)
		{
			var q = Panel(i);
			int h = i == KeyOverlay.LayerKbd ? (q.Height - Gap) / 2 : q.Height;
			return new Rectangle(q.X + 10, q.Y + 36, q.Width - 20, h - 44);
		}

		Rectangle KeyArea()
		{
			var q = Panel(KeyOverlay.LayerKbd);
			var a = ArtArea(KeyOverlay.LayerKbd);
			return new Rectangle(a.X, a.Bottom + 8, a.Width,
					     q.Bottom - a.Bottom - 20);
		}

		protected override void Draw(Graphics g, Rectangle clip)
		{
			if (backdrop == null || backdrop.Size != ClientSize)
				backdrop = Backdrop();
			g.CompositingMode = CompositingMode.SourceCopy;
			g.DrawImageUnscaled(backdrop, 0, 0);
			g.CompositingMode = CompositingMode.SourceOver;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			if (clip.Top < HeaderH)
				PaintHeader(g);
			string[] names = { "Controller (default)",
					   "Keyboard (touch both pads)" };
			for (int i = 0; i < 2; i++)
				if (clip.IntersectsWith(Panel(i)))
					PaintController(g, i, names[i]);
			if (clip.IntersectsWith(KeyArea()))
				PaintKeyMap(g, KeyArea());
		}

		Bitmap Backdrop()
		{
			var bmp = new Bitmap(ClientSize.Width, ClientSize.Height,
					     PixelFormat.Format32bppPArgb);
			using (var g = Graphics.FromImage(bmp)) {
				g.Clear(Theme.Bg);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				using (var pen = new Pen(Theme.Edge, 2))
				using (var bp = Shape.Round(new RectangleF(1, 1,
									  ClientSize.Width - 2,
									  ClientSize.Height - 2),
							    Shape.Window - 1))
					g.DrawPath(pen, bp);
				for (int i = 0; i < 2; i++)
					using (var p = Rounded(Panel(i), 12)) {
						using (var b = new SolidBrush(Theme.Panel))
							g.FillPath(b, p);
						Theme.Scales(g, p);
					}
			}
			return bmp;
		}

		void PaintHeader(Graphics g)
		{
			var chords = new List<Chord> {
				new Chord("Guide: close", false),
				// each says what it will do now
				new Chord(new[] { "Guide + left pad: show key map",
						  "Guide + left pad: small key map",
						  "Guide + left pad: hide key map" }
						  [status.KeymapSize % 3],
					  false),
				new Chord(status.KeymapLow ? "Guide + right pad: key map up" :
							     "Guide + right pad: key map down",
					  false),
				new Chord("Guide + Back: controller off", false),
			};
			// until installed, the next step is outlined
			if (!installed)
				chords.Add(new Chord("Guide + Start: install", true));
			// a puck tuned from the console isn't showing what ships
			TitleBar.Paint(g, ClientSize.Width,
				       status.Dev ? "LizardKing Guide · dev overrides" :
						    "LizardKing Guide",
				       status,
				       chords.ToArray());
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

		void PaintFrame(Graphics g, Rectangle q, double level, string name)
		{
			double f = (level - Dim) / (1 - Dim);
			// the fill and texture are in the backdrop
			using (var p = Rounded(q, 12)) {
				using (var pen = new Pen(Theme.Mix(Theme.Edge, Theme.Accent,
								   f),
							 (float)(1.5 + 1.5 * f)))
					g.DrawPath(pen, p);
			}
			Color ink = Theme.Mix(Theme.Dim, Theme.Ink, f);
			using (var b = new SolidBrush(ink))
				g.DrawString(name, head, b,
					     new RectangleF(q.X + 16, q.Y + 6, 300, 30),
					     left);
		}

		// What a panel shows. Buttons light in the panel of the layer that
		// owns them, so one held across a layer change stays lit where it
		// acts. Only the panel in use follows thumbs and sticks. Triggers:
		// the pull in the controller panel, and in the keyboard panel the
		// Space / Backspace key, on or off at the firmware's thresholds.
		InputState PanelInput(int layer, bool on)
		{
			var inp = on ? input : new InputState();
			const uint live = Btn.LPadT | Btn.RPadT | Btn.Steam;
			inp.Buttons = input.Own(layer) | (on ? input.Buttons & live : 0);
			if (layer == 0) {
				inp.LT = on || inp.Has(Btn.LT) ? input.LT : 0;
				inp.RT = on || inp.Has(Btn.RT) ? input.RT : 0;
			} else {
				inp.LT = inp.Has(Btn.LT) ? 31 : 0;
				inp.RT = inp.Has(Btn.RT) ? 31 : 0;
			}
			return inp;
		}

		void PaintController(Graphics g, int layer, string name)
		{
			var q = Panel(layer);
			bool on = state.Layer == layer;
			double k = Level(layer);
			PaintFrame(g, q, k, name);
			// faded panels mix their ink toward the panel colour
			Color ink = Theme.Mix(Theme.Panel, Theme.Ink, k);
			Color hidden = Theme.Mix(Theme.Panel, Theme.Dim, k);
			Color lead = Theme.Mix(Theme.Panel, Theme.Dim, k);
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
			var inp = PanelInput(layer, on);
			ControllerArt.Draw(g, layer, ink, hidden, label, inp,
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
			float cx = x0 + 140, dx = 18, dy = 52;
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

		// fades like the other inactive panels
		void PaintKeyMap(Graphics g, Rectangle q)
		{
			double k = Level(KeyOverlay.LayerKbd);
			if (!keys.HasKeymap)
				return;
			RectangleF area = q;
			var ks = keys.GridSize;
			float s = Math.Min(area.Width / ks.Width,
					   area.Height / ks.Height);
			var st = g.Save();
			g.SetClip(q);
			g.TranslateTransform(area.X + (area.Width - ks.Width * s) / 2,
					     area.Y + (area.Height - ks.Height * s) / 2);
			g.ScaleTransform(s, s);
			keys.PaintKeys(g, false, k > Dim, k);
			g.Restore(st);
		}
	}

	// A click-through, always-on-top window that shows itself for a moment
	// and hides again; nothing on it is clickable.
	abstract class Popup : Overlay
	{
		readonly System.Windows.Forms.Timer hide =
			new System.Windows.Forms.Timer();

		protected Popup(int showMs, Color bg, Size size) : base(bg, 0.95)
		{
			Size = size;
			hide.Interval = showMs;
			hide.Tick += delegate {
				hide.Stop();
				Up = false;
			};
		}

		protected void Pop()
		{
			Redraw();
			Up = true;
			hide.Stop();
			hide.Start();
		}

		protected void Pop(int ms)
		{
			hide.Interval = ms;
			Pop();
		}

		protected void Frame(Graphics g, Color edge)
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			using (var pen = new Pen(edge, 2))
			using (var bp = Shape.Round(new RectangleF(1, 1, Width - 2,
								  Height - 2),
						    Shape.Window - 1))
				g.DrawPath(pen, bp);
		}
	}

	// A brief word in the middle of the screen when a key map chord is used
	// with the Guide closed
	class Toast : Popup
	{
		readonly Font font = new Font("Segoe UI Semibold", 30f,
					      GraphicsUnit.Pixel);
		string text = "";

		public Toast() : base(1100, Theme.AccentFill, new Size(360, 84))
		{
			var s = Screen.PrimaryScreen.WorkingArea;
			Location = new Point(s.X + (s.Width - Width) / 2,
					     s.Y + s.Height / 6);
		}

		public void Say(string t)
		{
			Say(t, 1100);
		}

		// sized to the text, centred where the toast sits
		public void Say(string t, int ms)
		{
			text = t;
			int w = Math.Max(360, TextRenderer.MeasureText(t, font).Width + 56);
			var s = Screen.PrimaryScreen.WorkingArea;
			Bounds = new Rectangle(s.X + (s.Width - w) / 2, Top, w, Height);
			Pop(ms);
		}

		protected override void Draw(Graphics g, Rectangle clip)
		{
			Frame(g, Theme.Accent);
			using (var b = new SolidBrush(Theme.Ink))
				g.DrawString(text, font, b, ClientRectangle,
					     new StringFormat {
						     Alignment = StringAlignment.Center,
						     LineAlignment = StringAlignment.Center,
					     });
		}
	}

	// The tray icon's answer to a click: above the tray, the top of the
	// controller with the Guide button lit and an arrow pointing at it, and
	// one line saying to hold it. It points to the Guide button rather than
	// being a second way in.
	class HintCard : Popup
	{
		// "on the controller" tells a newcomer where the button is
		public const string Hint =
			"Hold the Guide button on the controller\nfor more info.";
		// the controller's top half, in ControllerArt's world units: the
		// triggers down to the sticks, with room above for the arrow
		static readonly RectangleF Crop = new RectangleF(-10, -330, 1484, 820);
		const float GuideX = 732, GuideTop = 192;
		readonly Font font = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
		readonly Font padFont = new Font("Segoe UI", 46f, GraphicsUnit.Pixel);

		public HintCard() : base(5000, Theme.Bg, new Size(380, 270))
		{
		}

		// placed as it appears: the taskbar may have moved or hidden
		public void Appear()
		{
			var s = Screen.PrimaryScreen.WorkingArea;
			Location = new Point(s.Right - Width - 12,
					     Native.AboveTaskbar() - Height - 12);
			Pop();
		}

		protected override void Draw(Graphics g, Rectangle clip)
		{
			Frame(g, Theme.Edge);
			var area = new RectangleF(16, 14, Width - 32, Height - 74);
			float k = Math.Min(area.Width / Crop.Width,
					   area.Height / Crop.Height);
			var st = g.Save();
			g.SetClip(area);
			g.TranslateTransform(area.X + (area.Width - Crop.Width * k) / 2,
					     area.Y);
			g.ScaleTransform(k, k);
			g.TranslateTransform(-Crop.X, -Crop.Y);
			var inp = new InputState();
			inp.Buttons = Btn.Steam;
			ControllerArt.Draw(g, 0, Theme.Dim, Theme.Dim, padFont, inp,
					   Theme.AccentFill, Theme.Flash);
			using (var arrow = new Pen(Theme.Flash, 18)) {
				arrow.CustomEndCap = new AdjustableArrowCap(3, 3);
				g.DrawLine(arrow, GuideX, Crop.Y + 20, GuideX,
					   GuideTop - 24);
			}
			g.Restore(st);
			// the controller fades out below the sticks
			var fade = new RectangleF(area.X, area.Bottom - 50, area.Width,
						  51);
			using (var b = new LinearGradientBrush(fade,
							       Color.FromArgb(0, Theme.Bg),
							       Theme.Bg, 90f))
				g.FillRectangle(b, fade);
			using (var b = new SolidBrush(Theme.Ink))
				g.DrawString(Hint, font, b,
					     new RectangleF(16, Height - 60, Width - 32,
							    48),
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
		readonly HintCard hint = new HintCard();
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
			Keys.DevicesChanged += CheckWired;
			// practice mode: while the Guide is up the controller's input
			// goes to it, not to the apps behind it
			beats.Interval = 250;
			beats.Tick += delegate { Beat(); };
			beats.Start();
			// a few frames with no new input
			settle.Interval = 50;
			settle.Tick += delegate { Settle(); };
		}

		// The timer alone can't keep the controller: Windows fires it only
		// when nothing else is queued, and a stream of input with its
		// redraws (a long trackball spin) keeps the queue full past the
		// puck's 1 s, which hands the mouse back. Handling input proves the
		// window thread alive just as well.
		int beatAt;

		void Beat()
		{
			if (!guideUp || Link == null ||
			    Environment.TickCount - beatAt < 250)
				return;
			beatAt = Environment.TickCount;
			Link.Beat();
		}

		public bool Installed
		{
			get { return installed; }
		}

		public void ShowWindows()
		{
			guide.Show();
			toast.Show();
			hint.Show();
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
			Keys.Allowed = !guideUp && (!hasSwitches || last.KeymapSize > 0);
			guide.Up = guideUp;
			RawSource.Listen(Keys.Handle, guideUp);
			if (Link != null) {
				if (guideUp)
					Link.Beat();
				else
					Link.Release();
			}
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

		public bool GuideUp
		{
			get { return guideUp; }
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

		public void ShowHint()
		{
			hint.Appear();
		}

		public void InstallChord()
		{
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

		// With no puck the Guide button can't close the Guide, and the
		// controller's own clicks don't count as another mouse: a Guide up
		// now would cover the screen for good.
		public void OnDisconnected()
		{
			if (guideUp)
				ToggleGuide();
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

		readonly MouseMotion motion = new MouseMotion();
		readonly System.Windows.Forms.Timer settle =
			new System.Windows.Forms.Timer();
		InputState input;

		public void OnInput(InputState inp)
		{
			Beat();
			motion.Step(ref inp);
			input = inp;
			guide.OnInput(inp);
			settle.Stop();
			settle.Start();
		}

		void Settle()
		{
			settle.Stop();
			motion.Settle(ref input);
			guide.OnInput(input);
		}

		// The controller turned on while on its cable (charging, say) skips
		// the puck for the PC, and LizardKing is gone until it's unplugged:
		// say so, once each time. It's the paired controller, by the serial
		// the puck reports, on USB as a wired Steam Controller.
		bool wiredSaid;

		void CheckWired()
		{
			bool wired = hasSwitches && !last.Connected && last.Serial != "-" &&
				     Native.DevicePresent(@"USB\VID_28DE&PID_1302\" +
							  last.Serial);
			if (wired && !wiredSaid)
				toast.Say("Unplug the controller and turn it back on", 6000);
			wiredSaid = wired;
		}

		public void OnStatus(CtlStatus st)
		{
			// without the controller the Guide button can't put it away
			if (guideUp && !st.Connected)
				ToggleGuide();
			if (hasSwitches && st.KeymapSize != last.KeymapSize) {
				if (!guideUp)
					toast.Say(new[] { "Key map off", "Key map large",
							  "Key map small" }[st.KeymapSize % 3]);
			}
			if (hasSwitches && st.KeymapLow != last.KeymapLow) {
				if (!guideUp)
					toast.Say(st.KeymapLow ? "Key map bottom" :
								 "Key map top");
			}
			bool lost = hasSwitches && last.Connected && !st.Connected;
			hasSwitches = true;
			last = st;
			if (lost)
				CheckWired();
			Keys.Low = st.KeymapLow;
			Keys.Small = st.KeymapSize == 2;
			Keys.Allowed = !guideUp && st.KeymapSize > 0;
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
			// way in; it is a way out, the one click the Guide window
			// (click-through) can't offer
			tray.MouseClick += delegate(object o, MouseEventArgs e) {
				if (e.Button != MouseButtons.Left)
					return;
				if (app.GuideUp)
					app.ToggleGuide();
				else
					app.ShowHint();
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

		// NotifyIcon refuses a tooltip of 64 characters or more
		static string Tip(string status)
		{
			string t = "LizardKing: " + status;
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

		// The controller's own outline, lime fading to green with a dark
		// rim, the two pads cut in dark (it reads as a face), wearing a gold
		// crown: still legible at 16 px. Drawn rather than shipped as a
		// file.
		public static Icon MakeIcon()
		{
			const int S = 32;
			using (var bmp = new Bitmap(S, S))
			using (var g = Graphics.FromImage(bmp))
			using (var body = ControllerArt.Body()) {
				g.Clear(Color.Transparent);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				var b = body.GetBounds();
				float k = (S - 2) / b.Width;
				// sits on the bottom edge, leaving the top for the crown
				float top = S - 1 - b.Height * k;
				var crown = new[] {
					new PointF(9, top + 2), new PointF(8, top - 7),
					new PointF(12.5f, top - 3), new PointF(16, top - 9),
					new PointF(19.5f, top - 3), new PointF(24, top - 7),
					new PointF(23, top + 2),
				};
				using (var gold = new SolidBrush(Color.FromArgb(0xf2, 0xc2, 0x30)))
					g.FillPolygon(gold, crown);
				using (var rim = new Pen(Color.FromArgb(0x6b, 0x4e, 0x0a), 1.2f))
					g.DrawPolygon(rim, crown);
				g.TranslateTransform(1, top);
				g.ScaleTransform(k, k);
				g.TranslateTransform(-b.X, -b.Y);
				using (var fill = new LinearGradientBrush(b, Theme.Flash,
									 Theme.Accent, 90f))
					g.FillPath(fill, body);
				using (var rim = new Pen(Theme.AccentFill, 1.6f / k))
					g.DrawPath(rim, body);
				using (var dark = new SolidBrush(Theme.AccentFill))
					foreach (var c in new[] { ControllerArt.PadL,
								  ControllerArt.PadR })
						using (var pad = Shape.Round(
							       new RectangleF(c.X - 150, c.Y - 150,
									      300, 300),
							       40))
							g.FillPath(dark, pad);
				IntPtr h = bmp.GetHicon();
				var icon = (Icon)Icon.FromHandle(h).Clone();
				Native.DestroyIcon(h);
				return icon;
			}
		}
	}
}
