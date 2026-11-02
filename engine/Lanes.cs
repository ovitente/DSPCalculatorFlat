// Lanes.cs - input lanes, lane split and tier choice (PLAN.md decisions 7, 8, 9). Pure model code (no game DLL).
// The Composer, Validator and FlatEngine call in here through small marked hook points ("lanes hook"):
//  - decision 7: every block input lane (raw, marked external, products of dropped steps) starts with its icon tail ON the
//    block's input edge (TailsToEdge in the composer, InputEdgeErrors in the validator);
//  - decision 9: machines per lane = floor(lane rate / one machine's full-speed per-minute demand), for every input and
//    the output lane (MachinesPerLane / LanesFor / GroupCap); a group that needs more lanes becomes parallel rows, each
//    with its own lanes, and the lanes of one material stay side by side on the edge (Composer.GroupByRaw);
//  - decision 8: the layout is built at the cap tiers (LayoutOptions.BeltItemMax / SorterItemMax), then every belt run
//    and every sorter gets the cheapest tier that carries its flow, never above the cap (AssignTiers).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{
public static class Lanes
{
    const double Eps = 1e-6;
    /// Pile Sorter item id (game-data.json; not an engine tier).
    public const int PileSorter = 2014;

    /// Belt and sorter tiers the engine can emit, cheapest first. The pile sorter (2014) is not supported (stacking,
    /// no prototype yet): a pile-sorter cap is clamped to Mk.III by the PlanMapper.
    public static readonly int[] BeltTiers = { Ids.BeltMk1, Ids.BeltMk2, Ids.BeltMk3 };
    public static readonly int[] SorterTiers = { Ids.SorterMk1, Ids.SorterMk2, Ids.SorterMk3 };

    /// Position of a belt or sorter item in its tier list (0 = Mk.I), -1 for anything else.
    public static int Rank(int item)
    {
        int i = Array.IndexOf(BeltTiers, item);
        return i >= 0 ? i : Array.IndexOf(SorterTiers, item);
    }

    /// True when the belt/sorter item is not above the cap item of the same kind (unknown items: false).
    public static bool WithinCap(int item, int cap)
    {
        int r = Rank(item), c = Rank(cap);
        return r >= 0 && c >= 0 && r <= c;
    }

    // ------------------------------------------------------------------ decision 8: tier choice under the caps
    /// Cheapest belt tier (not above cap, not in missing) whose rate carries flow (items/min); the cap itself when none
    /// does (the composer splits such flows into parallel lanes at the cap tier).
    public static int BeltFor(GameData gd, double flow, int cap, ICollection<int> missing)
    {
        foreach (int t in BeltTiers)
        {
            if (!WithinCap(t, cap) || (missing != null && missing.Contains(t))) continue;
            if (gd != null && gd.BeltRate(t) + Eps >= flow) return t;
        }
        return cap;
    }

    /// Cheapest sorter tier (not above cap, not in missing) that moves perSorter items/min at the given length.
    public static int SorterFor(GameData gd, double perSorter, int length, int cap, ICollection<int> missing)
    {
        foreach (int t in SorterTiers)
        {
            if (!WithinCap(t, cap) || (missing != null && missing.Contains(t))) continue;
            if (gd != null && gd.SorterRate(t, length) + Eps >= perSorter) return t;
        }
        return cap;
    }

    /// Sorter item for the buffer depot cell: its prototypes are the user's game-proven Mk.II records (cells depot-*),
    /// so the depot sorters stay Mk.II unless the cap is lower.
    public static int DepotSorter(int cap) { return WithinCap(cap, Ids.SorterMk2) ? cap : Ids.SorterMk2; }

    /// Belt/sorter caps from the calculator's limits: Mk.I-III accepted, the pile sorter clamped to Mk.III, anything
    /// unknown -> Mk.II. Warnings for every clamp.
    public static void ApplyCaps(GameData gd, LayoutOptions opt, Tiers tiers, List<string> warnings)
    {
        Func<int, string> n = id => gd != null ? gd.ItemName(id) : id.ToString(CultureInfo.InvariantCulture);
        tiers.Belt = Array.IndexOf(BeltTiers, opt.BeltItemMax) >= 0 ? opt.BeltItemMax : Ids.BeltMk2;
        if (tiers.Belt != opt.BeltItemMax) warnings.Add("belt cap " + n(opt.BeltItemMax) + " is not a belt tier the engine knows, using " + n(tiers.Belt));
        if (opt.SorterItemMax == PileSorter) { tiers.Sorter = Ids.SorterMk3; warnings.Add("sorter cap " + n(opt.SorterItemMax) + ": pile sorters are not supported yet, using " + n(tiers.Sorter)); }
        else
        {
            tiers.Sorter = Array.IndexOf(SorterTiers, opt.SorterItemMax) >= 0 ? opt.SorterItemMax : Ids.SorterMk2;
            if (tiers.Sorter != opt.SorterItemMax) warnings.Add("sorter cap " + n(opt.SorterItemMax) + " is not a sorter tier the engine knows, using " + n(tiers.Sorter));
        }
    }

