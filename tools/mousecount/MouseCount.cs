// Counts raw mouse motion per device, one line per swipe, so pointer speeds
// can be compared in the same units: the controller's own mouse on its
// cable, what Steam Input sends, and the puck. Also shows how far the
// cursor really went, which adds the OS pointer curve.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LizardKing
{
static class MouseCount {
	// a pause this long ends a swipe
	const int GAP_MS = 250;

	class Swipe {
		public int n, dx, dy, peak, absN;
		public int t0, t1;
		public int cx0;
	}

	static readonly Dictionary<IntPtr, Swipe> live =
		new Dictionary<IntPtr, Swipe>();
	static readonly Dictionary<IntPtr, string> names =
		new Dictionary<IntPtr, string>();

	[STAThread]
	static int Main(string[] args)
	{
		int seconds = args.Length > 0 ? int.Parse(args[0]) : 90;
		var w = new Sink();
		var rid = new RAWINPUTDEVICE();
		rid.usUsagePage = 1;
		rid.usUsage = 2;
		// RIDEV_INPUTSINK: keep receiving while another window has focus
		rid.dwFlags = 0x100;
		rid.hwndTarget = w.Handle;
		if (!RegisterRawInputDevices(new[] { rid }, 1,
					     Marshal.SizeOf(rid))) {
			Console.Error.WriteLine("RegisterRawInputDevices failed");
			return 1;
		}
		Console.WriteLine("Counting mouse motion for {0} s. A pause of " +
					  "{1} ms ends a swipe.",
				  seconds, GAP_MS);
		Console.WriteLine("device            reports     dx     dy  " +
				  "peak/rep    ms  cursor dx");
		var tick = new Timer();
		tick.Interval = 50;
		tick.Tick += (s, e) => Flush(false);
		tick.Start();
		var end = new Timer();
		end.Interval = seconds * 1000;
		end.Tick += (s, e) => {
			Flush(true);
			Application.Exit();
		};
		end.Start();
		Application.Run();
		return 0;
	}

	class Sink : NativeWindow {
		public Sink()
		{
			var cp = new CreateParams();
			// HWND_MESSAGE: a message-only window, never shown
			cp.Parent = new IntPtr(-3);
			CreateHandle(cp);
		}

		protected override void WndProc(ref Message m)
		{
			// WM_INPUT
			if (m.Msg == 0x00FF)
				OnInput(m.LParam);
			base.WndProc(ref m);
		}
	}

	static void OnInput(IntPtr hRaw)
	{
		int hdr = 8 + 2 * IntPtr.Size;
		int size = 0;
		GetRawInputData(hRaw, 0x10000003, IntPtr.Zero, ref size, hdr);
		IntPtr buf = Marshal.AllocHGlobal(size);
		try {
			if (GetRawInputData(hRaw, 0x10000003, buf, ref size, hdr) !=
			    size)
				return;
			// RIM_TYPEMOUSE
			if (Marshal.ReadInt32(buf, 0) != 0)
				return;
			IntPtr dev = Marshal.ReadIntPtr(buf, 8);
			int flags = Marshal.ReadInt16(buf, hdr);
			int x = Marshal.ReadInt32(buf, hdr + 12);
			int y = Marshal.ReadInt32(buf, hdr + 16);
			Add(dev, x, y, (flags & 1) != 0);
		} finally {
			Marshal.FreeHGlobal(buf);
		}
	}

	static void Add(IntPtr dev, int x, int y, bool absolute)
	{
		int now = Environment.TickCount;
		Swipe s;
		if (!live.TryGetValue(dev, out s)) {
			s = new Swipe();
			s.t0 = now;
			s.cx0 = Cursor.Position.X;
			live[dev] = s;
		}
		s.t1 = now;
		s.n++;
		if (absolute) {
			s.absN++;
			return;
		}
		s.dx += x;
		s.dy += y;
		int mag = Math.Max(Math.Abs(x), Math.Abs(y));
		if (mag > s.peak)
			s.peak = mag;
	}

	static void Flush(bool all)
	{
		int now = Environment.TickCount;
		var done = new List<IntPtr>();
		foreach (var kv in live)
			if (all || now - kv.Value.t1 > GAP_MS)
				done.Add(kv.Key);
		foreach (var dev in done) {
			Swipe s = live[dev];
			live.Remove(dev);
			if (s.n < 3)
				continue;
			int cdx = Cursor.Position.X - s.cx0;
			Console.WriteLine("{0,-16} {1,8} {2,6} {3,6} {4,9} {5,5} {6,10}" +
						  "{7}",
					  Name(dev), s.n, s.dx, s.dy, s.peak,
					  s.t1 - s.t0, cdx,
					  s.absN > 0 ? "  (" + s.absN +
							       " absolute)" :
						       "");
		}
	}

	static string Name(IntPtr dev)
	{
		// raw input from SendInput arrives with no device
		if (dev == IntPtr.Zero)
			return "injected";
		string n;
		if (names.TryGetValue(dev, out n))
			return n;
		int len = 0;
		GetRawInputDeviceInfo(dev, 0x20000007, null, ref len);
		var sb = new StringBuilder(len + 1);
		GetRawInputDeviceInfo(dev, 0x20000007, sb, ref len);
		string path = sb.ToString().ToUpperInvariant();
		int v = path.IndexOf("VID_");
		n = v >= 0 && path.Length >= v + 17 ?
			    path.Substring(v + 4, 4) + ":" +
				    path.Substring(v + 13, 4) :
			    "?";
		names[dev] = n;
		return n;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct RAWINPUTDEVICE {
		public ushort usUsagePage, usUsage;
		public uint dwFlags;
		public IntPtr hwndTarget;
	}

	[DllImport("user32.dll", SetLastError = true)]
	static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs,
						   int n, int size);
	[DllImport("user32.dll")]
	static extern int GetRawInputData(IntPtr h, uint cmd, IntPtr data,
					  ref int size, int hdr);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	static extern int GetRawInputDeviceInfo(IntPtr dev, uint cmd,
						StringBuilder data, ref int size);
}
}
