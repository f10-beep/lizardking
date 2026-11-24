// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The Guide's windows on this PC, checked without a puck or a person,
// compiled in from the Guide's source. First an overlay of plain magenta:
// read back off the screen it shows, at its opacity, and a click there
// passes through to what's under it. Then the key map, fed zone changes
// through the same calls the puck's reports make, each waiting until the
// screen shows it (at least 110 ms apart, brisk typing): the right pad's
// grid is read back and the lit cell found by its green. Run it over a game
// behind a browser.
//
//   Bench [seconds] [label]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LizardKing
{
	static class Bench
	{
		[DllImport("shcore.dll")]
		static extern int SetProcessDpiAwareness(int value);
		[DllImport("user32.dll")]
		static extern IntPtr WindowFromPoint(Point p);

		static readonly Stopwatch clock = Stopwatch.StartNew();
		// change sent -> the window thread has drawn and handed it over
		static readonly List<double> handed = new List<double>();
		// change sent -> on screen
		static readonly List<double> seen = new List<double>();
		static int unseen;
		// the newest screen read: when it started, the lit cell
		static double readAt;
		static int readZone = -1;
		static readonly object screen = new object();

		static double Now
		{
			get { return clock.Elapsed.TotalMilliseconds; }
		}

		static void Report(string name, List<double> v)
		{
			var s = v.OrderBy(x => x).ToList();
			if (s.Count == 0) {
				Console.WriteLine("{0}: none", name);
				return;
			}
			Console.WriteLine("{0}: n={1} median={2:F1} p95={3:F1} max={4:F0} " +
					  ">100ms={5} >1s={6} (ms)",
					  name, s.Count, s[s.Count / 2],
					  s[(int)(s.Count * 0.95)], s[s.Count - 1],
					  s.Count(x => x > 100), s.Count(x => x > 1000));
		}

		class Swatch : Overlay
		{
			public Swatch(double opacity) : base(Color.Magenta, opacity)
			{
			}

			protected override void Draw(Graphics g, Rectangle clip)
			{
			}
		}

		static readonly Point At = new Point(200, 160);

		static Color Read()
		{
			using (var b = new Bitmap(1, 1)) {
				using (var g = Graphics.FromImage(b))
					g.CopyFromScreen(At, Point.Empty, new Size(1, 1));
				return b.GetPixel(0, 0);
			}
		}

		static void Pump(int ms)
		{
			for (int i = 0; i < ms / 25; i++) {
				Application.DoEvents();
				Thread.Sleep(25);
			}
		}

		static bool Near(int got, double want)
		{
			return Math.Abs(got - want) < 8;
		}

		static void Swatches()
		{
			foreach (double opacity in new[] { 1, 0.5 }) {
				var w = new Swatch(opacity) {
					Bounds = new Rectangle(100, 100, 200, 120),
				};
				w.Show();
				Pump(200);
				Color under = Read();
				w.Up = true;
				Pump(500);
				Color c = Read();
				double k = 1 - opacity;
				bool seen = Near(c.R, 255 * opacity + k * under.R) &&
					    Near(c.G, k * under.G) &&
					    Near(c.B, 255 * opacity + k * under.B);
				bool through = WindowFromPoint(At) != w.Handle;
				w.Up = false;
				Pump(200);
				bool gone = Read() == under;
				w.Dispose();
				Console.WriteLine("overlay at opacity {0}: shows {1}, " +
						  "clicks pass {2}, hides {3}",
						  opacity, seen, through, gone);
			}
		}

		[STAThread]
		static void Main(string[] args)
		{
			// per monitor, as the Guide runs
			SetProcessDpiAwareness(2);
			Swatches();
			double seconds = args.Length > 0 ? double.Parse(args[0]) : 20;
			string label = args.Length > 1 ? args[1] : "key map";
			var app = new App(false, true);

			var km = new int[2, 9, 8];
			for (int p = 0; p < 2; p++)
				for (int z = 0; z < 9; z++)
					for (int k = 0; k < 8; k++)
						km[p, z, k] = 4 + (p * 9 + z + k) % 26;
			var st = new CtlStatus {
				Connected = true,
				KeymapSize = 1,
				Battery = 80,
				Rssi = 60,
				Serial = "-",
			};

			app.Keys.Shown += delegate {
				app.ShowWindows();
				app.OnStatus(st);
				app.OnKeymap(km);
				var r = app.Keys.Bounds;
				var t = new Thread(delegate() { Drive(app, seconds); });
				t.IsBackground = true;
				t.Start();
				var w = new Thread(delegate() { Watch(r); });
				w.IsBackground = true;
				w.Start();
			};
			Application.Run(app.Keys);
			Console.WriteLine(label + ":");
			Report("  handed over", handed);
			Report("  on screen  ", seen);
			Console.WriteLine("  not on screen within 5 s: {0}", unseen);
		}

		// zones the right thumb steps through while the left one holds,
		// both pads down: the keyboard layer, the key map up
		static void Drive(App app, double seconds)
		{
			int[] path = { 4, 1, 2, 5, 8, 7, 6, 3, 0, 4, 5, 4, 3 };
			Thread.Sleep(1500);
			double end = Now + seconds * 1000;
			for (int i = 0; Now < end; i++) {
				int zone = path[i % path.Length];
				var ps = new PuckState(1, zone, 4, false);
				double sent = Now;
				app.Post(delegate {
					app.OnState(ps);
					// the key map has just posted its redraw to
					// this window: a message posted after it runs
					// once the picture is handed over
					app.Post(delegate { handed.Add(Now - sent); });
				});
				bool ok = false;
				while (Now - sent < 5000) {
					lock (screen)
						ok = readAt > sent && readZone == zone;
					if (ok)
						break;
					Thread.Sleep(2);
				}
				if (ok)
					seen.Add(Now - sent);
				else
					unseen++;
				double rest = 110 - (Now - sent);
				if (rest > 0)
					Thread.Sleep((int)rest);
			}
			Thread.Sleep(500);
			app.Post(delegate { Application.Exit(); });
		}

		// Reads the right pad's grid off the screen, over and over.
		// Geometry as KeyOverlay draws its large grids: cells of 84 with
		// gaps of 6, the right pad 344 from the left, the grid 20 below the
		// title bar's tucked 34.
		static void Watch(Rectangle keys)
		{
			var at = new Point(keys.X + 344, keys.Y + 34 + 20);
			using (var b = new Bitmap(264, 264)) {
				while (true) {
					double t0 = Now;
					using (var g = Graphics.FromImage(b))
						g.CopyFromScreen(at, Point.Empty, b.Size);
					int z = Lit(b);
					lock (screen) {
						readAt = t0;
						readZone = z;
					}
					Thread.Sleep(3);
				}
			}
		}

		// the cell whose green stands out, -1 when none does
		static int Lit(Bitmap b)
		{
			var d = b.LockBits(new Rectangle(Point.Empty, b.Size),
					   ImageLockMode.ReadOnly,
					   PixelFormat.Format32bppArgb);
			var px = new int[d.Stride / 4 * b.Height];
			Marshal.Copy(d.Scan0, px, 0, px.Length);
			b.UnlockBits(d);
			int best = -1;
			double top = 0, second = 0;
			for (int z = 0; z < 9; z++) {
				int x0 = (z % 3) * 90, y0 = (z / 3) * 90;
				double sum = 0;
				for (int y = y0; y < y0 + 84; y += 2)
					for (int x = x0; x < x0 + 84; x += 2) {
						int c = px[y * d.Stride / 4 + x];
						sum += ((c >> 8) & 255) - ((c >> 16) & 255);
					}
				if (sum > top) {
					second = top;
					top = sum;
					best = z;
				} else if (sum > second)
					second = sum;
			}
			return top > second * 1.3 ? best : -1;
		}
	}
}
