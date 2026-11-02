// Kit.cs - kit mode (analysis\modules-design.md section 1 + module standard MOD v1 of section 2): instead of one
// whole-factory blueprint, one validated module blueprint per production row. No routing between modules: the user
// connects the module tails himself (splitters, side-joins, bus), following _kit.md.
//   Solver plan -> rows (consumer groups <= 3 rows / <= one belt, parts by belt rate and --max-row) -> per row:
//   Modules.MachineRow + port strips (inputs S on odd columns pitch 2, output N) + towers (keep-out) -> Validator on a
//   one-step plan -> Emitter (header with PORTS line) -> numbered files, identical rows written once ("build N times").
// Pure model code except Export (game DLL through Emitter/GameBp).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DspFlat.Engine;

namespace DspFlat.Host
{

/// One machine row of the kit (one copy of a module).
public class KitRow
{
    public Step Step; public int StepIndex; public int Count;
    public int[] InItems; public double[] InRates; public double OutRate;
    /// consumer rows this row feeds and the rate each takes (null = block product)
    public List<KeyValuePair<KitRow, double>> Feeds;
    public int Group, Part, Parts;            // consumer group of the step and part within the group
    public KitModule Module; public int Copy; // module (file) this row is a copy of, 1-based copy number
    public double Load;
}

/// One module file: a validated layout built Copies times.
public class KitModule
{
    public int No; public string FileName, Slug;
    public readonly List<KitRow> Rows = new List<KitRow>();
    public KitRow Proto { get { return Rows[0]; } }
    public Layout Layout; public ValidationReport Report; public ExportResult Export;
    public int W, H;                          // module frame (W padded to a multiple of 4)
    public readonly List<string> Notes = new List<string>();
    public string PortsLine;
    public readonly List<Port> InPorts = new List<Port>();
    public Port OutPort;
}

public class Kit
{
    readonly GameData gd;
    readonly BlockSpec spec;
    readonly double beltRate;
    public ProductionPlan Plan;
    public readonly List<KitRow> Rows = new List<KitRow>();
    public readonly List<KitModule> Modules = new List<KitModule>();
    public readonly List<string> Errors = new List<string>();
    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> Log = new List<string>();

    public Kit(GameData gd, BlockSpec spec)
    {
        this.gd = gd; this.spec = spec;
        double br = gd.BeltRate(spec.Tiers.Belt);
        beltRate = br > 0 ? br : double.MaxValue;
    }

    string N(int item) { return gd.ItemName(item); }
    static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        string r = sb.ToString();
        while (r.Contains("--")) r = r.Replace("--", "-");
        return r.Trim('-');
    }

    /// Name of the kit folder: kit-<product slug>-<rate>.
    public string KitName { get { return "kit-" + Slug(N(spec.Item)) + "-" + F(spec.Rate); } }