    /// Tiers without a prototype record cannot be emitted: they are excluded from the cheapest-tier choice
    /// (spec.MissingTiers), and a cap without a prototype drops to the highest tier below it that has one (warning).
    public static void ClampToPrototypes(GameData gd, BlockSpec spec, PrototypeCells cells, List<string> warnings)
    {
        if (cells == null) return;
        Func<int, string> n = id => gd != null ? gd.ItemName(id) : id.ToString(CultureInfo.InvariantCulture);
        foreach (int t in BeltTiers.Concat(SorterTiers)) if (cells.ProtoFor(t) == null) spec.MissingTiers.Add(t);
        int b = spec.Tiers.Belt, s = spec.Tiers.Sorter;
        while (spec.MissingTiers.Contains(spec.Tiers.Belt) && Rank(spec.Tiers.Belt) > 0) spec.Tiers.Belt = BeltTiers[Rank(spec.Tiers.Belt) - 1];
        while (spec.MissingTiers.Contains(spec.Tiers.Sorter) && Rank(spec.Tiers.Sorter) > 0) spec.Tiers.Sorter = SorterTiers[Rank(spec.Tiers.Sorter) - 1];
        if (b != spec.Tiers.Belt) warnings.Add("belt cap " + n(b) + ": no prototype record, using " + n(spec.Tiers.Belt));
        if (s != spec.Tiers.Sorter) warnings.Add("sorter cap " + n(s) + ": no prototype record, using " + n(spec.Tiers.Sorter));
    }

    // ------------------------------------------------------------------ decision 9: lane split
    static double Speed(GameData gd, Step s) { return s.Speed > 0 ? s.Speed : (gd != null ? gd.Speed(s.MachineItem) : 1.0); }

    /// One machine's full-speed demand for an input item, items/min (0 when unknown).
    public static double MachineDemand(GameData gd, Step s, int item)
    {
        if (s == null || s.Recipe == null || s.Recipe.TimeSec <= 0) return 0;
        return s.Recipe.InCount(item) * 60.0 * Speed(gd, s) / s.Recipe.TimeSec;
    }

    /// One machine's full-speed output of the step's product, items/min (0 when unknown).
    public static double MachineOutput(GameData gd, Step s)
    {
        if (s == null || s.Recipe == null || s.Recipe.TimeSec <= 0) return 0;
        return s.Recipe.OutCount(s.Item) * 60.0 * Speed(gd, s) / s.Recipe.TimeSec;
    }

    /// floor(lane rate / one machine's per-minute demand), at least 1 (int.MaxValue when the demand is unknown).
    public static int MachinesPerLane(double laneRate, double perMachine)
    {
        if (perMachine <= 0 || laneRate <= 0) return int.MaxValue;
        return Math.Max(1, (int)Math.Floor(laneRate / perMachine + 1e-9));
    }

    /// Machines one row of the step may have so that every input lane and the output lane carry the full-speed flow of
    /// all its machines: the minimum of MachinesPerLane over the inputs and the output. (The sorter side of decision 9 -
    /// each machine's sorters must deliver its demand - is MachineRow's extra-sorter rule plus the cheapest-tier choice;
    /// a sorter cannot raise a machine's draw, so it never allows more machines per lane.)
    public static int MachinesPerLane(GameData gd, Step s, double laneRate)
    {
        int m = MachinesPerLane(laneRate, MachineOutput(gd, s));
        if (s != null) foreach (ItemRate x in s.Inputs) m = Math.Min(m, MachinesPerLane(laneRate, MachineDemand(gd, s, x.Item)));
        return m;
    }

