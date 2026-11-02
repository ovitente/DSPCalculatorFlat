// SelfTestLabs.cs - self tests of the Matrix Lab towers (engine\LabTowers.cs) and Miniature Particle Collider rows
// (engine\Collider.cs), both laid out by engine\SlotRows.cs. Part of SelfTest (same runner, MAKEBLOCK_CASES filter).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static partial void RunLabCases()
    {
        Case("micro labs: lab / collider cells and the lab tower stacking re-read from their source blueprints", MicroLabCells);
        Case("micro labs: Electromagnetic Matrix 160/min (8 labs) -> towers within the sorter caps, Mk.I cap -> more, lower towers, valid + export", MicroLabTowers);
        Case("micro collider: Deuterium 240/min from external Hydrogen -> collider row valid + export; byproduct / 6-input steps still dropped", MicroColliderDeuterium);
        Case("pipeline labs: Electromagnetic Matrix 60/min full chain (solver) with a lab tower row, valid + export", PipelineLabChain);
    }

    static string MicroLabCells()
    {
        Cells c = GetCells();
        var e = new Errs();
        string line = c.Log.FirstOrDefault(l => l.StartsWith("lab / collider sorter cells checked"));
        int want = SlotRows.Defs().Sum(d => d.Sources.Count);
        e.Check(line != null && line.Contains(": " + want + " ok"), "slot-row self-check: " + (line ?? "missing") + ", expected " + want + " ok");
        e.Check(c.Log.Any(l => l.StartsWith("lab tower stacking checked")), "lab tower stacking not checked");
        e.Check(c.Prototypes.ProtoFor(Ids.MatrixLab) != null && c.Prototypes.ProtoFor(Ids.Collider) != null, "no lab / collider prototype");
        // every canonical cell lands on the slot the record names (slot table of the engine) at all 4 machine yaws
        foreach (SlotRowDef d in SlotRows.Defs())
            foreach (SorterGeo g0 in d.Cells)
                for (int q = 0; q < 4; q++)
                {
                    SorterGeo g = g0.Rotated(q);
                    var m = Bld.Machine(d.MachineItem, 0, 0, g.MachineYaw, 0);
                    var s = new Bld { Kind = Kind.Sorter, X = g.Sx, Y = g.Sy, X2 = g.Ex, Y2 = g.Ey };
                    int slot;
                    e.Check(SlotRows.SlotFor(m, s, g.BeltToMachine, out slot) && slot == g.Slot, g.Name + " rot" + (90 * q) + ": slot table gives " + slot);
                }
        // collider footprint: off-centre, rotation-aware (yaw 90: body reaches 5.85 m north, 4.45 m south)
        Rect r = new Footprints(gd).TileRect(Bld.Machine(Ids.Collider, 0, 0, 90, 40));
        e.Check(r.X0 == -2 && r.X1 == 2 && r.Y0 == -4 && r.Y1 == 5, "collider yaw 90 tile rect " + r);
        Bld up = Bld.Machine(Ids.MatrixLab, 0, 0, 0, 9); up.Z = LabTowers.LevelZ;
        e.Check(new Footprints(gd).TileRect(up).IsEmpty, "stacked lab occupies tiles");
        return e.Result();
    }

    static PlanInput LabPlan(double rate, int labs)
    {
        var p = new PlanInput { TargetItemId = 6001, TargetPerMin = rate };
        var s = new PlanStep { RecipeId = 9, BuildingItemId = Ids.MatrixLab, MachineCount = rate / 20.0, Machines = labs };
        s.InputsPerMin[Ids.MagneticCoil] = rate; s.InputsPerMin[1301] = rate; s.OutputsPerMin[6001] = rate;
        p.Steps.Add(s);
        p.ExternalItems.Add(Ids.MagneticCoil); p.ExternalItems.Add(1301);
        p.Options.TimeBudgetMs = 60000;
        return p;
    }

    /// Runs the engine, checks validator + export, returns the tower heights (base lab -> labs).
    static Dictionary<Bld, int> RunTowers(PlanInput p, string tag, Errs e, out EngineResult res)
    {
        res = FlatEngine.Generate(Source(), p, null, null);
        foreach (string m in res.Errors) e.L.Add(tag + " engine: " + m);
        var size = new Dictionary<Bld, int>();
        if (!res.Ok) return size;
        HashSet<Bld> upper;
        LabTowers.Index(res.Layout.All, out upper, out size);
        e.Check(res.Report != null && res.Report.Ok, tag + ": validator not ok");
        e.Check(!res.Warnings.Any(w => w.Contains("dropped")), tag + ": unexpected drop: " + string.Join("; ", res.Warnings.ToArray()));
        // stacked records: each upper lab links to the lab one level below (index), z step LevelZ, multi-level slots
        foreach (Bld u in upper)
        {
            BpBuilding rec = res.Buildings[u.Index];
            e.Check(u.Input != null && rec.tempInputObjIdx == u.Input.Index && Math.Abs(rec.localOffset_z - (u.Input.Z + LabTowers.LevelZ)) < 1e-4
                && rec.outputToSlot == 14 && rec.inputFromSlot == 15 && rec.outputFromSlot == 15 && rec.inputToSlot == 14
                && rec.parameters != null && rec.parameters.Length == 2 && rec.parameters[0] == 1, tag + ": stacked record " + u);
        }
        ExportResult xr = new GameEmitter(gd, GetCells()).Serialize(res.Buildings, res.Header, res.Layout);
        var check = new ValidationReport();
        GameEmitter.CheckExport(xr, res.Layout, check);
        foreach (string m in xr.Errors.Concat(check.Errors)) e.L.Add(tag + " export: " + m);
        Console.WriteLine("      " + tag + ": " + res.Buildings.Count + " records, " + res.AreaWidth + "x" + res.AreaHeight + ", towers "
            + string.Join("/", size.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", export " + (xr.Ok ? "ok" : "FAILED"));
        return size;
    }

    static string MicroLabTowers()
    {
        var e = new Errs();
        EngineResult res;
        // Mk.II: per lab 20/min coil, 20/min circuit, 20/min matrix; input L1 180 -> 9 labs, output L2 90 -> 4 labs => 2 x 4
        var t = RunTowers(LabPlan(160, 8), "Mk.II", e, out res);
        e.Check(t.Values.Sum() == 8 && t.Count == 2 && t.Values.All(h => h == 4), "Mk.II towers " + string.Join(",", t.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", expected 4,4");
        if (res.Ok)
        {
            int ls = res.Layout.All.Count(b => b.Kind == Kind.Sorter && ((b.Input != null && LabTowers.IsLab(b.Input.ItemId)) || (b.Output != null && LabTowers.IsLab(b.Output.ItemId))));
            e.Check(ls == 2 * 3, "Mk.II: " + ls + " lab sorters, expected 3 per tower");
        }
        // Mk.I cap: L1 90 -> 4, L2 45 -> 2 => 4 towers of 2 (more, lower towers)
        PlanInput p1 = LabPlan(160, 8); p1.Options.SorterItemMax = Ids.SorterMk1;
        p1.Options.Buffer = false;   // the buffer depot cell has Sorter Mk.II prototypes only (pre-existing, not lab related)
        var t1 = RunTowers(p1, "Mk.I", e, out res);
        e.Check(t1.Values.Sum() == 8 && t1.Count == 4 && t1.Values.All(h => h == 2), "Mk.I towers " + string.Join(",", t1.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", expected 2,2,2,2");
        // LabStackMax 3 (tech cap) below the sorter cap: 8 labs -> 3 towers 3/3/2
        PlanInput p3 = LabPlan(160, 8); p3.Options.LabStackMax = 3;
        var t3 = RunTowers(p3, "cap 3", e, out res);
        e.Check(t3.Values.Sum() == 8 && t3.Count == 3 && t3.Values.Max() == 3, "LabStackMax 3 towers " + string.Join(",", t3.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", expected 3,3,2");
        // validator negative: a tower taller than LabStackMax fails
        if (res.Ok)
        {
            var spec = new BlockSpec { Item = 6001, Rate = 160, LabStackMax = 2 };
            var rep = new Validator(gd, spec).Validate(res.Layout, res.Plan);
            e.Check(rep.Errors.Any(m => m.Contains("LabStackMax")), "tower above LabStackMax not rejected");
        }
        return e.Result();
    }

    static string MicroColliderDeuterium()
    {
        var e = new Errs();
        // Deuterium: 10 Hydrogen -> 5 Deuterium in 2.5 s = 240/min in, 120/min out per collider; 240/min -> 2 colliders
        var p = new PlanInput { TargetItemId = 1121, TargetPerMin = 240 };
        var s = new PlanStep { RecipeId = 40, BuildingItemId = Ids.Collider, MachineCount = 2, Machines = 2 };
        s.InputsPerMin[1120] = 480; s.OutputsPerMin[1121] = 240;
        p.Steps.Add(s); p.ExternalItems.Add(1120);
        p.Options.TimeBudgetMs = 60000;
        EngineResult res = FlatEngine.Generate(Source(), p, null, null);
        foreach (string m in res.Errors) e.L.Add("engine: " + m);
        if (res.Ok)
        {
            e.Check(res.Report.Ok, "validator not ok");
            e.Check(res.Layout.Count(Kind.Machine, Ids.Collider) == 2, res.Layout.Count(Kind.Machine, Ids.Collider) + " colliders, expected 2");
            // 240/min hydrogen per collider > one Mk.II L1 sorter (180): two input sorters per collider, one output
            var cols = res.Layout.All.Where(b => b.Kind == Kind.Machine).ToList();
            foreach (Bld c in cols)
            {
                int ins = res.Layout.All.Count(b => b.Kind == Kind.Sorter && b.Output == c), outs = res.Layout.All.Count(b => b.Kind == Kind.Sorter && b.Input == c);
                e.Check(ins == 2 && outs == 1, "collider at (" + c.X + "," + c.Y + "): " + ins + " in / " + outs + " out sorters, expected 2 / 1");
            }
            if (cols.Count == 2)
            {
                double pitch = Math.Abs(cols[0].X - cols[1].X) + Math.Abs(cols[0].Y - cols[1].Y);
                e.Check(pitch >= 9, "collider pitch " + Fmt(pitch) + " below the collider pitch 9");
                e.Check(gd.ColliderGap(cols[0], cols[1], 1.0) >= 0, "colliders overlap");
            }
            ExportResult xr = new GameEmitter(gd, GetCells()).Serialize(res.Buildings, res.Header, res.Layout);
            var check = new ValidationReport();
            GameEmitter.CheckExport(xr, res.Layout, check);
            foreach (string m in xr.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
            Console.WriteLine("      deuterium: " + res.Buildings.Count + " records, " + res.AreaWidth + "x" + res.AreaHeight + ", export " + (xr.Ok ? "ok" : "FAILED"));
        }
        // still dropped: Mass-energy Storage (byproduct), Universe Matrix (6 inputs), Self-evolution Lab (no cells)
        Func<int, int, string> why = (recipe, building) => StepSupport.WhyNot(gd, gd.Recipe(recipe), building);
        e.Check(why(74, Ids.Collider) != null, "Mass-energy Storage (byproduct) is not dropped");
        e.Check(why(75, Ids.MatrixLab) != null, "Universe Matrix (6 inputs) is not dropped");
        e.Check(why(9, 2902) != null, "Self-evolution Lab is not dropped");
        e.Check(why(104, Ids.Collider) == null, "Strange Matter (3 inputs) dropped: " + why(104, Ids.Collider));
        e.Check(why(18, Ids.MatrixLab) == null && why(9, Ids.MatrixLab) == null, "matrix recipes dropped");
        e.Check(why(1, Ids.ChemicalPlant) != null && why(1, Ids.Refinery) != null, "chemical plant / refinery not dropped");
        return e.Result();
    }

    static string PipelineLabChain()
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = 6001, Rate = 60 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Step lab = plan.StepFor(6001);
        e.Check(lab != null && lab.MachineItem == Ids.MatrixLab && lab.Machines == 3, "solver: " + plan.Describe(gd));
        PlanInput input = PlanMapper.FromPlan(plan, spec);
        input.Options.TimeBudgetMs = 120000;
        EngineResult res;
        var t = RunTowers(input, "chain", e, out res);
        e.Check(t.Values.Sum() == 3 && t.Values.All(h => h <= 4), "towers " + string.Join(",", t.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()));
        return e.Result();
    }
}
