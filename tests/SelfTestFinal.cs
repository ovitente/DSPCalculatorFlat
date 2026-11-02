// SelfTestFinal.cs - self tests of the final machine cell (engine\FinalCell.cs: rules 4, 5, 6, 7, 9, 11).
// Part of SelfTest (MAKEBLOCK_CASES filter "micro final"). Micro fixtures: small Electric Motor plans with raw items,
// so one Composer run takes seconds instead of the turret's minutes.
using System;
using System.Collections.Generic;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const int MotorId = 1203, CoilId = 1202, FcGearId = 1201, IronIngotId = 1101, MagnetId = 1102, CopperIngotId = 1104;

    static partial void RunFinalCases()
    {
        Case("micro final: rule 7 inline depot turned to yaw 90/180/270 = rigid paste rotation of useredit3 #530 #27 #382 (depot yaw 0, slots +3 per quarter)", MicroFinalDepotTurn);
        Case("micro final: motor 22.5 (iron, gear, magnet, copper raw) -> coil machine-to-machine S (rule 4), 1 L1 lane per face (6), inline depot (7), valid + export", MicroFinalMotorRaw);
        Case("micro final: motor 22.5 (iron, magnet, copper raw) -> coil M2M, gear lane on the E face (rule 5), rules 6/7 hold, valid", MicroFinalMotorGear);
        Case("micro final: motor 22.5 with product edge E -> cell turned 90 deg, valid, rules 6/7 hold", MicroFinalMotorEast);
        Case("micro final: FinalCell.Enabled = false -> the old final row (no depot cell, rule 7 not met)", MicroFinalOff);
    }

    static BlockSpec MotorSpec(params int[] raw)
    {
        var spec = new BlockSpec { Item = MotorId, Rate = 22.5 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        spec.Buffer = true;
        foreach (int r in raw) spec.TreatAsRaw.Add(r);
        return spec;
    }

    /// Composes, validates, checks the rule results and (optionally) the export. Returns the layout.
    static Layout FinalCompose(Errs e, BlockSpec spec, out ProductionPlan plan, out List<RuleResult> rs, int[] mustPass, bool export)
    {
        plan = new Solver(gd, spec).Solve();
        var comp = new Composer(gd, spec);
        Layout l = comp.Compose(plan);
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        Step fs = plan.StepFor(spec.Item);
        rs = Rules.Check(l, plan, fs.Recipe.Id);
        foreach (int rule in mustPass)
        {
            RuleResult r = rs.FirstOrDefault(x => x.Rule == rule);
            e.Check(r != null && r.Hard && r.Ok, "rule " + rule + ": " + (r == null ? "not measured" : r.Detail));
        }
        if (export && rep.Errors.Count == 0)
        {
            ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, Emitter.HeaderFor(l, plan, spec, gd));
            var check = new ValidationReport();
            GameEmitter.CheckExport(x, l, check);
            foreach (string m in x.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
        }
        Rect bx = l.Bounds();
        Console.WriteLine("      " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height);
        foreach (string n in comp.Notes.Where(n => n.Contains("final cell") || n.StartsWith("rules:"))) Console.WriteLine("      " + n);
        return l;
    }

    static string MicroFinalDepotTurn()
    {
        var e = new Errs();
        Bld m0 = Bld.Machine(Ids.AsmMk1, 3, 12, 0, 129);
        Fragment f0 = FinalCell.InlineDepot(m0, Ids.SorterMk2, Ids.BeltMk2, TurretId);
        Fragment r0 = Rules.InlineDepot(m0, Ids.SorterMk2, Ids.BeltMk2, TurretId);
        e.Check(f0 != null && r0 != null && f0.Blds.Count == 6, "yaw 0 cell missing");
        if (f0 == null || r0 == null) return e.Result();
        for (int i = 0; i < 6; i++)
            e.Check(Near(f0.Blds[i].X, r0.Blds[i].X, 1e-9) && Near(f0.Blds[i].Y, r0.Blds[i].Y, 1e-9), "yaw 0 differs from Rules.InlineDepot at " + r0.Blds[i]);
        for (int q = 1; q < 4; q++)
        {
            Bld m = Bld.Machine(Ids.AsmMk1, 3, 12, 90 * q, 129);
            Fragment f = FinalCell.InlineDepot(m, Ids.SorterMk2, Ids.BeltMk2, TurretId);
            e.Check(f != null, "yaw " + 90 * q + ": no cell");
            if (f == null) continue;
            for (int i = 0; i < 6; i++)
            {
                Bld a = r0.Blds[i], b = f.Blds[i];
                double ax = a.X - 3, ay = a.Y - 12, ex = ax, ey = ay;
                for (int k = 0; k < q; k++) { double t = ex; ex = ey; ey = -t; }   // clockwise quarter turn
                e.Check(Near(b.X - 3, ex, 1e-9) && Near(b.Y - 12, ey, 1e-9), "yaw " + 90 * q + ": " + b + " is not the turned " + a);
                if (b.Kind == Kind.Storage) e.Check(Near(b.Yaw, 0, 1e-9), "yaw " + 90 * q + ": depot turned to " + b.Yaw);
                if (b.Kind == Kind.Sorter && b.Output != null && b.Output.Kind == Kind.Storage) e.Check(b.OutputToSlot == (7 + 3 * q) % 12, "yaw " + 90 * q + ": depot in slot " + b.OutputToSlot);
                if (b.Kind == Kind.Sorter && b.Input != null && b.Input.Kind == Kind.Storage) e.Check(b.InputFromSlot == (1 + 3 * q) % 12, "yaw " + 90 * q + ": depot out slot " + b.InputFromSlot);
                if (b.Kind == Kind.Sorter && b.Input == m)
                {
                    int want = Geo.SlotAt(m.Yaw, b.X - m.X, b.Y - m.Y, Dirs.RotateCW(Dir.N, q));
                    e.Check(b.InputFromSlot == want && want == 1, "yaw " + 90 * q + ": machine slot " + b.InputFromSlot + " vs table " + want);
                }
            }
            Port p = f.Ports.First();
            e.Check(p.Dir == Dirs.RotateCW(Dir.N, q), "yaw " + 90 * q + ": port flows " + p.Dir);
        }
        e.Check(FinalCell.InlineDepot(Bld.Machine(Ids.AsmMk1, 3, 12, 45, 129), Ids.SorterMk2, Ids.BeltMk2, TurretId) == null, "yaw 45 built");
        return e.Result();
    }

    static void CheckCell(Errs e, Layout l, int dedicatedRecipe)
    {
        Bld fin = l.All.FirstOrDefault(b => b.Kind == Kind.Machine && b.Role == "rule 6 final machine");
        if (fin == null) { e.L.Add("no final cell in the layout"); return; }
        var m2m = l.All.Where(b => b.Kind == Kind.Sorter && b.Output == fin && b.Input != null && b.Input.Kind == Kind.Machine).ToList();
        if (dedicatedRecipe > 0)
        {
            e.Check(m2m.Count == 2 && m2m.All(s => s.SorterLength == 3 && s.Input.RecipeId == dedicatedRecipe), "rule 4: " + m2m.Count + " machine-to-machine sorters (want 2 x L3 from recipe " + dedicatedRecipe + ")");
            Bld ded = m2m.Count > 0 ? m2m[0].Input : null;
            if (ded != null)
            {
                // rule 11: both input lanes of the dedicated machine end on one face, at their dead ends
                var ins = l.All.Where(b => b.Kind == Kind.Sorter && b.Output == ded).ToList();
                e.Check(ins.Count == 2 && ins.All(s => s.Input.Kind == Kind.Belt && s.Input.Output == null && s.SorterLength == 1), "rule 11: dedicated machine inputs " + ins.Count + " (want 2 L1 from lane dead ends)");
            }
        }
        // rule 6: belt inputs L1, lane dead ends
        var beltIn = l.All.Where(b => b.Kind == Kind.Sorter && b.Output == fin && b.Input != null && b.Input.Kind == Kind.Belt).ToList();
        e.Check(beltIn.All(s => s.SorterLength == 1 && s.Input.Output == null), "rule 6: a final input is not an L1 at a lane dead end");
    }

    static string MicroFinalMotorRaw()
    {
        var e = new Errs();
        ProductionPlan plan; List<RuleResult> rs;
        Layout l = FinalCompose(e, MotorSpec(IronIngotId, FcGearId, MagnetId, CopperIngotId), out plan, out rs, new[] { 4, 6, 7 }, true);
        CheckCell(e, l, plan.StepFor(CoilId).Recipe.Id);
        e.Check(l.Count(Kind.Storage, Ids.DepotMk1) == 1, l.Count(Kind.Storage, 0) + " depots (want the inline one only)");
        return e.Result();
    }

    static string MicroFinalMotorGear()
    {
        var e = new Errs();
        ProductionPlan plan; List<RuleResult> rs;
        // rule 4 is not required: the gear also feeds only the motor but takes iron like the motor (one port per item),
        // so only the coil can be the S-face machine; the gear lane takes a side face (E, or W in the mirrored variant)
        // and the gear machine sits in the neighbouring column (rule 5)
        Layout l = FinalCompose(e, MotorSpec(IronIngotId, MagnetId, CopperIngotId), out plan, out rs, new[] { 6, 7 }, false);
        CheckCell(e, l, plan.StepFor(CoilId).Recipe.Id);
        Bld fin = l.All.FirstOrDefault(b => b.Kind == Kind.Machine && b.Role == "rule 6 final machine");
        if (fin == null) return e.Result();
        Bld gearIn = l.All.FirstOrDefault(b => b.Kind == Kind.Sorter && b.Output == fin && b.Carries == FcGearId);
        e.Check(gearIn != null && Math.Abs(gearIn.X - fin.X) > 1.5, "rule 5: the gear lane does not end on a side face (" + (gearIn == null ? "none" : gearIn.ToString()) + ")");
        Bld gear = l.All.FirstOrDefault(b => b.Kind == Kind.Machine && b.RecipeId == plan.StepFor(FcGearId).Recipe.Id);
        e.Check(gear != null && Math.Abs(gear.X - fin.X) <= 7, "rule 5: the gear machine is not in the neighbouring column (" + (gear == null ? "none" : gear.ToString()) + ", final at " + fin.X + ")");
        RuleResult r4 = rs.First(x => x.Rule == 4);
        Console.WriteLine("      " + r4);
        return e.Result();
    }

    static string MicroFinalMotorEast()
    {
        var e = new Errs();
        BlockSpec spec = MotorSpec(IronIngotId, FcGearId, MagnetId, CopperIngotId);
        spec.OutEdge = Dir.E;
        ProductionPlan plan; List<RuleResult> rs;
        Layout l = FinalCompose(e, spec, out plan, out rs, new[] { 4, 6, 7 }, true);
        CheckCell(e, l, plan.StepFor(CoilId).Recipe.Id);
        Bld fin = l.All.First(b => b.Kind == Kind.Machine && b.Role == "rule 6 final machine");
        e.Check(Near(Dirs.Norm(fin.Yaw), 90, 0.01), "final machine yaw " + fin.Yaw + " (want 90: cell turned to the E edge)");
        return e.Result();
    }

    static string MicroFinalOff()
    {
        var e = new Errs();
        bool was = FinalCell.Enabled;
        FinalCell.Enabled = false;
        try
        {
            ProductionPlan plan; List<RuleResult> rs;
            Layout l = FinalCompose(e, MotorSpec(IronIngotId, FcGearId, MagnetId, CopperIngotId), out plan, out rs, new int[0], false);
            RuleResult r7 = rs.First(x => x.Rule == 7);
            e.Check(!r7.Ok, "rule 7 holds without the cell: " + r7.Detail);
            e.Check(!l.All.Any(b => b.Role == "rule 6 final machine"), "cell built while disabled");
        }
        finally { FinalCell.Enabled = was; }
        return e.Result();
    }
}