    /// Parallel rows (each with its own lanes) that machines of the step need: ceil(machines / MachinesPerLane).
    public static int LanesFor(GameData gd, Step s, int machines, double laneRate)
    {
        int per = MachinesPerLane(gd, s, laneRate);
        if (per == int.MaxValue || machines <= 0) return 1;
        return Math.Max(1, (machines + per - 1) / per);
    }

    /// Largest output rate (items/min) one row of the step may make so that all of its lanes still fit one belt at full
    /// machine speed (grouped row mode: consumer groups are packed up to this rate instead of the plain belt rate).
    public static double GroupCap(GameData gd, Step s, double laneRate)
    {
        int per = MachinesPerLane(gd, s, laneRate);
        double out1 = MachineOutput(gd, s);
        if (per == int.MaxValue || out1 <= 0) return laneRate;
        return Math.Min(laneRate, per * out1);
    }

    // ------------------------------------------------------------------ decision 7: input lanes end on the input edge
    /// Composer hook (canonical frame, input edge = south, lanes flow north): every block input port whose first tile is
    /// above the southern edge row of the layout gets straight tail belts down to that row; the icon moves to the new
    /// first tile and the port follows. free(x, y) says whether the router may still use a tile. A lane that cannot be
    /// extended is a layout error (the attempt fails, another attempt may fit).
    public static void TailsToEdge(Layout work, List<Port> ports, Func<int, int, bool> free, List<string> errors)
    {
        Rect box = Rect.Empty;
        foreach (Bld b in work.All) if (b.Kind != Kind.Sorter && b.Kind != Kind.Tesla) box = box.Union(work.Footprints.TileRect(b));
        if (box.IsEmpty) return;
        int edge = box.Y0;
        foreach (Port p in ports)
        {
            if (p.Kind != PortKind.In || p.Belt == null || p.Seam || p.Y <= edge) continue;
            Bld first = p.Belt;
            bool ok = p.Dir == Dir.N && first.TileX == p.X && first.TileY == p.Y;
            for (int y = edge; y < p.Y && ok; y++) ok = free(p.X, y);
            if (!ok)
            {
                errors.Add("composer: input lane " + p.Item + " at (" + p.X + "," + p.Y + ") cannot be extended to the input edge (row " + edge + ")");
                continue;
            }
            Bld next = first;
            for (int y = p.Y - 1; y >= edge; y--)
            {
                Bld t = work.Add(Bld.Belt(first.ItemId, p.X, y, Dirs.Yaw(Dir.N), first.Carries, first.Chain));
                t.Role = "input tail";
                Layout.Link(t, next, 1);
                next = t;
            }
            next.Parameters = first.Parameters != null ? (int[])first.Parameters.Clone() : new[] { p.Item, 0 };
            first.Parameters = null;
            p.Belt = next; p.Y = edge;
        }
    }

    /// Validator hook: every block input port (except bus seams) sits on the outermost row/column of the layout on the
    /// spec's input edge (buildings without sorters and towers) and its lane enters the block from that edge.
    public static List<string> InputEdgeErrors(Layout l, BlockSpec spec)
    {
        var res = new List<string>();
        if (l == null || spec == null) return res;
        Rect box = Rect.Empty;
        foreach (Bld b in l.All) if (b.Kind != Kind.Sorter && b.Kind != Kind.Tesla) box = box.Union(l.Footprints.TileRect(b));
        if (box.IsEmpty) return res;
        Dir e = spec.InEdge;
        foreach (Port p in l.Ports)
        {
            if (p.Kind != PortKind.In || p.Seam || p.Belt == null) continue;
            bool on = e == Dir.S ? p.Y == box.Y0 : e == Dir.N ? p.Y == box.Y1 : e == Dir.W ? p.X == box.X0 : p.X == box.X1;
            if (!on) res.Add("input edge: lane " + p.Item + " starts at (" + p.X + "," + p.Y + "), not on the " + e + " edge of the block");
            else if (p.Dir != Dirs.Opposite(e)) res.Add("input edge: lane " + p.Item + " at (" + p.X + "," + p.Y + ") flows " + p.Dir + ", it must enter from the " + e + " edge");
        }
        return res;
    }

