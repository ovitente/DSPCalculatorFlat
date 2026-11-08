// Floorplan.cs - pattern-driven placement (docs\floorplan-design.md, "floorplan" row of the work split): ProductionPlan ->
// FpPlan in the canonical frame (block inputs at the south edge, product north, x east). No belts are laid here: the rows
// are built with the existing modules, stacked and placed into columns, and every connection is classified so that
// Bands.Lay can route it (Straight / Comb / Band / MachineToMachine). Oracle: the user's game-proven turret edit
// refs\engine-missile-turret-7.5-v1-useredit3.txt (analysis\user-edit-turret75.md rules 1-15).
//
// Steps (Build):
//   1. rows      one row per step part (lane split like the search composer: belt rate and Lanes.LanesFor), input lanes in
//                per-craft count order (user decision 2026-11-08), block inputs as tails, Flow N; the target step becomes the
//                final cell (engine\FinalCell.cs) under the same conditions as the composer, its rule-4 feeder is absorbed.
//   2. stacking  a producer row whose only consumer is one row goes directly under that row in one column when the
//                consumer has no block input lane (the producer's output lane continues straight into the consumer's input
//                lane; chains allowed: ore -> ingot -> gear). Everything else starts its own column.
//   3. order     final cell west; columns whose whole output goes to the final machine next (rule 5; the one feeding the
//                cell's E face is aligned lane-to-lane); then greedily the column with the largest flow to/from the placed
//                ones (ties by step order).
//   4. x         columns packed west -> east, width = fragment tiles (machines + own lanes), gap 0 (rule 1) except a 1-tile
//                corridor after the final cell when its N-face lane must enter past a taller neighbour.
//   5. links     Straight (stacked / aligned), MachineToMachine (absorbed feeder), Comb (block inputs: tails on the south
//                edge, pitch 1 per column), Band (everything else: one band lane per item above the columns; enclosed spans
//                get the lower lane).
// Lane flow: a lane fed from the north band must flow S inside its row. RowSpec has no per-lane flow in this branch yet
// (RowSpec.LaneFlows is added by the laneflow agent): rows are built with Flow N and every connection records the flow the
// target lane needs in FpConnection.TargetFlow; the assembly rebuilds the rows with those flows.
// Pure model code: no I/O, no console, no game DLL. C# 5.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

public static class Floorplan
{
    // ------------------------------------------------------------------ working types
    class RI
    {
        public Step Step; public int StepIndex, Part, Parts, Count; public double Share;
        public int[] InItems; public double[] InRates; public double OutRate;
        public string Name;
        public Fragment Frag;
        public FpRow Row;
        public Col Col;
        public bool Final, Absorbed;
        public bool HasRaw;                       // a block input lane (tail) belongs to this row
        public List<string> Notes = new List<string>();
    }

    class Ed
    {
        public RI From, To; public int Item; public double Rate;
        public bool M2M;                          // absorbed feeder -> final machine (no belt)
        public bool Straight;                     // stacked or lane-aligned: the producer lane continues into the consumer lane
    }

    class Col
    {
        public readonly List<RI> Rows = new List<RI>();   // south -> north
        public Rect Box = Rect.Empty;                     // column-local, then absolute after placement
        public int Order;                                 // creation order (step order)
        public bool Final;
        public int GapAfter;                              // free tiles east of the column
        public string Why = "";
        public FpColumn Fp;
        public RI Top { get { return Rows[Rows.Count - 1]; } }
    }

