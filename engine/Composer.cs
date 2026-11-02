// Composer.cs - ProductionPlan -> abstract Layout (no game DLL).
// Placement v1 (coil-360-v3 scheme): every Step becomes one or more MachineRow fragments (split when a lane would exceed
// the belt rate). Rows are columns flowing north; rows that only eat raw items start new "lines" at the south edge,
// a consumer row is stacked on top of the line of one of its producers (lanes aligned, so the producer's output lane
// continues straight into the consumer's input lane). Lines are placed west -> east, the line holding the product first.
// All other connections are routed by a grid router (Manhattan, turn penalty) over free tiles only, so belts never
// cross. Several consumer rows of one producer: Modules.Splitter (rule 7); several producers into one lane:
// Modules.SideMerge (rule 6). Raw inputs enter at spec.InEdge (icon on the first tile, rule 5), the product leaves at
// spec.OutEdge. Everything is built in a canonical frame (in = S) and rotated at the end. Tesla towers last.
// Layout problems never throw: they go to Layout.Errors. All gap/mode/route-order attempts are tried; the smallest
// (bounding-box area) attempt that also passes the Validator is returned.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DspFlat.Engine
{

public class Composer
{
    readonly GameData gd;
    readonly BlockSpec spec;

    /// Description text of the last Compose (block ports with edge and rate, machines, tiers). For the header desc.
    public string Desc;
    /// Info lines of the last Compose (gap configuration used, attempts that failed).
    public readonly List<string> Notes = new List<string>();

    /// Progress callback (MakeBlock console): called once per placement attempt and squeeze step.
    public Action<string> OnProgress;
    static string Size(Layout l) { Rect b = l.Bounds(); return b.Width + "x" + b.Height; }

    /// Placement attempts stop after this much time (the best attempt so far is returned).
    public int TimeBudgetMs = EngineTuning.ComposeBudgetMs;
    /// Polled between attempts (host cancel / overall time budget): when it returns true the composer stops and keeps
    /// the best attempt found so far (Stopped = true).
    public Func<bool> Cancel;
    public bool Stopped;
    bool Halt() { if (Stopped) return true; if (Cancel != null && Cancel()) { Stopped = true; Notes.Add("stopped (cancel or time budget), keeping the best attempt so far"); } return Stopped; }

    public Composer(GameData gd, BlockSpec spec) { this.gd = gd; this.spec = spec; }

    // (row gap, line gap) in free tiles; all are tried, the smallest clean attempt wins (see Compose). Row gap 0 stacks a
    // consumer row flush on its producer (user review 2026-10-31); Place widens it where the machine colliders need it.
    static readonly int[][] GapConfigs = { new[] { 1, 0 }, new[] { 1, 1 }, new[] { 2, 0 }, new[] { 2, 1 }, new[] { 1, 2 }, new[] { 2, 2 }, new[] { 3, 1 }, new[] { 3, 2 }, new[] { 2, 3 }, new[] { 3, 3 }, new[] { 3, 4 }, new[] { 4, 5 } };

    /// One finished attempt.
    class Tried
    {
        public Layout L; public Attempt A; public int Score; public string Name, VErr; public long Area, Cost; public int[] Routes;
        public int RowGap, LineGap, Strategy, Mode; public bool Raise, Heavy; public HashSet<string> Prio; public int[] Gaps;
    }

    /// Row modes: 0 grouped (one consolidated producer row per intermediate, splitter to its consumers), 1 grouped with
    /// single-consumer rows capped at CapMachines (parts join by side-join), 2 parallel copies (older engine), 3 one
    /// producer row per consumer row (older "split" mode). Modes 2/3 are only tried when 0/1 find nothing valid.
    public const int CapMachines = 8;
    static readonly string[] ModeNames = { "grouped rows + splitters", "grouped rows (single-consumer rows capped at " + CapMachines + ")", "parallel copies", "split rows instead of splitters" };

    Tried Try(ProductionPlan plan, int rowGap, int lineGap, int strategy, int mode, bool raise, bool heavy, int[] gaps, HashSet<string> prioIn)
    {
        // route order retry: edges that failed are routed first in the next try (same gaps)
        var prio = prioIn != null ? new HashSet<string>(prioIn) : new HashSet<string>();
        Attempt att = null; Layout l = null;
        for (int t = 0; t < 4; t++)
        {
            att = new Attempt(gd, spec, plan, rowGap, lineGap, strategy, mode, prio, raise, heavy, gaps);
            l = att.Run();
            if (att.Failed.Count == 0 || att.Failed.IsSubsetOf(prio)) break;
            prio.UnionWith(att.Failed);
        }
        var r = new Tried { L = l, A = att, RowGap = rowGap, LineGap = lineGap, Strategy = strategy, Mode = mode, Raise = raise, Heavy = heavy, Prio = prio, Gaps = gaps };
        r.Score = l.Errors.Count + att.PowerProblems.Count;
        r.Name = "row gap " + rowGap + ", line gap " + lineGap + (gaps != null ? " (squeezed " + string.Join("/", gaps.Select(g => g.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")" : "")
            + ", route order " + strategy + ", " + ModeNames[mode & 3] + ((mode & 4) != 0 ? ", 3-4 input rows not stacked" : "") + ((mode & 8) != 0 ? ", line rows mirrored" : "") + ((mode & 16) != 0 ? ", feeds reversed" : "") + ((mode & 32) != 0 ? ", no tail pickup" : "") + (raise ? ", raw rows raised to their consumers" : "") + (heavy ? ", heaviest feed stacked" : "")
            + (prio.Count > 0 ? ", " + prio.Count + " routes reordered" : "");
        if (r.Score == 0)
        {
            ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
            if (!rep.Ok) { r.Score = rep.Errors.Count; r.VErr = rep.Errors[0]; }
        }
        Rect bx = l.Bounds();
        r.Area = bx.IsEmpty ? long.MaxValue : (long)(bx.X1 - bx.X0 + 1) * (bx.Y1 - bx.Y0 + 1);
        r.Routes = RouteCounts(l);
        int belts = l.All.Count(b => b.Kind == Kind.Belt);
        r.Cost = r.Area == long.MaxValue ? long.MaxValue : r.Area + RunAreaCost * r.Routes[1] + RaisedAreaCost * r.Routes[0] + TurnAreaCost * r.Routes[2] + BeltAreaPct * belts / 100;
        return r;
    }

    /// Selection cost in tiles of area (user's in-game edit 2026-10-31: straight lanes + raised runs instead of
    /// detours): every raised run counts RunAreaCost tiles, every raised node RaisedAreaCost, every corner belt
    /// (ground or raised) TurnAreaCost, so a straight layout with a few raised runs wins over a jogged one.
    public static int RunAreaCost { get { return EngineTuning.RunAreaCost; } }
    public static int RaisedAreaCost { get { return EngineTuning.RaisedAreaCost; } }
    public static int TurnAreaCost { get { return EngineTuning.TurnAreaCost; } }
    public static int BeltAreaPct { get { return EngineTuning.BeltAreaPct; } }   // every belt counts 0.5 tile (user: blocks were mostly belts)

    /// Smaller cost (area + bridge/turn penalty) first; then raised raw rows (closer to their consumers, user review); then fewer buildings.
    static bool Better(Tried a, Tried b)
    {
        if (b == null) return true;
        if (a.Cost != b.Cost) return a.Cost < b.Cost;
        if (a.Area != b.Area) return a.Area < b.Area;
        if (a.Raise != b.Raise) return a.Raise;
        return a.L.All.Count < b.L.All.Count;
    }

    /// Plan -> one MachineRow per step -> placement -> belt routing -> raw input/product ports -> tesla grid.
    /// Returns the finished abstract Layout (Layout.Ports = block ports). Must not throw on layout problems:
    /// record them in Layout.Errors so the Validator reports them.
    public Layout Compose(ProductionPlan plan)
    {
        Notes.Clear();
        Stopped = false;
        // rules hook (engine\Rules.cs, rule 14): margin for zero-margin steps whose lane is split (mutates the plan, so the
        // Validator's supply check sees the same machine counts)
        // PACK HOOK: only for the engine's own plans (spec.AddMargin: CLI solver); injected DSPCalculator plans keep the user's
        // counts (LayoutOptions.AddMargin false). The grouped row split follows the raised counts (Packing.GroupCounts).
        if (Rules.Enabled && Rules.MarginInCompose && spec.AddMargin) Notes.AddRange(Rules.ApplyMargin(plan));
        int attempts = 0;
        Tried best = null, bestOk = null;
        // compactness: every gap configuration / mode / route order / raise variant is tried; among the attempts without
        // problems (layout, power AND the Validator) the smallest area wins. Then the gaps between neighbouring lines are
        // squeezed one by one while the result stays valid.
        bool done = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var failNotes = new List<string>();
        var validNotes = new List<string>();
        bool multi = plan.Steps.Any(s => s.ConsumerSteps.Distinct().Count() > 1);
        bool tall = plan.Steps.Any(s => s.Machines > CapMachines);
        // grouped modes first (user strategy); the older modes only when the grouped ones find nothing valid
        // bits 8/16 (user review: fewer crossings): line-start rows mirrored / producer lines visited in reverse entry order
        int[] grp = { 0, 4, 8, 12, 16, 20, 24, 28 };
        var modeSets = new List<int[]> { tall ? grp.Concat(new[] { 1, 5 }).ToArray() : grp, multi ? new[] { 2, 3 } : new[] { 2 } };
        if (Packing.Enabled && multi) modeSets.Insert(1, modeSets[0].Select(m => m | 32).ToArray());   // PACK HOOK: pre-rules packing (3-way, own rows) only when tails find nothing valid
        int fm = EngineTuning.ForcedMode;   // debug: one row mode only
        if (fm >= 0 && fm <= 63) modeSets = new List<int[]> { new[] { fm } }; // PACK HOOK: bit 32
        foreach (int[] modes in modeSets)
        {
        if (bestOk != null || Halt()) break;
        done = false;
        clock.Restart();   // each mode set has its own time budget
        foreach (var cfg in GapConfigs)
        {
            if (done || Halt()) break;
            if (clock.ElapsedMilliseconds > TimeBudgetMs && (bestOk != null || best != null)) { Notes.Add("time budget reached, keeping the best attempt"); break; }
            foreach (int mode in modes)
            foreach (bool raise in new[] { false, true })
            foreach (bool heavy in new[] { false, true })
            for (int strategy = 0; strategy < 3 && !done; strategy++)
            {
                if (Halt()) { done = true; break; }
                Tried t = Try(plan, cfg[0], cfg[1], strategy, mode, raise, heavy, null, null);
                attempts++;
                if (OnProgress != null) OnProgress("attempt " + attempts + (bestOk != null ? ", best valid " + Size(bestOk.L) : ", no valid layout yet"));
                if (t.Score == 0)
                {
                    validNotes.Add("valid: cost " + t.Cost + " area " + t.Area + " raised " + t.Routes[0] + " turns " + t.Routes[2] + " - " + t.Name);
                    if (Better(t, bestOk)) bestOk = t;
                    if (t.A.Failed.Count == 0 && t.Prio.Count == 0) strategy = 3;   // route order did not matter here
                    continue;
                }
                failNotes.Add("attempt " + t.Name + ": " + t.L.Errors.Count + " layout errors, " + t.A.PowerProblems.Count + " power problems"
                    + (t.VErr != null ? ", validator: " + t.VErr : "")
                    + (t.L.Errors.Count > 0 ? " (first: " + t.L.Errors[0] + ")" : t.A.PowerProblems.Count > 0 ? " (first: " + t.A.PowerProblems[0] + ")" : ""));
                if (t.A.Failed.Count == 0) strategy = 3;   // not a routing-order problem: other orders will not help
                if (best == null || t.Score < best.Score) best = t;
                if (t.A.Fatal) { done = true; break; }   // structural problem (plan not supported): wider gaps will not help
            }
        }
        }
        if (bestOk != null)
        {
            // squeeze: narrow each gap between neighbouring lines while the attempt stays valid
            int[] gaps = Enumerable.Repeat(bestOk.LineGap, Math.Max(0, bestOk.A.LineCount - 1)).ToArray();
            int squeezed = 0;
            for (bool changed = true; changed && clock.ElapsedMilliseconds < TimeBudgetMs * 2 && !Halt(); )
            {
                changed = false;
                for (int i = 0; i < gaps.Length; i++)
                {
                    if (gaps[i] == 0 || Halt()) continue;
                    int[] g = (int[])gaps.Clone(); g[i]--;
                    Tried t = Try(plan, bestOk.RowGap, bestOk.LineGap, bestOk.Strategy, bestOk.Mode, bestOk.Raise, bestOk.Heavy, g, bestOk.Prio);
                    if (OnProgress != null) OnProgress("squeezing line gaps, " + Size(bestOk.L));
                    if (t.Score == 0 && t.Cost <= bestOk.Cost) { bestOk = t; gaps = g; changed = true; squeezed++; }
                }
            }
            if (squeezed > 0) Notes.Add("squeezed " + squeezed + " line gap(s)");
            Layout sys = Retower(bestOk.L);
            if (sys != null && new Validator(gd, spec).Validate(sys, plan).Ok)
            {
                Notes.Add("towers: " + bestOk.L.Count(Kind.Tesla, 0) + " greedy -> " + sys.Count(Kind.Tesla, 0) + " systematic (lattice)");
                bestOk.L = sys;
            }
            else Notes.Add("towers: systematic lattice placement not valid, keeping the greedy towers");
            // rules hook (engine\Rules.cs, rule 13): drop towers whose coverage and links the others already give
            if (Rules.Enabled)
            {
                var cut = new List<Bld>();
                int nCut = Rules.PruneTowers(bestOk.L, cut);
                if (nCut > 0 && !new Validator(gd, spec).Validate(bestOk.L, plan).Ok) { foreach (Bld t in cut) bestOk.L.Add(t); nCut = 0; }
                if (nCut > 0) Notes.Add("rule 13: " + nCut + " redundant tower(s) removed");
            }
            foreach (string rn in bestOk.A.RowNotes.Distinct()) Notes.Add("row " + rn);
            int nb = bestOk.L.All.Count(b => b.IsRaised), ns = bestOk.L.Count(Kind.Splitter, 0);
            if (nb > 0) Notes.Add("bridges: " + nb + " raised belt node(s), cell BRIDGE (game-proven, refs\\bridges-splitters-user.txt)");
            if (ns > 0) Notes.Add("splitters: " + ns + " (cell SPLIT 1 in -> up to 3 out, game-proven; no cascades)");
            Notes.Add("routing: " + RouteStats(bestOk.L));
            Notes.Add(failNotes.Count + " other attempts failed (layout, power or validator)");
            if (EngineTuning.DebugNotes) { Notes.AddRange(failNotes); Notes.AddRange(validNotes); }
            Rect bx = bestOk.L.Bounds();
            Notes.Add("used " + bestOk.Name + " (" + (bx.X1 - bx.X0 + 1) + "x" + (bx.Y1 - bx.Y0 + 1) + ")");
            best = bestOk;
        }
        else
        {
            Notes.AddRange(failNotes);
            Notes.Add("used " + (best != null ? best.Name + " (" + best.Score + " problems left)" : "nothing"));
        }
        if (best == null)
        {
            // stopped before the first attempt finished
            var empty = new Layout(gd);
            empty.Errors.Add("composer: stopped before any placement attempt finished");
            Desc = "";
            return empty;
        }
        // lanes hook (PLAN.md decision 8): cheapest belt/sorter tier per lane and sorter inside the caps
        if (bestOk != null && spec.CheapestTiers) Lanes.AssignTiers(gd, spec, best.L, plan, Notes);
        // rules hook (engine\Rules.cs): report the layout against the user's 15 rules (measurable ones)
        if (Rules.Enabled && plan.Steps.Count > 0 && plan.Steps[plan.Steps.Count - 1].Recipe != null)
            Notes.AddRange(Rules.Report(Rules.Check(best.L, plan, plan.Steps[plan.Steps.Count - 1].Recipe.Id)));
        Desc = Describe(best.L, plan, spec, gd);
        return best.L;
    }

    /// Consumer packing of the grouped row mode (shared with Kit): consumer slots (row, demand) sorted by demand, packed
    /// first-fit into groups of at most 3 rows (one proven SPLIT cell, 1 in -> 3 out) and at most one belt. An item with
    /// 4+ consumer rows therefore gets parallel producer rows instead of a splitter cascade (cascades are unproven).
    /// Consumers whose item is in ownSupply (--own) always get a group of their own (own producer row).
    public static List<List<KeyValuePair<T, double>>> PackConsumers<T>(List<KeyValuePair<T, double>> slots, double beltRate, HashSet<int> ownSupply, Func<T, int> itemOf)
    {
        var groups = new List<List<KeyValuePair<T, double>>>();
        var own = new HashSet<List<KeyValuePair<T, double>>>();
        foreach (var sl in slots.OrderByDescending(x => x.Value))
        {
            bool mine = ownSupply != null && ownSupply.Contains(itemOf(sl.Key));
            var g = mine ? null : groups.FirstOrDefault(x => !own.Contains(x) && x.Count < 3 && x.Sum(y => y.Value) + sl.Value <= beltRate + 1e-9);
            if (g == null) { g = new List<KeyValuePair<T, double>>(); groups.Add(g); if (mine) own.Add(g); }
            g.Add(sl);
        }
        return groups;
    }

    /// Rigid quarter-turn rotation of a whole building set around (0,0). Splitters stay at yaw 0 (the only proven
    /// splitter yaw, refs\splitters.txt / bridges-splitters-user.txt; the cell is 4-fold symmetric), so the splitter
    /// side slots on the touching belts (incoming OutputToSlot, outgoing InputFromSlot = side N0 E1 S2 W3) turn with
    /// the layout (bug fix 2026-10-31: --in-edge W/N kept the canonical slots -> "rule 7: slot 2 but side W").
    public static void RotateLayout(IList<Bld> blds, int quarters)
    {
        int q = ((quarters % 4) + 4) % 4;
        if (q == 0) return;
        foreach (var b in blds) b.RotateCW(q, 0, 0);
        foreach (var b in blds)
        {
            if (b.Kind == Kind.Splitter || b.Kind == Kind.Storage) { b.Yaw = Dirs.Norm(b.Yaw - 90.0 * q); b.Yaw2 = Dirs.Norm(b.Yaw2 - 90.0 * q); continue; }
            // depot Mk.I stays at yaw 0 (multiLevelAllowRotate false): its 12 slots (3 per face, clockwise) turn instead
            if (b.Kind == Kind.Sorter && b.Output != null && b.Output.Kind == Kind.Storage && b.OutputToSlot >= 0) { b.OutputToSlot = (b.OutputToSlot + 3 * q) % 12; continue; }
            if (b.Kind == Kind.Sorter && b.Input != null && b.Input.Kind == Kind.Storage && b.InputFromSlot >= 0) { b.InputFromSlot = (b.InputFromSlot + 3 * q) % 12; continue; }
            if (b.Kind != Kind.Belt) continue;
            if (b.Output != null && b.Output.Kind == Kind.Splitter && b.OutputToSlot >= 0 && b.OutputToSlot <= 3) b.OutputToSlot = (b.OutputToSlot + q) % 4;
            if (b.Input != null && b.Input.Kind == Kind.Splitter && b.InputFromSlot >= 0 && b.InputFromSlot <= 3) b.InputFromSlot = (b.InputFromSlot + q) % 4;
        }
    }

    /// Fraction of full speed the machines of a row run at: row output rate / (count * full per-machine output).
    public static double RowLoad(GameData gd, Step s, int count, double rowOutRate)
    {
        if (s == null || s.Recipe == null || count <= 0 || s.Recipe.TimeSec <= 0) return 0;
        double speed = s.Speed > 0 ? s.Speed : (gd != null ? gd.Speed(s.MachineItem) : 1);
        double full = s.Recipe.OutCount(s.Item) * 60.0 * speed / s.Recipe.TimeSec;
        return full > 0 ? rowOutRate / (count * full) : 0;
    }

    /// Raised bridge nodes, separate bridges (runs of raised nodes) and corner belts (90-degree turns) of a layout.
    public static int[] RouteCounts(Layout l)
    {
        int raised = 0, bridges = 0, corners = 0;
        var raisedPred = new HashSet<Bld>();
        foreach (var p in l.All) if (p.Kind == Kind.Belt && p.Output != null && p.IsRaised && p.Cell != SplitterGeo.BeltCell) raisedPred.Add(p.Output);
        foreach (var b in l.All)
        {
            if (b.Kind != Kind.Belt || b.Cell == SplitterGeo.BeltCell) continue;
            if (b.IsRaised)
            {
                raised++;
                if (!raisedPred.Contains(b)) bridges++;
            }
            double y = Dirs.Norm(b.Yaw) % 90.0; if (y > 1 && y < 89) corners++;   // ground and raised corners
        }
        return new[] { raised, bridges, corners };
    }

    public static string RouteStats(Layout l)
    {
        int[] c = RouteCounts(l);
        return c[1] + " bridge(s) / " + c[0] + " raised node(s), " + c[2] + " turns (corner belts)";
    }

    /// Same layout with the towers re-placed by the systematic TeslaGrid (lattice); null when it leaves problems.
    Layout Retower(Layout l)
    {
        var n = new Layout(gd);
        foreach (var b in l.All) if (b.Kind != Kind.Tesla) n.Add(b);
        n.Ports.AddRange(l.Ports);
        n.Errors.AddRange(l.Errors);
        int before = n.Errors.Count;
        List<Bld> towers = Modules.TeslaGrid(n, gd, true);
        if (n.Errors.Count != before || towers == null || towers.Count == 0) return null;
        foreach (var t in towers) n.Add(t);
        return n;
    }

    /// Header description: title "<Product> <rate>/min", then one "- " line per fact (rates per minute):
    /// "- in S: Iron Ore 360 (2 belts)", "- out N: Magnetic Coil 360", "- 4 Assembling Machine Mk.I: Magnetic Coil",
    /// "- belts Mk.II, sorters Mk.II", "- buffer depot at the end". Lines are separated by '\n'.
    public static string Describe(Layout layout, ProductionPlan plan, BlockSpec spec, GameData gd)
    {
        var c = CultureInfo.InvariantCulture;
        Func<int, string> n = id => gd != null ? gd.ItemName(id) : "item" + id;
        Func<double, string> f = v => v.ToString("0.##", c);
        int target = plan != null && plan.TargetItem != 0 ? plan.TargetItem : spec.Item;
        double rate = plan != null && plan.TargetRate > 0 ? plan.TargetRate : spec.Rate;
        var facts = new List<string>();
        foreach (PortKind kind in new[] { PortKind.In, PortKind.Out })
        {
            Dir edge = kind == PortKind.In ? spec.InEdge : spec.OutEdge;
            foreach (var g in layout.Ports.Where(p => p.Kind == kind).GroupBy(p => p.Item))
            {
                var list = g.ToList();
                facts.Add((kind == PortKind.In ? "in " : "out ") + EdgeName(edge) + ": " + n(g.Key) + " " + f(list.Sum(p => p.Rate))
                    + (list.Count > 1 ? " (" + list.Count + " belts)" : ""));
            }
        }
        if (plan != null)
            foreach (var s in plan.Steps) facts.Add(s.Machines + " " + n(s.MachineItem) + ": " + n(s.Item));
        facts.Add("belts " + Lanes.TiersUsed(gd, layout, Lanes.BeltTiers, spec.Tiers.Belt) + ", sorters " + Lanes.TiersUsed(gd, layout, Lanes.SorterTiers, spec.Tiers.Sorter));   // lanes hook
        if (layout.All.Any(b => b.Kind == Kind.Storage)) facts.Add("buffer depot at the end");
        return BpText.Lines(n(target) + " " + f(rate) + "/min", facts);
    }

    static string EdgeName(Dir d) { return d == Dir.N ? "N" : d == Dir.E ? "E" : d == Dir.S ? "S" : "W"; }

    /// ASCII map of a layout (debug, MakeBlock --map): S smelter, A assembler, M other machine, ^ v < > belts, + corner,
    /// B bridge (raised node over a belt), X splitter, T tower, i input icon belt. North is up.
    public static string Map(Layout l)
    {
        Rect b = l.Bounds();
        if (b.IsEmpty) return "(empty)";
        int w = b.Width, h = b.Height;
        var g = new char[h, w];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) g[y, x] = '.';
        Action<int, int, char> put = (x, y, c) => { if (x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1) g[y - b.Y0, x - b.X0] = c; };
        foreach (var o in l.All)
        {
            if (o.Kind == Kind.Machine || o.Kind == Kind.Storage)
            {
                Rect r = l.Footprints.TileRect(o);
                char c = o.Kind == Kind.Storage ? 'D' : o.ItemId == Ids.ArcSmelter || o.ItemId == Ids.PlaneSmelter ? 'S' : o.ItemId >= Ids.AsmMk1 && o.ItemId <= Ids.AsmMk3 ? 'A' : 'M';
                for (int x = r.X0; x <= r.X1; x++) for (int y = r.Y0; y <= r.Y1; y++) put(x, y, c);
            }
        }
        foreach (var o in l.All)
        {
            if (o.Kind == Kind.Splitter) put(o.TileX, o.TileY, 'X');
            else if (o.Kind == Kind.Tesla) put(o.TileX, o.TileY, 'T');
            else if (o.Kind == Kind.Belt && !Footprints.IsSplitterTouch(o) && !o.IsRaised)
            {
                double yr = Dirs.Norm(o.Yaw) % 90.0;
                char c = yr > 1 && yr < 89 ? '+' : "^>v<"[(int)Dirs.FromYaw(o.Yaw)];
                if (o.Parameters != null && o.Parameters.Length == 2 && o.Parameters[0] != 0) c = 'i';
                put(o.TileX, o.TileY, c);
            }
        }
        foreach (var o in l.All) if (o.IsRaised) put(o.TileX, o.TileY, 'B');
        var sb = new StringBuilder();
        for (int y = h - 1; y >= 0; y--)
        {
            sb.Append((y + b.Y0).ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append(' ');
            for (int x = 0; x < w; x++) sb.Append(g[y, x]);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // =====================================================================================================
    // One placement attempt with fixed gaps.
    class RowInfo
    {
        public Step Step; public int StepIndex, Part, Count; public double Share;
        public int[] InItems; public double[] InRates; public double OutRate;
        public string Name;
        public Fragment Frag;
        public int[] Dedicated;                // split mode: {consumer step index, consumer part}
        /// grouped mode: the consumer rows this producer row feeds and the rate each one takes (1 = straight/stacked,
        /// 2-3 = one splitter, cell SPLIT); null in the older modes
        public List<KeyValuePair<RowInfo, double>> Feeds;
        /// PACK HOOK (rules 8/12): consumer rows fed from the END of a fed row's input lane: (consumer, rate, donor row)
        public List<Tuple<RowInfo, double, RowInfo>> TailFeeds;
    }

    class LineInfo
    {
        public readonly List<RowInfo> Rows = new List<RowInfo>();
        public Rect Box = Rect.Empty;          // line-local bounds (rows already translated in line-local x)
        public int Order;
    }

    class Edge
    {
        public RowInfo From, To; public int Item; public double Rate;
        public Port Source;                    // row out port, or a splitter out port
        public Port Target;                    // consumer in port
        public List<Bld> Run = new List<Bld>(); // connector belts (primary route)
        public bool Merge;                     // joins an earlier edge's route by a side merge
        public bool Done;                      // routed early (stacked link)
        public RowInfo TailOf;                 // PACK HOOK: fed from the end of this row's input lane (serial pickup)
    }

    class Goal { public Dir? NextDir; public Dir? Entry; public Bld Target; public bool SideJoin; public bool NoBridgeLanding; }

    class Attempt
    {
        readonly GameData gd; readonly BlockSpec spec; readonly ProductionPlan plan;
        readonly int rowGap, lineGap;
        readonly double beltRate;
        readonly List<string> errors = new List<string>();
        public readonly List<string> PowerProblems = new List<string>();
        public readonly List<string> RowNotes = new List<string>();   // MachineRow notes (unproven cells, extra sorters)
        public bool Fatal;
        public readonly HashSet<string> Failed = new HashSet<string>();   // edges whose primary route failed
        readonly HashSet<string> priority;                                // routed first (failed in an earlier try)
        Layout work;
        readonly List<RowInfo> rows = new List<RowInfo>();
        readonly List<Edge> edges = new List<Edge>();
        readonly List<LineInfo> lines = new List<LineInfo>();
        readonly HashSet<long> blocked = new HashSet<long>();
        readonly Dictionary<long, int> reserved = new Dictionary<long, int>();
        readonly HashSet<Bld> mergeTargetsUsed = new HashSet<Bld>();
        readonly List<Port> blockPorts = new List<Port>();
        int nextConn = 1, chainNo = 0;
        Dir outEdgeC;                           // product edge in the canonical frame (inputs at S)
        int quarters;                           // canonical -> real rotation (clockwise quarter turns)

        readonly int strategy;
        readonly bool splitMode;
        readonly bool grouped;        // modes 0/1: one consolidated producer row per consumer group (MakeRowsGrouped)
        readonly int capMachines;     // mode 1: single-consumer producer rows split into parts of at most this many machines
        readonly bool mirrorBase, reverseFeeds;   // mode bits 8/16
        readonly bool packTails;      // PACK HOOK (engine\Packing.cs, rules 8/12): mode bit 32 clear = 2-way splitters + tail pickup
        readonly bool noStackMulti;   // mode bit 4: rows with 3-4 inputs start their own line (entries reachable from below)
        readonly HashSet<long> bridged = new HashSet<long>();   // tiles crossed by a bridge and its feet (no two-level crossing, no side-join)
        readonly bool raise;          // raw-only rows feeding another line move up toward their consumer
        readonly bool heavy;          // stack a consumer on its heaviest feed (else on the lowest producer line)
        readonly int[] gapsAfter;     // optional per line gap (index = line position), else lineGap
        public int LineCount { get { return lines.Count; } }
        public int Bridges;           // raised bridge nodes laid by the router

        public Attempt(GameData gd, BlockSpec spec, ProductionPlan plan, int rowGap, int lineGap, int strategy, int mode, HashSet<string> priority,
            bool raise, bool heavy, int[] gapsAfter)
        {
            this.gd = gd; this.spec = spec; this.plan = plan; this.rowGap = rowGap; this.lineGap = lineGap; this.strategy = strategy;
            this.splitMode = (mode & 3) == 3; this.grouped = (mode & 3) <= 1; this.capMachines = (mode & 3) == 1 ? CapMachines : 0; this.noStackMulti = (mode & 4) != 0; this.mirrorBase = (mode & 8) != 0; this.reverseFeeds = (mode & 16) != 0;
            this.packTails = Packing.Enabled && (mode & 32) == 0;   // PACK HOOK
            this.raise = raise; this.heavy = heavy; this.gapsAfter = gapsAfter;
            this.priority = priority ?? new HashSet<string>();
            double br = gd != null ? gd.BeltRate(spec.Tiers.Belt) : 0;
            beltRate = br > 0 ? br : double.MaxValue;
            quarters = (((int)spec.InEdge - (int)Dir.S) % 4 + 4) % 4;
            outEdgeC = Dirs.RotateCW(spec.OutEdge, -quarters);
        }

        string N(int item) { return gd != null ? gd.ItemName(item) : "item" + item; }
        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }

        public Layout Run()
        {
            work = new Layout(gd);
            if (plan == null || plan.Steps.Count == 0) { errors.Add("composer: empty production plan"); Fatal = true; return Finish(); }
            if (outEdgeC == Dir.S) errors.Add("composer: product edge " + spec.OutEdge + " equals the input edge; routing the product back to the input edge is not supported");

            MakeRows();
            if (Fatal) return Finish();
            MakeEdges();
            if (Fatal) return Finish();
            Place();
            if (Fatal) return Finish();
            foreach (var r in rows) if (r.Frag != null) foreach (var b in r.Frag.Blds) AddBld(work, b);
            BlockTiles();
            Route();
            return Finish();
        }

        // ---------------------------------------------------------------- rows
        int LanesNeeded(Step s, double beltRate)
        {
            double outRate = s.Outputs.Count > 0 ? s.Outputs[0].Rate : s.MaxOutputRate;
            double worst = Math.Max(outRate, s.Inputs.Count > 0 ? s.Inputs.Max(x => x.Rate) : 0);
            int byRate = Math.Max(1, (int)Math.Ceiling(worst / beltRate - 1e-9));
            // lanes hook (PLAN.md decision 9): machines per lane = floor(lane rate / one machine's full-speed demand)
            return Math.Max(byRate, Lanes.LanesFor(gd, s, s.Machines, beltRate));
        }

        /// Machines per parallel row of a step (K copies rule, at least what the belt rate needs).
        int[] PartCounts(Step s, int K)
        {
            int need = LanesNeeded(s, beltRate);
            int k = Math.Max(1, Math.Min(Math.Max(need, Math.Min(K, s.Machines)), Math.Max(1, s.Machines)));
            var c = new int[k];
            for (int p = 0; p < k; p++) c[p] = s.Machines / k + (p < s.Machines % k ? 1 : 0);
            return c;
        }

        /// Splits total machines proportional to the weights (largest remainder, at least 1 each).
        static int[] Apportion(int total, double[] w)
        {
            int n = w.Length; var res = new int[n];
            double sum = w.Sum(); if (sum <= 0) sum = 1;
            int left = total - n;
            var exact = w.Select(x => Math.Max(0, x / sum * total - 1)).ToArray();
            for (int i = 0; i < n; i++) { res[i] = 1 + (int)Math.Floor(exact[i] + 1e-9); left -= res[i] - 1; }
            foreach (int i in Enumerable.Range(0, n).OrderByDescending(i => exact[i] - Math.Floor(exact[i] + 1e-9)))
            { if (left <= 0) break; res[i]++; left--; }
            return res;
        }

        /// Grouped mode (user's distribution strategy, review 2026-10-31): ONE consolidated producer row per intermediate
        /// (e.g. all 9 copper smelters on one output lane instead of a row per consumer), its output lane split to its
        /// consumer rows with one splitter (proven cell SPLIT: 1 in -> up to 3 out). Consumer rows are sized first
        /// (reverse topological order). The consumers of an item are packed into groups of at most 3 rows and at most one
        /// belt (Mk.II 720/min); every group gets its own producer row (2 rows only above the belt rate or above 3
        /// consumers, so no splitter cascade is ever needed). capMachines > 0: a producer row with a single consumer is
        /// split into parts of at most that many machines whose lanes join (side-join) into the consumer lane.
        void MakeRowsGrouped()
        {
            var rowsOf = new Dictionary<int, List<RowInfo>>();
            for (int si = plan.Steps.Count - 1; si >= 0; si--)
            {
                Step s = plan.Steps[si];
                var made = new List<RowInfo>();
                rowsOf[si] = made;
                if (s.Recipe == null) { errors.Add("composer: step " + N(s.Item) + " has no recipe"); Fatal = true; continue; }
                if (s.Outputs.Count > 1)
                    errors.Add("composer: " + s.Recipe.Name + " has byproducts (" + string.Join(", ", s.Outputs.Skip(1).Select(o => N(o.Item)).ToArray())
                        + "); byproduct lanes/sinks are not supported yet");
                var ins = s.Inputs.Select((x, i) => new { x, i }).OrderByDescending(a => a.x.Rate).ThenBy(a => a.i).Select(a => a.x).ToList();
                if (ins.Count == 0 || ins.Count > 4) { errors.Add("composer: " + s.Recipe.Name + " has " + ins.Count + " inputs; MachineRow supports 1-4"); Fatal = true; continue; }
                double outRate = s.Outputs.Count > 0 ? s.Outputs[0].Rate : s.MaxOutputRate;
                double totalOut = Math.Max(1e-9, outRate);

                // consumer slots: every consumer row that eats this item, with its demand
                var slots = new List<KeyValuePair<RowInfo, double>>();
                foreach (int ci in s.ConsumerSteps.Distinct())
                {
                    List<RowInfo> cr;
                    if (!rowsOf.TryGetValue(ci, out cr)) continue;
                    foreach (var c in cr)
                    {
                        int idx = Array.IndexOf(c.InItems, s.Item);
                        if (idx >= 0) slots.Add(new KeyValuePair<RowInfo, double>(c, c.InRates[idx]));
                    }
                }
                // lanes hook (PLAN.md decision 9): a group may only grow until one of its row's lanes is full at full speed
                var groups = PackConsumers(slots, Lanes.GroupCap(gd, s, beltRate), spec.OwnSupply, x => x.Step.Item);
                // PACK HOOK (engine\Packing.cs, rules 8/12): 2-way splitters + tail pickup instead of 3-way / own rows
                List<PackGroup<RowInfo>> pg = packTails && slots.Count > 0 ? Packing.Pack(slots, Lanes.GroupCap(gd, s, beltRate), beltRate, spec.OwnSupply, x => x.Step.Item) : null;
                if (pg != null) groups = pg.Select(x => x.All()).ToList();
                if (slots.Count == 0) groups.Add(null);   // block output: rows by lane rate only
                // PACK HOOK (engine\Packing.cs, rule 14): the groups together build Step.Machines (margin / user counts)
                int[] packCounts = Packing.GroupCounts(s.Machines, s.MachinesExact, groups.Select(x => x == null ? 1.0 : x.Sum(y => y.Value) / totalOut).ToArray());
                int partNo = 0;
                foreach (var g in groups)
                {
                    double gRate = g == null ? outRate : g.Sum(x => x.Value);
                    double share = g == null ? 1.0 : gRate / totalOut;
                    int count = Packing.Enabled ? packCounts[groups.IndexOf(g)] : Math.Max(1, (int)Math.Ceiling(s.MachinesExact * share - 1e-9));   // PACK HOOK
                    if (g == null) count = s.Machines;
                    // parts: lane rate (block output rows) or the height cap (single-consumer rows)
                    int parts = 1;
                    if (g == null) parts = Math.Max(1, Math.Min(count, LanesNeeded(s, beltRate)));
                    // lanes hook (decision 9): rows on block input lanes are split by lane capacity only, not by the height cap
                    bool laneRow = ins.Any(x => s.IsExternal(plan, x.Item));
                    if ((g == null || g.Count == 1) && capMachines > 0 && !laneRow) parts = Math.Max(parts, (int)Math.Ceiling(count / (double)capMachines - 1e-9));
                    if (g != null && g.Count == 1) parts = Math.Max(parts, Lanes.LanesFor(gd, s, count, beltRate));   // lanes hook (decision 9)
                    parts = Math.Min(parts, count);
                    for (int p = 0; p < parts; p++)
                    {
                        int cnt = count / parts + (p < count % parts ? 1 : 0);
                        double ps = cnt / (double)count;
                        var r = new RowInfo
                        {
                            Step = s, StepIndex = si, Part = partNo++, Count = cnt, Share = share * ps,
                            InItems = ins.Select(x => x.Item).ToArray(), InRates = ins.Select(x => x.Rate * share * ps).ToArray(),
                            OutRate = gRate * ps,
                            Feeds = g == null ? null : pg != null ? Packing.MainFeeds(pg[groups.IndexOf(g)], ps) : g.Select(x => new KeyValuePair<RowInfo, double>(x.Key, x.Value * ps)).ToList(),   // PACK HOOK
                            TailFeeds = g == null || pg == null ? null : pg[groups.IndexOf(g)].Tails.Select(t => Tuple.Create(t.Key.Key, t.Key.Value * ps, t.Value)).ToList()   // PACK HOOK
                        };
                        r.Name = N(s.Item) + (groups.Count > 1 || parts > 1 ? " #" + partNo : "");
                        made.Add(r);
                        for (int i = 0; i < r.InRates.Length; i++)
                            if (r.InRates[i] > beltRate + 1e-9) errors.Add("composer: row " + r.Name + " lane " + N(r.InItems[i]) + " " + F(r.InRates[i]) + "/min exceeds the belt rate");
                        if (r.OutRate > beltRate + 1e-9) errors.Add("composer: row " + r.Name + " output " + F(r.OutRate) + "/min exceeds the belt rate");
                    }
                }
            }
            for (int si = 0; si < plan.Steps.Count; si++) if (rowsOf.ContainsKey(si)) rows.AddRange(rowsOf[si]);
        }

        void MakeRows()
        {
            if (grouped) { MakeRowsGrouped(); return; }
            // parallel copies: when one step needs K rows (belt rate), every step is split into up to K rows, so
            // producer and consumer rows pair 1:1 (K copies of the same module) instead of crossing lines
            int K = plan.Steps.Max(s => LanesNeeded(s, beltRate));
            var parts = plan.Steps.Select(s => PartCounts(s, K)).ToList();
            for (int si = 0; si < plan.Steps.Count; si++)
            {
                Step s = plan.Steps[si];
                // split mode: a producer with several consumer rows becomes one row per consumer row (sized by demand)
                if (splitMode && s.Recipe != null && s.Outputs.Count == 1 && s.Inputs.Count >= 1 && s.Inputs.Count <= 4)
                {
                    var dem = new List<double>(); var ded = new List<int[]>();
                    foreach (int ci in s.ConsumerSteps.Distinct())
                    {
                        Step c = plan.Steps[ci];
                        double tot = c.Inputs.Where(x => x.Item == s.Item).Sum(x => x.Rate);
                        for (int p = 0; p < parts[ci].Length; p++) { dem.Add(tot * parts[ci][p] / Math.Max(1, c.Machines)); ded.Add(new[] { ci, p }); }
                    }
                    if (dem.Count > 1 && s.Machines >= dem.Count && dem.All(d => d <= beltRate + 1e-9))
                    {
                        var counts = Apportion(s.Machines, dem.ToArray());
                        var insS = s.Inputs.Select((x, i) => new { x, i }).OrderByDescending(a => a.x.Rate).ThenBy(a => a.i).Select(a => a.x).ToList();
                        double sumD = dem.Sum();
                        for (int j = 0; j < dem.Count; j++)
                        {
                            double share = dem[j] / sumD;
                            // every dedicated row must cover its consumer's demand (ceil per row; may exceed the plan)
                            int cnt = Math.Max(counts[j], (int)Math.Ceiling(s.MachinesExact * share - 1e-9));
                            rows.Add(new RowInfo
                            {
                                Step = s, StepIndex = si, Part = j, Count = cnt, Share = share, Dedicated = ded[j],
                                InItems = insS.Select(x => x.Item).ToArray(), InRates = insS.Select(x => x.Rate * share).ToArray(),
                                OutRate = dem[j],
                                Name = N(s.Item) + " for " + N(plan.Steps[ded[j][0]].Item) + (parts[ded[j][0]].Length > 1 ? " #" + (ded[j][1] + 1) : "")
                            });
                        }
                        continue;
                    }
                }
                if (s.Recipe == null) { errors.Add("composer: step " + N(s.Item) + " has no recipe"); Fatal = true; continue; }
                if (s.Outputs.Count > 1)
                    errors.Add("composer: " + s.Recipe.Name + " has byproducts (" + string.Join(", ", s.Outputs.Skip(1).Select(o => N(o.Item)).ToArray())
                        + "); byproduct lanes/sinks are not supported yet");
                var ins = s.Inputs.Select((x, i) => new { x, i }).OrderByDescending(a => a.x.Rate).ThenBy(a => a.i).Select(a => a.x).ToList();
                if (ins.Count == 0 || ins.Count > 4) { errors.Add("composer: " + s.Recipe.Name + " has " + ins.Count + " inputs; MachineRow supports 1-4"); Fatal = true; continue; }
                double outRate = s.Outputs.Count > 0 ? s.Outputs[0].Rate : s.MaxOutputRate;
                int need = LanesNeeded(s, beltRate);
                int k = parts[si].Length;
                if (need > k) errors.Add("composer: " + s.Recipe.Name + " needs " + need + " lanes but has only " + s.Machines + " machines");
                // parallel copies paired 1:1 with the target's rows: every copy gets the target's demand share and enough
                // machines for it (ceil per copy; may place a machine more than the plan, the validator warns)
                int[] tparts = parts[parts.Count - 1];
                double[] w = null;
                if (k > 1 && tparts.Length == k && tparts.Sum() > 0) w = tparts.Select(c => c / (double)tparts.Sum()).ToArray();
                for (int p = 0; p < k; p++)
                {
                    int count = parts[si][p];
                    double share = s.Machines > 0 ? count / (double)s.Machines : 1.0;
                    if (w != null)
                    {
                        share = w[p];
                        count = Math.Max(1, (int)Math.Ceiling(s.MachinesExact * share - 1e-9));
                    }
                    var r = new RowInfo
                    {
                        Step = s, StepIndex = si, Part = p, Count = count, Share = share,
                        InItems = ins.Select(x => x.Item).ToArray(), InRates = ins.Select(x => x.Rate * share).ToArray(),
                        OutRate = outRate * share,
                        Name = N(s.Item) + (k > 1 ? " #" + (p + 1) : "")
                    };
                    rows.Add(r);
                    for (int i = 0; i < r.InRates.Length; i++)
                        if (r.InRates[i] > beltRate + 1e-9) errors.Add("composer: row " + r.Name + " lane " + N(r.InItems[i]) + " " + F(r.InRates[i]) + "/min exceeds the belt rate");
                }
            }
        }

        List<RowInfo> RowsOf(int stepIndex) { return rows.Where(r => r.StepIndex == stepIndex).ToList(); }

        void MakeEdges()
        {
            for (int si = 0; si < plan.Steps.Count; si++)
            {
                Step s = plan.Steps[si];
                var prods = RowsOf(si);
                if (prods.Count == 0) continue;
                var cons = new List<RowInfo>();
                foreach (int ci in s.ConsumerSteps.Distinct()) cons.AddRange(RowsOf(ci));
                if (cons.Count == 0) continue;   // block output
                if (s.Item == plan.TargetItem)
                    errors.Add("composer: target item " + N(s.Item) + " is also consumed inside the block; not supported");
                int k = prods.Count, m = cons.Count;
                if (prods.All(p => p.Feeds != null))
                {
                    foreach (var p in prods)
                        foreach (var kv in p.Feeds)
                            edges.Add(new Edge { From = p, To = kv.Key, Item = s.Item, Rate = kv.Value });
                    // PACK HOOK (rules 8/12): one tail edge per tail consumer (first producer part that has it)
                    foreach (var p in prods) if (p.TailFeeds != null) foreach (var t in p.TailFeeds) if (!edges.Any(x => x.To == t.Item1 && x.Item == s.Item)) edges.Add(new Edge { From = p, To = t.Item1, Item = s.Item, Rate = t.Item2 * prods.Count, TailOf = t.Item3 });
                    continue;
                }
                if (prods.All(p => p.Dedicated != null))
                {
                    foreach (var p in prods)
                    {
                        RowInfo c = cons.FirstOrDefault(x => x.StepIndex == p.Dedicated[0] && x.Part == p.Dedicated[1]);
                        if (c == null) { errors.Add("composer: no consumer row for " + p.Name); continue; }
                        edges.Add(new Edge { From = p, To = c, Item = s.Item, Rate = p.OutRate });
                    }
                    continue;
                }
                for (int j = 0; j < m; j++)
                {
                    RowInfo c = cons[j];
                    int idx = Array.IndexOf(c.InItems, s.Item);
                    double need = idx >= 0 ? c.InRates[idx] : 0;
                    if (k <= m) edges.Add(new Edge { From = prods[j % k], To = c, Item = s.Item, Rate = need });
                    else
                    {
                        var feeders = Enumerable.Range(0, k).Where(i => i % m == j).ToList();
                        foreach (int i in feeders) edges.Add(new Edge { From = prods[i], To = c, Item = s.Item, Rate = need / feeders.Count });
                    }
                }
            }
            foreach (var g in edges.Where(e => e.TailOf == null).GroupBy(e => e.From))   // PACK HOOK: tails take no splitter output
                if (g.Count() > 3) { errors.Add("composer: " + g.Key.Name + " feeds " + g.Count() + " rows; splitters for more than 3 consumers are not supported yet"); }
        }

        // ---------------------------------------------------------------- placement
        Fragment Build(RowInfo r, bool mirror)
        {
            var rs = new RowSpec
            {
                Name = r.Name, MachineItem = r.Step.MachineItem, RecipeId = r.Step.Recipe.Id, Count = r.Count, Pitch = 0,
                InputItems = (int[])r.InItems.Clone(), InputRates = (double[])r.InRates.Clone(),
                OutputItem = r.Step.Item, OutputRate = r.OutRate, Flow = Dir.N, Mirror = mirror,
                BeltItem = spec.Tiers.Belt, SorterItem = spec.Tiers.Sorter, OutputLength = 0,
                LaneIcons = false,  // rule 5 icons are set by RouteRaw on block input chains only
                TailItems = r.InItems.Where(it => r.Step.IsExternal(plan, it)).ToArray(),   // raw lanes start with a tail
                Data = gd,          // per-sorter throughput check, collider pitch
                FinalQuarters = quarters, AlongEW = quarters % 2 == 1, Safe60 = spec.Safe60,
                LabStackMax = spec.LabStackMax,  // feat-labs: lab tower height cap
                // COATERS HOOK: proliferated step -> a spray coater on every input lane + the proliferator feed port
                CoatItems = Coaters.Coated(r.Step) && spec.ProliferatorItem != 0 ? (int[])r.InItems.Clone() : null,
                ProliferatorItem = spec.ProliferatorItem
            };
            Fragment f;
            try { f = Modules.MachineRow(rs); }
            catch (NotImplementedException) { throw; }
            catch (Exception e) { errors.Add("composer: MachineRow " + r.Name + ": " + e.Message); return null; }
            if (f == null) { errors.Add("composer: MachineRow " + r.Name + " returned nothing"); return null; }
            foreach (string p in rs.Problems) errors.Add("composer: " + p);
            foreach (string n in rs.Notes) RowNotes.Add(r.Name + ": " + n);
            // per-row machine load (bug fix 2026-10-31: the Validator used the step rate / all machines, the row ports
            // the row's own rate): every machine of the row runs at row rate / (count * full rate)
            double load = RowLoad(gd, r.Step, r.Count, r.OutRate);
            if (load > 1 + 1e-6) errors.Add("composer: row " + r.Name + " needs " + F(load * 100) + "% of its " + r.Count + " machines");
            foreach (var b in f.Blds) if (b.Kind == Kind.Machine) b.Load = Math.Min(1.0, load);
            foreach (int it in r.InItems) if (FindPort(f, "in:" + it) == null) { errors.Add("composer: row " + r.Name + " has no port in:" + it); return null; }
            if (FindPort(f, "out:" + r.Step.Item) == null) { errors.Add("composer: row " + r.Name + " has no port out:" + r.Step.Item); return null; }
            return f;
        }

        /// Lane order for a stacked row: the stacked item goes to in2 (side B, next to the output lane = the outer side
        /// of the line), so every routed input sits on side A facing the corridor (coil-360-v3: copper stacked west,
        /// magnets routed east). in1/in2 are both L1; the item pushed into an L2 slot must fit the L2 sorter rate.
        void Arrange(RowInfo r, int stacked)
        {
            int n = r.InItems.Length, si = Array.IndexOf(r.InItems, stacked);
            if (n < 2 || si < 0 || si == 1) return;
            var items = r.InItems.ToList(); var rates = r.InRates.ToList();
            int it = items[si]; double rt = rates[si];
            items.RemoveAt(si); rates.RemoveAt(si);
            items.Insert(1, it); rates.Insert(1, rt);
            double l2 = gd != null ? gd.SorterRate(spec.Tiers.Sorter, 2) : 0;
            for (int i = 2; i < items.Count; i++)
                if (l2 > 0 && r.Count > 0 && rates[i] / r.Count > l2 + 1e-9 && Array.IndexOf(r.InItems, items[i]) < 2) return;   // keep the default order
            r.InItems = items.ToArray(); r.InRates = rates.ToArray();
        }

        /// Any machine of a overlaps (game collider sizes, yaw-aware, gap < 0.05 tile) a machine of b.
        bool MachinesCollide(Fragment a, Fragment b)
        {
            if (gd == null) return false;
            foreach (var m in a.Blds.Where(x => x.Kind == Kind.Machine))
                foreach (var n in b.Blds.Where(x => x.Kind == Kind.Machine))
                {
                    if (gd.HasCollider(m.ItemId) && gd.HasCollider(n.ItemId))
                    {
                        // exact boxes (colliders.json); rows are stacked along the canonical y = final x when turned
                        if (gd.ColliderGap(m, n, 1.0) < 0.05) return true;
                        if (spec.Safe60 && quarters % 2 == 1)
                        {
                            Bld mt = new Bld { ItemId = m.ItemId, X = m.Y, Y = m.X, Yaw = m.Yaw + 90 }, nt = new Bld { ItemId = n.ItemId, X = n.Y, Y = n.X, Yaw = n.Yaw + 90 };
                            if (gd.ColliderGap(mt, nt, GameData.EwFactorMin) < 0.05) return true;
                        }
                        continue;
                    }
                    double[] sm = Size(m), sn = Size(n);
                    if (sm == null || sn == null) continue;
                    double gx = Math.Abs(m.X - n.X) - (sm[0] + sn[0]) / 2, gy = Math.Abs(m.Y - n.Y) - (sm[1] + sn[1]) / 2;
                    if (Math.Max(gx, gy) < 0.05) return true;
                }
            return false;
        }

        double[] Size(Bld m)
        {
            MachineDef d = gd.Machine(m.ItemId);
            if (d == null || d.FootprintTiles == null) return null;
            int q = (int)Math.Round(Dirs.Norm(m.Yaw) / 90.0) % 4;
            return q % 2 == 0 ? new[] { d.FootprintTiles[0], d.FootprintTiles[1] } : new[] { d.FootprintTiles[1], d.FootprintTiles[0] };
        }

        static Port FindPort(Fragment f, string name) { foreach (var p in f.Ports) if (p.Name == name) return p; return null; }
        Port InPort(RowInfo r, int item) { return FindPort(r.Frag, "in:" + item); }
        Port OutPort(RowInfo r) { return FindPort(r.Frag, "out:" + r.Step.Item); }

        void Place()
        {
            var fp = work.Footprints;
            var lineOf = new Dictionary<RowInfo, LineInfo>();
            foreach (var r in rows)
            {
                // stack on a producer line when that producer is the line top and feeds only this row
                RowInfo bestP = null; LineInfo bestL = null;
                bool rowHasRaw = r.InItems.Any(it => r.Step.IsExternal(plan, it)) || (noStackMulti && r.InItems.Length >= 3);   // raw lanes must start on the input edge
                if (Coaters.Coated(r.Step) && spec.ProliferatorItem != 0) rowHasRaw = true;   // COATERS HOOK: never stacked (feed port below the row)
                foreach (var e in edges.Where(e => e.To == r && !rowHasRaw && e.TailOf == null))   // PACK HOOK: a tail row is never stacked on the producer
                {
                    RowInfo p = e.From;
                    if (p.Frag == null || !lineOf.ContainsKey(p)) continue;
                    LineInfo l = lineOf[p];
                    if (l.Rows[l.Rows.Count - 1] != p) continue;
                    if (edges.Count(x => x.From == p && x.TailOf == null) != 1) continue;   // PACK HOOK: tails ride the consumer lane
                    // heavy: the heaviest feed is stacked (its lane continues straight, no route), ties the lower line;
                    // else the lowest producer line (shortest column), ties the heavier feed
                    double br = bestL == null ? -1 : edges.First(x => x.From == bestP && x.To == r).Rate;
                    bool take = bestL == null || (heavy
                        ? e.Rate > br + 1e-9 || (Math.Abs(e.Rate - br) <= 1e-9 && l.Box.Y1 < bestL.Box.Y1)
                        : l.Box.Y1 < bestL.Box.Y1 || (l.Box.Y1 == bestL.Box.Y1 && e.Rate > br + 1e-9));
                    if (take) { bestP = p; bestL = l; }
                }
                if (bestL != null)
                {
                    Port src = OutPort(bestP);
                    Arrange(r, bestP.Step.Item);
                    Fragment pick = null; int pickDx = 0, pickScore = int.MaxValue;
                    foreach (bool mirror in new[] { false, true })
                    {
                        Fragment f = Build(r, mirror);
                        if (f == null) continue;
                        Port ip = FindPort(f, "in:" + bestP.Step.Item);
                        int dx = src.X - ip.X;
                        Rect b = f.Bounds(fp);
                        int grow = Math.Max(0, bestL.Box.X0 - (b.X0 + dx)) + Math.Max(0, b.X1 + dx - bestL.Box.X1);
                        int score = Math.Abs(dx) * 4 + grow;
                        if (score < pickScore) { pick = f; pickDx = dx; pickScore = score; }
                    }
                    if (pick == null) { Fatal = true; continue; }
                    Rect pb = pick.Bounds(fp);
                    int gapHere = r.InItems.Length == 1 ? 0 : rowGap;   // nothing else enters a single-input row from below
                    pick.Translate(pickDx, bestL.Box.Y1 + gapHere + 1 - pb.Y0);
                    // flush stacking: widen the gap only as far as the real machine colliders need (assembler on
                    // assembler needs a free row, smelter -> assembler does not)
                    for (int guard = 0; guard < 4 && MachinesCollide(pick, bestP.Frag); guard++) pick.Translate(0, 1);
                    r.Frag = pick;
                    bestL.Rows.Add(r); bestL.Box = bestL.Box.Union(pick.Bounds(fp));
                    lineOf[r] = bestL;
                }
                else
                {
                    Fragment f = Build(r, mirrorBase);
                    if (f == null) { Fatal = true; continue; }
                    Rect b = f.Bounds(fp);
                    // a row fed only by other rows leaves free rows below it, so its lanes can be entered from the side
                    bool hasRaw = r.InItems.Any(it => r.Step.IsExternal(plan, it));
                    f.Translate(-b.X0, -b.Y0 + (hasRaw ? 0 : rowGap + 1));
                    r.Frag = f;
                    var l = new LineInfo(); l.Rows.Add(r); l.Box = f.Bounds(fp); l.Order = lines.Count;
                    lines.Add(l); lineOf[r] = l;
                }
            }
            // line order: product lines first (west), then depth-first through their producers, so a line sits next to
            // the line it feeds whenever the plan allows it
            var ordered = new List<LineInfo>();
            Action<LineInfo> visit = null;
            // every producer exits at the top of its line, so routes into one line nest only when the nearest producer
            // line feeds the lowest entry: visit producer lines by ascending entry height
            visit = l =>
            {
                if (ordered.Contains(l)) return;
                ordered.Add(l);
                var feeds = edges.Where(e => l.Rows.Contains(e.To) && lineOf.ContainsKey(e.TailOf ?? e.From) && lineOf[e.TailOf ?? e.From] != l)
                    .OrderBy(e => { Port p = InPort(e.To, e.Item); return (p != null ? p.Y : 0) * (reverseFeeds ? -1 : 1); }).ThenByDescending(e => e.Rate).ToList();
                foreach (var e in feeds) visit(lineOf[e.TailOf ?? e.From]);   // PACK HOOK: a tail row sits next to its donor line
            };
            foreach (var l in lines.Where(l => l.Rows.Any(r => r.Step.Item == plan.TargetItem)).OrderBy(l => l.Order).ToList()) visit(l);
            foreach (var l in lines.OrderBy(l => l.Order)) visit(l);
            ordered = GroupByRaw(ordered);
            lines.Clear(); lines.AddRange(ordered);
            int cursor = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                var l = ordered[i];
                int off = cursor - l.Box.X0;
                foreach (var r in l.Rows) r.Frag.Translate(off, 0);
                int gap = gapsAfter != null && i < gapsAfter.Length ? gapsAfter[i] : lineGap;
                cursor = l.Box.X1 + off + gap + 1;
            }
            if (raise) RaiseRawRows(ordered, lineOf);
        }

        /// User rule (review 2026-10-31): block input lanes grouped by raw material along the input edge. Lines are
        /// stably re-ordered so all lines whose bottom row eats the same raw item are neighbours; the material with the
        /// most lanes comes first (west), the others follow on one side. Lines without raw lanes stay with the line
        /// before them. Dependency order is kept inside a group.
        List<LineInfo> GroupByRaw(List<LineInfo> ordered)
        {
            Func<LineInfo, List<int>> raws = l => l.Rows.SelectMany(r => r.InItems.Where(it => r.Step.IsExternal(plan, it))).Distinct().ToList();
            var laneCount = new Dictionary<int, int>(); var firstSeen = new Dictionary<int, int>();
            for (int i = 0; i < ordered.Count; i++)
                foreach (int it in raws(ordered[i]))
                {
                    laneCount[it] = (laneCount.ContainsKey(it) ? laneCount[it] : 0) + 1;
                    if (!firstSeen.ContainsKey(it)) firstSeen[it] = i;
                }
            if (laneCount.Count < 2) return ordered;
            var rank = laneCount.Keys.OrderByDescending(it => laneCount[it]).ThenBy(it => firstSeen[it]).Select((it, i) => new { it, i }).ToDictionary(a => a.it, a => a.i);
            var key = new Dictionary<LineInfo, int>();
            int last = 0;
            foreach (var l in ordered)
            {
                var rw = raws(l);
                if (rw.Count > 0) last = rw.Min(it => rank[it]);
                key[l] = last;
            }
            return ordered.Select((l, i) => new { l, i }).OrderBy(a => key[a.l]).ThenBy(a => a.i).Select(a => a.l).ToList();
        }

        /// User rule (review 2026-10-31): a raw-eating row that is the only row of its line and feeds a row in another
        /// line moves up so its output exits just below its consumer's entry (flush with the consumer rows); its raw
        /// lanes are then fed straight up from the input edge (RouteRaw). The block never grows taller for it.
        void RaiseRawRows(List<LineInfo> ordered, Dictionary<RowInfo, LineInfo> lineOf)
        {
            var fp = work.Footprints;
            int top = int.MinValue;
            foreach (var r in rows) if (r.Frag != null) top = Math.Max(top, r.Frag.Bounds(fp).Y1);
            foreach (var l in ordered)
            {
                if (l.Rows.Count != 1) continue;
                RowInfo r = l.Rows[0];
                if (r.Frag == null || !r.InItems.All(it => r.Step.IsExternal(plan, it))) continue;
                var outs = edges.Where(e => e.From == r && e.To.Frag != null && lineOf.ContainsKey(e.To) && lineOf[e.To] != l).ToList();
                if (outs.Count == 0 || edges.Count(e => e.From == r) != outs.Count) continue;
                Port o = OutPort(r);
                int want = int.MaxValue;
                foreach (var e in outs) { Port t = InPort(e.To, e.Item); if (t != null) want = Math.Min(want, t.PrevY - 1 - o.NextY); }
                if (want == int.MaxValue) continue;
                Rect b = r.Frag.Bounds(fp);
                int dy = Math.Min(want, top - b.Y1);
                if (dy > 0) r.Frag.Translate(0, dy);
            }
        }

        // ---------------------------------------------------------------- tiles the router must not use
        void BlockTiles()
        {
            var outBelts = new HashSet<Bld>(rows.Where(r => r.Frag != null).Select(r => PortBelt(OutPort(r))).Where(b => b != null));
            foreach (var e in edges.Where(e => e.TailOf != null && e.TailOf.Frag != null)) { Port tp = Packing.LaneEnd(e.TailOf.Frag, e.Item); if (tp != null) outBelts.Add(tp.Belt); }   // PACK HOOK: tail sources
            foreach (var b in work.All)
            {
                if (b.Kind == Kind.Sorter)
                {
                    MarkSorter(b);
                    for (int i = 1; i < 20; i++)
                    {
                        double t = i / 20.0;
                        blocked.Add(K((int)Math.Round(b.X + (b.X2 - b.X) * t), (int)Math.Round(b.Y + (b.Y2 - b.Y) * t)));
                    }
                }
                else if (b.Kind == Kind.Belt)
                {
                    if (IsFractional(b)) blocked.Add(K(b.TileX, b.TileY));
                    // a chain end must not point straight into a foreign belt: keep the tile ahead of every lane end free
                    if (b.Output == null && !outBelts.Contains(b))
                    {
                        Dir d = b.FlowDir;
                        long ak = K(b.TileX + Dirs.Dx(d), b.TileY + Dirs.Dy(d));
                        if (blocked.Add(ak)) aheadOfEnd.Add(ak);
                    }
                }
            }
            foreach (int[] t in Coaters.RouterBlocked(work)) { long k = K(t[0], t[1]); blocked.Add(k); aheadOfEnd.Remove(k); }   // COATERS HOOK
        }

        Bld PortBelt(Port p)
        {
            if (p == null) return null;
            return p.Belt ?? work.BeltAt(p.X, p.Y);
        }

        static bool IsFractional(Bld b) { return Math.Abs(b.X - Math.Round(b.X)) > 0.01 || Math.Abs(b.Y - Math.Round(b.Y)) > 0.01; }

        static void AddBld(Layout l, Bld b)
        {
            // splitter-touching belts share the splitter tile: Footprints.IsSplitterTouch gives them no grid entry
            l.Add(b);
        }

        // ---------------------------------------------------------------- routing
        void Reserve(int x, int y, int owner)
        {
            long k = K(x, y);
            int o;
            if (reserved.TryGetValue(k, out o) && o != owner) return;   // first come keeps it; the router reports the conflict
            reserved[k] = owner;
        }

        bool Passable(int x, int y, int owner, Rect area)
        {
            if (!area.Contains(x, y)) return false;
            long k = K(x, y);
            if (!work.Grid.IsFree(x, y) || blocked.Contains(k)) return false;
            if (InForeignZone(k)) return false;
            int o;
            return !reserved.TryGetValue(k, out o) || o == owner;
        }

        // ---- splitter clearance (in-game test 2026-10-31): no belt of another connection within
        //      GameData.SplitterForeignBeltMin of a splitter centre (a pasted belt next to a pasted splitter collides)
        readonly Dictionary<long, int> zone = new Dictionary<long, int>();          // tile -> splitter number
        readonly Dictionary<Port, int> splitterOfPort = new Dictionary<Port, int>(); // splitter in/out ports
        readonly HashSet<int> zoneAllow = new HashSet<int>();                       // splitters the current route belongs to
        int splitterCount;
        Dictionary<Port, int> tgtConnF;

        bool InForeignZone(long k)
        {
            int z;
            return zone.TryGetValue(k, out z) && !zoneAllow.Contains(z);
        }

        static IEnumerable<int[]> ZoneOffsets()
        {
            double r2 = GameData.SplitterForeignBeltMin * GameData.SplitterForeignBeltMin;
            for (int dx = -3; dx <= 3; dx++)
                for (int dy = -3; dy <= 3; dy++)
                    if (dx * dx + dy * dy < r2 - 1e-9) yield return new[] { dx, dy };
        }

        /// Runs a route with the clearance zones of the splitters its ports belong to opened.
        T WithZones<T>(Func<T> f, params Port[] ports)
        {
            zoneAllow.Clear();
            int z;
            foreach (Port p in ports) if (p != null && splitterOfPort.TryGetValue(p, out z)) zoneAllow.Add(z);
            try { return f(); }
            finally { zoneAllow.Clear(); }
        }

        Rect Area(int below, int above)
        {
            Rect b = work.Bounds();
            const int M = 3;
            return new Rect(b.X0 - M, b.Y0 - below, b.X1 + M, b.Y1 + above);
        }

        void Route()
        {
            var rowsOk = rows.Where(r => r.Frag != null).ToList();
            var edgesOk = edges.Where(e => e.From.Frag != null && e.To.Frag != null).ToList();
            foreach (var e in edgesOk) e.Target = InPort(e.To, e.Item);

            // connection ids and reservations of the fixed port tiles
            var srcConn = new Dictionary<RowInfo, int>();
            foreach (var r in rowsOk)
            {
                int id = nextConn++; srcConn[r] = id;
                Port o = OutPort(r); Reserve(o.NextX, o.NextY, id);
            }
            var tgtConn = new Dictionary<Port, int>();
            tgtConnF = tgtConn;
            foreach (var e in edgesOk)
            {
                if (tgtConn.ContainsKey(e.Target)) continue;
                // a lane fed straight by a stacked producer shares the producer's connection id
                Port o = OutPort(e.From);
                int id = (o.NextX == e.Target.PrevX && o.NextY == e.Target.PrevY) || (o.NextX == e.Target.X && o.NextY == e.Target.Y) ? srcConn[e.From] : nextConn++;
                tgtConn[e.Target] = id; Reserve(e.Target.PrevX, e.Target.PrevY, id);
            }
            // raw lanes not on the input edge need a feed: keep their entry tile free
            int edgeY = work.Bounds().Y0;
            var rawFeeds = new List<KeyValuePair<RowInfo, Port>>();
            var rawConn = new Dictionary<Port, int>();
            foreach (var r in rowsOk)
                for (int i = 0; i < r.InItems.Length; i++)
                {
                    if (!r.Step.IsExternal(plan, r.InItems[i])) continue;
                    Port p = InPort(r, r.InItems[i]);
                    rawFeeds.Add(new KeyValuePair<RowInfo, Port>(r, p));
                    if (!(p.Y == edgeY && p.Dir == Dir.N)) { int id = nextConn++; rawConn[p] = id; Reserve(p.PrevX, p.PrevY, id); }
                }
            // COATERS HOOK: proliferator feed ports are block input lanes like the raw ones
            foreach (var r in rowsOk)
                foreach (Port p in r.Frag.Ports.Where(Coaters.IsFeedPort).ToList())
                {
                    rawFeeds.Add(new KeyValuePair<RowInfo, Port>(r, p));
                    if (!(p.Y == edgeY && p.Dir == Dir.N)) { int id = nextConn++; rawConn[p] = id; Reserve(p.PrevX, p.PrevY, id); }
                }

            // -1. stacked links first (producer lane continues straight into the consumer lane above it): nothing may cut
            //     through the gap between stacked rows
            foreach (var e in edgesOk)
            {
                Port o = OutPort(e.From);
                if (e.TailOf != null || edgesOk.Count(x => x.From == e.From && x.TailOf == null) != 1 || o.X != e.Target.X || o.Dir != e.Target.Dir || e.Target.Y <= o.Y) continue;   // PACK HOOK
                e.Source = o;
                reserved[K(o.NextX, o.NextY)] = tgtConn[e.Target];
                RoutePortToPort(e, tgtConn[e.Target]);
                e.Done = true;
            }

            // 0. product first: straight to the out edge, its exit column stays free
            foreach (var r in rowsOk.Where(r => r.Step.Item == plan.TargetItem && !edgesOk.Any(e => e.From == r)))
                RouteProduct(r, OutPort(r), srcConn[r]);

            // 1. splitters for producers with several consumer rows
            foreach (var e in edgesOk.Where(x => x.TailOf != null)) { e.Source = Packing.LaneEnd(e.TailOf.Frag, e.Item); if (e.Source == null) errors.Add("composer: row " + e.TailOf.Name + " has no lane end for " + N(e.Item)); }   // PACK HOOK: serial pickup
            foreach (var g in edgesOk.Where(x => x.TailOf == null).GroupBy(e => e.From).ToList())   // PACK HOOK: tails take no splitter output
            {
                var list = g.ToList();
                Port o = OutPort(g.Key);
                if (list.Count == 1) { if (list[0].Source == null) list[0].Source = o; continue; }
                PlaceSplitter(g.Key, o, srcConn[g.Key], list);
            }

            // 2. primary routes (one per consumer lane), shortest first
            var byTarget = edgesOk.Where(e => e.Source != null).GroupBy(e => e.Target).ToList();
            var primaries = new List<Edge>();
            foreach (var g in byTarget)
            {
                var list = g.OrderBy(e => Dist(e.Source, e.Target)).ToList();
                primaries.Add(list[0]);
                for (int i = 1; i < list.Count; i++) list[i].Merge = true;
            }
            // order strategy: 0 shortest first; 1 highest entry first (inner corridors), 2 lowest entry first
            Func<Edge, int> ord = e => strategy == 1 ? -e.Target.Y * 1000 + Dist(e.Source, e.Target)
                                     : strategy == 2 ? e.Target.Y * 1000 + Dist(e.Source, e.Target) : Dist(e.Source, e.Target);
            foreach (var e in primaries.Where(e => !e.Done).OrderBy(e => priority.Contains(Key(e)) ? 0 : 1).ThenBy(ord))
            {
                int id = tgtConn[e.Target];
                // the source tile belongs to the source connection; let this route use it
                reserved[K(e.Source.NextX, e.Source.NextY)] = id;
                RoutePortToPort(e, id);
            }

            // 3. merges: other producers side-join the primary route of the same lane
            foreach (var e in edgesOk.Where(x => x.Merge && x.Source != null))
            {
                Edge prim = primaries.FirstOrDefault(p => p.Target == e.Target);
                RouteMerge(e, prim);
            }

            // 4. raw inputs
            foreach (var kv in rawFeeds) RouteRaw(kv.Key, kv.Value, rawConn.ContainsKey(kv.Value) ? rawConn[kv.Value] : 0, edgeY);
            // lanes hook (PLAN.md decision 7): every input lane's icon tail ends on the input edge row
            Lanes.TailsToEdge(work, blockPorts, (x, y) => work.Grid.IsFree(x, y) && !blocked.Contains(K(x, y)) && !InForeignZone(K(x, y)), errors);

            foreach (var r in rowsOk.Where(r => r.Step.Item != plan.TargetItem && !edgesOk.Any(e => e.From == r)))
                errors.Add("composer: output of row " + r.Name + " (" + N(r.Step.Item) + ") has no consumer");
        }

        static string Key(Edge e) { return e.From.Name + ">" + e.To.Name + ":" + e.Item; }

        static int Dist(Port a, Port b) { return Math.Abs(a.NextX - b.PrevX) + Math.Abs(a.NextY - b.PrevY); }

        void RoutePortToPort(Edge e, int id)
        {
            Port s = e.Source, t = e.Target;
            Bld sb = PortBelt(s), tb = PortBelt(t);
            if (sb == null || tb == null) { errors.Add("composer: port without belt (" + s + " / " + t + ")"); return; }
            // directly adjacent and aligned: link without connector tiles
            if (s.NextX == t.X && s.NextY == t.Y && s.Dir == t.Dir) { Layout.Link(sb, tb, 1); return; }
            var goals = new Dictionary<long, Goal>();
            goals[K(t.PrevX, t.PrevY)] = new Goal { NextDir = t.Dir, Target = tb };
            Goal hit;
            // no bridge foot right next to a splitter belt (not proven): a fractional neighbour is no approach node
            goals[K(t.PrevX, t.PrevY)].NoBridgeLanding = IsFractional(tb);
            Goal h0 = null;
            var path = WithZones(() => Route2(new[] { new StartState(s.NextX, s.NextY, s.Dir, null) { HasPred = !IsFractional(sb) } }, goals, id, Area(0, 2), out h0), s, t);
            hit = h0;
            if (path == null)
            {
                Failed.Add(Key(e));
                errors.Add("composer: no route for " + N(e.Item) + " from " + e.From.Name + " (" + s.NextX + "," + s.NextY + ") to " + e.To.Name + " (" + t.PrevX + "," + t.PrevY + ")");
                return;
            }
            var run = Lay(path, s.Dir, t.Dir, e.Item, "link:" + e.Item + "#" + (chainNo++));
            Layout.Link(sb, run[0], 1);
            Layout.Link(run[run.Count - 1], tb, 1);
            e.Run = run;
        }

        void RouteMerge(Edge e, Edge prim)
        {
            Port s = e.Source;
            Bld sb = PortBelt(s);
            int id = nextConn++;
            reserved[K(s.NextX, s.NextY)] = id;
            var goals = new Dictionary<long, Goal>();
            var cands = new List<Bld>();
            if (prim != null) cands.AddRange(prim.Run);
            Bld tb = PortBelt(e.Target);
            if (tb != null) cands.Add(tb);
            var area = Area(0, 2);
            foreach (var t in cands)
            {
                if (mergeTargetsUsed.Contains(t) || IsCorner(t) || t.IsRaised || bridged.Contains(K(t.TileX, t.TileY))) continue;
                Dir f = t.FlowDir;
                foreach (Dir side in new[] { Dirs.Left(f), Dirs.Right(f) })
                {
                    int gx = t.TileX + Dirs.Dx(side), gy = t.TileY + Dirs.Dy(side);
                    if (!Passable(gx, gy, id, area)) continue;
                    goals[K(gx, gy)] = new Goal { NextDir = Dirs.Opposite(side), Target = t, SideJoin = true };
                }
            }
            Goal hit = null;
            var path = sb == null || goals.Count == 0 ? null : WithZones(() => Route2(new[] { new StartState(s.NextX, s.NextY, s.Dir, null) { HasPred = !IsFractional(sb) } }, goals, id, area, out hit), s);
            if (path == null) { errors.Add("composer: no side-merge route for " + N(e.Item) + " from " + e.From.Name + " into the lane of " + e.To.Name); return; }
            var run = Lay(path, s.Dir, hit.NextDir, e.Item, "merge:" + e.Item + "#" + (chainNo++));
            Layout.Link(sb, run[0], 1);
            try { Modules.SideMerge(run[run.Count - 1], hit.Target); mergeTargetsUsed.Add(hit.Target); sideJoined.Add(hit.Target); }
            catch (NotImplementedException) { throw; }
            catch (Exception ex) { errors.Add("composer: SideMerge into " + hit.Target + ": " + ex.Message); }
            e.Run = run;
        }

        static bool IsCorner(Bld b) { double y = Dirs.Norm(b.Yaw) % 90.0; return y > 1 && y < 89; }

        void PlaceSplitter(RowInfo prod, Port o, int id, List<Edge> outs)
        {
            if (outs.Count > 3) return;   // reported in MakeEdges
            // output sides: the ones facing most consumers (straight ahead wins ties)
            Dir[] cand = { o.Dir, Dirs.Left(o.Dir), Dirs.Right(o.Dir) };
            Func<Dir, int> facing = d => outs.Count(e => (e.Target.PrevX - o.X) * Dirs.Dx(d) + (e.Target.PrevY - o.Y) * Dirs.Dy(d) > 0);
            Dir[] dirs = cand.Select((d, i) => new { d, i }).OrderByDescending(a => facing(a.d)).ThenBy(a => a.i).Take(outs.Count).Select(a => a.d).ToArray();
            var area = Area(0, 4);
            Fragment best = null;
            for (int d = 1; d <= 8 && best == null; d++)
                foreach (int lat in new[] { 0, 1, -1, 2, -2 })
                {
                    Dir side = Dirs.Right(o.Dir);
                    int x = o.X + Dirs.Dx(o.Dir) * d + Dirs.Dx(side) * lat, y = o.Y + Dirs.Dy(o.Dir) * d + Dirs.Dy(side) * lat;
                    Fragment f;
                    try { f = Modules.Splitter(x, y, Dirs.Opposite(o.Dir), dirs, spec.Tiers.Belt, prod.Step.Item, false); }
                    catch (NotImplementedException) { throw; }
                    catch (Exception ex) { errors.Add("composer: Modules.Splitter: " + ex.Message); return; }
                    if (SplitterFits(f, o, id, area, outs) && FeedFeasible(f, o, id)) { best = f; break; }
                }
            if (best == null) { errors.Add("composer: no room for a splitter after " + prod.Name); return; }
            foreach (var b in best.Blds) AddBld(work, b);
            foreach (var b in best.Blds) if (b.Kind == Kind.Belt && IsFractional(b)) blocked.Add(K(b.TileX, b.TileY));
            Port sin = FindPort(best, "in");
            var souts = dirs.Select(dd => FindPort(best, "out:" + dd)).ToList();
            // clearance zone: only this splitter's feed and output routes may lay belts near it
            int num = ++splitterCount;
            Bld sp = best.Blds.First(b => b.Kind == Kind.Splitter);
            foreach (int[] z in ZoneOffsets()) { long k = K(sp.TileX + z[0], sp.TileY + z[1]); if (!zone.ContainsKey(k)) zone[k] = num; }
            // the 4 tiles straight next to the splitter: a belt there that is more than 3 hops from the splitter collides
            // (probe sphere inside the arm box); used sides hold the fractional cell belts, the rest stay empty
            foreach (Dir dd in new[] { Dir.N, Dir.E, Dir.S, Dir.W }) blocked.Add(K(sp.TileX + Dirs.Dx(dd), sp.TileY + Dirs.Dy(dd)));
            if (sin != null) splitterOfPort[sin] = num;
            foreach (var so in souts) if (so != null) splitterOfPort[so] = num;
            // the feed must not run through the tiles the outputs leave on
            foreach (var so in souts) if (so != null) reserved[K(so.NextX, so.NextY)] = -1;
            // feed: producer out -> splitter in
            var feed = new Edge { From = prod, To = prod, Item = prod.Step.Item, Source = o, Target = sin };
            reserved[K(sin.PrevX, sin.PrevY)] = id;
            RoutePortToPort(feed, id);
            // assign consumers to splitter outputs by minimal total distance (<= 3! permutations)
            var bestPerm = Perms(outs.Count).OrderBy(pm => pm.Select((oi, ei) => souts[oi] == null ? 1000 : Dist(souts[oi], outs[ei].Target)).Sum()).First();
            for (int i = 0; i < outs.Count; i++) outs[i].Source = souts[bestPerm[i]];
            foreach (var e in outs) if (e.Source == null) errors.Add("composer: splitter has no out port for " + e.To.Name);
            foreach (var e in outs) if (e.Source != null) reserved[K(e.Source.NextX, e.Source.NextY)] = -1;   // taken over by its edge's route
        }

        /// The producer -> splitter feed can be routed with the splitter in place (checked before anything is added).
        bool FeedFeasible(Fragment f, Port o, int id)
        {
            var pin = FindPort(f, "in");
            if (o.NextX == pin.X && o.NextY == pin.Y && o.Dir == pin.Dir) return true;
            var temp = new List<long>();
            foreach (var b in f.Blds)
                if (b.Kind != Kind.Sorter) { long k = K(b.TileX, b.TileY); if (blocked.Add(k)) temp.Add(k); }
            foreach (var p in f.Ports)
                if (p.Kind == PortKind.Out) { long k = K(p.NextX, p.NextY); if (blocked.Add(k)) temp.Add(k); }
            foreach (var b in f.Blds)
                if (b.Kind == Kind.Splitter)
                    foreach (Dir dd in new[] { Dir.N, Dir.E, Dir.S, Dir.W }) { long k = K(b.TileX + Dirs.Dx(dd), b.TileY + Dirs.Dy(dd)); if (blocked.Add(k)) temp.Add(k); }
            var goals = new Dictionary<long, Goal>();
            goals[K(pin.PrevX, pin.PrevY)] = new Goal { NextDir = pin.Dir };
            Goal hit;
            var path = Route2(new[] { new StartState(o.NextX, o.NextY, o.Dir, null) }, goals, id, Area(0, 4), out hit);
            foreach (long k in temp) blocked.Remove(k);
            return path != null;
        }

        bool SplitterFits(Fragment f, Port o, int id, Rect area, List<Edge> outs)
        {
            // clearance: no foreign belt (laid already) or foreign reserved entry tile near the splitter centre
            Bld sc = f.Blds.FirstOrDefault(b => b.Kind == Kind.Splitter);
            if (sc != null)
            {
                var ownChains = new HashSet<string>();
                Bld ob = PortBelt(o);
                if (ob != null && ob.Chain != null) ownChains.Add(ob.Chain);
                var okOwners = new HashSet<int> { id, -1 };
                foreach (var e in outs)
                {
                    Bld tb = e.Target != null ? PortBelt(e.Target) : null;
                    if (tb != null && tb.Chain != null) ownChains.Add(tb.Chain);
                    int c;
                    if (e.Target != null && tgtConnF != null && tgtConnF.TryGetValue(e.Target, out c)) okOwners.Add(c);
                }
                double min = GameData.SplitterForeignBeltMin - 1e-6;
                foreach (var b in work.All)
                {
                    if (b.Kind != Kind.Belt || (b.Chain != null && ownChains.Contains(b.Chain))) continue;
                    if (Math.Abs(b.X - sc.X) > 3 || Math.Abs(b.Y - sc.Y) > 3) continue;
                    if (Math.Sqrt((b.X - sc.X) * (b.X - sc.X) + (b.Y - sc.Y) * (b.Y - sc.Y)) < min) return false;
                }
                foreach (int[] z in ZoneOffsets())
                {
                    long k = K(sc.TileX + z[0], sc.TileY + z[1]);
                    int ow;
                    if (reserved.TryGetValue(k, out ow) && !okOwners.Contains(ow)) return false;
                    if (zone.ContainsKey(k)) return false;   // another splitter's zone
                }
            }
            foreach (var b in f.Blds)
            {
                if (b.Kind == Kind.Sorter) continue;
                if (b.Kind == Kind.Belt && IsFractional(b)) continue;
                if (!Passable(b.TileX, b.TileY, id, area)) return false;
                // exact colliders (colliders.json): the splitter's 2.38 m arms reach into a neighbouring machine's box
                if (b.Kind == Kind.Splitter && gd != null && gd.HasCollider(b.ItemId))
                    foreach (var m in work.All)
                    {
                        if (m.Kind != Kind.Machine && m.Kind != Kind.Splitter && m.Kind != Kind.Tesla) continue;
                        if (Math.Abs(m.X - b.X) > 4 || Math.Abs(m.Y - b.Y) > 4) continue;
                        if (gd.ColliderGap(b, m, 1.0) < 0.05) return false;
                        if (spec.Safe60 && gd.ColliderGap(b, m, GameData.EwFactorMin) < 0.05) return false;
                    }
            }
            var pin = FindPort(f, "in");
            if (pin == null) return false;
            bool direct = o.NextX == pin.X && o.NextY == pin.Y && o.Dir == pin.Dir;
            if (!direct && !Passable(pin.PrevX, pin.PrevY, id, area)) return false;
            foreach (var p in f.Ports) if (p.Kind == PortKind.Out && !Passable(p.NextX, p.NextY, id, area)) return false;
            return true;
        }

        static List<int[]> Perms(int n)
        {
            var res = new List<int[]>();
            Action<List<int>> rec = null;
            rec = cur =>
            {
                if (cur.Count == n) { res.Add(cur.ToArray()); return; }
                for (int i = 0; i < n; i++) if (!cur.Contains(i)) { cur.Add(i); rec(cur); cur.RemoveAt(cur.Count - 1); }
            };
            rec(new List<int>());
            return res;
        }

        void RouteRaw(RowInfo r, Port p, int id, int edgeY)
        {
            Bld pb = PortBelt(p);
            int item = 0;
            foreach (int it in r.InItems) if (FindPort(r.Frag, "in:" + it) == p) item = it;
            bool coat = Coaters.IsFeedPort(p);   // COATERS HOOK: proliferator feed port carries its own item and rate
            if (coat) item = p.Item;
            double rate = coat ? p.Rate : r.InRates[Array.IndexOf(r.InItems, item)];
            if (pb == null) { errors.Add("composer: raw port " + p + " has no belt"); return; }
            Bld first = pb;
            if (id != 0)
            {
                Bld runFirst = null;
                foreach (int below in new[] { 0, 1 })
                {
                    var area = Area(below, 2);
                    int sy = edgeY - below;
                    var starts = new List<StartState>();
                    for (int x = area.X0; x <= area.X1; x++) starts.Add(new StartState(x, sy, Dir.N, Dir.N) { HasPred = false });
                    var goals = new Dictionary<long, Goal>();
                    goals[K(p.PrevX, p.PrevY)] = new Goal { NextDir = p.Dir, Target = pb };
                    Goal hit;
                    var path = Route2(starts, goals, id, area, out hit);
                    if (path == null) continue;
                    var run = Lay(path, Dir.N, p.Dir, item, "raw:" + item + "#" + (chainNo++));
                    Layout.Link(run[run.Count - 1], pb, 1);
                    runFirst = run[0];
                    break;
                }
                if (runFirst == null) { errors.Add("composer: no feed route for raw " + N(item) + " from the input edge to " + r.Name + " (" + p.PrevX + "," + p.PrevY + ")"); return; }
                first = runFirst;
            }
            first.Parameters = new[] { item, 0 };   // rule 5: icon on the first tile of every block input chain
            blockPorts.Add(new Port { Name = "in:" + item + "#" + blockPorts.Count(q => q.Kind == PortKind.In), Kind = PortKind.In, X = first.TileX, Y = first.TileY,
                Dir = Dir.N, Item = item, Rate = rate, Belt = first, Chain = first.Chain });
        }

        void RouteProduct(RowInfo r, Port o, int id)
        {
            Bld ob = PortBelt(o);
            if (ob == null) { errors.Add("composer: product port of " + r.Name + " has no belt"); return; }
            Bld last = ob;
            Rect b = work.Bounds();
            if (!AtEdge(o.X, o.Y, o.Dir, b, outEdgeC))
            {
                List<Bld> run = null;
                foreach (int beyond in new[] { 0, 1 })
                {
                    Rect area = Area(0, 2);
                    // allow one extra row/column beyond the out edge
                    if (outEdgeC == Dir.N) area.Y1 = b.Y1 + beyond; else if (outEdgeC == Dir.E) area.X1 = b.X1 + beyond; else if (outEdgeC == Dir.W) area.X0 = b.X0 - beyond;
                    if (outEdgeC == Dir.E || outEdgeC == Dir.W) area.Y1 = b.Y1 + 2;
                    var goals = new Dictionary<long, Goal>();
                    for (int x = area.X0; x <= area.X1; x++)
                        for (int y = area.Y0; y <= area.Y1; y++)
                            if (OnEdgeLine(x, y, b, outEdgeC, beyond)) goals[K(x, y)] = new Goal { NextDir = null, Entry = outEdgeC };
                    Goal hit;
                    reserved[K(o.NextX, o.NextY)] = id;
                    var path = Route2(new[] { new StartState(o.NextX, o.NextY, o.Dir, null) }, goals, id, area, out hit);
                    if (path == null) continue;
                    run = Lay(path, o.Dir, null, r.Step.Item, "product:" + r.Step.Item + "#" + (chainNo++));
                    Layout.Link(ob, run[0], 1);
                    break;
                }
                if (run == null) { errors.Add("composer: no route for the product " + N(r.Step.Item) + " from " + r.Name + " to the " + spec.OutEdge + " edge"); return; }
                last = run[run.Count - 1];
            }
            Dir exitDir = outEdgeC;
            if (spec.Buffer)
            {
                Bld t2 = PlaceBuffer(r, last, id, out exitDir);
                if (t2 == null) { errors.Add("composer: no room for the buffer depot (cell DEPOT_BUFFER) at the product end of " + r.Name + " (--no-buffer disables it)"); exitDir = outEdgeC; }
                else last = t2;
            }
            // keep the exit free: nothing may be built ahead of the product chain end
            for (int i = 1; i <= 8; i++) blocked.Add(K(last.TileX + Dirs.Dx(exitDir) * i, last.TileY + Dirs.Dy(exitDir) * i));
            blockPorts.Add(new Port { Name = "out:" + r.Step.Item + "#" + blockPorts.Count(q => q.Kind == PortKind.Out), Kind = PortKind.Out, X = last.TileX, Y = last.TileY,
                Dir = exitDir, Item = r.Step.Item, Rate = r.OutRate, Belt = last, Chain = last.Chain });
        }

        /// Buffer depot at the block end (user rule, cell DEPOT_BUFFER of the user's in-game edit): the product chain
        /// continues past a Depot Mk.I (1 in sorter from the upstream tile, 2 out sorters onto the two downstream tiles).
        /// Tried in order: the user's exact shape (lane on 3 more tiles, corner to the right, approach + 3 tiles along the
        /// depot, depot between the lane end and the run = flow E with the depot on the right), its mirror, then the
        /// same cell straight on (lane continues 3 tiles, depot beside it, right / left). First fit wins.
        /// Returns the new chain end (t2) or null.
        Bld PlaceBuffer(RowInfo r, Bld last, int id, out Dir exitDir)
        {
            exitDir = outEdgeC;
            Dir F = outEdgeC;
            int lx = last.TileX, ly = last.TileY;
            Rect area = Area(0, 8);
            area = new Rect(area.X0 - 6, area.Y0 - 6, area.X1 + 6, area.Y1 + 6);
            foreach (int shape in new[] { 0, 1, 2, 3 })
            {
                var path = new List<int[]>();
                Dir run; bool depotRight;
                if (shape < 2)
                {
                    run = shape == 0 ? Dirs.Right(F) : Dirs.Left(F);
                    depotRight = shape == 0;   // the depot sits between the old lane end and the run
                    for (int i = 1; i <= 4; i++) path.Add(new[] { lx + Dirs.Dx(F) * i, ly + Dirs.Dy(F) * i });
                    int cx = lx + Dirs.Dx(F) * 4, cy = ly + Dirs.Dy(F) * 4;
                    for (int i = 1; i <= 4; i++) path.Add(new[] { cx + Dirs.Dx(run) * i, cy + Dirs.Dy(run) * i });
                }
                else
                {
                    run = F; depotRight = shape == 2;
                    for (int i = 1; i <= 3; i++) path.Add(new[] { lx + Dirs.Dx(F) * i, ly + Dirs.Dy(F) * i });
                }
                if (!path.All(p => Passable(p[0], p[1], id, area) && !raisedAt.ContainsKey(K(p[0], p[1])))) continue;
                int n = path.Count;
                int[] p1 = path[n - 2];
                Dir side = depotRight ? Dirs.Right(run) : Dirs.Left(run);
                int dx = p1[0] + 2 * Dirs.Dx(side), dy = p1[1] + 2 * Dirs.Dy(side);
                bool ok = true;
                for (int x = dx - 1; x <= dx + 1 && ok; x++)
                    for (int y = dy - 1; y <= dy + 1 && ok; y++)
                    {
                        long k = K(x, y); int o;
                        if (!work.Grid.IsFree(x, y) || blocked.Contains(k) || raisedAt.ContainsKey(k) || InForeignZone(k) || path.Any(p => p[0] == x && p[1] == y)
                            || (reserved.TryGetValue(k, out o) && o != id)) ok = false;
                    }
                if (!ok) continue;
                Bld probe = Bld.Depot(dx, dy);
                if (gd != null && gd.HasCollider(Ids.DepotMk1))
                    foreach (var m in work.All)
                    {
                        if (m.Kind != Kind.Machine && m.Kind != Kind.Splitter && m.Kind != Kind.Tesla && m.Kind != Kind.Storage) continue;
                        if (Math.Abs(m.X - dx) > 5 || Math.Abs(m.Y - dy) > 5) continue;
                        if (gd.ColliderGap(probe, m, 1.0) < 0.05 || (spec.Safe60 && gd.ColliderGap(probe, m, GameData.EwFactorMin) < 0.05)) { ok = false; break; }
                    }
                if (!ok) continue;
                var belts = Lay(path, F, null, r.Step.Item, last.Chain);
                Layout.Link(last, belts[0], 1);
                foreach (var b in belts) b.Role = "product buffer";
                Fragment f = Modules.DepotBuffer(belts[n - 3], belts[n - 2], belts[n - 1], run, depotRight, Lanes.DepotSorter(spec.Tiers.Sorter), r.Step.Item);   // lanes hook: depot cell is Mk.II
                foreach (var b in f.Blds)
                {
                    AddBld(work, b);
                    if (b.Kind == Kind.Sorter) { MarkSorter(b); for (int i = 1; i < 20; i++) blocked.Add(K((int)Math.Round(b.X + (b.X2 - b.X) * i / 20.0), (int)Math.Round(b.Y + (b.Y2 - b.Y) * i / 20.0))); }
                }
                RowNotes.Add("buffer: Depot Mk.I + 1 in / 2 out sorters L1 (cell DEPOT_BUFFER, " + (Modules.IsProvenDepot(Dirs.RotateCW(run, quarters), depotRight) ? "game-proven orientation" : "derived: turned/mirrored game cell") + ")");
                exitDir = run;
                return belts[n - 1];
            }
            return null;
        }

        static bool AtEdge(int x, int y, Dir d, Rect b, Dir edge)
        {
            if (d != edge) return false;
            switch (edge)
            {
                case Dir.N: return y >= b.Y1;
                case Dir.S: return y <= b.Y0;
                case Dir.E: return x >= b.X1;
                default: return x <= b.X0;
            }
        }

        static bool OnEdgeLine(int x, int y, Rect b, Dir edge, int beyond)
        {
            switch (edge)
            {
                case Dir.N: return y == b.Y1 + beyond;
                case Dir.S: return y == b.Y0 - beyond;
                case Dir.E: return x == b.X1 + beyond;
                default: return x == b.X0 - beyond;
            }
        }

        // ---------------------------------------------------------------- belts
        static double Yaw(Dir din, Dir dout)
        {
            double yIn = Dirs.Yaw(din), yOut = Dirs.Yaw(dout);
            if (din == dout) return yIn;
            double diff = ((yOut - yIn + 540.0) % 360.0) - 180.0;
            return (yIn + diff / 2.0 + 360.0) % 360.0;
        }

        /// Belts on the path; arrive = flow into the first tile, next = flow out of the last tile (null = chain end).
        List<Bld> Lay(List<int[]> path, Dir arrive, Dir? next, int item, string chain)
        {
            var made = new List<Bld>();
            for (int i = 0; i < path.Count; i++)
            {
                Dir din = i == 0 ? arrive : Dirs.FromDelta(path[i][0] - path[i - 1][0], path[i][1] - path[i - 1][1]);
                Dir dout = i < path.Count - 1 ? Dirs.FromDelta(path[i + 1][0] - path[i][0], path[i + 1][1] - path[i][1]) : (next ?? din);
                var b = Bld.Belt(spec.Tiers.Belt, path[i][0], path[i][1], Yaw(din, dout), item, chain);
                b.Role = chain;
                if (Modules.IsRaisedTile(path[i]))
                {
                    // cell RAISED_RUN: raised node at z 0.5 (occupies no grid tile; over free ground or a belt)
                    b.Z = b.Z2 = GameData.BridgeZ; b.Role = chain + " raised";
                    bridged.Add(K(path[i][0], path[i][1]));
                    raisedAt[K(path[i][0], path[i][1])] = new[] { din, dout };
                    if (i > 0 && !Modules.IsRaisedTile(path[i - 1])) bridged.Add(K(path[i - 1][0], path[i - 1][1]));   // feet: no side-join, no crossing
                    if (i + 1 < path.Count && !Modules.IsRaisedTile(path[i + 1])) bridged.Add(K(path[i + 1][0], path[i + 1][1]));
                    Bridges++;
                }
                work.Add(b);
                made.Add(b);
            }
            for (int i = 0; i < made.Count - 1; i++) Layout.Link(made[i], made[i + 1], 1);
            return made;
        }

        // ---------------------------------------------------------------- grid router (Dijkstra over tile x arrival dir x level)
        class StartState
        {
            public int X, Y; public Dir Arrive; public Dir? FirstMove;
            public bool HasPred = true;   // a belt in line behind the start tile (port belt); false for edge starts
            public bool Raised;           // start on the raised level (after a splitter high port, m39/m40)
            public StartState(int x, int y, Dir arrive, Dir? firstMove) { X = x; Y = y; Arrive = arrive; FirstMove = firstMove; }
        }

        class Node { public int X, Y; public Dir D; public int G; public Node Parent; public Dir? Fixed; public bool Raised, HasPred; }

        /// One search over both levels (cell RAISED_RUN, user's in-game edit): straight ground runs plus raised runs over
        /// belts beat multi-corner detours (corners cost TurnCost, a raised run RampCost per ramp + RaisedCost per node).
        /// Path entries are {x, y, raised}.
        List<int[]> Route2(IList<StartState> starts, Dictionary<long, Goal> goals, int owner, Rect area, out Goal hit)
        {
            int cost;
            return FindPath(starts, goals, owner, area, out hit, true, out cost);
        }

        // raised nodes laid so far: tile -> {in dir, out dir}
        readonly Dictionary<long, Dir[]> raisedAt = new Dictionary<long, Dir[]>();
        readonly HashSet<Bld> sideJoined = new HashSet<Bld>();     // belts that take a side-join (never raised over)
        readonly HashSet<long> aheadOfEnd = new HashSet<long>();  // blocked only for ground belts (tile ahead of a lane end)
        readonly HashSet<long> nearSorter = new HashSet<long>();   // tile centre 0.5..1.0 from a sorter (raised: unproven, avoided)
        readonly HashSet<long> onSorter = new HashSet<long>();     // tile centre < 0.5 from a sorter (no raised node)

        /// Sorter proximity of the tiles around a sorter segment (raised nodes keep >= 1.0 tile, proven in the user's file).
        void MarkSorter(Bld s)
        {
            int x0 = (int)Math.Floor(Math.Min(s.X, s.X2)) - 2, x1 = (int)Math.Ceiling(Math.Max(s.X, s.X2)) + 2;
            int y0 = (int)Math.Floor(Math.Min(s.Y, s.Y2)) - 2, y1 = (int)Math.Ceiling(Math.Max(s.Y, s.Y2)) + 2;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    double d = GameData.SegmentDist(x, y, s.X, s.Y, s.X2, s.Y2);
                    if (d < 0.5) onSorter.Add(K(x, y));
                    else if (d < GameData.RaisedSorterMin - 1e-6) nearSorter.Add(K(x, y));
                }
        }

        /// A tile a raised node (arriving in direction d) may sit on: free ground, or a straight ground belt of another
        /// chain (predecessor and successor in line, no side-join, no corner) flowing across d or the same way as d;
        /// never a building, a sorter path, a splitter cell tile, a foreign splitter zone or another raised node.
        bool RaiseOk(int x, int y, Dir d, int owner, Rect area)
        {
            if (!area.Contains(x, y)) return false;
            long k = K(x, y);
            if (raisedAt.ContainsKey(k) || onSorter.Contains(k) || InForeignZone(k)) return false;
            if (blocked.Contains(k) && !aheadOfEnd.Contains(k)) return false;
            int o;
            if (reserved.TryGetValue(k, out o) && o != owner) return false;
            Bld occ = work.Grid.At(x, y);
            if (occ == null) return true;
            if (occ.Kind != Kind.Belt) return false;
            Bld b = work.BeltAt(x, y);
            if (b == null || b.IsRaised || IsCorner(b) || mergeTargetsUsed.Contains(b) || sideJoined.Contains(b) || IsFractional(b) || bridged.Contains(k)) return false;
            Dir f = b.FlowDir;
            if (f == Dirs.Opposite(d)) return false;
            if (b.Output == null || b.Output.Kind != Kind.Belt || b.OutputToSlot != 1) return false;
            if (b.Output.TileX != x + Dirs.Dx(f) || b.Output.TileY != y + Dirs.Dy(f) || b.Output.IsRaised || IsFractional(b.Output)) return false;
            Bld prev = work.BeltAt(x - Dirs.Dx(f), y - Dirs.Dy(f));
            if (prev == null || prev.Output != b || prev.OutputToSlot != 1 || IsFractional(prev) || prev.IsRaised) return false;
            return true;
        }

        /// Ground belt moving in d under a raised node: across a straight raised node or along it, along a leg of a
        /// raised corner (proven: corner N->W over a belt flowing W, W->S over W).
        static bool UnderOk(Dir[] r, Dir d)
        {
            if (r[0] == r[1]) return d != Dirs.Opposite(r[0]);
            return d == r[0] || d == r[1];
        }

        /// Ground flow under a raised corner at tile k may only follow one of the corner's legs.
        bool CornerOverOk(int x, int y, Dir din, Dir dout)
        {
            Bld b = work.BeltAt(x, y);
            if (b == null) return true;
            Dir f = b.FlowDir;
            return f == din || f == dout;
        }

        List<int[]> FindPath(IList<StartState> starts, Dictionary<long, Goal> goals, int owner, Rect area, out Goal hit, bool raise, out int cost)
        {
            hit = null; cost = int.MaxValue;
            const int Step = 10;
            int Turn = TurnCost;
            var best = new Dictionary<long, int>();
            var heap = new List<Node>();
            Func<int, int, Dir, bool, Dir?, long> key = (x, y, d, r, fx) => (K(x, y) << 6) ^ ((long)d << 4) ^ (r ? 8L : 0L) ^ (fx != null ? (long)fx.Value + 4L : 0L);
            foreach (var s in starts)
            {
                long sk = K(s.X, s.Y);
                if (s.Raised)
                {
                    if (!raise || !RaiseOk(s.X, s.Y, s.Arrive, owner, area)) continue;
                    var rn = new Node { X = s.X, Y = s.Y, D = s.Arrive, G = RaisedCost, Fixed = s.Arrive, HasPred = true, Raised = true };
                    best[key(s.X, s.Y, s.Arrive, true, s.Arrive)] = rn.G;
                    Push(heap, rn);
                    continue;
                }
                if (!Passable(s.X, s.Y, owner, area) || raisedAt.ContainsKey(sk)) continue;
                var n = new Node { X = s.X, Y = s.Y, D = s.Arrive, G = 0, Fixed = s.FirstMove, HasPred = s.HasPred };
                best[key(s.X, s.Y, s.Arrive, false, s.FirstMove)] = 0;
                Push(heap, n);
            }
            int guard = 0;
            while (heap.Count > 0 && guard++ < 600000)
            {
                Node cur = Pop(heap);
                int bg;
                if (best.TryGetValue(key(cur.X, cur.Y, cur.D, cur.Raised, cur.Fixed), out bg) && bg < cur.G) continue;
                Goal g;
                if (!cur.Raised && goals.TryGetValue(K(cur.X, cur.Y), out g))
                {
                    bool okEntry = g.Entry == null || g.Entry.Value == cur.D;
                    bool okNext = g.NextDir == null || g.NextDir.Value != Dirs.Opposite(cur.D);
                    // a raised run's far foot (and a belt under a raised node) runs straight on: never a chain end there
                    bool landing = cur.Parent != null && cur.Parent.Raised;
                    bool under = raisedAt.ContainsKey(K(cur.X, cur.Y));
                    bool okFixed = cur.Fixed == null || (g.NextDir == null ? !(landing || under) : g.NextDir.Value == cur.Fixed.Value);
                    if (landing && (g.NoBridgeLanding || g.SideJoin)) okFixed = false;
                    if (under && (g.SideJoin || g.NextDir == null || g.NextDir.Value != cur.D)) okFixed = false;
                    if (okEntry && okNext && okFixed)
                    {
                        hit = g; cost = cur.G;
                        var path = new List<int[]>();
                        for (Node n = cur; n != null; n = n.Parent) path.Add(new[] { n.X, n.Y, n.Raised ? 1 : 0 });
                        path.Reverse();
                        return path;
                    }
                }
                for (int di = 0; di < 4; di++)
                {
                    Dir d = (Dir)di;
                    if (d == Dirs.Opposite(cur.D)) continue;
                    if (cur.Fixed != null && d != cur.Fixed.Value) continue;
                    int nx = cur.X + Dirs.Dx(d), ny = cur.Y + Dirs.Dy(d);
                    if (OnPath(cur, nx, ny)) continue;
                    long nk = K(nx, ny);
                    if (!cur.Raised)
                    {
                        // ground -> ground (possibly under a raised node: straight through)
                        if (Passable(nx, ny, owner, area))
                        {
                            Dir[] over;
                            bool ok = true; Dir? fx = null;
                            if (raisedAt.TryGetValue(nk, out over)) { ok = UnderOk(over, d); fx = d; }
                            if (ok)
                            {
                                int ng = cur.G + Step + (d != cur.D ? Turn : 0) + (NearForeignEntry(nx, ny, owner) ? Crowd : 0);
                                Relax(heap, best, key, new Node { X = nx, Y = ny, D = d, G = ng, Parent = cur, Fixed = fx });
                            }
                        }
                        // ground -> raised: one-tile up-ramp, straight in line with the foot's predecessor
                        bool hasPred = cur.Parent != null ? !cur.Parent.Raised : cur.HasPred;
                        if (raise && d == cur.D && hasPred && !raisedAt.ContainsKey(K(cur.X, cur.Y)) && RaiseOk(nx, ny, d, owner, area))
                        {
                            int ng = cur.G + Step + RampCost + RaisedCost + (nearSorter.Contains(nk) ? Crowd : 0);
                            Relax(heap, best, key, new Node { X = nx, Y = ny, D = d, G = ng, Parent = cur, Raised = true, Fixed = d });
                        }
                    }
                    else
                    {
                        // a raised corner sits on cur's tile: the ground belt below must follow one of its legs
                        if (d != cur.D && !CornerOverOk(cur.X, cur.Y, cur.D, d)) continue;
                        if (RaiseOk(nx, ny, d, owner, area))
                        {
                            int ng = cur.G + Step + RaisedCost + (d != cur.D ? Turn : 0) + (nearSorter.Contains(nk) ? Crowd : 0);
                            Relax(heap, best, key, new Node { X = nx, Y = ny, D = d, G = ng, Parent = cur, Raised = true });
                        }
                        // raised -> ground: one-tile down-ramp from a straight raised node onto free ground (not under
                        // another raised node), followed by one more straight node
                        if (d == cur.D && Passable(nx, ny, owner, area) && !raisedAt.ContainsKey(nk))
                        {
                            int ng = cur.G + Step + RampCost + (NearForeignEntry(nx, ny, owner) ? Crowd : 0);
                            Relax(heap, best, key, new Node { X = nx, Y = ny, D = d, G = ng, Parent = cur, Fixed = d });
                        }
                    }
                }
            }
            return null;
        }

        static void Relax(List<Node> heap, Dictionary<long, int> best, Func<int, int, Dir, bool, Dir?, long> key, Node n)
        {
            long kk = key(n.X, n.Y, n.D, n.Raised, n.Fixed);
            int old;
            if (best.TryGetValue(kk, out old) && old <= n.G) return;
            best[kk] = n.G;
            Push(heap, n);
        }

        const int Crowd = 30;   // cost of passing next to another connection's reserved tile (keeps its approach open)
        /// Router costs (tenths of a tile): corner 4 tiles ("strong cost on corners", user's edit), ramp 1 tile each,
        /// raised node 0.2 tile on top of its step. A 2-corner detour (8 tiles) loses to a straight raised run of up to
        /// ~ 30 tiles; a straight ground route stays cheapest.
        static int TurnCost { get { return EngineTuning.RouterTurnCost; } }
        static int RampCost { get { return EngineTuning.RouterRampCost; } }
        static int RaisedCost { get { return EngineTuning.RouterRaisedCost; } }

        bool NearForeignEntry(int x, int y, int owner)
        {
            for (int di = 0; di < 4; di++)
            {
                int o;
                if (reserved.TryGetValue(K(x + Dirs.Dx((Dir)di), y + Dirs.Dy((Dir)di)), out o) && o > 0 && o != owner) return true;
            }
            return false;
        }

        static bool OnPath(Node n, int x, int y)
        {
            for (; n != null; n = n.Parent) if (n.X == x && n.Y == y) return true;
            return false;
        }

        static void Push(List<Node> h, Node n)
        {
            h.Add(n);
            int i = h.Count - 1;
            while (i > 0) { int p = (i - 1) / 2; if (h[p].G <= h[i].G) break; var t = h[p]; h[p] = h[i]; h[i] = t; i = p; }
        }

        static Node Pop(List<Node> h)
        {
            Node top = h[0];
            Node last = h[h.Count - 1]; h.RemoveAt(h.Count - 1);
            if (h.Count > 0)
            {
                h[0] = last; int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < h.Count && h[l].G < h[m].G) m = l;
                    if (r < h.Count && h[r].G < h[m].G) m = r;
                    if (m == i) break;
                    var t = h[m]; h[m] = h[i]; h[i] = t; i = m;
                }
            }
            return top;
        }

        // ---------------------------------------------------------------- finish: rotate to the real edges, normalize, towers
        Layout Finish()
        {
            var final = new Layout(gd);
            var blds = work.All.ToList();
            if (quarters != 0) RotateLayout(blds, quarters);
            var ports = blockPorts.ToList();
            foreach (var p in ports)
                for (int i = 0; i < quarters; i++) { int rx = p.X, ry = p.Y; p.X = ry; p.Y = -rx; p.Dir = Dirs.Right(p.Dir); }
            // normalize: bounding box starts at (0,0)
            var box = Rect.Empty;
            foreach (var b in blds) if (b.Kind != Kind.Sorter) box = box.Union(final.Footprints.TileRect(b));
            if (!box.IsEmpty)
            {
                foreach (var b in blds) b.Translate(-box.X0, -box.Y0);
                foreach (var p in ports) { p.X -= box.X0; p.Y -= box.Y0; }
            }
            foreach (var b in blds) AddBld(final, b);
            final.Ports.AddRange(ports);
            final.Errors.AddRange(errors);
            if (!Fatal && blds.Count > 0)
            {
                List<Bld> towers = Modules.TeslaGrid(final, gd, false);   // fast; the winner is re-towered systematically
                if (towers != null) foreach (var t in towers) final.Add(t);
                CheckPower(final);
            }
            return final;
        }

        void CheckPower(Layout l)
        {
            var towers = l.All.Where(b => b.Kind == Kind.Tesla).ToList();
            double cover = GameData.PlaceTeslaCover, link = GameData.PlaceTeslaLink;
            foreach (var b in l.All)
            {
                if (b.Kind != Kind.Machine && b.Kind != Kind.Sorter) continue;
                foreach (var pt in GameData.PowerPoints(b))
                    if (!towers.Any(t => Math.Sqrt((t.X - pt[0]) * (t.X - pt[0]) + (t.Y - pt[1]) * (t.Y - pt[1])) <= cover + 1e-6))
                    { PowerProblems.Add("no tower within " + cover + " tiles of " + b); break; }
            }
            if (towers.Count == 0) return;
            var seen = new HashSet<Bld> { towers[0] };
            var q = new Queue<Bld>(); q.Enqueue(towers[0]);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                foreach (var u in towers)
                    if (!seen.Contains(u) && Math.Sqrt((t.X - u.X) * (t.X - u.X) + (t.Y - u.Y) * (t.Y - u.Y)) <= link + 1e-6) { seen.Add(u); q.Enqueue(u); }
            }
            if (seen.Count != towers.Count) PowerProblems.Add("tesla grid not connected: " + seen.Count + " of " + towers.Count + " towers linked");
        }
    }
}
}
