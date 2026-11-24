// Scores the shipped LizardKing letter layout and searches for a better one
// (docs/KEYBOARD_LAYOUT.md). Run it after any change to the letters in
// KEYMAP: it reads them straight from the firmware source.
//
// A letter sits in a slot: a pad, a page (the zone the other thumb rests in)
// and one of the pad's 8 rim zones. The search uses the pages the shipped
// layout puts letters on, each pad its own: home (the other thumb in its top
// inner corner) and one more. Each page keeps as many letters as it has
// now, so the punctuation keeps its slots. --fixed keeps the home page's
// letters where they are and searches only the others. A layout is scored
// on English letter bigrams:
//
// - After a letter, the clicking thumb stays on its zone and the other thumb
//   stays on the page zone it was holding.
// - The next letter needs its own thumb on its zone and the other thumb on
//   its page; both thumbs move at once, so a step costs the larger of the two
//   moves.
// - A thumb that stays put costs nothing. One that moves pays for landing on
//   its zone, by how hard the zone is to find blind (LAND_RANK), from 1 for
//   the top inner corner to 1 + --spread for the outer side, plus --travel
//   per zone of distance covered.
// - Using the same thumb twice in a row costs --same extra (release,
//   re-press); alternating thumbs overlap.
// - A letter off the home page costs --page extra (holding the other thumb
//   off home).
//
// Build: tools/layout/build.cmd (the .NET Framework's own csc). Run from the
// repository root:
//   build\layout\LayoutOpt.exe [count_2l.txt] [--keymap file] [--fixed]
//     [--seeds 6] [--iters 150000] [--same 1.0] [--page 0.3]
//     [--spread 1.0] [--travel 0.25]
// count_2l.txt ("xy count" lines, https://norvig.com/ngrams/count_2l.txt)
// defaults to build\layout\count_2l.txt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

static class LayoutOpt
{
	// firmware order: KEYMAP[pad][zone the other thumb rests in][ring]
	const int PadR = 0, PadL = 1;
	static readonly string[] Zones = { "NW", "N", "NE", "W", "C", "E",
					   "SW", "S", "SE" };
	static readonly string[] Ring = { "N", "NE", "E", "SE", "S", "SW", "W",
					  "NW" };
	static readonly Dictionary<string, int[]> Pos =
		new Dictionary<string, int[]> {
			{ "C", new[] { 0, 0 } },    { "N", new[] { 0, 1 } },
			{ "NE", new[] { 1, 1 } },   { "E", new[] { 1, 0 } },
			{ "SE", new[] { 1, -1 } },  { "S", new[] { 0, -1 } },
			{ "SW", new[] { -1, -1 } }, { "W", new[] { -1, 0 } },
			{ "NW", new[] { -1, 1 } },
		};

	// How hard each zone is for the right thumb to find blind, easiest
	// first: top inner corner, top side, top outer corner, inner side,
	// bottom inner corner, bottom side, bottom outer corner, outer side,
	// center (docs/DESIGN.md). The left thumb's is the mirror image.
	static readonly string[] LAND_RANK = { "NW", "N", "NE", "W", "SW",
					       "S", "SE", "E", "C" };

	struct Slot
	{
		public int Pad;
		public string Page, Zone;
	}

	static readonly List<Slot> Slots = new List<Slot>();
	static double[,] cost;
	static double spread = 1.0, travel = 0.25;

	static string Mirror(string z)
	{
		return z.Replace('E', 'x').Replace('W', 'E').Replace('x', 'W');
	}

	// the home page: the other thumb in its top inner corner
	static string Home(int pad)
	{
		return pad == PadR ? "NE" : "NW";
	}

	// effort for the thumb on this pad to go from zone a to zone b
	static double Effort(int pad, string a, string b)
	{
		if (a == b)
			return 0;
		int rank = Array.IndexOf(LAND_RANK, pad == PadR ? b : Mirror(b));
		int[] p = Pos[a], q = Pos[b];
		int dx = q[0] - p[0], dy = q[1] - p[1];
		return 1 + spread * rank / (LAND_RANK.Length - 2) +
		       travel * Math.Sqrt(dx * dx + dy * dy);
	}

