// Rules.cs - the user's 15 layout rules approved on 2026-11-02 (PLAN.md phase 4, "15 layout rules").
// Source: blueprint-tools\analysis\user-edit-turret75.md (Russian list at the top, evidence below); game-proven
// reference: blueprint-tools\refs\engine-missile-turret-7.5-v1-useredit3.txt (record ids cited below are of that file).
//
// Two parts:
//  - generative rules the engine applies (Rules.ApplyMargin = rule 14, Rules.PruneTowers = rule 13);
//  - Rules.Check: a layout-agnostic measurement of every rule that can be measured on a finished layout. It is the
//    acceptance oracle: the user's game-proven useredit3 passes every hard check (selftest "micro rules"), and the
//    engine's output is reported against the same checks (MakeBlock notes, "rules:" lines).
// Hook points: Composer.Compose calls ApplyMargin before placement and PruneTowers after Retower (marked "rules hook").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{
    /// One rule's result on a layout. Hard = the rule is measurable exactly and Ok tells whether it holds;
    /// not Hard = a metric only (Detail), never a failure.
    public class RuleResult
    {
        public int Rule; public bool Hard; public bool Ok = true; public string Detail = "";
        public override string ToString()
        {
            return "rule " + Rule + (Hard ? (Ok ? " ok" : " VIOLATED") : " metric") + ": " + Detail;
        }
    }

    public static class Rules
    {
        /// Switch for the generative rules (14 margin, 13 tower pruning). Off = the pre-rules engine behaviour.
        public static bool Enabled = true;
        /// Rule 14 inside Composer.Compose (only when BlockSpec.AddMargin: the CLI solver's plans; injected DSPCalculator
        /// plans keep the user's counts, LayoutOptions.AddMargin false). On since feat-rules-pack: the grouped row split
        /// builds Step.Machines (engine\Packing.cs GroupCounts). ApplyMargin / CheckMargin also work on a plan on their own.
        public static bool MarginInCompose = true;

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        static string At(double x, double y) { return "(" + F(x) + "," + F(y) + ")"; }

        // ================================================================ rule 14: margin at the bottleneck
        /// Rule 14 (user-edit-turret75.md R14 + section 4/5): a step that runs at exactly its machine count (zero margin)
        /// and feeds two or more consumer steps (its lane is split) gets one more machine; nothing is taken from another
        /// chain (every step keeps at least ceil(exact)). useredit3: Magnetic Coil 1.0 -> 2 (2 x 90 = 180 >= 90, record
        /// #53 + #512), Copper Ingot 3.0 -> 4 (x=30, 3 consumers), Iron Ingot 6.75 -> 7 (unchanged, already has margin).
        /// The user's 4th Engine (single consumer) is a buffer the rule does not add (analysis section 4).
        /// Returns the notes of the changed steps.
        public static List<string> ApplyMargin(ProductionPlan plan)
        {
            var notes = new List<string>();
            if (plan == null) return notes;
            foreach (Step s in plan.Steps)
            {
                if (s.Recipe == null || s.Machines <= 0) continue;
                if (Math.Abs(s.MachinesExact - s.Machines) > 1e-6) continue;   // already has margin from rounding up
                if (s.ConsumerSteps.Distinct().Count() < 2) continue;          // one consumer: exact is enough (contract rule 17)
                double per = s.MaxOutputRate / s.Machines;
                s.Machines++;
                s.MaxOutputRate = per * s.Machines;
                notes.Add("rule 14 margin: " + s.Recipe.Name + " " + F(s.MachinesExact) + " machines exact feeds " + s.ConsumerSteps.Distinct().Count()
                    + " consumers -> " + s.Machines + " machines");
            }
            return notes;
        }

        /// Rule 14 as a check on a plan: no step at zero margin that feeds two or more consumers.
        public static RuleResult CheckMargin(ProductionPlan plan)
        {
            var r = new RuleResult { Rule = 14, Hard = true };
            var bad = new List<string>();
            foreach (Step s in plan.Steps)
                if (s.Recipe != null && s.ConsumerSteps.Distinct().Count() >= 2 && s.Machines - s.MachinesExact < 1e-6)
                    bad.Add(s.Recipe.Name + " " + s.Machines + " = " + F(s.MachinesExact) + " exact");
            r.Ok = bad.Count == 0;
            r.Detail = r.Ok ? "every split-fed step has margin" : "zero margin on a split lane: " + string.Join(", ", bad.ToArray());
            return r;
        }

        // ================================================================ rule 13: minimum towers
        static List<double[]> Points(Layout l)
        {
            var pts = new List<double[]>();
            foreach (Bld b in l.All) if (b.Kind == Kind.Machine || b.Kind == Kind.Sorter) pts.AddRange(GameData.PowerPoints(b));
            return pts;
        }

        static bool Covered(List<double[]> pts, List<Bld> towers)
        {
            double c2 = GameData.GameTeslaCoverTiles * GameData.GameTeslaCoverTiles;
            foreach (double[] p in pts)
                if (!towers.Any(t => (t.X - p[0]) * (t.X - p[0]) + (t.Y - p[1]) * (t.Y - p[1]) <= c2)) return false;
            return true;
        }

        static bool Connected(List<Bld> towers)
        {
            if (towers.Count <= 1) return true;
            double l2 = GameData.GameTeslaLinkTiles * GameData.GameTeslaLinkTiles;
            var seen = new HashSet<Bld> { towers[0] };
            var q = new Queue<Bld>(); q.Enqueue(towers[0]);
            while (q.Count > 0)
            {
                Bld a = q.Dequeue();
                foreach (Bld b in towers)
                    if (!seen.Contains(b) && (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) <= l2) { seen.Add(b); q.Enqueue(b); }
            }
            return seen.Count == towers.Count;
        }

        /// Towers whose removal keeps every power point covered (game radius 8.36) and the tower network connected.
        public static List<Bld> RedundantTowers(Layout l)
        {
            var towers = l.All.Where(b => b.Kind == Kind.Tesla).ToList();
            var pts = Points(l);
            var res = new List<Bld>();
            foreach (Bld t in towers)
            {
                var rest = towers.Where(x => x != t).ToList();
                if (rest.Count > 0 && Covered(pts, rest) && Connected(rest)) res.Add(t);
            }
            return res;
        }

        /// Rule 13 (R13; useredit2/3: 6 towers, each with unique coverage): remove redundant towers one by one, the tower
        /// covering the fewest points first, while coverage and connectivity hold. Returns the number removed.
        public static int PruneTowers(Layout l) { return PruneTowers(l, null); }

        /// removedOut (optional) receives the removed towers so a caller can put them back (Layout.Add) when its own
        /// validation of the pruned layout fails.
        public static int PruneTowers(Layout l, List<Bld> removedOut)
        {
            int removed = 0;
            var pts = Points(l);
            double c2 = GameData.GameTeslaCoverTiles * GameData.GameTeslaCoverTiles;
            for (;;)
            {
                var red = RedundantTowers(l);
                if (red.Count == 0) break;
                Bld t = red.OrderBy(x => pts.Count(p => (x.X - p[0]) * (x.X - p[0]) + (x.Y - p[1]) * (x.Y - p[1]) <= c2)).First();
                l.All.Remove(t);
                l.Grid.Release(t);
                if (removedOut != null) removedOut.Add(t);
                removed++;
            }
            return removed;
        }

        // ================================================================ rule 7: inline output buffer (cell)
        /// Rule 7 cell (R7, useredit3, all numbers relative to the final machine, assembler yaw 0, output face N):
        /// turret #274 (3,12) -> #530 L2 (0,+0.875)->(0,+3.205), machine slot 1 -> depot slot 7 -> Depot Mk.I #27 (0,+4)
        /// -> #382 L1 (0,+4.795)->(0,+6), depot slot 1 -> belts #381 #272 #42 (0,+6)..(0,+8) flowing N, last one the chain
        /// end (block output port "out:<item>"). Only this orientation is game-proven; other machine yaws / faces return
        /// null (not derived). Not yet placed by the Composer (its final row still ends in an output lane + side buffer).
        public static Fragment InlineDepot(Bld fin, int sorterItem, int beltItem, int carries)
        {
            if (fin == null || fin.Kind != Kind.Machine || Math.Abs(Dirs.Norm(fin.Yaw + 0.5) - 0.5) > 1.0) return null;
            double x = fin.X, y = fin.Y;
            var f = new Fragment("INLINE_DEPOT");
            Bld dep = f.Add(Bld.Depot(x, y + 4));
            Bld s1 = f.Add(Bld.Sorter(sorterItem, x, y + 0.875, x, y + 3.205, 0, fin, dep, 1, 2, 0, carries));
            s1.OutputToSlot = 7;   // depot slot 7 (record #530)
            var belts = new List<Bld>();
            for (int i = 0; i < 3; i++) belts.Add(f.Add(Bld.Belt(beltItem, x, y + 6 + i, 0, carries, "out:" + carries)));
            for (int i = 0; i < 2; i++) Layout.Link(belts[i], belts[i + 1], 1);
            Bld s2 = f.Add(Bld.Sorter(sorterItem, x, y + 4.795, x, y + 6, 0, dep, belts[0], 1, 1, 0, carries));   // depot slot 1 (#382)
            foreach (Bld b in f.Blds) b.Role = "rule 7 inline buffer";
            f.AddPort(new Port { Name = "out:" + carries, Kind = PortKind.Out, X = (int)Math.Round(x), Y = (int)Math.Round(y + 8), Dir = Dir.N, Item = carries, Belt = belts[2], Chain = belts[2].Chain });
            return f;
        }

        // ================================================================ checks on a finished layout
        /// Face of a machine a sorter end touches (dominant axis of the end point relative to the machine centre).
        static Dir Face(Bld machine, double x, double y)
        {
            double dx = x - machine.X, dy = y - machine.Y;
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? Dir.E : Dir.W;
            return dy >= 0 ? Dir.N : Dir.S;
        }

        /// Belt lanes: connected components of belts joined by Output links (splitters and side-joins included, since a
        /// side-join is the same lane downstream). Returns belt -> lane id.
        static Dictionary<Bld, int> Lanes(Layout l)
        {
            var parent = new Dictionary<Bld, Bld>();
            Func<Bld, Bld> find = null;
            find = b => { Bld p = parent[b]; if (p == b) return b; Bld r = find(p); parent[b] = r; return r; };
            foreach (Bld b in l.All) if (b.Kind == Kind.Belt) parent[b] = b;
            foreach (Bld b in l.All)
                if (b.Kind == Kind.Belt && b.Output != null && b.Output.Kind == Kind.Belt && parent.ContainsKey(b.Output))
                {
                    Bld x = find(b), y = find(b.Output);
                    if (x != y) parent[x] = y;
                }
            var id = new Dictionary<Bld, int>(); var ids = new Dictionary<Bld, int>();
            foreach (Bld b in parent.Keys.ToList())
            {
                Bld r = find(b); int n;
                if (!ids.TryGetValue(r, out n)) { n = ids.Count; ids[r] = n; }
                id[b] = n;
            }
            return id;
        }

        static int ChainLength(Bld start)
        {
            int n = 0; var seen = new HashSet<Bld>();
            for (Bld b = start; b != null && b.Kind == Kind.Belt && seen.Add(b); b = b.Output) n++;
            return n;
        }

        /// Measures the rules on a layout. finalRecipe = recipe id of the block's final machine (Missile Turret 129);
        /// plan (optional) adds the plan-level rules (4 needs it to know which steps feed only the final machine, 14).
        public static List<RuleResult> Check(Layout l, ProductionPlan plan, int finalRecipe)
        {
            var res = new List<RuleResult>();
            var machines = l.All.Where(b => b.Kind == Kind.Machine).ToList();
            var sorters = l.All.Where(b => b.Kind == Kind.Sorter).ToList();
            var lanes = Lanes(l);
            Bld fin = machines.FirstOrDefault(m => m.RecipeId == finalRecipe);

            // rule 1: column pitch (metric) - distinct machine centre x (machine columns), largest gap between neighbours
            {
                var xs = machines.Select(m => (int)Math.Round(m.X)).Distinct().OrderBy(x => x).ToList();
                int maxGap = 0; for (int i = 1; i < xs.Count; i++) maxGap = Math.Max(maxGap, xs[i] - xs[i - 1]);
                Rect bx = l.Bounds();
                res.Add(new RuleResult { Rule = 1, Detail = xs.Count + " machine columns, largest column pitch " + maxGap + " (target 5-6), area " + bx.Width + "x" + bx.Height });
            }
            // rules 2/3: bus tiers (metric) - raised belt nodes by level
            {
                int z05 = l.All.Count(b => b.Kind == Kind.Belt && b.Z > 0.25 && b.Z < 0.75), z1 = l.All.Count(b => b.Kind == Kind.Belt && b.Z >= 0.75);
                res.Add(new RuleResult { Rule = 2, Detail = "stacked bus: " + z05 + " belt nodes at z 0.5, " + z1 + " at z 1 (useredit3: one edge bus, 3 lanes on one row)" });
            }
            if (fin == null)
            {
                res.Add(new RuleResult { Rule = 6, Hard = true, Ok = false, Detail = "no final machine with recipe " + finalRecipe });
                return res;
            }
            var finIn = sorters.Where(s => s.Output == fin).ToList();
            var m2m = finIn.Where(s => s.Input != null && s.Input.Kind == Kind.Machine).ToList();

            // rule 4: a single machine whose only consumer is the final machine hands over by direct sorters (useredit3
            // circuit #374 (3,7) -> turret #274 (3,12): L3 #397 + #408, 2 x 60 = 120 >= 90/min)
            if (plan != null)
            {
                Step fs = plan.Steps.FirstOrDefault(s => s.Recipe != null && s.Recipe.Id == finalRecipe);
                int fi = fs != null ? plan.Steps.IndexOf(fs) : -1;
                var only = plan.Steps.Where(s => s.Machines == 1 && s.ConsumerSteps.Count > 0 && s.ConsumerSteps.All(c => c == fi)).ToList();
                var r4 = new RuleResult { Rule = 4, Hard = true };
                var bad = new List<string>();
                foreach (Step s in only)
                {
                    bool direct = m2m.Any(x => x.Input.RecipeId == s.Recipe.Id);
                    if (!direct) bad.Add(s.Recipe.Name);
                }
                r4.Ok = bad.Count == 0;
                r4.Detail = only.Count == 0 ? "no single machine feeds only the final machine" : r4.Ok ? only.Count + " machine(s) hand over machine-to-machine (" + m2m.Count + " direct sorters)" : "belt instead of a direct handover: " + string.Join(", ", bad.ToArray());
                res.Add(r4);
            }

            // rule 6: one lane per face, all belt input sorters L1 (useredit3 turret #274: W #405, N #428, E #430 L1; S = M2M)
            {
                var r6 = new RuleResult { Rule = 6, Hard = true };
                var bad = new List<string>();
                var perFace = new Dictionary<Dir, HashSet<string>>();
                foreach (Bld s in finIn)
                {
                    Dir f = Face(fin, s.X2, s.Y2);
                    string src = s.Input == null ? "?" : s.Input.Kind == Kind.Belt && lanes.ContainsKey(s.Input) ? "lane" + lanes[s.Input] : "m" + s.Input.X + "," + s.Input.Y;
                    if (!perFace.ContainsKey(f)) perFace[f] = new HashSet<string>();
                    perFace[f].Add(src);
                    if (s.Input != null && s.Input.Kind == Kind.Belt && s.SorterLength != 1) bad.Add("L" + s.SorterLength + " input at " + At(s.X, s.Y));
                }
                foreach (var kv in perFace) if (kv.Value.Count > 1) bad.Add(kv.Value.Count + " lanes on face " + kv.Key);
                r6.Ok = bad.Count == 0;
                r6.Detail = finIn.Count + " input sorters on " + perFace.Count + " face(s)" + (r6.Ok ? "" : ": " + string.Join(", ", bad.ToArray()));
                res.Add(r6);
            }

            // rule 7: final machine -> depot by a direct sorter (#530 L2), depot -> belt by one L1 sorter (#382), output tail
            // of 3 belts ((3,18)..(3,20) in useredit3)
            {
                var r7 = new RuleResult { Rule = 7, Hard = true };
                Bld toDepot = sorters.FirstOrDefault(s => s.Input == fin && s.Output != null && s.Output.Kind == Kind.Storage);
                Bld fromDepot = toDepot == null ? null : sorters.FirstOrDefault(s => s.Input == toDepot.Output && s.Output != null && s.Output.Kind == Kind.Belt);
                int outs = toDepot == null ? 0 : sorters.Count(s => s.Input == toDepot.Output);
                int tail = fromDepot == null ? 0 : ChainLength(fromDepot.Output);
                r7.Ok = toDepot != null && fromDepot != null && outs == 1 && fromDepot.SorterLength == 1 && tail <= 3;
                r7.Detail = toDepot == null ? "no direct final machine -> depot sorter (depot " + (l.All.Any(b => b.Kind == Kind.Storage) ? "beside the output lane" : "missing") + ")"
                    : "final -> depot L" + toDepot.SorterLength + ", depot -> " + outs + " sorter(s)" + (fromDepot != null ? " L" + fromDepot.SorterLength + ", tail " + tail + " belts" : "");
                res.Add(r7);
            }

            // rule 8: splitters have at most 2 outputs
            {
                // metric, not hard: the user's own game-proven useredit3 keeps one 3-way splitter (copper, (28,17) #448); the rule
                // applies where a third consumer can take from the lane tail (iron (14,23) #13 and coil (26,20) #259 are 2-way)
                var r8 = new RuleResult { Rule = 8, Hard = false };
                var spl = l.All.Where(b => b.Kind == Kind.Splitter).ToList();
                var bad = new List<string>();
                foreach (Bld sp in spl)
                {
                    int outs = l.All.Count(b => b.Kind == Kind.Belt && b.Input == sp);
                    if (outs == 0) outs = l.All.Count(b => b.Kind == Kind.Belt && b.Output != sp && Math.Abs(b.X - sp.X) < 0.3 && Math.Abs(b.Y - sp.Y) < 0.3) - 1;
                    if (outs > 2) bad.Add(outs + "-way at " + At(sp.X, sp.Y));
                }
                r8.Ok = bad.Count == 0;
                r8.Detail = spl.Count + " splitter(s)" + (r8.Ok ? ", none with 3 outputs" : ", " + bad.Count + " with 3 outputs: " + string.Join(", ", bad.ToArray()));
                res.Add(r8);
            }

            // rule 12: no raw input lane that feeds a single machine (useredit3: iron ore x=10 -> 7 smelters, x=26 -> 3 magnet
            // smelters, copper ore x=32 -> 4 smelters; engine v1 had an ore lane + smelter for the gear alone)
            {
                var r12 = new RuleResult { Rule = 12, Hard = true };
                var icons = l.All.Where(b => b.Kind == Kind.Belt && b.Parameters != null && b.Parameters.Length == 2 && b.Parameters[0] > 0 && lanes.ContainsKey(b)).ToList();
                var bad = new List<string>();
                foreach (Bld ic in icons)
                {
                    int lane = lanes[ic];
                    int pick = sorters.Count(s => s.Input != null && s.Input.Kind == Kind.Belt && lanes.ContainsKey(s.Input) && lanes[s.Input] == lane && s.Output != null && s.Output.Kind == Kind.Machine);
                    // feat-rules-pack: only when another input lane of the same item exists (a main line to take it from);
                    // the only lane of an item (e.g. one copper smelter for one circuit) is not a separate small input
                    bool other = icons.Any(o => o != ic && o.Parameters[0] == ic.Parameters[0] && lanes[o] != lane);
                    if (pick == 1 && other) bad.Add("lane at " + At(ic.X, ic.Y) + " feeds 1 machine");
                }
                r12.Ok = bad.Count == 0;
                r12.Detail = icons.Count + " input lane(s)" + (r12.Ok ? ", none for a single machine" : ": " + string.Join(", ", bad.ToArray()));
                res.Add(r12);
            }

            // rule 13: no redundant tower
            {
                var red = RedundantTowers(l);
                res.Add(new RuleResult { Rule = 13, Hard = true, Ok = red.Count == 0, Detail = l.Count(Kind.Tesla, 0) + " towers" + (red.Count == 0 ? ", each needed" : ", redundant: " + string.Join(" ", red.Select(t => At(t.X, t.Y)).ToArray())) });
            }

            if (plan != null) res.Add(CheckMargin(plan));

            // rule 9 (metric): 2-input assemblers with both input faces opposite the output face (useredit3 engines x=36:
            // inputs E, output W)
            {
                int n = 0, ok = 0;
                foreach (Bld m in machines)
                {
                    var ins = sorters.Where(s => s.Output == m && s.Input != null && s.Input.Kind == Kind.Belt).ToList();
                    var outs = sorters.Where(s => s.Input == m && s.Output != null && s.Output.Kind == Kind.Belt).ToList();
                    if (ins.Count != 2 || outs.Count != 1) continue;
                    n++;
                    Dir of = Face(m, outs[0].X, outs[0].Y);
                    if (ins.All(s => Face(m, s.X2, s.Y2) == Dirs.Opposite(of))) ok++;
                }
                res.Add(new RuleResult { Rule = 9, Detail = ok + " of " + n + " 2-input machines take both inputs on the face opposite their output" });
            }
            return res;
        }

        /// Short report lines (MakeBlock notes).
        public static List<string> Report(List<RuleResult> rs)
        {
            return rs.Select(r => "rules: " + r).ToList();
        }
    }
}
