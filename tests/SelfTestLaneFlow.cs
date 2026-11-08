// SelfTestLaneFlow.cs - per-lane flow overrides of MachineRow / DoubleRow (RowSpec.InputFlows / OutputFlow, FLOORPLAN
// style item 5 of docs\floorplan-design.md: consumer lanes fed from the north band flow SOUTH while the product lane
// flows north, as in the user's turret refs\engine-missile-turret-7.5-v1-useredit3.txt motor column). Part of SelfTest
// (same runner, MAKEBLOCK_CASES filter: "micro laneflow").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static partial void RunLaneFlowCases()
    {
        Case("micro laneflow a: Electric Motor x4 Mk.II, iron + coil lanes flow S, gear + output N: ports/tails/icons at the lane starts, 4 sorters per machine, offsets from the real flow, ValidateBelts + full Validate + export", MicroLaneFlowMotor);
        Case("micro laneflow b: default flows == explicit Flow on every lane (P08 row tile by tile); off-axis / coated reversed lanes are rejected", MicroLaneFlowDefaults);
        Case("micro laneflow c: DoubleRow 12 smelters with both ore lanes flowing S and the shared output N validates and exports", MicroLaneFlowDouble);
    }

    static RowSpec LaneFlowMotorSpec()
    {
        RowSpec spec = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        spec.InputFlows = new Dir?[] { Dir.S, null, Dir.S };   // iron (A L1) and coil (A L2) descend from the band, gear (B L1) keeps Flow N
        spec.OutputFlow = Dir.N;
        return spec;
    }

    static PlanInput MotorPlan()
    {
        var p = new PlanInput { TargetItemId = MotorId, TargetPerMin = 120 };
        var s = new PlanStep { RecipeId = MotorRecipe, BuildingItemId = Ids.AsmMk2, MachineCount = 4, Machines = 4 };
        s.InputsPerMin[Ids.IronIngot] = 240; s.InputsPerMin[GearId] = 120; s.InputsPerMin[Ids.MagneticCoil] = 120;
        s.OutputsPerMin[MotorId] = 120;
        p.Steps.Add(s);
        p.ExternalItems.AddRange(new[] { Ids.IronIngot, GearId, Ids.MagneticCoil });
        return p;
    }

    /// Belt-side end displacement of a sorter along its belt's yaw (the rule SorterGeo.Make and the Validator use).
    static double BeltDisp(Bld s, Bld belt, bool toMachine)
    {
        double bx = toMachine ? s.X : s.X2, by = toMachine ? s.Y : s.Y2;
        double fy = belt.Yaw * Math.PI / 180.0;
        return (bx - belt.X) * Math.Sin(fy) + (by - belt.Y) * Math.Cos(fy);
    }

    /// Checks one lane of a fragment: all belts carry the item and have the yaw of the flow, tiles are linked in flow
    /// order, the port sits on the first (In) / last (Out) tile, the In tile carries the icon and is a tail (no pickup,
    /// leads on), the lane's dead end (In lane) is a pickup tile. Returns the lane's belts.
    static List<Bld> CheckLane(Errs e, Fragment f, string port, int item, Dir flow, string tag)
    {
        Port p = f.Ports.FirstOrDefault(x => x.Name == port);
        var belts = f.Blds.Where(b => b.Kind == Kind.Belt && b.Chain != null && b.Chain.EndsWith((port.StartsWith("in:") ? ":in:" : ":out:") + item + (port.EndsWith(DoubleRows.PortSuffixB) ? DoubleRows.PortSuffixB : ""), StringComparison.Ordinal)).ToList();
        if (p == null) { e.L.Add(tag + ": port " + port + " missing"); return belts; }
        e.Check(p.Dir == flow, tag + ": port " + port + " dir " + p.Dir + ", expected " + flow);
        e.Check(belts.Count > 0, tag + ": lane " + port + " has no belts");
        if (belts.Count == 0) return belts;
        double yaw = Dirs.Yaw(flow);
        foreach (Bld b in belts)
        {
            e.Check(b.Carries == item, tag + ": lane " + port + " belt " + b + " carries " + b.Carries);
            e.Check(NearYaw(b.Yaw, yaw, 0.01), tag + ": lane " + port + " belt " + b + " yaw " + Fmt(b.Yaw) + ", expected " + Fmt(yaw));
            if (b.Output != null) e.Check(b.Output.TileX == b.TileX + Dirs.Dx(flow) && b.Output.TileY == b.TileY + Dirs.Dy(flow), tag + ": lane " + port + " belt " + b + " links against the flow");
        }
        var heads = new HashSet<Bld>(belts.Where(b => b.Output != null).Select(b => b.Output));
        Bld first = belts.FirstOrDefault(b => !heads.Contains(b)), last = belts.FirstOrDefault(b => b.Output == null);
        e.Check(first != null && last != null && belts.Count(b => !heads.Contains(b)) == 1 && belts.Count(b => b.Output == null) == 1, tag + ": lane " + port + " is not one chain");
        Bld end = p.Kind == PortKind.In ? first : last;
        e.Check(end != null && p.Belt == end && p.X == end.TileX && p.Y == end.TileY, tag + ": port " + port + " at (" + p.X + "," + p.Y + ") is not the lane's " + (p.Kind == PortKind.In ? "first" : "last") + " tile" + (end == null ? "" : " " + end));
        var sorters = f.Blds.Where(s => s.Kind == Kind.Sorter).ToList();
        if (p.Kind == PortKind.In && first != null && last != null)
        {
            e.Check(first.Parameters != null && first.Parameters.Length == 2 && first.Parameters[0] == item, tag + ": lane " + port + " first tile has no icon [" + item + ",0]");
            e.Check(!sorters.Any(s => s.Input == first), tag + ": lane " + port + " first tile " + first + " is a pickup tile (no tail)");
            e.Check(first.Output != null, tag + ": lane " + port + " tail leads nowhere");
            e.Check(sorters.Any(s => s.Input == last), tag + ": lane " + port + " dead end " + last + " is not a pickup tile");
        }
        return belts;
    }

    /// (a) the motor row with two south-flowing input lanes.
    static string MicroLaneFlowMotor()
    {
        var e = new Errs();
        RowSpec spec = LaneFlowMotorSpec();
        Fragment f = Modules.MachineRow(spec);
        foreach (string p in spec.Problems) e.L.Add("row problem: " + p);
        foreach (string n in spec.Notes) if (n.Contains("unproven") || n.Contains("not proven")) e.L.Add("row note: " + n);
        var machines = f.Blds.Where(b => b.Kind == Kind.Machine).OrderBy(b => b.Y).ToList();
        e.Check(machines.Count == 4, machines.Count + " machines, expected 4");
        int north = machines.Count > 0 ? (int)Math.Round(machines[machines.Count - 1].Y) + 1 : 0;   // row's top line (last machine footprint edge)
        int south = -1;
        // lanes and ports
        var iron = CheckLane(e, f, "in:" + Ids.IronIngot, Ids.IronIngot, Dir.S, "iron");
        var coil = CheckLane(e, f, "in:" + Ids.MagneticCoil, Ids.MagneticCoil, Dir.S, "coil");
        var gear = CheckLane(e, f, "in:" + GearId, GearId, Dir.N, "gear");
        var outl = CheckLane(e, f, "out:" + MotorId, MotorId, Dir.N, "motor");
        Port pi = f.Ports.First(p => p.Name == "in:" + Ids.IronIngot), pc = f.Ports.First(p => p.Name == "in:" + Ids.MagneticCoil),
             pg = f.Ports.First(p => p.Name == "in:" + GearId), po = f.Ports.First(p => p.Name == "out:" + MotorId);
        e.Check(pi.Y >= north, "iron In port at y " + pi.Y + ", expected at the north end (>= " + north + ")");
        e.Check(pc.Y >= north, "coil In port at y " + pc.Y + ", expected at the north end (>= " + north + ")");
        e.Check(pg.Y <= south, "gear In port at y " + pg.Y + ", expected at the south end (<= " + south + ")");
        e.Check(po.Y == north, "motor Out port at y " + po.Y + ", expected the north end " + north);
        // the S lanes still span every machine: the lane's first tile is north of the last machine's pickup, its end is
        // the first machine's pickup
        e.Check(iron.Count > 0 && iron.Min(b => b.TileY) <= 0 && iron.Max(b => b.TileY) >= north - 1, "iron lane spans y " + (iron.Count == 0 ? "-" : iron.Min(b => b.TileY) + ".." + iron.Max(b => b.TileY)));
        e.Check(coil.Count > 0 && coil.Min(b => b.TileY) <= 0 && coil.Max(b => b.TileY) >= north - 1, "coil lane spans y " + (coil.Count == 0 ? "-" : coil.Min(b => b.TileY) + ".." + coil.Max(b => b.TileY)));
        // sorters: 4 per machine, every one on a lane belt of its item, offsets by the Make rule with the REAL belt yaw
        var sorters = f.Blds.Where(b => b.Kind == Kind.Sorter).ToList();
        var laneBelts = new HashSet<Bld>(iron.Concat(coil).Concat(gear).Concat(outl));
        foreach (Bld m in machines)
        {
            var own = sorters.Where(s => s.Input == m || s.Output == m).ToList();
            e.Check(own.Count == 4, "machine " + m + " has " + own.Count + " sorters, expected 4");
            foreach (Bld s in own)
            {
                bool toMachine = s.Output == m;
                Bld belt = toMachine ? s.Input : s.Output;
                if (belt == null || !laneBelts.Contains(belt)) { e.L.Add("sorter " + s.Role + " of " + m + " is not on a lane belt"); continue; }
                e.Check(belt.Carries == s.Carries, "sorter " + s.Role + " carries " + s.Carries + " on a lane of " + belt.Carries);
                double d = BeltDisp(s, belt, toMachine);
                bool behind = f.Blds.Any(b => b.Kind == Kind.Belt && b.Output == belt);
                int want = (int)Math.Floor(20.0 * d + 1e-6);
                if (toMachine || !behind) want = Math.Max(0, want);
                int off = toMachine ? s.InputOffset : s.OutputOffset;
                e.Check(off == want, "sorter " + s.Role + " belt offset " + off + ", real-flow rule gives " + want + " (d " + Fmt(d) + ", belt yaw " + Fmt(belt.Yaw) + ")");
            }
        }
        // the iron L1 cell (lateral -1, belt-side end 0.2 before the tile centre along N) gets offset 4 when flowing N and 0
        // when flowing S: the offset follows the lane's real direction, not the row's
        var ironIn = sorters.Where(s => s.Input != null && iron.Contains(s.Input)).ToList();
        RowSpec specN = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        Fragment fn = Modules.MachineRow(specN);
        var ironInN = fn.Blds.Where(s => s.Kind == Kind.Sorter && s.Input != null && s.Input.Kind == Kind.Belt && s.Input.Carries == Ids.IronIngot).ToList();
        e.Check(ironIn.Count == 4 && ironInN.Count == 4, "iron input sorters: " + ironIn.Count + " (S) / " + ironInN.Count + " (N), expected 4 / 4");
        if (ironIn.Count == 4 && ironInN.Count == 4)
        {
            double dS = BeltDisp(ironIn[0], ironIn[0].Input, true), dN = BeltDisp(ironInN[0], ironInN[0].Input, true);
            e.Check(Near(dS, -dN, 1e-9), "iron sorter displacement S " + Fmt(dS) + " is not the mirror of N " + Fmt(dN));
            e.Check(ironIn.All(s => s.InputOffset == Math.Max(0, (int)Math.Floor(20.0 * dS + 1e-6))), "iron S input offsets " + string.Join(",", ironIn.Select(s => s.InputOffset.ToString(CultureInfo.InvariantCulture)).ToArray()));
            e.Check(ironInN.All(s => s.InputOffset == Math.Max(0, (int)Math.Floor(20.0 * dN + 1e-6))), "iron N input offsets " + string.Join(",", ironInN.Select(s => s.InputOffset.ToString(CultureInfo.InvariantCulture)).ToArray()));
        }
        // same machine yaw and sorter cells as the P08 row (only the lanes turned)
        var mN = fn.Blds.Where(b => b.Kind == Kind.Machine).OrderBy(b => b.Y).ToList();
        e.Check(mN.Count == machines.Count && machines.Zip(mN, (a, b) => Near(a.X, b.X, 1e-9) && Near(a.Y, b.Y, 1e-9) && NearYaw(a.Yaw, b.Yaw, 0.01)).All(x => x), "machines differ from the default-flow row");
        var cellsS = sorters.Select(s => s.Role + " " + Fmt(s.X) + "," + Fmt(s.Y) + "->" + Fmt(s.X2) + "," + Fmt(s.Y2)).OrderBy(x => x).ToList();
        var cellsN = fn.Blds.Where(b => b.Kind == Kind.Sorter).Select(s => s.Role + " " + Fmt(s.X) + "," + Fmt(s.Y) + "->" + Fmt(s.X2) + "," + Fmt(s.Y2)).OrderBy(x => x).ToList();
        e.Check(cellsS.SequenceEqual(cellsN), "sorter geometry differs from the default-flow row");
        // belts-only validation of the bare fragment
        var l0 = new Layout(gd);
        l0.Add(f);
        ValidationReport belts = new Validator(gd, null).ValidateBelts(l0);
        foreach (string m in belts.Errors) e.L.Add("ValidateBelts: " + m);
        // full validation with a one-step plan: the S lanes are band-fed inside the block (no tail to the input edge)
        Fragment f2 = Modules.MachineRow(LaneFlowMotorSpec());
        foreach (Port p in f2.Ports) if (p.Kind == PortKind.In && p.Dir == Dir.S) p.FedFromBand = true;
        ValidateFragment(e, f2, MotorPlan(), "motor S/N lanes", "laneflow-motor-s.txt", null);
        Console.WriteLine("      ports: " + string.Join(" | ", f.Ports.Select(p => p.ToString()).ToArray()) + "; notes: " + (spec.Notes.Count == 0 ? "-" : string.Join(" | ", spec.Notes.ToArray())));
        return e.Result();
    }

    static string Dump(Fragment f)
    {
        var l = new List<string>();
        foreach (Bld b in f.Blds)
            l.Add(b.Kind + " " + b.ItemId + " " + Fmt(b.X) + "," + Fmt(b.Y) + "," + Fmt(b.Z) + " " + Fmt(b.X2) + "," + Fmt(b.Y2) + " yaw " + Fmt(b.Yaw) + " r" + b.RecipeId
                + " out " + (b.Output == null ? "-" : Fmt(b.Output.X) + "," + Fmt(b.Output.Y)) + "/" + b.OutputToSlot + " in " + (b.Input == null ? "-" : Fmt(b.Input.X) + "," + Fmt(b.Input.Y)) + "/" + b.InputFromSlot
                + " off " + b.InputOffset + "/" + b.OutputOffset + " par " + (b.Parameters == null ? "-" : string.Join(":", b.Parameters.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())) + " [" + b.Chain + "]");
        foreach (Port p in f.Ports) l.Add("port " + p);
        return string.Join("\n", l.ToArray());
    }

    /// (b) explicit Flow on every lane == no overrides (P08 motor row and the 12-smelter double row, record by record);
    /// an off-axis flow and a coated reversed lane throw ArgumentException.
    static string MicroLaneFlowDefaults()
    {
        var e = new Errs();
        RowSpec a = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        RowSpec b = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        b.InputFlows = new Dir?[] { Dir.N, Dir.N, Dir.N }; b.OutputFlow = Dir.N;
        string da = Dump(Modules.MachineRow(a)), db = Dump(Modules.MachineRow(b));
        e.Check(da == db, "MachineRow: explicit N flows differ from the default");
        RowSpec c = SmelterDoubleSpec(), d = SmelterDoubleSpec();
        d.InputFlows = new Dir?[] { Dir.N }; d.OutputFlow = Dir.N;
        e.Check(Dump(Modules.DoubleRow(c)) == Dump(Modules.DoubleRow(d)), "DoubleRow: explicit N flows differ from the default");
        // a row with Flow S: an override S is the default there, N reverses
        RowSpec s1 = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        s1.Flow = Dir.S;
        RowSpec s2 = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        s2.Flow = Dir.S; s2.InputFlows = new Dir?[] { Dir.S, null, Dir.S }; s2.OutputFlow = Dir.S;
        e.Check(Dump(Modules.MachineRow(s1)) == Dump(Modules.MachineRow(s2)), "MachineRow Flow S: explicit S flows differ from the default");
        RowSpec s3 = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        s3.Flow = Dir.S; s3.InputFlows = new Dir?[] { Dir.N };
        Fragment f3 = Modules.MachineRow(s3);
        Port p3 = f3.Ports.First(p => p.Name == "in:" + Ids.IronIngot);
        e.Check(p3.Dir == Dir.N && f3.Blds.Where(x => x.Kind == Kind.Belt && x.Carries == Ids.IronIngot).All(x => NearYaw(x.Yaw, 0, 0.01)), "Flow S row with iron N: port " + p3 + ", belts not yawed N");
        // rejected specs
        RowSpec bad = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        bad.InputFlows = new Dir?[] { Dir.E };
        try { Modules.MachineRow(bad); e.L.Add("InputFlows E accepted"); } catch (ArgumentException) { }
        RowSpec bad2 = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        bad2.OutputFlow = Dir.W;
        try { Modules.MachineRow(bad2); e.L.Add("OutputFlow W accepted"); } catch (ArgumentException) { }
        RowSpec bad3 = Rows34Spec("motor", MotorRecipe, new[] { Ids.IronIngot, GearId, Ids.MagneticCoil }, new[] { 240.0, 120.0, 120.0 }, MotorId, 120);
        bad3.InputFlows = new Dir?[] { Dir.S }; bad3.CoatItems = new[] { Ids.IronIngot }; bad3.ProliferatorItem = Coaters.ProliferatorMk1;
        try { Modules.MachineRow(bad3); e.L.Add("coater on a reversed lane accepted"); } catch (ArgumentException) { }
        return e.Result();
    }

    /// (c) double row: both ore lanes descend (S), the shared ingot lane ascends (N).
    static string MicroLaneFlowDouble()
    {
        var e = new Errs();
        RowSpec spec = SmelterDoubleSpec();
        spec.InputFlows = new Dir?[] { Dir.S };
        Fragment f = Modules.DoubleRow(spec);
        foreach (string p in spec.Problems) e.L.Add("problem: " + p);
        var machines = f.Blds.Where(b => b.Kind == Kind.Machine).ToList();
        e.Check(machines.Count == 12, machines.Count + " machines, expected 12");
        int north = (int)Math.Round(machines.Max(m => m.Y)) + 1;
        var la = CheckLane(e, f, "in:" + Ids.IronOre, Ids.IronOre, Dir.S, "ore A");
        var lb = CheckLane(e, f, "in:" + Ids.IronOre + DoubleRows.PortSuffixB, Ids.IronOre, Dir.S, "ore B");
        var lo = CheckLane(e, f, "out:" + Ids.IronIngot, Ids.IronIngot, Dir.N, "ingot");
        Port pa = f.Ports.First(p => p.Name == "in:" + Ids.IronOre), pb = f.Ports.First(p => p.Name == "in:" + Ids.IronOre + DoubleRows.PortSuffixB), po = f.Ports.First(p => p.Name == "out:" + Ids.IronIngot);
        e.Check(pa.Y >= north && pb.Y >= north, "ore In ports at y " + pa.Y + " / " + pb.Y + ", expected the north end (>= " + north + ")");
        e.Check(po.Y == north && po.X == 0, "ingot Out port " + po + ", expected (0," + north + ")");
        var sorters = f.Blds.Where(b => b.Kind == Kind.Sorter).ToList();
        var laneBelts = new HashSet<Bld>(la.Concat(lb).Concat(lo));
        foreach (Bld m in machines)
        {
            var own = sorters.Where(s => s.Input == m || s.Output == m).ToList();
            e.Check(own.Count == 2, "machine " + m + " has " + own.Count + " sorters, expected 2");
            foreach (Bld s in own)
            {
                Bld belt = s.Output == m ? s.Input : s.Output;
                e.Check(belt != null && laneBelts.Contains(belt), "sorter " + s.Role + " of " + m + " is not on a lane belt");
            }
        }
        var l0 = new Layout(gd);
        l0.Add(f);
        ValidationReport belts = new Validator(gd, null).ValidateBelts(l0);
        foreach (string m in belts.Errors) e.L.Add("ValidateBelts: " + m);
        RowSpec spec2 = SmelterDoubleSpec();
        spec2.InputFlows = new Dir?[] { Dir.S };
        Fragment f2 = Modules.DoubleRow(spec2);
        foreach (Port p in f2.Ports) if (p.Kind == PortKind.In && p.Dir == Dir.S) p.FedFromBand = true;
        ValidateFragment(e, f2, IronPlan(12), "smelters 2x6 ore S", "laneflow-double-s.txt", null);
        Console.WriteLine("      ports: " + string.Join(" | ", f.Ports.Select(p => p.ToString()).ToArray()));
        return e.Result();
    }
}
