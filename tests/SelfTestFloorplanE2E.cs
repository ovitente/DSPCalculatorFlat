// SelfTestFloorplanE2E.cs - full path through the pattern-driven composer (engine\FloorplanComposer.cs): plan -> floorplan ->
// bands -> towers -> Validator -> export round trip, no fallback. MAKEBLOCK_CASES filter "micro e2e" (fast fixtures, no
// search reference), "e2e" (all, with the search composer's numbers where it finishes inside its budget). Writes the
// blueprints to the worktree's work\floorplan (never to the user's Blueprint folder).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const int E2eSearchBudgetMs = 60000;

    static partial void RunFloorplanE2ECases()
    {
        Case("micro e2e: coil 360 through the floorplan composer (no fallback), validator + export", () => E2eFixture("coil-360", Ids.MagneticCoil, 360, null, false, null));
        Case("micro e2e: electric motor 120 through the floorplan composer (no fallback), validator + export", () => E2eFixture("motor-120", MotorId, 120, null, false, null));
        Case("e2e: turbine 60 through the floorplan composer (no fallback), validator + export", () => E2eFixture("turbine-60", gd.ResolveItem("Electromagnetic Turbine"), 60, null, false, null));
        Case("e2e: missile set 90 through the floorplan composer (no fallback), validator + export", () => E2eFixture("missile-90", MissileSetId, 90, null, false, new[] { "no valid layout in 409 s (60 s budget per mode set, measured 2026-11-09)" }));
        Case("e2e: missile turret 7.5 through the floorplan composer (no fallback), validator + export + rule check vs useredit3", () => E2eFixture("missile-turret-7.5", TurretId, 7.5, null, true, new[] { "48x30, 780 buildings", "40x26, 565 buildings, 451 belts, 68 raised" }));
        Case("micro e2e: --floorplan fallback: an unsupported plan falls back to the search composer with a note", E2eFallback);
    }

    static string E2eOutDir()
    {
        string d = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\work\floorplan"));
        if (d.StartsWith(Path.GetFullPath(bpDir), StringComparison.OrdinalIgnoreCase)) throw new Exception("refusing to write into the Blueprint folder");
        Directory.CreateDirectory(d);
        return d;
    }

    static string E2eStats(Layout l)
    {
        Rect b = l.Bounds();
        int[] rc = Composer.RouteCounts(l);
        return b.Width + "x" + b.Height + ", " + l.All.Count + " buildings, " + l.Count(Kind.Belt, 0) + " belts, " + rc[0] + " raised, " + rc[2] + " corners, " + l.Count(Kind.Splitter, 0) + " splitters";
    }

    /// reference = null: the search composer is run for its numbers inside E2eSearchBudgetMs; else the known strings are printed.
    static string E2eFixture(string name, int item, double rate, Action<BlockSpec> tune, bool rules, string[] reference)
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = item, Rate = rate, AddMargin = true };
        if (tune != null) tune(spec);
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var notes = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (Environment.GetEnvironmentVariable("MAKEBLOCK_E2E_NOTES") != null) EngineTuning.DebugNotes = true;
        Layout l = FloorplanComposer.Compose(gd, spec, plan, notes);
        long ms = clock.ElapsedMilliseconds;
        foreach (string n in notes.Where(n => n.StartsWith("floorplan composer", StringComparison.Ordinal))) Console.WriteLine("      " + n);
        string dump = Environment.GetEnvironmentVariable("MAKEBLOCK_E2E_NOTES");
        if (dump != null) foreach (string n in notes.Where(n => n.StartsWith("fp:", StringComparison.Ordinal))) Console.WriteLine("      " + n);
        if (dump != null) foreach (string n in notes) Console.WriteLine("      note: " + n);
        Console.WriteLine("      floorplan: " + E2eStats(l) + " (" + ms + " ms)");
        foreach (string m in l.Errors) e.L.Add(m);
        if (l.Errors.Count == 0)
        {
            ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
            foreach (string m in rep.Errors) e.L.Add("validate: " + m);
            foreach (string w in rep.Warnings) Console.WriteLine("      warn: " + w);
            if (rep.Ok)
            {
                BpHeader h = Emitter.HeaderFor(l, plan, spec, gd);
                ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, h);
                foreach (string m in x.Errors) e.L.Add("export: " + m);
                var check = new ValidationReport();
                GameEmitter.CheckExport(x, l, check);
                foreach (string m in check.Errors) e.L.Add("check-export: " + m);
                if (e.L.Count == 0)
                {
                    string path = Path.Combine(E2eOutDir(), name + ".txt");
                    GameBp.Write(path, x.Text);
                    Console.WriteLine("      written " + path);
                }
            }
        }
        if (dump != null) Console.Write(Composer.Map(l));
        if (rules && plan.Steps.Count > 0)
        {
            Step fs = plan.StepFor(plan.TargetItem);
            if (fs != null && fs.Recipe != null)
                foreach (RuleResult r in Rules.Check(l, plan, fs.Recipe.Id)) Console.WriteLine("      " + r);
        }
        if (reference != null)
        {
            Console.WriteLine("      search reference: " + reference[0]);
            if (reference.Length > 1) Console.WriteLine("      user's edit: " + reference[1]);
        }
        else if (!FilterIsMicroOnly())
        {
            ProductionPlan sp = new Solver(gd, spec).Solve();
            var comp = new Composer(gd, spec) { TimeBudgetMs = E2eSearchBudgetMs };
            var sc = System.Diagnostics.Stopwatch.StartNew();
            Layout s = comp.Compose(sp);
            bool ok = new Validator(gd, spec).Validate(s, sp).Ok;
            Console.WriteLine("      search:    " + (ok ? E2eStats(s) : "no valid layout") + " (" + sc.ElapsedMilliseconds + " ms)");
        }
        return e.Result();
    }

    static bool FilterIsMicroOnly()
    {
        if (Environment.GetEnvironmentVariable("MAKEBLOCK_E2E_NOSEARCH") != null) return true;
        string f = Environment.GetEnvironmentVariable("MAKEBLOCK_CASES");
        return !string.IsNullOrEmpty(f) && f.IndexOf("micro e2e", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// FlatEngine with the floorplan on: a plan the floorplan refuses (product edge not opposite the input edge) goes to the search
    /// composer, and the notes say why and which composer produced the result.
    static string E2eFallback()
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = FcGearId, Rate = 60, OutEdge = Dir.E };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        PlanInput input = PlanMapper.FromPlan(plan, spec);
        bool on = EngineTuning.Floorplan, only = EngineTuning.FloorplanOnly;
        EngineTuning.Floorplan = true; EngineTuning.FloorplanOnly = false;
        EngineResult res;
        try { res = FlatEngine.Generate(Source(), input, null, null); }
        finally { EngineTuning.Floorplan = on; EngineTuning.FloorplanOnly = only; }
        e.Check(res.Ok, "fallback did not produce a block: " + string.Join("; ", res.Errors.ToArray()));
        e.Check(res.Notes.Any(n => n.Contains("floorplan not used")), "no note naming the floorplan failure");
        e.Check(res.Notes.Any(n => n == "composer: search"), "the result does not say the search composer made it");
        Console.WriteLine("      " + string.Join(" | ", res.Notes.Where(n => n.Contains("floorplan") || n.StartsWith("composer", StringComparison.Ordinal)).ToArray()));
        return e.Result();
    }
}