    static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }

    static Port FindPort(Fragment f, string name)
    {
        if (f == null) return null;
        foreach (Port p in f.Ports) if (p.Name == name) return p;
        return null;
    }

    // ------------------------------------------------------------------ entry
    /// ProductionPlan -> FpPlan (canonical frame). Never throws on plan problems: they go to FpPlan.Errors.
    public static FpPlan Build(GameData gd, BlockSpec spec, ProductionPlan plan)
    {
        var fp = new FpPlan();
        if (plan == null || plan.Steps.Count == 0) { fp.Errors.Add("floorplan: empty production plan"); return fp; }
        if (spec == null) { fp.Errors.Add("floorplan: no block spec"); return fp; }
        if (gd == null) { fp.Errors.Add("floorplan: no game data"); return fp; }
        var b = new Builder(gd, spec, plan, fp);
        try { b.Run(); }
        catch (NotImplementedException) { throw; }
        catch (Exception e) { fp.Errors.Add("floorplan: " + e.GetType().Name + ": " + e.Message); }
        return fp;
    }

    // ------------------------------------------------------------------ builder
    class Builder
    {
        readonly GameData gd; readonly BlockSpec spec; readonly ProductionPlan plan; readonly FpPlan fp;
        readonly Footprints foot;
        readonly double beltRate;
        readonly List<RI> rows = new List<RI>();
        readonly List<Ed> edges = new List<Ed>();
        readonly List<Col> cols = new List<Col>();
        FinalCell.Choice cellChoice; RI cellRow, cellDed;
        int[] faces = new int[3], dedLanes = new int[2];
        int finalIndex = -1;

        public Builder(GameData gd, BlockSpec spec, ProductionPlan plan, FpPlan fp)
        {
            this.gd = gd; this.spec = spec; this.plan = plan; this.fp = fp;
            foot = new Footprints(gd);
            double br = gd.BeltRate(spec.Tiers.Belt);
            beltRate = br > 0 ? br : double.MaxValue;
        }

        string N(int item) { return gd.ItemName(item); }
        void Err(string m) { fp.Errors.Add("floorplan: " + m); }
        void Note(string m) { fp.Notes.Add(m); }

        public void Run()
        {
            MakeRows();
            if (rows.Count == 0) { Err("no row could be made"); return; }
            MakeEdges();
            PickFinalCell();
            BuildAndStack();
            if (cols.Count == 0) { Err("no column could be placed"); return; }
            List<Col> order = OrderColumns();
            PlaceColumns(order);
            Fill(order);
            MakeConnections();
        }

        // ---------------------------------------------------------------- 1. rows
        /// Copy of Composer.Attempt.LanesNeeded (private there): lanes by belt rate and by decision 9 (machines per lane).
        int LanesNeeded(Step s)
        {
            double outRate = s.Outputs.Count > 0 ? s.Outputs[0].Rate : s.MaxOutputRate;
            double worst = Math.Max(outRate, s.Inputs.Count > 0 ? s.Inputs.Max(x => x.Rate) : 0);
            int byRate = Math.Max(1, (int)Math.Ceiling(worst / beltRate - 1e-9));
            return Math.Max(byRate, Lanes.LanesFor(gd, s, s.Machines, beltRate));
        }

        void MakeRows()
        {
            for (int si = 0; si < plan.Steps.Count; si++)
            {
                Step s = plan.Steps[si];
                if (s.Recipe == null) { Err("step " + N(s.Item) + " has no recipe (no row)"); continue; }
                if (s.Outputs.Count > 1) Err(s.Recipe.Name + " has byproducts (" + string.Join(", ", s.Outputs.Skip(1).Select(o => N(o.Item)).ToArray()) + "); byproduct lanes are not supported");
                // lane priority (user decision 2026-11-08, docs\pattern-catalog.md): the ingredient with the largest per-craft
                // count takes the near lane (in1 L1), the next the near lane of the other side, then the far lanes (L2)
                var ins = s.Inputs.Select((x, i) => new { x, i }).OrderByDescending(a => s.Recipe.InCount(a.x.Item)).ThenByDescending(a => a.x.Rate).ThenBy(a => a.i).Select(a => a.x).ToList();
                if (ins.Count == 0 || ins.Count > 4) { Err(s.Recipe.Name + " has " + ins.Count + " inputs; rows support 1-4 (no row)"); continue; }
                if (s.Machines < 1) { Err(s.Recipe.Name + " has no machines (no row)"); continue; }
                double outRate = s.Outputs.Count > 0 ? s.Outputs[0].Rate : s.MaxOutputRate;
                int parts = Math.Max(1, Math.Min(s.Machines, LanesNeeded(s)));
                for (int p = 0; p < parts; p++)
                {
                    int cnt = s.Machines / parts + (p < s.Machines % parts ? 1 : 0);
                    double share = cnt / (double)s.Machines;
                    var r = new RI
                    {
                        Step = s, StepIndex = si, Part = p, Parts = parts, Count = cnt, Share = share,
                        InItems = ins.Select(x => x.Item).ToArray(), InRates = ins.Select(x => x.Rate * share).ToArray(),
                        OutRate = outRate * share,
                        Name = N(s.Item) + (parts > 1 ? " #" + I(p + 1) : "")
                    };
                    r.HasRaw = r.InItems.Any(it => s.IsExternal(plan, it));
                    for (int i = 0; i < r.InRates.Length; i++)
                        if (r.InRates[i] > beltRate + 1e-9) Err("row " + r.Name + " lane " + N(r.InItems[i]) + " " + F(r.InRates[i]) + "/min exceeds the belt rate");
                    if (r.OutRate > beltRate + 1e-9) Err("row " + r.Name + " output " + F(r.OutRate) + "/min exceeds the belt rate");
                    rows.Add(r);
                }
                if (parts > 1) Note("rows: " + s.Recipe.Name + " split into " + parts + " lanes of " + string.Join("/", Enumerable.Range(0, parts).Select(p => I(s.Machines / parts + (p < s.Machines % parts ? 1 : 0))).ToArray()) + " machines (belt rate / machines per lane)");
            }
        }

        List<RI> RowsOf(int stepIndex) { return rows.Where(r => r.StepIndex == stepIndex).ToList(); }

        // ---------------------------------------------------------------- edges (producer part -> consumer part, block inputs)
        void MakeEdges()
        {
            for (int si = 0; si < plan.Steps.Count; si++)
            {
                Step s = plan.Steps[si];
                var prods = RowsOf(si);
                if (prods.Count == 0) continue;
                var cons = new List<RI>();
                foreach (int ci in s.ConsumerSteps.Distinct())
                {
                    if (ci < 0 || ci >= plan.Steps.Count) { Err("step " + N(s.Item) + " has consumer index " + ci + " outside the plan"); continue; }
                    if (plan.Steps[ci].IsExternal(plan, s.Item)) continue;   // BlockSpec.RawFor: that consumer takes it from a block input lane
                    cons.AddRange(RowsOf(ci));
                }
                if (cons.Count == 0) continue;   // block output (or every consumer takes it externally)
                if (s.Item == plan.TargetItem) Err("target item " + N(s.Item) + " is also consumed inside the block; not supported");
                int k = prods.Count, m = cons.Count;
                // pairing as in Composer.Attempt.MakeEdges (parallel copies): consumer j <- producer j % k, or several producers share one consumer
                for (int j = 0; j < m; j++)
                {
                    RI c = cons[j];
                    int idx = Array.IndexOf(c.InItems, s.Item);
                    double need = idx >= 0 ? c.InRates[idx] : 0;
                    if (k <= m) edges.Add(new Ed { From = prods[j % k], To = c, Item = s.Item, Rate = need });
                    else
                    {
                        var feeders = Enumerable.Range(0, k).Where(i => i % m == j).ToList();
                        foreach (int i in feeders) edges.Add(new Ed { From = prods[i], To = c, Item = s.Item, Rate = need / feeders.Count });
                    }
                }
            }
            // block inputs: one comb lane per external input lane of every row
            foreach (RI r in rows)
                for (int i = 0; i < r.InItems.Length; i++)
                    if (r.Step.IsExternal(plan, r.InItems[i])) edges.Add(new Ed { From = null, To = r, Item = r.InItems[i], Rate = r.InRates[i] });
            // cycle check (a step consuming its own output through other steps): topological plans never have one
            for (int si = 0; si < plan.Steps.Count; si++)
                foreach (int ci in plan.Steps[si].ConsumerSteps)
                    if (ci <= si && ci >= 0 && ci < plan.Steps.Count && !plan.Steps[ci].IsExternal(plan, plan.Steps[si].Item))
                        Err("cycle: " + N(plan.Steps[si].Item) + " feeds step " + ci + " (" + N(plan.Steps[ci].Item) + ") which comes before it in the plan");
        }

        // ---------------------------------------------------------------- final cell (same choice as the composer, FinalCell.cs)
        void PickFinalCell()
        {
            FinalCell.Choice c = FinalCell.Pick(gd, spec, plan, Dir.N);
            if (c == null) { Note("final cell: not used (FinalCell.Pick declined: target not a 1-machine assembler with L1 inputs / L2 depot, or rules off)"); return; }
            var fr = RowsOf(c.FinalIndex);
            if (fr.Count != 1 || fr[0].Count != 1) { Note("final cell: not used (target step has " + fr.Count + " rows)"); return; }
            RI f = fr[0], d = null;
            if (c.Dedicated != null)
            {
                var dr = RowsOf(c.DedicatedIndex);
                if (dr.Count == 1 && dr[0].Count == 1 && edges.Count(e => e.From == dr[0]) == 1 && edges.Any(e => e.From == dr[0] && e.To == f)) d = dr[0];
                else c.Dedicated = null;
            }
            if (c.Dedicated == null && f.InItems.Length > 3) { Note("final cell: not used (4 lanes and no machine for the S face)"); return; }
            if (d != null)
            {
                // absorb the rule-4 machine: its output edge becomes machine-to-machine, its input edges aim at the cell's rule-11 lanes
                Ed outE = edges.First(e => e.From == d);
                outE.M2M = true;
                d.Absorbed = true;   // its input edges keep To = d; d shares the cell's fragment (ports in:<item> on the rule-11 lane ends)
                var items = new List<int>(); var rates = new List<double>();
                for (int i = 0; i < f.InItems.Length; i++) if (f.InItems[i] != d.Step.Item) { items.Add(f.InItems[i]); rates.Add(f.InRates[i]); }
                items.AddRange(d.InItems); rates.AddRange(d.InRates);
                f.InItems = items.ToArray(); f.InRates = rates.ToArray();
                f.HasRaw = f.InItems.Any(it => f.Step.IsExternal(plan, it) || d.Step.IsExternal(plan, it));
            }
            cellChoice = c; cellRow = f; cellDed = d; finalIndex = c.FinalIndex;
            f.Final = true;
            // faces (copied from Composer.Attempt.FinalCellBuild): rule 5 lane (producer feeds only the final machine) on E,
            // the others by rate: W then N; the dedicated machine's lanes W then E (rule 11)
            var ded = d != null ? d.InItems.ToList() : new List<int>();
            var own = f.InItems.Where(it => !ded.Contains(it)).ToList();
            own = own.OrderBy(it => { Step p = plan.StepFor(it); return p != null && !f.Step.IsExternal(plan, it) && p.ConsumerSteps.Distinct().Count() == 1 && p.ConsumerSteps[0] == finalIndex ? 0 : 1; })
                     .ThenByDescending(it => f.InRates[Array.IndexOf(f.InItems, it)]).ToList();
            for (int i = 0; i < own.Count && i < 3; i++) faces[i] = own[i];
            for (int i = 0; i < ded.Count && i < 2; i++) dedLanes[i] = ded[i];
            Note(FinalCell.Note(c, gd));
            Note("final cell faces: E " + (faces[0] != 0 ? N(faces[0]) : "-") + " (rule 5 when its producer feeds only the final machine), W " + (faces[1] != 0 ? N(faces[1]) : "-") + ", N " + (faces[2] != 0 ? N(faces[2]) : "-")
                + (d != null ? "; S machine " + d.Step.Recipe.Name + " with lane ends W " + (dedLanes[0] != 0 ? N(dedLanes[0]) : "-") + " / E " + (dedLanes[1] != 0 ? N(dedLanes[1]) : "-") + " (rule 11; W/E order = per-craft order, not derived from the oracle)" : ""));
        }

        // ---------------------------------------------------------------- row fragments
        Fragment BuildRow(RI r, bool mirror)
        {
            if (r.Final) return BuildFinal(r);
            var rs = new RowSpec
            {
                Name = r.Name, MachineItem = r.Step.MachineItem, RecipeId = r.Step.Recipe.Id, Count = r.Count, Pitch = 0,
                InputItems = (int[])r.InItems.Clone(), InputRates = (double[])r.InRates.Clone(),
                OutputItem = r.Step.Item, OutputRate = r.OutRate, Flow = Dir.N, Mirror = mirror,
                BeltItem = spec.Tiers.Belt, SorterItem = spec.Tiers.Sorter, OutputLength = 0,
                LaneIcons = false,
                TailItems = r.InItems.Where(it => r.Step.IsExternal(plan, it)).ToArray(),
                Data = gd,
                FinalQuarters = 0, AlongEW = false, Safe60 = spec.Safe60,
                LabStackMax = spec.LabStackMax,
                CoatItems = Coaters.Coated(r.Step) && spec.ProliferatorItem != 0 ? (int[])r.InItems.Clone() : null,
                ProliferatorItem = spec.ProliferatorItem
            };
            Fragment f;
            try
            {
                f = Modules.MachineRow(rs);   // dispatches to SlotRows for labs / colliders
                // double row (pattern sheet) when it applies and is not wider than the plain row
                if (DoubleRows.Applies(rs, beltRate))
                {
                    var plainNotes = new List<string>(rs.Notes); var plainProblems = new List<string>(rs.Problems);
                    Fragment dbl = null; string why = null;
                    try { dbl = DoubleRows.DoubleRow(rs); } catch (InvalidOperationException e) { why = e.Message; }
                    if (dbl != null && dbl.Bounds(foot).Width <= f.Bounds(foot).Width) { f = dbl; r.Notes.Add("double row (not wider than the plain row)"); }
                    else { rs.Notes.Clear(); rs.Notes.AddRange(plainNotes); rs.Problems.Clear(); rs.Problems.AddRange(plainProblems); if (why != null) r.Notes.Add("double row not possible: " + why); }
                }
            }
            catch (NotImplementedException) { throw; }
            catch (Exception e) { Err("row " + r.Name + ": " + e.Message); return null; }
            if (f == null) { Err("row " + r.Name + " returned nothing"); return null; }
            foreach (string p in rs.Problems) Err("row " + r.Name + ": " + p);
            foreach (string n in rs.Notes) r.Notes.Add(n);
            double load = Composer.RowLoad(gd, r.Step, r.Count, r.OutRate);
            if (load > 1 + 1e-6) Err("row " + r.Name + " needs " + F(load * 100) + "% of its " + r.Count + " machines");
            foreach (Bld b in f.Blds) if (b.Kind == Kind.Machine) b.Load = Math.Min(1.0, load);
            foreach (int it in r.InItems) if (FindPort(f, "in:" + it) == null) { Err("row " + r.Name + " has no port in:" + it); return null; }
            if (FindPort(f, "out:" + r.Step.Item) == null) { Err("row " + r.Name + " has no port out:" + r.Step.Item); return null; }
            return f;
        }

        Fragment BuildFinal(RI r)
        {
            double fl = Composer.RowLoad(gd, r.Step, 1, r.OutRate);
            double dl = cellDed != null ? Composer.RowLoad(gd, cellDed.Step, 1, cellDed.OutRate) : 0;
            Fragment f;
            try { f = FinalCell.Build(gd, spec, cellChoice, faces, dedLanes, fl, dl); }
            catch (Exception e) { Err("final cell: " + e.Message); return null; }
            if (f == null) { Err("final cell not built"); return null; }
            if (fl > 1 + 1e-6) Err("final machine needs " + F(fl * 100) + "%");
            foreach (int it in r.InItems) if (FindPort(f, "in:" + it) == null) { Err("final cell has no port in:" + it); return null; }
            return f;
        }

        /// Copy of Composer.Attempt.MachinesCollide (private there; the Safe60 E-W variant is left to the assembly, which
        /// knows the final rotation): any machine of a overlaps a machine of b by the game colliders (gap < 0.05 tile).
        bool MachinesCollide(Fragment a, Fragment b)
        {
            foreach (Bld m in a.Blds.Where(x => x.Kind == Kind.Machine))
                foreach (Bld n in b.Blds.Where(x => x.Kind == Kind.Machine))
                {
                    if (gd.HasCollider(m.ItemId) && gd.HasCollider(n.ItemId)) { if (gd.ColliderGap(m, n, 1.0) < 0.05) return true; continue; }
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

        // ---------------------------------------------------------------- 2. stacking into columns
        void BuildAndStack()
        {
            foreach (RI r in rows)
            {
                if (r.Absorbed) continue;
                // candidate producers: already placed, top of their column, feeding exactly this row and nothing else
                Ed best = null;
                bool stackable = !r.HasRaw && !r.Final && !(Coaters.Coated(r.Step) && spec.ProliferatorItem != 0);
                if (stackable)
                    foreach (Ed e in edges.Where(e => e.To == r && e.From != null && !e.M2M))
                    {
                        RI p = e.From;
                        if (p.Frag == null || p.Col == null || p.Col.Top != p || p.Final) continue;
                        if (edges.Count(x => x.From == p && !x.M2M) != 1) continue;
                        if (best == null || e.Rate > best.Rate + 1e-9 || (Math.Abs(e.Rate - best.Rate) <= 1e-9 && p.StepIndex < best.From.StepIndex)) best = e;
                    }
                if (best != null)
                {
                    RI p = best.From; Col c = p.Col;
                    Port src = FindPort(p.Frag, "out:" + p.Step.Item);
                    Fragment pick = null; int pickDx = 0, pickScore = int.MaxValue; bool pickMirror = false;
                    foreach (bool mirror in new[] { false, true })
                    {
                        Fragment f = BuildRow(r, mirror);
                        if (f == null) continue;
                        Port ip = FindPort(f, "in:" + best.Item);
                        if (ip == null || ip.Dir != src.Dir) continue;
                        int dx = src.X - ip.X;
                        // the narrower column wins (rule 1 pitch), then the smaller shift
                        Rect fb = f.Bounds(foot);
                        int score = (Math.Max(c.Box.X1, fb.X1 + dx) - Math.Min(c.Box.X0, fb.X0 + dx) + 1) * 8 + Math.Abs(dx);
                        if (score < pickScore) { pick = f; pickDx = dx; pickScore = score; pickMirror = mirror; }
                    }
                    if (pick != null)
                    {
                        Rect pb = pick.Bounds(foot);
                        pick.Translate(pickDx, c.Box.Y1 + 1 - pb.Y0);
                        int widened = 0;
                        for (int guard = 0; guard < 4 && MachinesCollide(pick, p.Frag); guard++) { pick.Translate(0, 1); widened++; }
                        r.Frag = pick; r.Col = c;
                        c.Rows.Add(r); c.Box = c.Box.Union(pick.Bounds(foot));
                        best.Straight = true;
                        Note("stack: " + r.Name + " on " + p.Name + " (" + N(best.Item) + " " + F(best.Rate) + "/min, only consumer of that row; lane dx " + pickDx + (pickMirror ? ", mirrored" : "") + (widened > 0 ? ", gap " + widened + " for the machine colliders" : ", flush") + ")");
                        continue;
                    }
                    Note("stack: " + r.Name + " could not be aligned on " + p.Name + ", own column");
                }
                Fragment g = BuildRow(r, false);
                if (g == null) continue;
                Rect gb = g.Bounds(foot);
                g.Translate(-gb.X0, -gb.Y0);
                var nc = new Col { Order = cols.Count, Final = r.Final };
                nc.Rows.Add(r); nc.Box = g.Bounds(foot);
                r.Frag = g; r.Col = nc;
                cols.Add(nc);
            }
            if (cellDed != null && cellRow.Frag != null) { cellDed.Frag = cellRow.Frag; cellDed.Col = cellRow.Col; }
            foreach (RI r in rows) if (r.Frag == null && !r.Absorbed) Err("row " + r.Name + " has no fragment");
        }

        // ---------------------------------------------------------------- 3. column order
        bool WholeOutputToFinal(Col c)
        {
            if (cellRow == null || c.Final) return false;
            var outs = edges.Where(e => e.From != null && e.From.Col == c && !e.M2M).ToList();
            return outs.Count > 0 && outs.All(e => e.To == cellRow || e.To == cellDed);
        }

        double FlowBetween(Col c, HashSet<Col> placed)
        {
            double sum = 0;
            foreach (Ed e in edges)
            {
                if (e.From == null || e.M2M || e.From.Col == null || e.To.Col == null) continue;
                if ((e.From.Col == c && placed.Contains(e.To.Col)) || (e.To.Col == c && placed.Contains(e.From.Col))) sum += e.Rate;
            }
            return sum;
        }

        List<Col> OrderColumns()
        {
            var order = new List<Col>();
            var placed = new HashSet<Col>();
            Col fin = cols.FirstOrDefault(c => c.Final);
            if (fin != null) { fin.Why = "final cell (west, product tail north)"; order.Add(fin); placed.Add(fin); }
            else
            {
                // no final cell: the target row's column first (its product leaves north)
                Col t = cols.FirstOrDefault(c => c.Rows.Any(r => r.Step.Item == plan.TargetItem));
                if (t != null) { t.Why = "target row (west)"; order.Add(t); placed.Add(t); }
            }
            // rule 5: the column whose whole output goes to the final machine's E face sits next to it, lane to lane (useredit3
            // steel). Other single-consumer feeders of the final machine (motors, engines) reach it through the band and take
            // their place by flow like everybody else (useredit3: motors 4th, engines last).
            Col al = fin != null ? cols.FirstOrDefault(c => Aligned(fin, c)) : null;
            if (al != null) { al.Why = "rule 5: whole output goes to the final machine's E face, lane aligned"; order.Add(al); placed.Add(al); }
            // greedy: the unplaced column with the largest flow to / from the placed ones, ties by step order
            while (placed.Count < cols.Count)
            {
                Col pick = null; double pickFlow = -1;
                foreach (Col c in cols.Where(c => !placed.Contains(c)).OrderBy(c => c.Order))
                {
                    double f = FlowBetween(c, placed);
                    if (pick == null || f > pickFlow + 1e-9) { pick = c; pickFlow = f; }
                }
                pick.Why = pickFlow > 0 ? "largest flow to/from the placed columns: " + F(pickFlow) + "/min" : "no flow to the placed columns yet, step order";
                order.Add(pick); placed.Add(pick);
            }
            return order;
        }

        // ---------------------------------------------------------------- 4. x / y placement
        void Translate(Col c, int dx, int dy)
        {
            foreach (RI r in c.Rows) r.Frag.Translate(dx, dy);
            c.Box = new Rect(c.Box.X0 + dx, c.Box.Y0 + dy, c.Box.X1 + dx, c.Box.Y1 + dy);
        }

        /// The column whose top row feeds the final cell's E face and nothing else (rule 5 lane alignment candidate).
        bool Aligned(Col fin, Col c) { return fin != null && c != fin && faces[0] != 0 && c.Top.Step.Item == faces[0] && WholeOutputToFinal(c); }

        void PlaceColumns(List<Col> order)
        {
            int cursor = 0;
            int baseY = 1;   // CombY = 0 is the tail row; the lowest row tile sits right above it
            Col fin = order.Count > 0 && order[0].Final ? order[0] : null;
            Port finN = fin != null && faces[2] != 0 ? FindPort(fin.Top.Frag, "in:" + faces[2]) : null;
            var finGrid = new Grid(foot);
            // the lane-aligned column (rule 5) may reach lower than the cell: the pair sits so that its lowest tile is on baseY
            int finY = baseY;
            Col alc = fin != null ? order.FirstOrDefault(c => Aligned(fin, c)) : null;
            if (alc != null)
            {
                Port t0 = FindPort(fin.Top.Frag, "in:" + faces[0]), s0 = FindPort(alc.Top.Frag, "out:" + alc.Top.Step.Item);
                if (t0 != null && s0 != null && s0.Dir == t0.Dir) finY = baseY + Math.Max(0, fin.Box.Y0 - (alc.Box.Y0 + (t0.Y - s0.NextY)));
            }
            for (int i = 0; i < order.Count; i++)
            {
                Col c = order[i];
                bool done = false;
                if (Aligned(fin, c))
                {
                    // rule 5: the E-face feeder's output lane continues straight into the cell's E lane end (useredit3 steel x=5 -> (5,13))
                    Port tgt = FindPort(fin.Top.Frag, "in:" + faces[0]);
                    Port src = FindPort(c.Top.Frag, "out:" + c.Top.Step.Item);
                    if (tgt != null && src != null && src.Dir == tgt.Dir)
                    {
                        int dx = tgt.X - src.NextX, dy = tgt.Y - src.NextY;
                        Translate(c, dx, dy);
                        bool clear = true;
                        foreach (RI r in c.Rows) foreach (Bld b in r.Frag.Blds) { Rect tr = foot.TileRect(b); if (tr.IsEmpty) continue; if (!finGrid.IsFree(tr)) { clear = false; break; } }
                        if (clear && c.Box.X0 > fin.Box.X0)
                        {
                            foreach (Ed e in edges) if (e.From != null && e.From == c.Top && e.To == cellRow) e.Straight = true;
                            cursor = Math.Max(cursor, c.Box.X1 + 1 + c.GapAfter);
                            Note("column " + N(c.Top.Step.Item) + ": output lane aligned with the final cell's E lane end (rule 5), x " + c.Box.X0 + ".." + c.Box.X1 + " (shares the lane x " + tgt.X + " with the cell, tiles disjoint)");
                            done = true;
                        }
                        else
                        {
                            Translate(c, -dx, -dy);
                            Note("column " + N(c.Top.Step.Item) + ": lane alignment with the final cell's E face collides, placed adjacent with a band link instead");
                        }
                    }
                }
                if (!done)
                {
                    Translate(c, cursor - c.Box.X0, (c == fin ? finY : baseY) - c.Box.Y0);
                    // rule 1: no corridor between columns; the cell's N-face lane enters from the east over the neighbour,
                    // so the neighbour must stay below it or leave one tile
                    if (c == fin && finN != null && i + 1 < order.Count && !Aligned(fin, order[i + 1]) && baseY + order[i + 1].Box.Height - 1 >= finN.Y) { c.GapAfter = 1; Note("gap: 1 free tile east of the final cell for its N-face lane (the neighbour column is taller than the lane)"); }
                    cursor = c.Box.X1 + 1 + c.GapAfter;
                }
                if (c == fin) foreach (RI r in c.Rows) foreach (Bld b in r.Frag.Blds) finGrid.Occupy(b);
            }
            // safety: nothing below baseY (an alignment that failed its clearance check leaves the cell where it was planned)
            int minY = int.MaxValue;
            foreach (Col c in order) minY = Math.Min(minY, c.Box.Y0);
            if (minY < baseY) foreach (Col c in order) Translate(c, 0, baseY - minY);
            // columns that do not reach the comb row: their tails are extended down by the bands (Lanes.TailsToEdge style)
            foreach (Col c in order) if (c.Box.Y0 > baseY && c.Rows.Any(r => r.HasRaw)) Note("column " + c.Top.Name + " bottom y" + c.Box.Y0 + ": its block input lanes need " + (c.Box.Y0 - baseY) + " extra tail tile(s) down to the comb row");
        }

        // ---------------------------------------------------------------- FpPlan rows / columns
        void Fill(List<Col> order)
        {
            for (int i = 0; i < order.Count; i++)
            {
                Col c = order[i];
                var fc = new FpColumn { Index = i, X0 = c.Box.X0, Width = c.Box.Width, GapAfter = c.GapAfter };
                c.Fp = fc;
                foreach (RI r in c.Rows)
                {
                    Rect b = r.Frag.Bounds(foot);
                    var row = new FpRow { Name = r.Name, Step = r.Step, StepIndex = r.StepIndex, Frag = r.Frag, Column = fc, Y0 = b.Y0, Y1 = b.Y1, FinalCell = r.Final, Count = r.Count, Part = r.Part };
                    r.Row = row; fc.Rows.Add(row); fp.Rows.Add(row);
                }
                fp.Columns.Add(fc);
                Note("column " + i + " x" + c.Box.X0 + ".." + c.Box.X1 + ": " + string.Join(" / ", c.Rows.Select(r => r.Name).ToArray()) + " - " + c.Why);
                foreach (RI r in c.Rows) foreach (string n in r.Notes) Note("row " + r.Name + ": " + n);
            }
            if (cellDed != null && cellRow.Row != null)
            {
                var row = new FpRow { Name = cellDed.Name + " (in final cell)", Step = cellDed.Step, StepIndex = cellDed.StepIndex, Frag = cellRow.Frag, Column = cellRow.Row.Column, Y0 = cellRow.Row.Y0, Y1 = cellRow.Row.Y1, FinalCell = true, Absorbed = true, Count = 1, Part = 0 };
                cellDed.Row = row; fp.Rows.Add(row);
            }
            int top = int.MinValue;
            foreach (FpRow r in fp.Rows) top = Math.Max(top, r.Y1);
            fp.BandY0 = top + 1;
            fp.CombY = 0;
        }

        // ---------------------------------------------------------------- 5. connections
        void MakeConnections()
        {
            var conns = new List<FpConnection>();
            foreach (Ed e in edges)
            {
                if (e.To == null || e.To.Row == null) continue;
                var c = new FpConnection { Item = e.Item, Rate = e.Rate, From = e.From != null ? e.From.Row : null, To = e.To.Row };
                if (e.M2M) { c.Route = FpRoute.MachineToMachine; c.Slot = 0; c.TargetFlow = Dir.N; conns.Add(c); continue; }
                if (e.From != null && e.From.Row == null) continue;   // producer without a fragment (error reported)
                c.Target = FindPort(e.To.Frag, "in:" + e.Item);
                if (c.Target == null) { Err("no port in:" + e.Item + " on " + e.To.Name); continue; }
                if (e.From == null)
                {
                    c.Route = FpRoute.Comb;
                    c.TargetFlow = c.Target.Dir;   // a tail lane flows N; a rule-11 lane end keeps its own direction
                }
                else
                {
                    c.Source = FindPort(e.From.Frag, "out:" + e.Item);
                    if (c.Source == null) { Err("no port out:" + e.Item + " on " + e.From.Name); continue; }
                    bool straight = e.Straight && c.Source.X == c.Target.X && c.Source.Dir == c.Target.Dir && c.Target.Y > c.Source.Y;
                    c.Route = straight ? FpRoute.Straight : FpRoute.Band;
                    c.TargetFlow = e.To.Final || e.To.Absorbed ? c.Target.Dir : (straight ? Dir.N : Dir.S);
                    if (straight) c.Slot = 0;
                }
                conns.Add(c);
            }
            // band: one lane per item; an item whose span is enclosed by another's gets the lower lane (fewer crossings)
            var band = conns.Where(c => c.Route == FpRoute.Band).ToList();
            var span = new Dictionary<int, int[]>();
            foreach (FpConnection c in band)
            {
                int[] s; if (!span.TryGetValue(c.Item, out s)) { s = new[] { int.MaxValue, int.MinValue }; span[c.Item] = s; }
                s[0] = Math.Min(s[0], Math.Min(c.Source.X, c.Target.X)); s[1] = Math.Max(s[1], Math.Max(c.Source.X, c.Target.X));
            }
            // narrow spans first (enclosed -> lower); two items whose spans leave at least one free tile between them share a row
            var laneOf = new Dictionary<int, int>();
            var laneSpans = new List<List<int[]>>();
            foreach (int it in span.Keys.OrderBy(it => span[it][1] - span[it][0]).ThenBy(it => span[it][0]).ThenBy(it => it))
            {
                int[] s = span[it];
                int lane = -1;
                for (int l = 0; l < laneSpans.Count && lane < 0; l++)
                    if (laneSpans[l].All(o => o[1] + 2 <= s[0] || s[1] + 2 <= o[0])) lane = l;
                if (lane < 0) { lane = laneSpans.Count; laneSpans.Add(new List<int[]>()); }
                laneSpans[lane].Add(s);
                laneOf[it] = lane;
            }
            var sources = new List<Port>();
            foreach (FpConnection c in band)
            {
                c.Slot = laneOf[c.Item];
                int g = sources.IndexOf(c.Source);
                if (g < 0) { g = sources.Count; sources.Add(c.Source); }
                c.Group = g;
            }
            fp.BandLanes = laneSpans.Count;
            foreach (int it in laneOf.Keys.OrderBy(it => laneOf[it]).ThenBy(it => span[it][0]))
            {
                var tg = band.Where(c => c.Item == it).Select(c => c.To.Name).Distinct().ToArray();
                Note("band lane " + laneOf[it] + " (y" + (fp.BandY0 + laneOf[it]) + "): " + N(it) + " x" + span[it][0] + ".." + span[it][1] + " -> " + string.Join(", ", tg) + (tg.Length > 2 ? " (2-way fork + third consumer from the lane tail, rule 8)" : tg.Length == 2 ? " (one 2-way fork, rule 8)" : "")
                    + (laneSpans[laneOf[it]].Count > 1 ? " [row shared, spans disjoint]" : ""));
            }
            // rule 2 (not laid in this flat version): lanes whose only consumer is the final machine form one z-stacked bus row
            if (cellRow != null)
            {
                var bus = laneOf.Keys.Where(it => band.Where(c => c.Item == it).All(c => c.To == cellRow.Row)).ToList();
                if (bus.Count > 1) Note("rule 2: " + string.Join(", ", bus.Select(it => N(it)).ToArray()) + " all end at the final machine; z-stacked (0 / 0.5 / 1, later-joining higher) they are one bus row -> " + (fp.BandLanes - bus.Count + 1) + " band rows instead of " + fp.BandLanes + " (flat rows kept here)");
            }
            // comb: tails on the south edge, grouped by target column (west -> east), pitch 1 inside a group
            var comb = conns.Where(c => c.Route == FpRoute.Comb).ToList();
            var usedX = new HashSet<int>();
            foreach (var grp in comb.GroupBy(c => c.To.Column).OrderBy(g => g.Key.X0))
            {
                var lanes = grp.OrderBy(c => c.Target.X).ToList();
                int x0 = lanes[0].Target.X;
                if (lanes.Count > 1)
                {
                    // P12: tails adjacent, the lanes fan out along the edge (the lane whose tail is not under it turns along the edge)
                    foreach (FpConnection c in lanes) { if (c.Target.Dir == Dir.N) { x0 = c.Target.X; break; } }
                }
                int x = x0;
                foreach (FpConnection c in lanes)
                {
                    while (usedX.Contains(x)) x++;
                    c.Slot = x; usedX.Add(x);
                    if (c.Slot != c.Target.X || c.Target.Dir != Dir.N) Note("comb tail x" + c.Slot + " (" + N(c.Item) + " -> " + c.To.Name + "): lane at x" + c.Target.X + " flowing " + c.Target.Dir + ", the lane turns along the south edge (P12)");
                    x++;
                }
            }
            fp.CombTails = comb.Count;
            if (comb.Count > 0) Note("comb: " + comb.Count + " tail(s) on y" + fp.CombY + " at x " + string.Join(",", comb.OrderBy(c => c.Slot).Select(c => I(c.Slot)).ToArray()));
            Note("band: " + fp.BandLanes + " lane(s) from y" + fp.BandY0 + "; consumer lanes fed from the band flow S (FpConnection.TargetFlow; rows built with Flow N until RowSpec.LaneFlows lands)");
            fp.Connections.AddRange(conns);
        }
    }
}
}
