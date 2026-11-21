// Searches for a LizardPuck keyboard letter layout (docs/KEYBOARD_LAYOUT.md).
//
// Letters go into 32 slots: each pad's 8 outer zones, with the other thumb
// resting either in the center ("home", 16 slots) or in the top zone ("N
// page", 16 slots). A layout is scored on English letter bigrams:
//
// - After a letter, the clicking thumb stays on its zone and the other thumb
//   stays where its page needed it (center or N).
// - The next letter needs its own thumb on its zone and the other thumb on
//   its page; both thumbs move at once, so a step costs the longer of the
//   two moves (grid units: side zone = 1, corner = 1.41 from center).
// - Using the same thumb twice in a row costs --same extra (release,
//   re-press); alternating thumbs overlap.
// - A letter on the N page costs --page extra (holding the other thumb up).
//
// Build: tools/layout/build.cmd (the .NET Framework's own csc, nothing to
// install). Usage:
//   LayoutOpt count_2l.txt [--seeds 8] [--iters 200000] [--same 0.6]
//                          [--page 0.3]
// count_2l.txt: https://norvig.com/ngrams/count_2l.txt ("xy count" lines).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

static class LayoutOpt
{
	static readonly string[] Pads = { "L", "R" };
	static readonly string[] Pages = { "home", "N" };
	static readonly string[] Ring = { "N", "NE", "E", "SE", "S", "SW", "W",
					  "NW" };
	static readonly Dictionary<string, int[]> Pos =
		new Dictionary<string, int[]> {
			{ "C", new[] { 0, 0 } },   { "N", new[] { 0, 1 } },
			{ "NE", new[] { 1, 1 } },  { "E", new[] { 1, 0 } },
			{ "SE", new[] { 1, -1 } }, { "S", new[] { 0, -1 } },
			{ "SW", new[] { -1, -1 } }, { "W", new[] { -1, 0 } },
			{ "NW", new[] { -1, 1 } },
		};

	struct Slot
	{
		public string Pad, Page, Zone;
	}

	static readonly List<Slot> Slots = new List<Slot>();
	static double[,] cost;

	static double Dist(string a, string b)
	{
		int[] p = Pos[a], q = Pos[b];
		return Math.Sqrt((p[0] - q[0]) * (p[0] - q[0]) +
				 (p[1] - q[1]) * (p[1] - q[1]));
	}

	// cost of typing slot b right after slot a
	static double StepCost(Slot a, Slot b, double same, double page)
	{
		string otherA = a.Page == "home" ? "C" : "N";
		string otherPadA = a.Pad == "L" ? "R" : "L";
		var where = new Dictionary<string, string> { { a.Pad, a.Zone },
							     { otherPadA, otherA } };
		string needOther = b.Page == "home" ? "C" : "N";
		string otherPadB = b.Pad == "L" ? "R" : "L";
		double move = Math.Max(Dist(where[b.Pad], b.Zone),
				       Dist(where[otherPadB], needOther));
		return move + (a.Pad == b.Pad ? same : 0) +
		       (b.Page == "N" ? page : 0);
	}

	static void SetWeights(double same, double page)
	{
		int n = Slots.Count;
		cost = new double[n, n];
		for (int i = 0; i < n; i++)
			for (int j = 0; j < n; j++)
				cost[i, j] = StepCost(Slots[i], Slots[j], same, page);
	}

	// bigram -> share of all bigrams, letters a..z as 0..25
	static Dictionary<int, double> Load(string path)
	{
		var counts = new Dictionary<int, long>();
		foreach (string line in File.ReadLines(path)) {
			var f = line.Split((char[])null,
					   StringSplitOptions.RemoveEmptyEntries);
			if (f.Length != 2 || f[0].Length != 2 ||
			    !f[0].All(char.IsLetter))
				continue;
			string k = f[0].ToLowerInvariant();
			counts[(k[0] - 'a') * 26 + (k[1] - 'a')] =
				long.Parse(f[1], CultureInfo.InvariantCulture);
		}
		double total = counts.Values.Sum();
		return counts.ToDictionary(kv => kv.Key, kv => kv.Value / total);
	}

	static double Score(int[] slotOf, Dictionary<int, double> big)
	{
		double s = 0;
		foreach (var kv in big)
			s += kv.Value * cost[slotOf[kv.Key / 26], slotOf[kv.Key % 26]];
		return s;
	}

