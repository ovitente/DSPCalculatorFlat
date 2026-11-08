// SelfTestRows34.cs - pattern rows P08 / P09 (docs\patterns-design.md, rows agent). Part of SelfTest (same runner,
// MAKEBLOCK_CASES filter: "micro rows34"). Oracle: the user's in-game drawings refs\patterns\p08-asm-row-3in.txt
// (4 Assembler Mk.II, Electric Motor, 3 inputs) and p09-asm-row-4in.txt (Missile Set, 4 inputs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const int MotorRecipe = 97, MissileRecipe = 144;
    // MotorId (1203) and GearId (1201) are defined in SelfTestFinal.cs / SelfTest.cs
    const int MissileSetId = 1609, CircuitBoardId = 1301, CombustibleUnitId = 1128, EngineId = 1407;
    // the first machine of the drawn rows is at (4,5); the engine row puts it at (0,0)
    const int RowsDx = 4, RowsDy = 5;

    static partial void RunRows34Cases()
    {
        Case("micro rows34: Geo cells p08/p09 (asm2 yaw 0: W L1 in, W L2 in, W L3 out, E L1 in, E L2 in) match their source records", MicroRows34Cells);
        Case("micro rows34: MachineRow Electric Motor x4 Mk.II (A=W: iron L1, coil L2, out L3; B=E: gear L1) reproduces p08-asm-row-3in sorters", MicroRows34MotorP08);
        Case("micro rows34: MachineRow Missile Set x4 Mk.II (A=W: copper L1, circuit L2, out L3; B=E: combustible L1, engine L2) reproduces p09-asm-row-4in sorters", MicroRows34MissileP09);
        Case("micro rows34: p08 / p09 drawings pass ValidateBelts; the full Validate (1-step plan, ports on the tails) reports nothing but the lane-end rule conflict", MicroRows34Validate);
    }

    static string Rows34File(string key)
    {
        foreach (string rel in Geo.SourceFiles[key].Split('|'))
        {
            string path = Cells.Resolve(bpDir, rel);
            if (!File.Exists(path)) path = Path.Combine(root, rel);
            if (File.Exists(path)) return path;
        }
        throw new SkipException("missing " + Geo.SourceFiles[key]);
    }

    /// (a) every Geo cell with SourceKey p08 / p09 equals its blueprint record (belt tile, start/end within 0.01, slot,
    /// length), and the CLI host's own self-check (Cells.Load -> CheckSorterTable) reports no error for them.
    static string MicroRows34Cells()
    {
        var e = new Errs();
        var cells = Geo.All().Where(g => g.SourceKey == "p08" || g.SourceKey == "p09").ToList();
        e.Check(cells.Count == 5, cells.Count + " pattern-row cells, expected 5");
        foreach (string want in new[] { "asm2 y0 in W L1 lat-1 (p08)", "asm2 y0 in W L2 lat0 (p08)", "asm2 y0 out W L3 lat+1", "asm2 y0 in E L1 lat-1", "asm2 y0 in E L2 lat0 (p09)" })
            e.Check(cells.Any(g => g.Name == want), "cell " + want + " missing");
        var files = new Dictionary<string, Dictionary<int, object>>();
        foreach (SorterGeo g in cells)
        {
            e.Check(g.Status == "game" && g.Proven && g.MachineItem == Ids.AsmMk2 && NearYaw(g.MachineYaw, 0, 0.01), g.Name + ": status/item/yaw " + g.Status + "/" + g.MachineItem + "/" + g.MachineYaw);
            Dictionary<int, object> recs;
            if (!files.TryGetValue(g.SourceKey, out recs)) { recs = GameBp.ByIndex(GameBp.Load(Rows34File(g.SourceKey))); files[g.SourceKey] = recs; }
            object s;
            if (!recs.TryGetValue(g.SourceIndex, out s)) { e.L.Add(g.Name + ": no record #" + g.SourceIndex); continue; }
            object i = GameBp.Get(s, "inputObj"), o = GameBp.Get(s, "outputObj");
            if (i == null || o == null) { e.L.Add(g.Name + ": record #" + g.SourceIndex + " has no links"); continue; }
            bool toM = Ids.IsBelt(GameBp.In(i, "itemId"));
            object m = toM ? o : i, b = toM ? i : o;
            double mx = GameBp.Fl(m, "localOffset_x"), my = GameBp.Fl(m, "localOffset_y");
            e.Check(toM == g.BeltToMachine && GameBp.In(m, "itemId") == Ids.AsmMk2 && NearYaw(GameBp.Fl(m, "yaw"), 0, 1), g.Name + ": direction / machine of record #" + g.SourceIndex);
            e.Check((int)Math.Round(GameBp.Fl(b, "localOffset_x") - mx) == g.BeltDx && (int)Math.Round(GameBp.Fl(b, "localOffset_y") - my) == g.BeltDy, g.Name + ": belt tile");
            e.Check(Near(GameBp.Fl(s, "localOffset_x") - mx, g.Sx, 0.01) && Near(GameBp.Fl(s, "localOffset_y") - my, g.Sy, 0.01)
                && Near(GameBp.Fl(s, "localOffset_x2") - mx, g.Ex, 0.01) && Near(GameBp.Fl(s, "localOffset_y2") - my, g.Ey, 0.01), g.Name + ": start/end differ from record #" + g.SourceIndex);
            e.Check((toM ? GameBp.In(s, "outputToSlot") : GameBp.In(s, "inputFromSlot")) == g.Slot, g.Name + ": slot");
            e.Check(((int[])GameBp.Get(s, "parameters"))[0] == g.Length, g.Name + ": length");
            e.Check(Geo.SlotAt(0, toM ? g.Ex : g.Sx, toM ? g.Ey : g.Sy, g.Face) == g.Slot, g.Name + ": slot " + g.Slot + " disagrees with Geo.SlotAt");
        }
        // the host self-check (Cells.Load re-reads every checkable sorter cell) accepts the new cells; other sources of
        // the host table (user templates outside refs\) may be missing on this machine and are not this case's subject
        Cells c = Cells.Load(bpDir, gd);
        foreach (string m in c.Errors.Where(m => m.Contains("p08") || m.Contains("p09") || m.Contains("(p0"))) e.L.Add("host self-check: " + m);
        string checkedLine = c.Log.FirstOrDefault(m => m.StartsWith("sorter cells checked"));
        e.Check(checkedLine != null, "host self-check did not run the sorter table check");
        Console.WriteLine("      " + cells.Count + " cells; " + (checkedLine ?? "-"));
        return e.Result();
    }

    static RowSpec Rows34Spec(string name, int recipe, int[] inputs, double[] rates, int output, double outRate)
    {
        return new RowSpec { Name = name, MachineItem = Ids.AsmMk2, RecipeId = recipe, Count = 4, Mirror = true,   // side A = west, as drawn
            InputItems = inputs, InputRates = rates, OutputItem = output, OutputRate = outRate, Flow = Dir.N,
            BeltItem = Ids.BeltMk2, SorterItem = Ids.SorterMk2, LaneIcons = true, TailItems = inputs, Data = gd };
    }

    /// (b) MachineRow sorters == the drawing's sorter records, matched by machine + belt tile: start/end within 0.02,
    /// slot, length, input/output offsets. Machines at yaw 0, pitch 4, sorter count per machine as drawn.
    static string Rows34CompareRow(string key, RowSpec spec, int sortersPerMachine)
    {
        var e = new Errs();
        Layout file = FullLayout(GameBp.Load(Rows34File(key)), false);
        Fragment f = Modules.MachineRow(spec);
        foreach (string p in spec.Problems) e.L.Add("row problem: " + p);
        foreach (string n in spec.Notes) if (n.Contains("unproven") || n.Contains("not proven")) e.L.Add("row note: " + n);
        var machines = f.Blds.Where(b => b.Kind == Kind.Machine).OrderBy(b => b.Y).ToList();
        e.Check(machines.Count == 4, machines.Count + " machines, expected 4");
        for (int k = 1; k < machines.Count; k++) e.Check(Near(machines[k].Y - machines[k - 1].Y, 4, 0.001), "pitch " + Fmt(machines[k].Y - machines[k - 1].Y) + ", drawn 4");
        int matched = 0;
        foreach (Bld m in machines)
        {
            e.Check(m.ItemId == Ids.AsmMk2 && NearYaw(m.Yaw, 0, 0.01) && m.RecipeId == spec.RecipeId, "machine " + m + ": item/yaw/recipe");
            Bld fm = file.All.FirstOrDefault(b => b.Kind == Kind.Machine && Near(b.X, m.X + RowsDx, 0.01) && Near(b.Y, m.Y + RowsDy, 0.01));
            if (fm == null) { e.L.Add("no drawn machine at (" + (m.X + RowsDx) + "," + (m.Y + RowsDy) + ")"); continue; }
            var ours = f.Blds.Where(s => s.Kind == Kind.Sorter && (s.Input == m || s.Output == m)).ToList();
            var theirs = file.All.Where(s => s.Kind == Kind.Sorter && (s.Input == fm || s.Output == fm)).ToList();
            e.Check(ours.Count == sortersPerMachine && theirs.Count == sortersPerMachine, "machine at (" + Fmt(fm.X) + "," + Fmt(fm.Y) + "): " + ours.Count + " sorters, drawn " + theirs.Count + ", expected " + sortersPerMachine);
            foreach (Bld s in ours)
            {
                bool input = s.Output == m;
                Bld belt = input ? s.Input : s.Output;
                if (belt == null || belt.Kind != Kind.Belt) { e.L.Add("sorter " + s.Role + " has no belt"); continue; }
                int bx = belt.TileX + RowsDx, by = belt.TileY + RowsDy;
                Bld t = theirs.FirstOrDefault(o => (o.Output == fm) == input && (input ? o.Input : o.Output) != null
                    && Near((input ? o.Input : o.Output).X, bx, 0.01) && Near((input ? o.Input : o.Output).Y, by, 0.01));
                if (t == null) { e.L.Add("no drawn sorter for " + s.Role + " on belt (" + bx + "," + by + ")"); continue; }
                string w = "machine (" + Fmt(fm.X) + "," + Fmt(fm.Y) + ") " + s.Role + ": ";
                e.Check(Near(s.X + RowsDx, t.X, 0.02) && Near(s.Y + RowsDy, t.Y, 0.02) && Near(s.X2 + RowsDx, t.X2, 0.02) && Near(s.Y2 + RowsDy, t.Y2, 0.02),
                    w + "start/end (" + Fmt(s.X + RowsDx) + "," + Fmt(s.Y + RowsDy) + ")->(" + Fmt(s.X2 + RowsDx) + "," + Fmt(s.Y2 + RowsDy) + "), drawn (" + Fmt(t.X) + "," + Fmt(t.Y) + ")->(" + Fmt(t.X2) + "," + Fmt(t.Y2) + ")");
                e.Check(s.OutputToSlot == t.OutputToSlot && s.InputFromSlot == t.InputFromSlot, w + "slots " + s.OutputToSlot + "/" + s.InputFromSlot + ", drawn " + t.OutputToSlot + "/" + t.InputFromSlot);
                e.Check(s.SorterLength == t.SorterLength, w + "L" + s.SorterLength + ", drawn L" + t.SorterLength);
                e.Check(s.InputOffset == t.InputOffset && s.OutputOffset == t.OutputOffset, w + "offsets in/out " + s.InputOffset + "/" + s.OutputOffset + ", drawn " + t.InputOffset + "/" + t.OutputOffset);
                e.Check(NearYaw(s.Yaw, t.Yaw, 1), w + "yaw " + Fmt(s.Yaw) + ", drawn " + Fmt(t.Yaw));
                // the lane carries the drawn item (icon tail of the drawing: first tile of the lane at y 0)
                Bld tail = file.All.FirstOrDefault(b => b.Kind == Kind.Belt && b.Parameters != null && b.Parameters.Length == 2 && Near(b.X, bx, 0.01) && Near(b.Y, 0, 0.01));
                if (input) e.Check(tail != null && tail.Parameters[0] == belt.Carries, w + "lane x" + bx + " carries " + belt.Carries + ", drawn icon " + (tail == null ? "none" : tail.Parameters[0].ToString()));
                matched++;
            }
        }
        // the output lane starts at the first machine's pickup tile, as drawn (x0 lane: y 6 .. 18)
        Bld outStart = f.Blds.Where(b => b.Kind == Kind.Belt && b.Carries == spec.OutputItem).OrderBy(b => b.TileY).FirstOrDefault();
        e.Check(outStart != null && outStart.TileY + RowsDy == 6, "output lane starts at drawn y " + (outStart == null ? -1 : outStart.TileY + RowsDy) + ", expected 6");
        Console.WriteLine("      " + matched + " sorters matched record by record; notes: " + (spec.Notes.Count == 0 ? "-" : string.Join(" | ", spec.Notes.ToArray())));
        return e.Result();
    }

    static string MicroRows34MotorP08()
    {
        // Electric Motor on 4 Mk.II: 30/min per machine -> iron 240, gear 120, coil 120, motor 120 for the row
        return Rows34CompareRow("p08", Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120), 4);
    }

    static string MicroRows34MissileP09()
    {
        // Missile Set on 4 Mk.II: 30/min per machine -> copper 720 (180 per machine = L1 Mk.II cap), circuit 360 (90 = L2 cap)
        return Rows34CompareRow("p09", Rows34Spec("missile", MissileRecipe, new[] { Ids.CopperIngot, CombustibleUnitId, CircuitBoardId, EngineId },
            new[] { 720.0, 240.0, 360.0, 120.0 }, MissileSetId, 120), 5);
    }

    /// (c) the drawings pass the conveyor checks and, with block ports on the icon tails / lane end and a one-step
    /// plan (inputs treated as raw), the full Validate.
    static string MicroRows34Validate()
    {
        var e = new Errs();
        var runs = new[] {
            new { Key = "p08", Item = MotorId, Inputs = new[] { Ids.IronIngot, GearId, Ids.MagneticCoil } },
            new { Key = "p09", Item = MissileSetId, Inputs = new[] { Ids.CopperIngot, CombustibleUnitId, CircuitBoardId, EngineId } } };
        foreach (var r in runs)
        {
            Layout l = FullLayout(GameBp.Load(Rows34File(r.Key)), false);
            ValidationReport belts = new Validator(gd, null).ValidateBelts(l);
            foreach (string m in belts.Errors) e.L.Add(r.Key + " ValidateBelts: " + m);
            // one-step plan: the row's inputs come from outside the block
            var spec = new BlockSpec { Item = r.Item, Rate = 120 };
            spec.Tiers.Assembler = Ids.AsmMk2; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
            foreach (int it in r.Inputs) spec.TreatAsRaw.Add(it);
            ProductionPlan plan = new Solver(gd, spec).Solve();
            Step step = plan.StepFor(r.Item);
            e.Check(step != null && step.Machines == 4, r.Key + ": plan has " + (step == null ? -1 : step.Machines) + " machines, drawn 4");
            // ports: icon tails (first tile of every input lane, y 0) and the output lane's last tile; lanes carry their items
            var heads = new HashSet<Bld>(l.All.Where(b => b.Kind == Kind.Belt && b.Output != null).Select(b => b.Output));
            foreach (Bld b in l.All.Where(b => b.Kind == Kind.Belt && !heads.Contains(b)))
            {
                int item = b.Parameters != null && b.Parameters.Length == 2 ? b.Parameters[0] : 0;
                if (item == 0)
                {
                    // the output lane starts without an icon: its item is what the machine sorters put on it
                    Bld t = b; while (t != null && item == 0) { Bld src = l.All.FirstOrDefault(s => s.Kind == Kind.Sorter && s.Output == t && s.Input != null && s.Input.Kind == Kind.Machine); if (src != null) item = r.Item; t = t.Output; }
                }
                string chain = (item == r.Item ? "out:" : "in:") + item;
                Bld last = b;
                for (Bld t = b; t != null; t = t.Output) { t.Carries = item; t.Chain = chain; last = t; }
                if (item == r.Item) l.Ports.Add(new Port { Name = "out:" + item, Kind = PortKind.Out, X = last.TileX, Y = last.TileY, Dir = Dir.N, Item = item, Rate = 120, Belt = last, Chain = chain });
                else
                {
                    double rate = step != null && step.Recipe != null ? step.Recipe.InCount(item) * 60.0 * gd.Speed(Ids.AsmMk2) / step.Recipe.TimeSec * 4 : 0;
                    l.Ports.Add(new Port { Name = "in:" + item, Kind = PortKind.In, X = b.TileX, Y = b.TileY, Dir = Dir.N, Item = item, Rate = rate, Belt = b, Chain = chain });
                }
            }
            foreach (Bld s in l.All.Where(b => b.Kind == Kind.Sorter))
            {
                Bld belt = s.Input != null && s.Input.Kind == Kind.Belt ? s.Input : s.Output;
                if (belt != null && belt.Kind == Kind.Belt) s.Carries = belt.Carries;
            }
            foreach (Bld m in l.All.Where(b => b.Kind == Kind.Machine)) m.Carries = r.Item;
            e.Check(l.Ports.Count == r.Inputs.Length + 1, r.Key + ": " + l.Ports.Count + " ports found, expected " + (r.Inputs.Length + 1));
            ValidationReport full = new Validator(gd, spec).Validate(l, plan);
            // Known conflict left to the drawn agent / the user: the drawings end every input lane at y 18, 1-2 tiles
            // beyond the last pickup, while the engine's lane-end rule (user's edit of refs\engine-coil-360.txt) forbids
            // such a stub. Reported here, not counted as a failure of the row geometry.
            var laneEnd = full.Errors.Where(m => m.Contains("beyond its last pickup")).ToList();
            foreach (string m in full.Errors.Except(laneEnd)) e.L.Add(r.Key + " Validate: " + m);
            Console.WriteLine("      " + r.Key + ": " + l.All.Count + " records, belts-only " + belts.Errors.Count + " errors, full " + full.Errors.Count + " errors (" + laneEnd.Count
                + " lane-end rule conflicts, see docs\\patterns-design.md) / " + full.Warnings.Count + " warnings");
            foreach (string m in laneEnd.Concat(full.Warnings)) Console.WriteLine("        " + m);
        }
        return e.Result();
    }
}