    // ================================================================ 1. rows
    /// Solver plan -> rows. Consumers are sized first (reverse topological order); a producer step's consumer rows are
    /// packed into groups of <= 3 rows and <= one belt (Composer.PackConsumers, same as the grouped block mode; --own
    /// consumers get their own group); every group is one producer row, cut into parts when a lane exceeds the belt or the
    /// row exceeds --max-row machines. Each part runs at the same load, so its rates are its machine share.
    public void MakeRows(ProductionPlan plan)
    {
        Plan = plan;
        var rowsOf = new Dictionary<int, List<KitRow>>();
        for (int si = plan.Steps.Count - 1; si >= 0; si--)
        {
            Step s = plan.Steps[si];
            var made = new List<KitRow>(); rowsOf[si] = made;
            if (s.Recipe == null) { Errors.Add("step " + N(s.Item) + " has no recipe"); continue; }
            if (s.Outputs.Count > 1) Errors.Add(s.Recipe.Name + " has byproducts; kit mode does not support byproduct lanes yet");
            var ins = s.Inputs.Select((x, i) => new { x, i }).OrderByDescending(a => a.x.Rate).ThenBy(a => a.i).Select(a => a.x).ToList();
            if (ins.Count == 0 || ins.Count > 4) { Errors.Add(s.Recipe.Name + " has " + ins.Count + " inputs; MachineRow supports 1-4"); continue; }
            double outRate = s.Outputs[0].Rate;
            var slots = new List<KeyValuePair<KitRow, double>>();
            foreach (int ci in s.ConsumerSteps.Distinct())
            {
                List<KitRow> cr;
                if (!rowsOf.TryGetValue(ci, out cr)) continue;
                foreach (var c in cr) { int k = Array.IndexOf(c.InItems, s.Item); if (k >= 0) slots.Add(new KeyValuePair<KitRow, double>(c, c.InRates[k])); }
            }
            var groups = Composer.PackConsumers(slots, beltRate, spec.OwnSupply, r => r.Step.Item);
            if (slots.Count == 0) groups.Add(null);
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                double gRate = g == null ? outRate : g.Sum(x => x.Value);
                double share = gRate / Math.Max(1e-9, outRate);
                int count = g == null ? s.Machines : Math.Max(1, (int)Math.Ceiling(s.MachinesExact * share - 1e-9));
                double worstLane = Math.Max(gRate, ins.Max(x => x.Rate * share));
                int parts = Math.Max((int)Math.Ceiling(count / (double)Math.Max(1, spec.MaxRow) - 1e-9), (int)Math.Ceiling(worstLane / beltRate - 1e-9));
                parts = Math.Max(1, Math.Min(parts, count));
                for (int p = 0; p < parts; p++)
                {
                    int cnt = count / parts + (p < count % parts ? 1 : 0);
                    double ps = cnt / (double)count;
                    var r = new KitRow
                    {
                        Step = s, StepIndex = si, Count = cnt, Group = gi, Part = p, Parts = parts,
                        InItems = ins.Select(x => x.Item).ToArray(), InRates = ins.Select(x => x.Rate * share * ps).ToArray(),
                        OutRate = gRate * ps,
                        Feeds = g == null ? null : g.Select(x => new KeyValuePair<KitRow, double>(x.Key, x.Value * ps)).ToList()
                    };
                    r.Load = Composer.RowLoad(gd, s, cnt, r.OutRate);
                    if (r.Load > 1 + 1e-6) Errors.Add("row " + N(s.Item) + " x" + cnt + " needs " + F(r.Load * 100) + "% load");
                    made.Add(r);
                }
            }
        }
        for (int si = 0; si < plan.Steps.Count; si++) if (rowsOf.ContainsKey(si)) Rows.AddRange(rowsOf[si]);
        CheckFlows();
    }

    /// Kit-level port check (design section 4, "port level"): every consumer row's input is covered by the rows that
    /// feed it, every row's output is fully taken, no lane above the belt, no fan-out above 3 (one proven SPLIT cell).
    void CheckFlows()
    {
        foreach (var r in Rows)
        {
            for (int i = 0; i < r.InItems.Length; i++)
            {
                if (r.InRates[i] > beltRate + 1e-6) Errors.Add("row " + N(r.Step.Item) + " x" + r.Count + ": input lane " + N(r.InItems[i]) + " " + F(r.InRates[i]) + "/min > belt " + F(beltRate));
                if (r.Step.IsExternal(Plan, r.InItems[i])) continue;
                double got = Rows.Where(p => p.Step.Item == r.InItems[i] && p.Feeds != null).SelectMany(p => p.Feeds.Where(kv => kv.Key == r)).Sum(kv => kv.Value);
                if (got + 1e-6 < r.InRates[i]) Errors.Add("row " + N(r.Step.Item) + " x" + r.Count + " gets " + F(got) + "/min of " + N(r.InItems[i]) + ", needs " + F(r.InRates[i]));
            }
            if (r.OutRate > beltRate + 1e-6) Errors.Add("row " + N(r.Step.Item) + " x" + r.Count + ": output " + F(r.OutRate) + "/min > belt");
            if (r.Feeds != null)
            {
                if (r.Feeds.Count > 3) Errors.Add("row " + N(r.Step.Item) + " feeds " + r.Feeds.Count + " rows (> 3 = splitter cascade, unproven)");
                double take = r.Feeds.Sum(kv => kv.Value);
                if (Math.Abs(take - r.OutRate) > 1e-6) Errors.Add("row " + N(r.Step.Item) + " makes " + F(r.OutRate) + "/min, consumers take " + F(take));
            }
        }
    }

    // ================================================================ 2. modules
    /// Identical rows (same step, machine count and rates) become one module; modules are numbered in build order:
    /// level (longest producer chain below the step; raw-only steps 0) -> item id -> row order.
    public void MakeModules()
    {
        var level = new Dictionary<int, int>();
        for (int si = 0; si < Plan.Steps.Count; si++)
        {
            Step s = Plan.Steps[si]; int lv = 0;
            foreach (var x in s.Inputs)
            {
                if (s.IsExternal(Plan, x.Item)) continue;
                int pi = Plan.Steps.IndexOf(Plan.StepFor(x.Item));
                int l; if (level.TryGetValue(pi, out l)) lv = Math.Max(lv, l + 1);
            }
            level[si] = lv;
        }
        Func<KitRow, string> key = r => r.StepIndex + "|" + r.Count + "|" + string.Join(",", r.InRates.Select(v => v.ToString("0.0000", CultureInfo.InvariantCulture)).ToArray()) + "|" + r.OutRate.ToString("0.0000", CultureInfo.InvariantCulture);
        var order = Rows.Select((r, i) => new { r, i }).OrderBy(a => level[a.r.StepIndex]).ThenBy(a => a.r.Step.Item).ThenBy(a => a.i).Select(a => a.r).ToList();
        var byKey = new Dictionary<string, KitModule>();
        foreach (var r in order)
        {
            KitModule m;
            if (!byKey.TryGetValue(key(r), out m))
            {
                m = new KitModule { No = Modules.Count + 1 };
                m.Slug = Slug(N(r.Step.Item));
                byKey[key(r)] = m; Modules.Add(m);
            }
            m.Rows.Add(r); r.Module = m; r.Copy = m.Rows.Count;
        }
        foreach (var m in Modules)
            m.FileName = m.No.ToString("00", CultureInfo.InvariantCulture) + "-" + m.Slug + "-x" + m.Proto.Count + (m.Rows.Count > 1 ? "-build" + m.Rows.Count + "x" : "") + ".txt";
    }

    // ================================================================ 3. module layout (MOD v1 port standard)
    public void BuildLayouts()
    {
        foreach (var m in Modules)
        {
            string err = null;
            foreach (bool mirror in new[] { false, true })
            {
                err = BuildModule(m, mirror);
                if (err == null) break;
            }
            if (err != null) Errors.Add(m.FileName + ": " + err);
            Report(m, err == null ? "ok (built, validated)" : "FAILED: " + err);
        }
    }

    /// Per-module progress for the console (MakeBlock): "[module 4/11] Copper Ingot x9 ... ok".
    public Action<string> OnModule;
    void Report(KitModule m, string what)
    {
        if (OnModule == null) return;
        OnModule("[module " + (Modules.IndexOf(m) + 1) + "/" + Modules.Count + "] " + N(m.Proto.Step.Item) + " x" + m.Proto.Count + " ... " + what);
    }

    BlockSpec ModuleSpec(KitRow r)
    {
        var s = new BlockSpec { Item = r.Step.Item, Rate = r.OutRate, Tiers = spec.Tiers, InEdge = Dir.S, OutEdge = Dir.N, Safe60 = spec.Safe60 };
        return s;
    }

    /// One-step plan of a module row (inputs = block inputs, output = block port), for the Validator and the header.
    public ProductionPlan ModulePlan(KitRow r)
    {
        Step s = r.Step;
        var st = new Step
        {
            Item = s.Item, Recipe = s.Recipe, MachineItem = s.MachineItem, Speed = s.Speed, Machines = r.Count,
            MachinesExact = r.Count * r.Load, MaxOutputRate = r.OutRate / Math.Max(1e-9, r.Load)
        };
        for (int i = 0; i < r.InItems.Length; i++) st.Inputs.Add(new ItemRate(r.InItems[i], r.InRates[i]));
        st.Outputs.Add(new ItemRate(s.Item, r.OutRate));
        var p = new ProductionPlan { TargetItem = s.Item, TargetRate = r.OutRate };
        p.Steps.Add(st);
        for (int i = 0; i < r.InItems.Length; i++) p.RawInputs.Add(new ItemRate(r.InItems[i], r.InRates[i]));
        return p;
    }

    string BuildModule(KitModule m, bool mirror)
    {
        KitRow r = m.Proto;
        m.Notes.Clear(); m.InPorts.Clear(); m.OutPort = null;
        var rs = new RowSpec
        {
            Name = m.Slug, MachineItem = r.Step.MachineItem, RecipeId = r.Step.Recipe.Id, Count = r.Count, Pitch = 0,
            InputItems = (int[])r.InItems.Clone(), InputRates = (double[])r.InRates.Clone(),
            OutputItem = r.Step.Item, OutputRate = r.OutRate, Flow = Dir.N, Mirror = mirror,
            BeltItem = spec.Tiers.Belt, SorterItem = spec.Tiers.Sorter, OutputLength = 0,
            LaneIcons = false, TailItems = (int[])r.InItems.Clone(), Data = gd, FinalQuarters = 0, AlongEW = false, Safe60 = spec.Safe60
        };
        Fragment f;
        try { f = DspFlat.Engine.Modules.MachineRow(rs); }
        catch (Exception e) { return "MachineRow: " + e.Message; }
        if (rs.Problems.Count > 0) return string.Join("; ", rs.Problems.ToArray());
        foreach (string n in rs.Notes) m.Notes.Add(n);
        foreach (var b in f.Blds) if (b.Kind == Kind.Machine) b.Load = Math.Min(1.0, r.Load);

        // ---- port columns: odd x, pitch >= 2, lane order kept (no crossings)
        var inPorts = r.InItems.Select(it => f.Ports.First(p => p.Name == "in:" + it)).OrderBy(p => p.X).ToList();
        Port outP = f.Ports.First(p => p.Name == "out:" + r.Step.Item);
        int bestO = 0, bestCost = int.MaxValue; int[] bestT = null; int bestTo = 0;
        foreach (int o in new[] { 0, 1 })
        {
            int[] xs = inPorts.Select(p => p.X + o).ToArray();
            int[] t; int cost = AssignColumns(xs, out t);
            if (t == null) continue;
            int xo = outP.X + o, to = IsOdd(xo) ? xo : (xo < 0 ? xo - 1 : xo + 1);   // output: away from the row centre
            cost += Math.Abs(to - xo) * 3;
            if (cost < bestCost) { bestCost = cost; bestO = o; bestT = t; bestTo = to; }
        }
        if (bestT == null) return "no port column assignment";
        var lanes = new List<Bld>(); var blds = new List<Bld>(f.Blds);
        // jog rows: left movers (target west of the lane) and right movers; the outer lane jogs higher (nesting)
        var left = Enumerable.Range(0, inPorts.Count).Where(i => bestT[i] < inPorts[i].X + bestO).ToList();
        var right = Enumerable.Range(0, inPorts.Count).Where(i => bestT[i] > inPorts[i].X + bestO).ToList();
        int J = Math.Max(left.Count, right.Count);
        var jogRow = new Dictionary<int, int>();
        for (int k = 0; k < left.Count; k++) jogRow[left[k]] = 2 + (left.Count - 1 - k);                       // westmost highest
        var rightDesc = right.OrderByDescending(i => inPorts[i].X).ToList();
        for (int k = 0; k < rightDesc.Count; k++) jogRow[rightDesc[k]] = 2 + (rightDesc.Count - 1 - k);         // eastmost highest
        int minStart = inPorts.Min(p => p.Y);
        int dy = (J > 0 ? J + 2 : 1) - minStart;
        int dx = bestO;
        f.Translate(dx, dy);   // ports move with the fragment

        int topY = outP.Y;     // last output tile (after translation)
        for (int i = 0; i < inPorts.Count; i++)
        {
            Port p = inPorts[i];
            int t = bestT[i];
            var tiles = new List<int[]>();
            if (t == p.X) for (int y = 0; y < p.Y; y++) tiles.Add(new[] { t, y });
            else
            {
                int yj = jogRow[i];
                for (int y = 0; y <= yj; y++) tiles.Add(new[] { t, y });
                int step = p.X > t ? 1 : -1;
                for (int x = t + step; x != p.X; x += step) tiles.Add(new[] { x, yj });
                for (int y = yj; y < p.Y; y++) tiles.Add(new[] { p.X, y });
            }
            if (tiles.Count == 0) return "lane " + N(p.Item) + " starts on the port row";
            List<Bld> run = Chain(tiles, p.Belt, Dir.N, p.Item, p.Chain);
            run[0].Parameters = new[] { p.Item, 0 };   // rule 5 icon on the port tile (tail, rule 9)
            blds.AddRange(run);
            m.InPorts.Add(new Port { Name = "in:" + p.Item, Kind = PortKind.In, X = t, Y = 0, Dir = Dir.N, Item = p.Item,
                Rate = r.InRates[Array.IndexOf(r.InItems, p.Item)], Belt = run[0], Chain = p.Chain });
        }
        // output strip: (x, top+1) [jog] then 2 straight tiles north, chain end on the top row
        {
            int xo = outP.X, to = bestTo;
            var tiles = new List<int[]>();
            tiles.Add(new[] { xo, topY + 1 });
            int y = topY + 1;
            if (to != xo)
            {
                int step = to > xo ? 1 : -1;
                for (int x = xo + step; x != to + step; x += step) tiles.Add(new[] { x, y });
            }
            tiles.Add(new[] { to, y + 1 }); tiles.Add(new[] { to, y + 2 });
            bool buffer = spec.Buffer && Plan != null && r.Step.Item == Plan.TargetItem;
            if (buffer) tiles.Add(new[] { to, y + 3 });   // 3 straight tiles along the depot (t0 t1 t2)
            List<Bld> run = Chain(tiles, null, Dir.N, r.Step.Item, outP.Chain);
            Layout.Link(outP.Belt, run[0], 1);
            blds.AddRange(run);
            Bld last = run[run.Count - 1];
            if (buffer)
            {
                // final product module only (user rule): buffer depot beside the output strip (cell DEPOT_BUFFER, the
                // game cell turned to flow N: derived orientation); depot east (mirror false) or west (mirror true)
                int n = run.Count;
                Fragment dep = DspFlat.Engine.Modules.DepotBuffer(run[n - 3], run[n - 2], run[n - 1], Dir.N, !mirror, spec.Tiers.Sorter, r.Step.Item);
                blds.AddRange(dep.Blds);
                m.Notes.Add("buffer: Depot Mk.I + 1 in / 2 out sorters L1 beside the output strip (cell DEPOT_BUFFER turned to flow N: derived)");
            }
            m.OutPort = new Port { Name = "out:" + r.Step.Item, Kind = PortKind.Out, X = to, Y = last.TileY, Dir = Dir.N, Item = r.Step.Item, Rate = r.OutRate, Belt = last, Chain = outP.Chain };
        }
        // normalise x so the west edge is x 0 or 1 (keeps odd port columns)
        int minX = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        var fp = new Footprints(gd);
        foreach (var b in blds) { Rect rr = fp.TileRect(b); if (rr.IsEmpty) continue; minX = Math.Min(minX, rr.X0); maxX = Math.Max(maxX, rr.X1); maxY = Math.Max(maxY, rr.Y1); }
        int shift = -(minX - (((minX % 2) + 2) % 2));   // even shift: minX -> 0 or 1
        foreach (var b in blds) b.Translate(shift, 0);
        foreach (var p in m.InPorts) p.X += shift;
        m.OutPort.X += shift;
        maxX += shift;
        m.H = maxY + 1;
        if (m.OutPort.Y != m.H - 1) return "output port not on the top row";

        // ---- towers (keep-out 2 tiles from the W/E frame edges, none in the port strips); widen the frame if needed
        ProductionPlan mp = ModulePlan(r);
        BlockSpec ms = ModuleSpec(r);
        string lastErr = null;
        for (int widen = 0; widen < 4; widen++)
        {
            int W = ((maxX + 1 + 3) / 4) * 4 + 4 * widen;
            var l = new Layout(gd);
            foreach (var b in blds) l.Add(b);
            if (l.Errors.Count > 0) return "layout: " + l.Errors[0];
            l.Ports.AddRange(m.InPorts); l.Ports.Add(m.OutPort);
            int H = m.H;
            DspFlat.Engine.Modules.TeslaAllowed = (x, y) => x >= 2 && x <= W - 3 && y >= 2 && y <= H - 3;
            List<Bld> towers;
            try { towers = DspFlat.Engine.Modules.TeslaGrid(l, gd, true); }
            finally { DspFlat.Engine.Modules.TeslaAllowed = null; }
            if (l.Errors.Count > 0) { lastErr = "towers: " + l.Errors[0]; continue; }
            foreach (var t in towers) l.Add(t);
            var rep = new Validator(gd, ms).Validate(l, mp);
            if (!rep.Ok) { lastErr = "validator: " + rep.Errors[0]; if (rep.Errors.Any(e => e.Contains("tesla") || e.Contains("power"))) continue; return lastErr + (rep.Errors.Count > 1 ? " (+" + (rep.Errors.Count - 1) + " more)" : ""); }
            m.Layout = l; m.Report = rep; m.W = W;
            m.PortsLine = "PORTS MOD1 W=" + W + " H=" + H + " IN=" + string.Join(",", m.InPorts.Select(p => p.X + ":" + p.Item + ":" + F(p.Rate)).ToArray())
                + " OUT=" + m.OutPort.X + ":" + m.OutPort.Item + ":" + F(m.OutPort.Rate);
            return null;
        }
        return lastErr ?? "towers: no valid placement";
    }

    static bool IsOdd(int v) { return ((v % 2) + 2) % 2 == 1; }

    /// Odd target column per lane (lanes sorted west -> east), order kept, neighbours >= 2 apart, total move minimal.
    static int AssignColumns(int[] xs, out int[] best)
    {
        best = null; int bestCost = int.MaxValue;
        int n = xs.Length;
        var cur = new int[n];
        Action<int, int, int> rec = null;
        int[] bestLocal = null;
        rec = (i, prev, cost) =>
        {
            if (cost >= bestCost) return;
            if (i == n) { bestCost = cost; bestLocal = (int[])cur.Clone(); return; }
            for (int t = xs[i] - 3; t <= xs[i] + 3; t++)
            {
                if (!IsOdd(t) || (i > 0 && t < prev + 2)) continue;
                cur[i] = t;
                int c = Math.Abs(t - xs[i]); c = c * 10 + (c > 0 ? 5 : 0);
                rec(i + 1, t, cost + c);
            }
        };
        rec(0, int.MinValue / 2, 0);
        best = bestLocal;
        return bestCost;
    }

    /// Belt chain through tiles (straight + corners, corner yaw = bisector), last tile linked into next (flowing
    /// nextDir) or a chain end when next is null.
    List<Bld> Chain(List<int[]> tiles, Bld next, Dir endDir, int item, string chain)
    {
        var run = new List<Bld>();
        for (int i = 0; i < tiles.Count; i++)
        {
            Dir dIn = i > 0 ? Dirs.FromDelta(tiles[i][0] - tiles[i - 1][0], tiles[i][1] - tiles[i - 1][1]) : Dir.N;
            Dir dOut = i < tiles.Count - 1 ? Dirs.FromDelta(tiles[i + 1][0] - tiles[i][0], tiles[i + 1][1] - tiles[i][1])
                     : next != null ? Dirs.FromDelta(next.TileX - tiles[i][0], next.TileY - tiles[i][1]) : endDir;
            if (i == 0) dIn = dOut == Dir.N || dOut == Dir.S ? dOut : Dir.N;
            Bld b = Bld.Belt(spec.Tiers.Belt, tiles[i][0], tiles[i][1], DspFlat.Engine.Modules.CornerYaw(dIn, dOut), item, chain);
            b.Role = chain + " port strip";
            if (run.Count > 0) Layout.Link(run[run.Count - 1], b, 1);
            run.Add(b);
        }
        Layout.Link(run[run.Count - 1], next, next != null ? 1 : 0);
        return run;
    }

    // ================================================================ 4. export
    public void Export(Cells cells)
    {
        foreach (var m in Modules)
        {
            if (m.Layout == null) continue;
            KitRow r = m.Proto;
            ProductionPlan mp = ModulePlan(r);
            BlockSpec ms = ModuleSpec(r);
            BpHeader h = Emitter.HeaderFor(m.Layout, mp, ms, gd);
            var icons = new List<int> { r.Step.Item };
            foreach (var p in m.InPorts.OrderBy(p => p.X)) if (icons.Count < 5 && !icons.Contains(p.Item)) icons.Add(p.Item);
            while (icons.Count < 5) icons.Add(0);
            h.Icons = icons.ToArray();
            h.ShortDesc = BpText.KitThumb(gd, m.No, r.Step.MachineItem, r.Count)
                ?? m.No.ToString("00", CultureInfo.InvariantCulture) + " " + N(r.Step.Item) + " x" + r.Count;
            h.Desc = ModuleDesc(m);
            var ex = new GameEmitter(gd, cells).Emit(m.Layout, h);
            GameEmitter.CheckExport(ex, m.Layout, m.Report);
            foreach (var e in ex.Errors) m.Report.Fail(e);
            m.Export = ex;
            if (!m.Report.Ok) Errors.Add(m.FileName + ": export: " + m.Report.Errors[0]);
            Report(m, m.Report.Ok ? "exported, re-import + hash ok" : "export FAILED: " + m.Report.Errors[0]);
        }
    }

    string ModuleDesc(KitModule m)
    {
        // title + "- " fact lines (BpText.Lines), same wording as tools-text\SetKitDesc.cs
        KitRow r = m.Proto;
        var facts = new List<string>();
        facts.Add(r.Count + " " + N(r.Step.MachineItem));
        if (m.Rows.Count > 1) facts.Add("build " + m.Rows.Count + "x");
        foreach (var p in m.InPorts.OrderBy(p => p.X))
        {
            string src = SourceOf(r, p.Item);
            if (src.StartsWith("outside (ore", StringComparison.Ordinal)) src = "ore"; else if (src.StartsWith("outside", StringComparison.Ordinal)) src = "outside";
            facts.Add("in S: " + N(p.Item) + " " + F(p.Rate) + " <- " + src);
        }
        facts.Add("out N: " + N(r.Step.Item) + " " + F(r.OutRate) + " -> " + FeedText(m));
        facts.Add("belts " + BpText.Tier(N(spec.Tiers.Belt)) + ", sorters " + BpText.Tier(N(spec.Tiers.Sorter)));
        facts.Add(m.PortsLine);
        return BpText.Lines(m.No.ToString("00", CultureInfo.InvariantCulture) + " " + N(r.Step.Item) + " " + F(r.OutRate) + "/min (kit "
            + N(spec.Item) + " " + F(spec.Rate) + "/min)", facts);
    }

    string Mod(KitRow r) { return r.Module.No.ToString("00", CultureInfo.InvariantCulture) + (r.Module.Rows.Count > 1 ? "#" + r.Copy : ""); }

    string SourceOf(KitRow r, int item)
    {
        if (r.Step.IsExternal(Plan, item)) return "outside (" + (gd.Item(item) != null && gd.Item(item).IsResource ? "ore" : "block input") + ")";
        var src = Rows.Where(p => p.Step.Item == item && p.Feeds != null && p.Feeds.Any(kv => kv.Key.Module == r.Module)).Select(p => p.Module.No).Distinct().ToList();
        return string.Join("+", src.Select(n => n.ToString("00", CultureInfo.InvariantCulture)).ToArray());
    }

    string FeedText(KitModule m)
    {
        KitRow r = m.Proto;
        if (r.Feeds == null) return "product";
        var cons = m.Rows.SelectMany(x => x.Feeds).Select(kv => kv.Key.Module.No).Distinct().OrderBy(x => x).Select(x => x.ToString("00", CultureInfo.InvariantCulture));
        return string.Join("+", cons.ToArray());
    }

    // ================================================================ 5. files
    /// Writes the module blueprints and the _intro_ folder description into bpKitDir (when given) and everything plus
    /// _kit.md into workDir. Never deletes anything.
    public void Write(string bpKitDir, string workDir)
    {
        foreach (string d in new[] { bpKitDir, workDir })
        {
            if (d == null) continue;
            Directory.CreateDirectory(d);
            foreach (var m in Modules) GameBp.Write(Path.Combine(d, m.FileName), m.Export.Text);
            File.WriteAllText(Path.Combine(d, "_intro_"), IntroText(), new UTF8Encoding(false));
        }
        if (workDir != null) File.WriteAllText(Path.Combine(workDir, "_kit.md"), KitMd(), new UTF8Encoding(false));
    }

    /// Folder description in the game's format (see Blueprint\*\_intro_): BPBOOK:0,1,icon0..icon4,0,<url-encoded text>.
    public string IntroText()
    {
        var ic = new List<int> { spec.Item };
        foreach (var it in Plan.RawInputs.Select(x => x.Item)) if (ic.Count < 5 && !ic.Contains(it)) ic.Add(it);
        while (ic.Count < 5) ic.Add(0);
        var sb = new StringBuilder();
        sb.Append("Kit ").Append(N(spec.Item)).Append(' ').Append(F(spec.Rate)).Append("/min: ").Append(Rows.Sum(r => r.Count)).Append(" machines in ")
          .Append(Modules.Count).Append(" modules. Build in number order, inputs south, output north; connect tails per _kit.md (work\\out\\kits\\")
          .Append(KitName).Append("). ");
        sb.Append(string.Join("; ", Modules.Select(m => m.No.ToString("00", CultureInfo.InvariantCulture) + " " + N(m.Proto.Step.Item) + " x" + m.Proto.Count + (m.Rows.Count > 1 ? " (build " + m.Rows.Count + "x)" : "") + " -> " + FeedText(m)).ToArray()));
        return "BPBOOK:0,1," + string.Join(",", ic.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ",0," + Uri.EscapeDataString(sb.ToString());
    }

    public string KitMd()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int total = Rows.Sum(r => r.Count);
        sb.AppendLine("# Kit: " + N(spec.Item) + " " + F(spec.Rate) + "/min");
        sb.AppendLine();
        sb.AppendLine("Generated by `MakeBlock --kit`. Tiers: " + N(spec.Tiers.Smelter) + ", " + N(spec.Tiers.Assembler) + ", " + N(spec.Tiers.Belt) + ", " + N(spec.Tiers.Sorter) + ".");
        sb.AppendLine("Machines: " + total + " (plan " + Plan.Steps.Sum(s => s.Machines) + ", exact " + Plan.Steps.Sum(s => s.MachinesExact).ToString("0.##", c) + "); max " + spec.MaxRow + " machines per row.");
        sb.AppendLine("Raw inputs: " + string.Join(", ", Plan.RawInputs.Select(x => N(x.Item) + " " + F(x.Rate) + "/min (" + (int)Math.Ceiling(x.Rate / beltRate - 1e-9) + " Mk.II lane(s))").ToArray()) + ".");
        sb.AppendLine();
        sb.AppendLine("Every module: inputs on the SOUTH edge (port row y=0, odd columns, icon on the port belt), output on the NORTH edge");
        sb.AppendLine("(top row); bottom and top 2 rows are straight port strips; towers >= 2 tiles from the W/E frame edges (own power grid).");
        sb.AppendLine("Columns below are counted from the module's west edge (x=0). Identical rows are one file: build it N times.");
        sb.AppendLine();
        sb.AppendLine("## Build order / modules");
        sb.AppendLine();
        sb.AppendLine("| NN | file | build | machines | inputs (per copy: item rate @column <- source) | output (per copy) | frame WxH |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var m in Modules)
        {
            KitRow r = m.Proto;
            sb.AppendLine("| " + m.No.ToString("00", c) + " | `" + m.FileName + "` | " + m.Rows.Count + "x | " + r.Count + " " + N(r.Step.MachineItem) + " (load " + (r.Load * 100).ToString("0.#", c) + "%) | "
                + string.Join("; ", m.InPorts.OrderBy(p => p.X).Select(p => N(p.Item) + " " + F(p.Rate) + " @" + p.X + " <- " + SourceOf(r, p.Item)).ToArray())
                + " | " + N(r.Step.Item) + " " + F(r.OutRate) + " @" + m.OutPort.X + " | " + m.W + "x" + m.H + " |");
        }
        sb.AppendLine();
        sb.AppendLine("## Connections (which output feeds which input)");
        sb.AppendLine();
        foreach (var it in Plan.RawInputs)
        {
            var users = Rows.Where(r => Array.IndexOf(r.InItems, it.Item) >= 0 && r.Step.IsExternal(Plan, it.Item)).ToList();
            sb.AppendLine("- " + N(it.Item) + " " + F(it.Rate) + "/min from outside -> " + string.Join(", ", users.Select(r => Mod(r) + " " + F(r.InRates[Array.IndexOf(r.InItems, it.Item)])).ToArray()));
        }
        foreach (var m in Modules)
        {
            foreach (var r in m.Rows)
            {
                if (r.Feeds == null) { sb.AppendLine("- " + Mod(r) + " " + N(r.Step.Item) + " " + F(r.OutRate) + "/min -> PRODUCT"); continue; }
                string how;
                var sameGroup = Rows.Where(x => x.Step == r.Step && x.Group == r.Group).ToList();
                if (r.Feeds.Count == 1) how = "straight";
                else how = "split 1->" + r.Feeds.Count + " (one splitter)";
                if (sameGroup.Count > 1 && r.Part == 0)
                    sb.AppendLine("- " + string.Join(" + ", sameGroup.Select(x => Mod(x)).ToArray()) + " " + N(r.Step.Item) + ": side-join the " + sameGroup.Count + " output tails into one belt ("
                        + F(sameGroup.Sum(x => x.OutRate)) + "/min), then " + (r.Feeds.Count == 1 ? "straight" : "split 1->" + r.Feeds.Count) + " -> "
                        + string.Join(" | ", r.Feeds.Select((kv, i) => Mod(kv.Key) + " " + F(sameGroup.Sum(x => x.Feeds[i].Value))).ToArray()));
                else if (sameGroup.Count == 1)
                    sb.AppendLine("- " + Mod(r) + " " + N(r.Step.Item) + " " + F(r.OutRate) + "/min --" + how + "--> " + string.Join(" | ", r.Feeds.Select(kv => Mod(kv.Key) + " " + F(kv.Value)).ToArray()));
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Warnings / unproven");
        sb.AppendLine();
        var warn = new List<string>();
        foreach (var m in Modules)
        {
            foreach (string n in m.Notes.Distinct()) warn.Add(m.No.ToString("00", c) + ": " + n);
            if (m.Report != null) foreach (string w in m.Report.Warnings.Distinct()) warn.Add(m.No.ToString("00", c) + ": validator WARN " + w);
        }
        foreach (var r in Rows)
        {
            for (int i = 0; i < r.InItems.Length; i++) if (Math.Abs(r.InRates[i] - beltRate) < 1e-6) warn.Add(Mod(r) + ": input lane " + N(r.InItems[i]) + " at exactly " + F(beltRate) + "/min (no headroom)");
            if (Math.Abs(r.OutRate - beltRate) < 1e-6) warn.Add(Mod(r) + ": output lane at exactly " + F(beltRate) + "/min (no headroom)");
        }
        warn.Add("port pitch 2 with a corner right after a splitter output (c+2u tile) is unproven in game (modules-design 6.1); the kit itself has no splitters");
        warn.Add("_intro_ format copied from the user's Blueprint folders (BPBOOK:0,1,icons,0,text); the game's limit on its text length is not checked");
        warn.AddRange(Warnings);
        foreach (string w in warn.Distinct()) sb.AppendLine("- " + w);
        return sb.ToString();
    }

    /// Short console summary.
    public string Summary()
    {
        var sb = new StringBuilder();
        foreach (var m in Modules)
        {
            KitRow r = m.Proto;
            sb.AppendLine("  " + m.FileName + "  " + r.Count + " x " + N(r.Step.MachineItem) + ", out " + N(r.Step.Item) + " " + F(r.OutRate) + "/min -> " + FeedText(m)
                + (m.Layout != null ? ", " + m.W + "x" + m.H + ", " + m.Layout.All.Count + " buildings, " + m.Layout.Count(Kind.Tesla, 0) + " towers" : ", NOT BUILT")
                + (m.Report != null ? ", " + m.Report.Warnings.Count + " warn" : ""));
        }
        return sb.ToString();
    }
}
}
