// FinalCell.cs - the final machine's neighbourhood per the user's approved layout rules 4, 5, 6, 7, 9, 11
// (blueprint-tools\analysis\user-edit-turret75.md). Every building position is copied from the user's game-proven
// blueprint refs\engine-missile-turret-7.5-v1-useredit3.txt (record ids below), relative to the final machine
// (turret #274 at (3,12), Assembling Machine yaw 0, output face N):
//
//   rule 4  dedicated machine #374 (circuit) at (0,-5) yaw 90, machine-to-machine sorters L3 #397 (0.803,-4.125)->
//           (0.801,-0.874) slots 11->6 and #408 (-0.803,-4.125)->(-0.801,-0.874) slots 9->8: 2 x L3, no belt.
//   rule 11 the dedicated machine takes both lanes from their dead ends on its S face: belt (-1,-7) flowing E ->
//           L1 #270 (-1,-7)->(-0.804,-5.874) slot 5; belt (1,-7) flowing W -> L1 #377 (1,-7)->(0.804,-5.874) slot 3.
//   rule 6  one lane per face, all L1: W belt (-2,0) flowing S -> #405 (-2,0.001)->(-0.88,0) slot 10; N belt (1,2)
//           flowing W -> #428 (0.799,2)->(0.8,0.875) slot 2; E belt (2,1) flowing N -> #430 (2,0.796)->(0.88,0.795)
//           slot 3; S = the dedicated machine (rule 4). Every lane ends on its pickup tile (dead end).
//   rule 5  a lane whose producer feeds only the final machine takes the E face: its lane runs north on x=+2 and ends
//           at (2,1) right at the final machine (useredit3 steel lane x=5 ends at (5,13)).
//   rule 7  output N: Rules.InlineDepot (#530 L2 -> Depot Mk.I #27 -> #382 L1 -> 3-tile tail, the last tile is the
//           block output port; it stays inside the block like the user's (3,18)).
//   rule 9  the output takes the face no input uses (N centre slot 1; the N lane uses the N-east slot 2).
//
// Other orientations: the whole cell (or the inline depot alone, InlineDepot) is turned rigidly by quarter turns
// with Composer.RotateLayout, the same rule the game applies to a pasted blueprint (machines, sorters, belts turn;
// Depot Mk.I stays at yaw 0 and its slots shift by 3 per quarter). Only yaw 0 / output N is game-proven itself.
//
// Composer integration (marked "FINALCELL HOOK" in Composer.cs): after MakeEdges the dedicated machine's row is
// absorbed into the final row (its input edges retargeted, its output edge dropped); the final row is built by this
// cell instead of Modules.MachineRow, never stacked on a producer line, and its product port is the inline depot's
// tail (no route to the edge, no side DEPOT_BUFFER). The Validator accepts machine->machine and machine->depot
// sorters (hooks in Validator.cs, implemented at the end of this file).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{
    public static class FinalCell
    {
        /// Switch for the final cell (Composer). Off = the final machine is an ordinary MachineRow.
        public static bool Enabled = true;

        /// What the Composer builds for the final machine.
        public class Choice
        {
            public Step Final; public int FinalIndex;
            public Step Dedicated; public int DedicatedIndex = -1;   // rule 4 machine (null = none)
            public int OutQuarters;                                   // canonical cell (out N) turned clockwise
            public string Why = "";
        }

        static bool IsAsm(int item) { return item == Ids.AsmMk1 || item == Ids.AsmMk2 || item == Ids.AsmMk3; }

        static double FullOut(GameData gd, Step s)
        {
            if (s.Recipe == null || s.Recipe.TimeSec <= 0) return 0;
            double speed = s.Speed > 0 ? s.Speed : gd.Speed(s.MachineItem);
            return s.Recipe.OutCount(s.Item) * 60.0 * speed / s.Recipe.TimeSec;
        }

        static double FullIn(GameData gd, Step s, int item)
        {
            if (s.Recipe == null || s.Recipe.TimeSec <= 0) return 0;
            double speed = s.Speed > 0 ? s.Speed : gd.Speed(s.MachineItem);
            return s.Recipe.InCount(item) * 60.0 * speed / s.Recipe.TimeSec;
        }

        /// Steps whose only consumer is step fi.
        static bool OnlyFeeds(Step s, int fi) { var c = s.ConsumerSteps.Distinct().ToList(); return c.Count == 1 && c[0] == fi; }

        /// Decides whether the final machine gets the cell. outEdge = product edge in the canonical frame (inputs S).
        public static Choice Pick(GameData gd, BlockSpec spec, ProductionPlan plan, Dir outEdge)
        {
            if (!Enabled || !Rules.Enabled || gd == null || plan == null || spec == null) return null;
            if (outEdge == Dir.S) return null;
            int fi = plan.Steps.FindIndex(s => s.Item == plan.TargetItem && s.Recipe != null);
            if (fi < 0) return null;
            Step f = plan.Steps[fi];
            if (f.Machines != 1 || f.Outputs.Count != 1 || !IsAsm(f.MachineItem) || f.Inputs.Count < 1 || f.Inputs.Count > 4) return null;
            int sorter = spec.Tiers.Sorter;
            double l1 = gd.SorterRate(sorter, 1), l3 = gd.SorterRate(sorter, 3);
            double l2d = gd.SorterRate(Lanes.DepotSorter(sorter), 2);
            if (l2d <= 0 || FullOut(gd, f) > l2d + 1e-9) return null;                    // rule 7: final -> depot L2
            foreach (ItemRate ir in f.Inputs) if (FullIn(gd, f, ir.Item) > l1 + 1e-9) return null;   // rule 6: L1 inputs
            var fin = f.Inputs.Select(x => x.Item).ToList();
            Step ded = null; int di = -1;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                Step s = plan.Steps[i];
                if (i == fi || s.Recipe == null || s.Machines != 1 || s.Outputs.Count != 1 || !IsAsm(s.MachineItem) || !OnlyFeeds(s, fi)) continue;
                if (!fin.Contains(s.Item) || s.Inputs.Count < 1 || s.Inputs.Count > 2) continue;
                if (s.Inputs.Any(x => fin.Contains(x.Item))) continue;                // one in:<item> port per item
                if (l3 <= 0 || FullOut(gd, s) / 2 > l3 + 1e-9) continue;              // rule 4: 2 x L3 at full speed
                if (s.Inputs.Any(x => FullIn(gd, s, x.Item) > l1 + 1e-9)) continue;    // rule 11: L1 from the dead ends
                if (ded == null || s.Outputs[0].Rate > ded.Outputs[0].Rate) { ded = s; di = i; }
            }
            int faces = fin.Count - (ded != null ? 1 : 0);
            if (faces > 3) return null;   // a 4th lane on the S face is not proven (useredit3 S = machine-to-machine)
            var c = new Choice { Final = f, FinalIndex = fi, Dedicated = ded, DedicatedIndex = di };
            c.OutQuarters = outEdge == Dir.N ? 0 : outEdge == Dir.E ? 1 : 3;
            return c;
        }

        // ------------------------------------------------------------------------------------------ geometry
        static Bld Sorter(int item, double sx, double sy, double ex, double ey, double yaw, Bld from, Bld to, int len, int carries)
        {
            Bld s = Bld.Sorter(item, sx, sy, ex, ey, yaw, from, to, 0, len, 0, carries);
            s.Yaw = yaw; s.Yaw2 = yaw;
            return s;
        }

        /// Belt -> machine L1 sorter with the slot from the game slot table (Geo.SlotAt) and the record's belt offset.
        static Bld InSorter(int item, Bld belt, Bld m, double sx, double sy, double ex, double ey, int inOff, int carries, int recordSlot)
        {
            double yaw = Dirs.Norm(Math.Atan2(ex - sx, ey - sy) * 180.0 / Math.PI);
            Bld s = Sorter(item, sx, sy, ex, ey, yaw, belt, m, 1, carries);
            Dir travel = Math.Abs(ex - sx) >= Math.Abs(ey - sy) ? (ex > sx ? Dir.E : Dir.W) : (ey > sy ? Dir.N : Dir.S);
            int slot = Geo.SlotAt(m.Yaw, ex - m.X, ey - m.Y, Dirs.Opposite(travel));
            if (slot != recordSlot) throw new InvalidOperationException("final cell: slot " + slot + " vs record slot " + recordSlot);
            s.OutputToSlot = slot; s.InputFromSlot = -1; s.InputOffset = inOff;
            return s;
        }

        static Port LaneEnd(Fragment f, int item, int beltItem, int x, int y, Dir flow, string role)
        {
            Bld b = f.Add(Bld.Belt(beltItem, x, y, Dirs.Yaw(flow), item, "final:" + item));
            b.Role = role;
            return f.AddPort(new Port { Name = "in:" + item, Kind = PortKind.In, X = x, Y = y, Dir = flow, Item = item, Belt = b, Chain = b.Chain });
        }

        /// The canonical cell (final machine at (0,0) yaw 0, output N), then turned by c.OutQuarters.
        /// faceItems: items for the faces E, W, N (0 = face unused); dedItems: the dedicated machine's lanes on its
        /// W and E side (0 = unused). Returns null when an item list does not fit.
        public static Fragment Build(GameData gd, BlockSpec spec, Choice c, int[] faceItems, int[] dedItems, double finalLoad, double dedLoad)
        {
            int belt = spec.Tiers.Belt, sorter = spec.Tiers.Sorter;
            Step fs = c.Final;
            var f = new Fragment("FINAL_CELL");
            Bld T = f.Add(Bld.Machine(fs.MachineItem, 0, 0, 0, fs.Recipe.Id));          // turret #274
            T.Role = "rule 6 final machine"; T.Load = Math.Min(1.0, finalLoad);
            // rule 6 lanes (useredit3 #430 E, #405 W, #428 N)
            if (faceItems[0] != 0)
            {
                Port p = LaneEnd(f, faceItems[0], belt, 2, 1, Dir.N, "rule 6 lane E");
                f.Add(InSorter(sorter, p.Belt, T, 2, 0.796, 0.88, 0.795, -5, faceItems[0], 3)).Role = "rule 6 input L1";
            }
            if (faceItems[1] != 0)
            {
                Port p = LaneEnd(f, faceItems[1], belt, -2, 0, Dir.S, "rule 6 lane W");
                f.Add(InSorter(sorter, p.Belt, T, -2, 0.001, -0.88, 0, -1, faceItems[1], 10)).Role = "rule 6 input L1";
            }
            if (faceItems[2] != 0)
            {
                Port p = LaneEnd(f, faceItems[2], belt, 1, 2, Dir.W, "rule 6 lane N");
                f.Add(InSorter(sorter, p.Belt, T, 0.799, 2, 0.8, 0.875, 0, faceItems[2], 2)).Role = "rule 6 input L1";
            }
            // rule 4: dedicated machine S of the final machine, 2 x L3 machine-to-machine (#397, #408)
            if (c.Dedicated != null)
            {
                Step ds = c.Dedicated;
                Bld C = f.Add(Bld.Machine(ds.MachineItem, 0, -5, 90, ds.Recipe.Id));     // circuit #374
                C.Role = "rule 4 dedicated machine"; C.Load = Math.Min(1.0, dedLoad);
                foreach (double sx in new[] { 0.803, -0.803 })
                {
                    double ex = sx > 0 ? 0.801 : -0.801;
                    Bld s = Sorter(sorter, sx, -4.125, ex, -0.874, 0, C, T, 3, ds.Item);
                    s.InputFromSlot = Geo.SlotAt(C.Yaw, sx - C.X, -4.125 - C.Y, Dir.N);
                    s.OutputToSlot = Geo.SlotAt(T.Yaw, ex - T.X, -0.874 - T.Y, Dir.S);
                    int wantIn = sx > 0 ? 11 : 9, wantOut = sx > 0 ? 6 : 8;
                    if (s.InputFromSlot != wantIn || s.OutputToSlot != wantOut) throw new InvalidOperationException("final cell: M2M slots differ from #397/#408");
                    s.Role = "rule 4 machine-to-machine L3";
                    f.Add(s);
                }
                // rule 11: both lanes end on the dedicated machine's S face (#270 from the W, #377 from the E)
                if (dedItems[0] != 0)
                {
                    Port p = LaneEnd(f, dedItems[0], belt, -1, -7, Dir.E, "rule 11 lane end W");
                    f.Add(InSorter(sorter, p.Belt, C, -1, -7, -0.804, -5.874, 0, dedItems[0], 5)).Role = "rule 11 input L1";
                }
                if (dedItems[1] != 0)
                {
                    Port p = LaneEnd(f, dedItems[1], belt, 1, -7, Dir.W, "rule 11 lane end E");
                    f.Add(InSorter(sorter, p.Belt, C, 1, -7, 0.804, -5.874, 0, dedItems[1], 3)).Role = "rule 11 input L1";
                }
            }
            // rule 7: inline output buffer N
            Fragment dep = Rules.InlineDepot(T, Lanes.DepotSorter(sorter), belt, fs.Item);
            if (dep == null) return null;
            f.Blds.AddRange(dep.Blds);
            f.Ports.AddRange(dep.Ports);
            Rotate(f, c.OutQuarters, 0, 0);
            return f;
        }

        /// Rigid quarter turns (clockwise) around tile (px,py) by the game's paste rotation (Composer.RotateLayout):
        /// depot stays yaw 0, its sorter slots shift by 3 per quarter, splitter side slots turn.
        public static void Rotate(Fragment f, int quarters, int px, int py)
        {
            int q = ((quarters % 4) + 4) % 4;
            if (q == 0) return;
            foreach (Bld b in f.Blds) b.Translate(-px, -py);
            Composer.RotateLayout(f.Blds, q);
            foreach (Bld b in f.Blds) b.Translate(px, py);
            foreach (Port p in f.Ports)
            {
                for (int i = 0; i < q; i++) { int rx = p.X - px, ry = p.Y - py; p.X = px + ry; p.Y = py - rx; }
                p.Dir = Dirs.RotateCW(p.Dir, q);
            }
        }

        /// Rule 7 cell for any machine yaw (multiple of 90): Rules.InlineDepot (game-proven at yaw 0, output N) built
        /// at yaw 0 and turned rigidly with the machine (output face = the machine's local N). Null for other yaws.
        public static Fragment InlineDepot(Bld fin, int sorterItem, int beltItem, int carries)
        {
            if (fin == null || fin.Kind != Kind.Machine) return null;
            double yq = Dirs.Norm(fin.Yaw) / 90.0;
            int q = (int)Math.Round(yq);
            if (Math.Abs(yq - q) * 90.0 > 1.0) return null;
            q %= 4;
            if (Math.Abs(fin.X - Math.Round(fin.X)) > 1e-6 || Math.Abs(fin.Y - Math.Round(fin.Y)) > 1e-6) return null;
            Bld zero = Bld.Machine(fin.ItemId, fin.X, fin.Y, 0, fin.RecipeId);
            Fragment f = Rules.InlineDepot(zero, sorterItem, beltItem, carries);
            if (f == null) return null;
            foreach (Bld b in f.Blds) { if (b.Input == zero) b.Input = fin; if (b.Output == zero) b.Output = fin; }
            Rotate(f, q, (int)Math.Round(fin.X), (int)Math.Round(fin.Y));
            return f;
        }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        internal static string Note(Choice c, GameData gd)
        {
            string n = "final cell: " + c.Final.Recipe.Name + " fed on " + (c.Dedicated != null ? "S by " + c.Dedicated.Recipe.Name + " machine-to-machine (2 x L3, rule 4)" : "no dedicated machine")
                + ", one L1 lane per face (rule 6), inline Depot Mk.I output (rule 7)";
            if (c.OutQuarters != 0) n += ", cell turned " + (c.OutQuarters * 90) + " deg (rigid paste rotation of the game-proven yaw-0 cell)";
            return n + " - useredit3 records #274 #374 #397 #408 #270 #377 #405 #428 #430 #530 #27 #382";
        }
    }

    // ============================================================================================== Composer side
    public partial class Composer
    {
        partial class Attempt
        {
            FinalCell.Choice cellChoice;
            RowInfo cellRow, cellDed;

            bool IsFinalCellRow(RowInfo r) { return r != null && cellRow != null && r == cellRow; }

            /// After MakeEdges: absorb the rule-4 machine's row into the final row.
            void FinalCellAbsorb()
            {
                cellChoice = null; cellRow = null; cellDed = null;
                FinalCell.Choice c = FinalCell.Pick(gd, spec, plan, outEdgeC);
                if (c == null) return;
                var fr = RowsOf(c.FinalIndex);
                if (fr.Count != 1 || fr[0].Count != 1) return;
                RowInfo f = fr[0], d = null;
                if (c.Dedicated != null)
                {
                    var dr = RowsOf(c.DedicatedIndex);
                    if (dr.Count == 1 && dr[0].Count == 1 && edges.Count(e => e.From == dr[0]) == 1 && edges.Any(e => e.From == dr[0] && e.To == f)) d = dr[0];
                    else c.Dedicated = null;
                }
                if (c.Dedicated == null && f.InItems.Length > 3) return;
                if (d != null)
                {
                    edges.RemoveAll(e => e.From == d);
                    foreach (Edge e in edges) if (e.To == d) e.To = f;
                    foreach (RowInfo r in rows)
                        if (r.Feeds != null) r.Feeds = r.Feeds.Select(kv => kv.Key == d ? new KeyValuePair<RowInfo, double>(f, kv.Value) : kv).ToList();
                    rows.Remove(d);
                    var items = new List<int>(); var rates = new List<double>();
                    for (int i = 0; i < f.InItems.Length; i++) if (f.InItems[i] != d.Step.Item) { items.Add(f.InItems[i]); rates.Add(f.InRates[i]); }
                    items.AddRange(d.InItems); rates.AddRange(d.InRates);
                    f.InItems = items.ToArray(); f.InRates = rates.ToArray();
                }
                cellChoice = c; cellRow = f; cellDed = d;
            }

            /// The final row's fragment (instead of Modules.MachineRow). mirror swaps the W/E lane assignment.
            Fragment FinalCellBuild(RowInfo r, bool mirror)
            {
                var ded = cellDed != null ? cellDed.InItems.ToList() : new List<int>();
                var own = r.InItems.Where(it => !ded.Contains(it)).ToList();
                // rule 5: a lane whose producer feeds only the final machine takes the E face (adjacent column); the other
                // lanes W then N
                int fi = cellChoice.FinalIndex;
                own = own.OrderBy(it => { Step p = plan.StepFor(it); return p != null && !r.Step.IsExternal(plan, it) && p.ConsumerSteps.Distinct().Count() == 1 && p.ConsumerSteps[0] == fi ? 0 : 1; })
                         .ThenByDescending(it => r.InRates[Array.IndexOf(r.InItems, it)]).ToList();
                var faces = new int[3];
                for (int i = 0; i < own.Count && i < 3; i++) faces[i] = own[i];
                if (mirror) { int t = faces[0]; faces[0] = faces[1]; faces[1] = t; }
                var dl = new int[2];
                for (int i = 0; i < ded.Count && i < 2; i++) dl[i] = ded[i];
                if (mirror) { int t = dl[0]; dl[0] = dl[1]; dl[1] = t; }
                double fl = RowLoad(gd, r.Step, 1, r.OutRate);
                double dlo = cellDed != null ? RowLoad(gd, cellDed.Step, 1, cellDed.OutRate) : 0;
                Fragment f;
                try { f = FinalCell.Build(gd, spec, cellChoice, faces, dl, fl, dlo); }
                catch (Exception e) { errors.Add("composer: final cell: " + e.Message); return null; }
                if (f == null) { errors.Add("composer: final cell not built"); return null; }
                if (fl > 1 + 1e-6) errors.Add("composer: final machine needs " + F(fl * 100) + "%");
                RowNotes.Add(FinalCell.Note(cellChoice, gd));
                return f;
            }

            /// After BlockTiles: the gap between the final and the dedicated machine (both L3 sorters, the tower spot of
            /// useredit3 (3,8)) and the tiles beside the depot tail stay free of routed belts.
            void FinalCellBlock()
            {
                if (cellRow == null || cellRow.Frag == null) return;
                Bld T = cellRow.Frag.Blds.FirstOrDefault(b => b.Kind == Kind.Machine && b.RecipeId == cellChoice.Final.Recipe.Id);
                if (T == null) return;
                int q = cellChoice.OutQuarters;
                // the E lane's dead end (2,1) points at (2,2), the N lane's approach tile (useredit3 #278 under corner #367):
                // BlockTiles kept it free as "ahead of a lane end"; the N lane must be routed through it
                if (cellRow.Frag.Blds.Any(b => b.Role == "rule 6 lane N") && cellRow.Frag.Blds.Any(b => b.Role == "rule 6 lane E"))
                {
                    int ax = 2, ay = 2;
                    for (int i = 0; i < q; i++) { int t = ax; ax = ay; ay = -t; }
                    long k = K((int)Math.Round(T.X) + ax, (int)Math.Round(T.Y) + ay);
                    if (aheadOfEnd.Remove(k)) blocked.Remove(k);
                }
                if (cellChoice.Dedicated == null) return;
                for (int x = -1; x <= 1; x++)
                    for (int y = -3; y <= -1; y++)
                    {
                        int rx = x, ry = y;
                        for (int i = 0; i < q; i++) { int t = rx; rx = ry; ry = -t; }
                        blocked.Add(K((int)Math.Round(T.X) + rx, (int)Math.Round(T.Y) + ry));
                    }
            }

            /// Product port = the inline depot's tail end (rule 7: tail of 3 inside the block, like useredit3 (3,18));
            /// the tiles ahead of it stay free.
            void FinalCellProduct(RowInfo r, Port o)
            {
                Bld last = PortBelt(o);
                if (last == null) { errors.Add("composer: final cell product port has no belt"); return; }
                for (int i = 1; i <= 8; i++) blocked.Add(K(last.TileX + Dirs.Dx(o.Dir) * i, last.TileY + Dirs.Dy(o.Dir) * i));
                blockPorts.Add(new Port { Name = "out:" + r.Step.Item + "#" + blockPorts.Count(q => q.Kind == PortKind.Out), Kind = PortKind.Out, X = last.TileX, Y = last.TileY,
                    Dir = o.Dir, Item = r.Step.Item, Rate = r.OutRate, Belt = last, Chain = last.Chain });
            }
        }
    }

    // ============================================================================================== Validator side
    public partial class Validator
    {
        sealed partial class Run
        {
            readonly HashSet<Bld> cellM2M = new HashSet<Bld>();                                   // machine -> machine
            readonly Dictionary<Bld, List<Bld>> cellDepotIn = new Dictionary<Bld, List<Bld>>();   // depot -> machine->depot sorters
            readonly Dictionary<Bld, List<Bld>> cellDepotOut = new Dictionary<Bld, List<Bld>>();  // belt -> depot->belt sorters of a machine-fed depot

            /// Sorters of the final cell (rules 4 and 7): machine -> machine and machine -> depot. Checks both ends,
            /// length rule, slots by geometry (3x3 machines, depot yaw 0), one sorter per slot, yaw = direction.
            /// Returns false for every other sorter (checked by the ordinary rules).
            bool CellSorter(Bld s, Bld i, Bld o, HashSet<string> usedSlots, string at)
            {
                bool m2m = i.Kind == Kind.Machine && o.Kind == Kind.Machine, m2d = i.Kind == Kind.Machine && o.Kind == Kind.Storage;
                if (!m2m && !m2d) return false;
                if (!Inside(i, s.X, s.Y, 0.05)) R.Fail("sorter start not on its input " + i + at);
                if (!Inside(o, s.X2, s.Y2, 0.05)) R.Fail("sorter end not on its output " + o + at);
                double seg = Dist(s.X, s.Y, s.X2, s.Y2);
                if (s.SorterLength != (int)Math.Round(seg - 0.3) || s.SorterLength < 1 || s.SorterLength > 3) R.Fail("sorter length param " + s.SorterLength + " vs segment " + F(seg) + at);
                int[] isz = fp.Size(i.ItemId);
                if (isz[0] == 3 && isz[1] == 3) { int w = SlotAt(i, s, false); if (s.InputFromSlot != w) R.Fail("sorter input slot " + s.InputFromSlot + " but geometry says " + w + at); }
                if (m2m)
                {
                    int[] osz = fp.Size(o.ItemId);
                    if (osz[0] == 3 && osz[1] == 3) { int w = SlotAt(o, s, true); if (s.OutputToSlot != w) R.Fail("sorter output slot " + s.OutputToSlot + " but geometry says " + w + at); }
                }
                else
                {
                    if (Math.Abs(Dirs.Norm(o.Yaw + 0.5) - 0.5) > 1.0) R.Fail("depot at " + At(o.X, o.Y) + " has yaw " + F(o.Yaw) + " (Depot Mk.I does not rotate, yaw 0)");
                    int w = SlotAt(o, s, true);
                    if (s.OutputToSlot != w) R.Fail("depot sorter slot " + s.OutputToSlot + " but geometry says " + w + at);
                }
                if (!usedSlots.Add(i.GetHashCode() + ":" + s.InputFromSlot + ":" + At(i.X, i.Y))) R.Fail("two sorters on slot " + s.InputFromSlot + " of machine at " + At(i.X, i.Y));
                if (!usedSlots.Add(o.GetHashCode() + ":" + s.OutputToSlot + ":" + At(o.X, o.Y))) R.Fail("two sorters on slot " + s.OutputToSlot + " at " + At(o.X, o.Y));
                double vx = s.X2 - s.X, vy = s.Y2 - s.Y;
                if (YawDiff(s.Yaw, Dirs.Norm(Math.Atan2(vx, vy) * 180.0 / Math.PI)) > 5) R.Fail("sorter yaw " + F(s.Yaw) + " does not match its direction" + at);
                Get(outSorters, i).Add(s);
                RecipeDef rd = gd != null ? gd.Recipe(i.RecipeId) : null;
                if (m2m)
                {
                    Get(inSorters, o).Add(s);
                    cellM2M.Add(s);
                    if (rd != null) items[i] = new HashSet<int>(rd.Outputs.Select(x => x.Item));   // machine "lane" for the pickup check
                }
                else Get(cellDepotIn, o).Add(s);
                return true;
            }

            /// After all sorters: depot -> belt sorters of a machine-fed depot (rule 7 inline buffer) feed their belt.
            void CellIndex()
            {
                cellDepotOut.Clear();
                foreach (Bld s in sorters)
                    if (s.Input != null && s.Input.Kind == Kind.Storage && cellDepotIn.ContainsKey(s.Input) && s.Output != null && s.Output.Kind == Kind.Belt)
                        Get(cellDepotOut, s.Output).Add(s);
            }

            /// Rule 11 (useredit3 circuit #374: #270 from belt (2,5) flowing E, #377 from belt (4,5) flowing W, both dead ends on
            /// its S face): an L1 pickup at a lane dead end whose belt flows toward another dead-end pickup of the same machine
            /// on the same face is exempt from the one-lane-per-face rule 1.
            bool CellRule11(Bld m, Bld s)
            {
                Bld b = s.Input;
                if (b == null || b.Kind != Kind.Belt || b.Output != null) return false;
                Dir side; int dist;
                if (!SideOf(m, b, out side, out dist) || dist != 1) return false;
                Dir flow = FlowOf(b);
                foreach (Bld t in Peek(inSorters, m))
                {
                    Bld c = t.Input;
                    if (t == s || c == null || c.Kind != Kind.Belt || c.Output != null || t.SorterLength != 1) continue;
                    Dir side2; int d2;
                    if (!SideOf(m, c, out side2, out d2) || side2 != side || d2 != 1 || FlowOf(c) != Dirs.Opposite(flow)) continue;
                    int dx = c.TileX - b.TileX, dy = c.TileY - b.TileY;
                    if (dx * Dirs.Dx(flow) + dy * Dirs.Dy(flow) > 0) return true;   // the two lanes run toward each other
                }
                return false;
            }

            /// useredit3 turret: the E lane ends at (5,13) (#278, flows N) right under the N lane corner (5,14) (#367) that
            /// feeds the same machine (#428 from (4,14)); the game keeps them unlinked. A dead end that feeds a machine may
            /// point into a belt whose lane (that belt or the next) also feeds that machine.
            bool CellEndOk(Bld b, Bld o)
            {
                Bld s = sorters.FirstOrDefault(x => x.Input == b && x.Output != null && x.Output.Kind == Kind.Machine);
                if (s == null) return false;
                Bld n2 = o.Output != null && o.Output.Kind == Kind.Belt ? o.Output : null;
                return sorters.Any(x => (x.Input == o || (n2 != null && x.Input == n2)) && x.Output == s.Output);
            }

            bool CellIsM2M(Bld s) { return cellM2M.Contains(s); }
            bool CellFed(Bld belt) { return cellDepotOut.ContainsKey(belt); }

            /// Items a machine-fed depot drops on a belt.
            void CellItems(Bld belt, HashSet<int> set)
            {
                List<Bld> outs;
                if (!cellDepotOut.TryGetValue(belt, out outs)) return;
                foreach (Bld s in outs)
                    foreach (Bld fs in cellDepotIn[s.Input])
                    {
                        RecipeDef rd = gd != null ? gd.Recipe(fs.Input.RecipeId) : null;
                        if (rd != null) set.UnionWith(rd.Outputs.Select(x => x.Item));
                    }
            }

            /// Rates a machine-fed depot drops on a belt: what its machines put in, shared over the depot's out sorters.
            void CellFlow(Bld belt, Dictionary<int, double> f)
            {
                List<Bld> outs;
                if (!cellDepotOut.TryGetValue(belt, out outs)) return;
                foreach (Bld s in outs)
                {
                    int nOut = sorters.Count(x => x.Input == s.Input && x.Output != null && x.Output.Kind == Kind.Belt);
                    foreach (Bld fs in cellDepotIn[s.Input])
                    {
                        Bld m = fs.Input;
                        RecipeDef rd = gd != null ? gd.Recipe(m.RecipeId) : null;
                        if (rd == null) continue;
                        var mouts = Peek(outSorters, m);
                        foreach (ItemCount o in rd.Outputs)
                        {
                            int share = Math.Max(1, mouts.Count(x => x.FilterId == 0 || x.FilterId == o.Item));
                            double v; f.TryGetValue(o.Item, out v);
                            f[o.Item] = v + MachineRate(m, o.Item, true) / share / Math.Max(1, nOut);
                        }
                    }
                }
            }
        }
    }
}
