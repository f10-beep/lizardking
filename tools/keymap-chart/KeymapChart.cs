// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The README's key map illustration, drawn by the Guide's own small key map,
// so it looks as the overlay does: each pad's pages in its own box, each
// page where the other thumb rests to pick it, mirrored onto this pad (home
// in the top inner corner of both).
//
//   KeymapChart out.png < "~K pad zone k0..k7" lines (tools/keymap-lines.ps1)

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LizardKing
{
	static class KeymapChart
	{
		[DllImport("shcore.dll")]
		static extern int SetProcessDpiAwareness(int value);

		// pixels per Guide pixel, for a sharp picture on a high-DPI screen
		const int S = 2;
		// the small key map's grid in its overlay: margin and span
		const int GridAt = 12, Grid = 140, PadGap = 64;
		const int Tile = Grid + 8, TileGap = 10, PadIn = 16, Margin = 24;

		class Shot : KeyOverlay
		{
			public Shot(int[,,] km)
			{
				Small = true;
				Status = new CtlStatus { Connected = true, Battery = 80,
							 Rssi = 60, Serial = "-" };
				OnKeymap(km);
			}

			// pad's grid with the other thumb resting on zone and its
			// own thumb on NoZone, which lights no cell
			public Bitmap PadGrid(int pad, int zone)
			{
				OnState(pad == PadR ?
					new PuckState(LayerKbd, NoZone, zone, false) :
					new PuckState(LayerKbd, zone, NoZone, false));
				var all = new Bitmap(ClientSize.Width * S,
						     ClientSize.Height * S,
						     PixelFormat.Format32bppPArgb);
				using (var g = Graphics.FromImage(all)) {
					g.ScaleTransform(S, S);
					g.Clear(Theme.Bg);
					Draw(g, ClientRectangle);
				}
				int x = pad == PadL ? GridAt : GridAt + Grid + PadGap;
				var src = new Rectangle((x - 4) * S, (GridAt - 4) * S,
							Tile * S, Tile * S);
				using (all)
					return all.Clone(src, all.PixelFormat);
			}
		}

		// The overlay paints both pads, each from the other's zone: a
		// thumb on no pad zone still names a page, so the key map gets an
		// empty one past the nine real zones for it.
		const int NoZone = 9;

		static int[,,] ReadKeymap()
		{
			var km = new int[2, NoZone + 1, 8];
			string line;
			while ((line = Console.ReadLine()) != null) {
				var f = line.Trim().Split(' ');
				if (f.Length != 11 || f[0] != "~K")
					continue;
				int pad = Convert.ToInt32(f[1], 16);
				int zone = Convert.ToInt32(f[2], 16);
				for (int k = 0; k < 8; k++)
					km[pad, zone, k] = Convert.ToInt32(f[3 + k], 16);
			}
			return km;
		}

		[STAThread]
		static void Main(string[] args)
		{
			SetProcessDpiAwareness(2);
			var shot = new Shot(ReadKeymap());
			int padW = 3 * Tile + 2 * TileGap + 2 * PadIn;
			int w = 2 * Margin + 2 * padW + 48, h = 2 * Margin + padW;

			using (var chart = new Bitmap(w * S, h * S)) {
				using (var g = Graphics.FromImage(chart)) {
					g.SmoothingMode = SmoothingMode.AntiAlias;
					g.ScaleTransform(S, S);
					g.Clear(Theme.Bg);
					for (int side = 0; side < 2; side++) {
						int pad = side == 0 ? KeyOverlay.PadL :
							  KeyOverlay.PadR;
						int x0 = Margin + side * (padW + 48);
						using (var p = Shape.Round(
							       new RectangleF(x0, Margin, padW, padW),
							       56))
						using (var pen = new Pen(Theme.Edge, 2))
							g.DrawPath(pen, p);
						for (int z = 0; z < 9; z++) {
							int col = 2 - z % 3, row = z / 3;
							int x = x0 + PadIn + col * (Tile + TileGap);
							int y = Margin + PadIn + row * (Tile + TileGap);
							using (var tile = shot.PadGrid(pad, z))
								g.DrawImage(tile, x, y, Tile, Tile);
						}
					}
				}
				chart.Save(args[0], ImageFormat.Png);
			}
		}
	}
}