	// cost of typing slot b right after slot a
	static double StepCost(Slot a, Slot b, double same, double page)
	{
		var at = new string[2];
		at[a.Pad] = a.Zone;
		at[1 - a.Pad] = a.Page;
		double move = Math.Max(Effort(b.Pad, at[b.Pad], b.Zone),
				       Effort(1 - b.Pad, at[1 - b.Pad], b.Page));
		return move + (a.Pad == b.Pad ? same : 0) +
		       (b.Page != Home(b.Pad) ? page : 0);
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
		if (counts.Count == 0)
			throw new InvalidDataException(path + ": no \"xy count\" lines");
		double total = counts.Values.Sum();
		return counts.ToDictionary(kv => kv.Key, kv => kv.Value / total);
	}

	// The letters of the firmware's KEYMAP: letter -> (pad, page, ring).
	// Entries are K(name) or other tokens (0, media codes) in nested braces.
	static Dictionary<char, int[]> ReadKeymap(string path)
	{
		string src = File.ReadAllText(path);
		int i = src.IndexOf("KEYMAP[2][Z_COUNT][8] = {");
		if (i < 0)
			throw new InvalidDataException(path + ": no KEYMAP");
		int end = src.IndexOf("};", i);
		string body = Regex.Replace(src.Substring(i, end - i), @"//[^\n]*",
					    "");
		body = body.Substring(body.IndexOf('{'));
		var letters = new Dictionary<char, int[]>();
		int depth = 0, pad = -1, page = -1, ring = 0;
		foreach (Match m in Regex.Matches(body,
						  @"\{|\}|K\((\w+)\)|[\w]+")) {
			string t = m.Value;
			if (t == "{") {
				depth++;
				if (depth == 2) {
					pad++;
					page = -1;
				} else if (depth == 3) {
					page++;
					ring = 0;
				}
			} else if (t == "}") {
				depth--;
			} else if (depth == 3) {
				string name = m.Groups[1].Value;
				if (name.Length == 1 && char.IsLetter(name[0]))
					letters[char.ToLower(name[0])] =
						new[] { pad, page, ring };
				ring++;
			}
		}
		if (letters.Count != 26)
			throw new InvalidDataException(path + ": KEYMAP has " +
						       letters.Count + " letters");
		return letters;
	}

	static double Score(int[] slotOf, Dictionary<int, double> big)
	{
		double s = 0;
		foreach (var kv in big)
			s += kv.Value * cost[slotOf[kv.Key / 26], slotOf[kv.Key % 26]];
		return s;
	}