	static double Anneal(Dictionary<int, double> big, int iters, Random rng,
			     out int[] bestMap)
	{
		int n = Slots.Count;
		var shuffled = Enumerable.Range(0, n).OrderBy(x => rng.Next())
				       .ToArray();
		var slotOf = new int[26];
		var occupant = Enumerable.Repeat(-1, n).ToArray();
		for (int c = 0; c < 26; c++) {
			slotOf[c] = shuffled[c];
			occupant[shuffled[c]] = c;
		}
		var byLetter = new List<KeyValuePair<int, double>>[26];
		for (int c = 0; c < 26; c++)
			byLetter[c] = new List<KeyValuePair<int, double>>();
		foreach (var kv in big) {
			int a = kv.Key / 26, b = kv.Key % 26;
			byLetter[a].Add(kv);
			if (b != a)
				byLetter[b].Add(kv);
		}

		Func<int, int, double> local = (c, d) => {
			var seen = new HashSet<int>();
			double tot = 0;
			foreach (int x in d >= 0 ? new[] { c, d } : new[] { c })
				foreach (var kv in byLetter[x])
					if (seen.Add(kv.Key))
						tot += kv.Value * cost[slotOf[kv.Key / 26],
								       slotOf[kv.Key % 26]];
			return tot;
		};

		double cur = Score(slotOf, big), best = cur;
		bestMap = (int[])slotOf.Clone();
		const double t0 = 0.02, t1 = 0.00005;
		for (int i = 0; i < iters; i++) {
			double t = t0 * Math.Pow(t1 / t0, (double)i / iters);
			int c = rng.Next(26), s2 = rng.Next(n), s1 = slotOf[c];
			if (s1 == s2)
				continue;
			int d = occupant[s2];
			double before = local(c, d);
			slotOf[c] = s2;
			if (d >= 0)
				slotOf[d] = s1;
			double delta = local(c, d) - before;
			if (delta <= 0 || rng.NextDouble() < Math.Exp(-delta / t)) {
				occupant[s1] = d;
				occupant[s2] = c;
				cur += delta;
				if (cur < best) {
					best = cur;
					bestMap = (int[])slotOf.Clone();
				}
			} else {
				slotOf[c] = s1;
				if (d >= 0)
					slotOf[d] = s2;
			}
		}
		return best;
	}

	static string Show(int[] slotOf)
	{
		var inv = new Dictionary<int, char>();
		for (int c = 0; c < 26; c++)
			inv[slotOf[c]] = (char)('A' + c);
		string[][] grid = { new[] { "NW", "N", "NE" },
				    new[] { "W", "C", "E" },
				    new[] { "SW", "S", "SE" } };
		var sb = new StringBuilder();
		foreach (string page in Pages) {
			sb.AppendLine("  other thumb " +
				      (page == "home" ? "center:" : "in N:"));
			foreach (var row in grid) {
				var cells = new List<string>();
				foreach (string pad in Pads)
					cells.Add(string.Join(" ", row.Select(z => {
						if (z == "C")
							return ".";
						int s = Slots.FindIndex(x => x.Pad == pad &&
									     x.Page == page &&
									     x.Zone == z);
						char ch;
						return inv.TryGetValue(s, out ch) ?
							       ch.ToString() :
							       "-";
					})));
				sb.AppendLine("    L: " + cells[0] + "     R: " +
					      cells[1]);
			}
		}
		return sb.ToString();
	}

	// rows: (pad, page) -> ring string clockwise from N
	static int[] FromRows(Dictionary<string, string> rows)
	{
		var slotOf = new int[26];
		foreach (var kv in rows) {
			var key = kv.Key.Split('/');
			for (int i = 0; i < Ring.Length && i < kv.Value.Length; i++) {
				char ch = kv.Value[i];
				if (!char.IsLetter(ch))
					continue;
				slotOf[char.ToLower(ch) - 'a'] = Slots.FindIndex(
					x => x.Pad == key[0] && x.Page == key[1] &&
					     x.Zone == Ring[i]);
			}
		}
		return slotOf;
	}

	static int Main(string[] args)
	{
		if (args.Length < 1) {
			Console.Error.WriteLine("usage: LayoutOpt count_2l.txt " +
						"[--seeds 8] [--iters 200000] " +
						"[--same 0.6] [--page 0.3]");
			return 2;
		}
		int seeds = 8, iters = 200000;
		double same = 0.6, page = 0.3;
		for (int i = 1; i + 1 < args.Length; i += 2) {
			string v = args[i + 1];
			var inv = CultureInfo.InvariantCulture;
			if (args[i] == "--seeds")
				seeds = int.Parse(v, inv);
			else if (args[i] == "--iters")
				iters = int.Parse(v, inv);
			else if (args[i] == "--same")
				same = double.Parse(v, inv);
			else if (args[i] == "--page")
				page = double.Parse(v, inv);
		}
		foreach (string pad in Pads)
			foreach (string pg in Pages)
				foreach (string z in Ring)
					Slots.Add(new Slot { Pad = pad, Page = pg, Zone = z });
		SetWeights(same, page);
		var big = Load(args[0]);

		var v0 = FromRows(new Dictionary<string, string> {
			{ "L/home", "etaoinsh" }, { "R/home", "rdlcumwf" },
			{ "L/N", "gypbvkjx" },     { "R/N", "qz......" },
		});
		double v0Score = Score(v0, big);
		Console.WriteLine("v0 layout: {0:F4}\n{1}", v0Score, Show(v0));

		var results = new List<Tuple<double, int[]>>();
		for (int seed = 0; seed < seeds; seed++) {
			int[] map;
			double b = Anneal(big, iters, new Random(seed), out map);
			results.Add(Tuple.Create(b, map));
			Console.WriteLine("seed {0}: {1:F4}", seed, b);
		}
		foreach (var r in results.OrderBy(r => r.Item1).Take(3))
			Console.WriteLine("\nscore {0:F4} ({1:F0}% of v0)\n{2}", r.Item1,
					  100 * r.Item1 / v0Score, Show(r.Item2));
		return 0;
	}
}