    // ------------------------------------------------------------------ decision 8: cheapest tiers on a finished layout
    /// Composer hook on the chosen layout: every belt run (straight/corner links; side-joins and splitters start new runs)
    /// gets the cheapest belt tier that carries the run's highest load, every machine sorter the cheapest tier that moves
    /// max(planned, full-speed) items at its length - all within the spec caps and only tiers with prototypes. Loads come
    /// from the Validator's flow walk. The result is validated again; if it fails, the cap tiers are restored (note).
    /// Returns true when tiers were changed and kept.
    public static bool AssignTiers(GameData gd, BlockSpec spec, Layout l, ProductionPlan plan, List<string> notes)
    {
        if (gd == null || spec == null || l == null) return false;
        var v = new Validator(gd, spec);
        ValidationReport rep = v.Validate(l, plan);
        if (!rep.Ok || v.BeltLoads == null) return false;
        var before = new Dictionary<Bld, int>();
        foreach (Bld b in l.All) if (b.Kind == Kind.Belt || b.Kind == Kind.Sorter) before[b] = b.ItemId;

        // belt runs: union over straight/corner links (OutputToSlot <= 1) between belts
        var parent = new Dictionary<Bld, Bld>();
        Func<Bld, Bld> find = null;
        find = x => { Bld p = parent[x]; if (p == x) return x; Bld r = find(p); parent[x] = r; return r; };
        var belts = l.All.Where(b => b.Kind == Kind.Belt).ToList();
        foreach (Bld b in belts) parent[b] = b;
        foreach (Bld b in belts)
            if (b.Output != null && b.Output.Kind == Kind.Belt && parent.ContainsKey(b.Output) && b.OutputToSlot <= 1) parent[find(b)] = find(b.Output);
        var runMax = new Dictionary<Bld, double>();
        foreach (Bld b in belts)
        {
            Bld r = find(b);
            double load, cur;
            if (!v.BeltLoads.TryGetValue(b, out load)) load = double.MaxValue;   // unknown load: keep the cap tier
            runMax[r] = runMax.TryGetValue(r, out cur) ? Math.Max(cur, load) : load;
        }
        foreach (Bld b in belts)
        {
            double m = runMax[find(b)];
            b.ItemId = m == double.MaxValue ? spec.Tiers.Belt : BeltFor(gd, m, spec.Tiers.Belt, spec.MissingTiers);
        }
        // machine sorters (the depot buffer sorters keep their named Mk.II prototypes)
        foreach (Bld s in l.All.Where(x => x.Kind == Kind.Sorter))
        {
            if (s.Cell != null && s.Cell.StartsWith("depot-", StringComparison.Ordinal)) continue;
            double need;
            if (v.SorterNeeds == null || !v.SorterNeeds.TryGetValue(s, out need) || s.SorterLength <= 0) continue;
            s.ItemId = SorterFor(gd, need, s.SorterLength, spec.Tiers.Sorter, spec.MissingTiers);
        }
        ValidationReport again = new Validator(gd, spec).Validate(l, plan);
        if (!again.Ok)
        {
            foreach (var kv in before) kv.Key.ItemId = kv.Value;
            if (notes != null) notes.Add("tiers: cheapest-tier choice failed validation (" + again.Errors[0] + "), keeping the cap tiers");
            return false;
        }
        if (notes != null) notes.Add("tiers: " + TierSummary(gd, l) + " (cheapest per lane / sorter, caps " + Name(gd, spec.Tiers.Belt) + " / " + Name(gd, spec.Tiers.Sorter) + ")");
        return true;
    }

    static string Name(GameData gd, int id) { return gd != null ? gd.ItemName(id) : id.ToString(CultureInfo.InvariantCulture); }

    /// "belts Mk.I 40, Mk.II 12; sorters Mk.I 20, Mk.II 4" (counts per item in the layout).
    public static string TierSummary(GameData gd, Layout l)
    {
        Func<int[], string> part = tiers => string.Join(", ", tiers.Where(t => l.All.Any(b => b.ItemId == t))
            .Select(t => BpText.Tier(Name(gd, t)) + " " + l.All.Count(b => b.ItemId == t).ToString(CultureInfo.InvariantCulture)).ToArray());
        return "belts " + part(BeltTiers) + "; sorters " + part(SorterTiers);
    }

    /// Distinct belt / sorter tiers used by a layout, cheapest first ("Mk.I/Mk.II"); fallback when none is used.
    public static string TiersUsed(GameData gd, Layout l, int[] tiers, int fallback)
    {
        var used = tiers.Where(t => l != null && l.All.Any(b => b.ItemId == t)).ToList();
        if (used.Count == 0) used.Add(fallback);
        return string.Join("/", used.Select(t => BpText.Tier(Name(gd, t))).ToArray());
    }
}
}