	// Moves the letters in movable among the slots in allowed; the others
	// stay where start has them. A letter moves to an empty slot only on its
	// own page, so each page keeps its number of letters.
	static double Anneal(Dictionary<int, double> big, int iters, Random rng,
			     int[] start, int[] movable, int[] allowed,
			     out int[] bestMap)
	{
		int n = Slots.Count;
		var shuffled = movable.Select(c => start[c])
				       .OrderBy(x => rng.Next()).ToArray();
		var slotOf = (int[])start.Clone();
		var occupant = Enumerable.Repeat(-1, n).ToArray();
		for (int i = 0; i < movable.Length; i++)
			slotOf[movable[i]] = shuffled[i];
		for (int c = 0; c < 26; c++)
			occupant[slotOf[c]] = c;
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
			int c = movable[rng.Next(movable.Length)];
			int s2 = allowed[rng.Next(allowed.Length)], s1 = slotOf[c];
			if (s1 == s2)
				continue;
			int d = occupant[s2];
			if (d < 0 && (Slots[s1].Pad != Slots[s2].Pad ||
				      Slots[s1].Page != Slots[s2].Page))
				continue;
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

	// each page as the Guide draws it: left pad, then right pad
	static string Show(int[] slotOf, Dictionary<int, List<string>> pages)
	{
		var inv = new Dictionary<int, char>();
		for (int c = 0; c < 26; c++)
			inv[slotOf[c]] = (char)('A' + c);
		string[][] grid = { new[] { "NW", "N", "NE" },
				    new[] { "W", "C", "E" },
				    new[] { "SW", "S", "SE" } };
		var sb = new StringBuilder();
		for (int k = 0; k < pages[PadL].Count; k++) {
			string pl = pages[PadL][k], pr = pages[PadR][k];
			sb.AppendLine(pl == Home(PadL) ? "  home:" :
				      "  right thumb in " + pl + " (L), left thumb in " +
					      pr + " (R):");
			foreach (var row in grid) {
				var cells = new List<string>();
				foreach (int pad in new[] { PadL, PadR })
					cells.Add(string.Join(" ", row.Select(z => {
						if (z == "C")
							return ".";
						string page = pages[pad][k];
						int s = Slots.FindIndex(x => x.Pad == pad &&
									     x.Page == page &&
									     x.Zone == z);
						char ch;
						return s >= 0 && inv.TryGetValue(s, out ch) ?
							       ch.ToString() :
							       "-";
					})));
				sb.AppendLine("    L: " + cells[0] + "     R: " +
					      cells[1]);
			}
		}
		return sb.ToString();
	}

	static int Main(string[] args)
	{
		string counts = @"build\layout\count_2l.txt";
		string keymap = @"LizardKing\mode_lizardking.cpp";
		int seeds = 6, iters = 150000;
		double same = 1.0, page = 0.3;
		bool fix = false;
		var inv = CultureInfo.InvariantCulture;
		for (int i = 0; i < args.Length; i++) {
			if (!args[i].StartsWith("--")) {
				counts = args[i];
				continue;
			}
			if (args[i] == "--fixed") {
				fix = true;
				continue;
			}
			if (i + 1 >= args.Length) {
				Console.Error.WriteLine(args[i] + " needs a value");
				return 2;
			}
			string v = args[++i];
			switch (args[i - 1]) {
			case "--keymap": keymap = v; break;
			case "--seeds": seeds = int.Parse(v, inv); break;
			case "--iters": iters = int.Parse(v, inv); break;
			case "--same": same = double.Parse(v, inv); break;
			case "--page": page = double.Parse(v, inv); break;
			case "--spread": spread = double.Parse(v, inv); break;
			case "--travel": travel = double.Parse(v, inv); break;
			default:
				Console.Error.WriteLine("unknown option " + args[i - 1]);
				return 2;
			}
		}

		var shipped = ReadKeymap(keymap);
		// the pages the shipped layout puts letters on, per pad, home first
		var pages = new Dictionary<int, List<string>>();
		foreach (int pad in new[] { PadR, PadL }) {
			pages[pad] = shipped.Values.Where(v => v[0] == pad)
					     .Select(v => Zones[v[1]]).Distinct()
					     .OrderBy(z => z == Home(pad) ? 0 : 1)
					     .ToList();
			if (pages[pad][0] != Home(pad) || pages[pad].Count != 2)
				throw new InvalidDataException(
					"each pad needs letters on its home page and one other");
			foreach (string pg in pages[pad])
				foreach (string z in Ring)
					Slots.Add(new Slot { Pad = pad, Page = pg, Zone = z });
		}
		int n = Slots.Count;
		cost = new double[n, n];
		for (int a = 0; a < n; a++)
			for (int b = 0; b < n; b++)
				cost[a, b] = StepCost(Slots[a], Slots[b], same, page);
		var big = Load(counts);

		var shippedMap = new int[26];
		foreach (var kv in shipped)
			shippedMap[kv.Key - 'a'] = Slots.FindIndex(
				x => x.Pad == kv.Value[0] &&
				     x.Page == Zones[kv.Value[1]] &&
				     x.Zone == Ring[kv.Value[2]]);
		double shippedScore = Score(shippedMap, big);
		Console.WriteLine("shipped: {0:F4}\n{1}", shippedScore,
				  Show(shippedMap, pages));

		var movable = Enumerable.Range(0, 26)
				      .Where(c => !fix || Slots[shippedMap[c]].Page !=
							  Home(Slots[shippedMap[c]].Pad))
				      .ToArray();
		var allowed = Enumerable.Range(0, Slots.Count)
				      .Where(x => !fix ||
					  Slots[x].Page != Home(Slots[x].Pad))
			      .ToArray();
		var results = new List<Tuple<double, int[]>>();
		for (int seed = 0; seed < seeds; seed++) {
			int[] map;
			double b = Anneal(big, iters, new Random(seed), shippedMap,
					  movable, allowed, out map);
			results.Add(Tuple.Create(b, map));
			Console.WriteLine("seed {0}: {1:F4}", seed, b);
		}
		var best = results.OrderBy(r => r.Item1).First();
		Console.WriteLine("\nbest found: {0:F4}\n{1}", best.Item1,
				  Show(best.Item2, pages));
		Console.WriteLine("shipped costs {0:F1}% more than the best found",
				  100 * (shippedScore / best.Item1 - 1));
		return 0;
	}
}
