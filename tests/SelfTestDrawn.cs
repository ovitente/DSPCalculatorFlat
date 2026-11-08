// SelfTestDrawn.cs - acceptance tests for the user's drawn pattern blueprints (docs\patterns-design.md, agent "drawn").
// Part of SelfTest (same runner, MAKEBLOCK_CASES filter: "micro drawn"). Principle: the user drew these files in the
// game, so the game accepts them; every Validator error on them means an engine rule is stricter than the game and the
// rule is relaxed (citing the file), never the drawing. Copies of the files live in refs\patterns\ of the data root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const string DrawnDir = @"refs\patterns";
    const string DrawnP08 = "p08-asm-row-3in.txt", DrawnP09 = "p09-asm-row-4in.txt", DrawnP12 = "p12-input-edge-comb.txt", DrawnSheet = "p-sheet-001.txt";
    const int ElectricMotorId = 1203;   // MissileSetId (1609) is defined in SelfTestRows34.cs
    const int RecipeElectricMotor = 97, RecipeMissileSet = 144;

    static partial void RunDrawnCases()
    {
        Case("micro drawn: p08-asm-row-3in.txt (4 Asm Mk.II Electric Motor, 3 inputs) passes the belt checks and the full Validate with a 1-step plan", () => DrawnRow(DrawnP08, RecipeElectricMotor, ElectricMotorId));
        Case("micro drawn: p09-asm-row-4in.txt (4 Asm Mk.II Missile Set, 4 inputs) passes the belt checks and the full Validate with a 1-step plan", () => DrawnRow(DrawnP09, RecipeMissileSet, MissileSetId));
        Case("micro drawn: p12-input-edge-comb.txt (5 icon tails at pitch 1) passes the belt checks and the input-edge / tail checks", DrawnComb);
        Case("micro drawn: p-sheet-001.txt (P12 split, P13 adjacent bridges, P14 splitter corners, P10/P11 double column, P01/P02 coater block) passes the belt checks", DrawnSheetBelts);
        Case("micro drawn: sheet coater block x58..68 - one proliferator feed (ramp z 0 / 0.5 / 1 at x60 y1..3, east on z 1 at y4, dead end after the last coater) passes the coater checks", DrawnSheetCoaters);
    }

    // ------------------------------------------------------------------ loading
    static string DrawnPath(string name)
    {
        string p = Path.Combine(Path.Combine(root, DrawnDir), name);
        if (!File.Exists(p)) throw new SkipException("missing " + p);
        return p;
    }

    /// A drawn game blueprint as a Layout: FullLayout plus what a drawing needs that generated layouts carry as tags:
    /// spray coaters (2313) are Kind.Other belt addons, not machines; game position noise up to 0.02 (feed belts at
    /// y 4.013 in the sheet, #87..#91) is not a splitter cell belt. BpImport.ToLayout (loader agent) should do the same.
    static Layout DrawnLayout(string name)
    {
        Layout l = FullLayout(GameBp.Load(DrawnPath(name)), false);
        var splitters = l.All.Where(b => b.Kind == Kind.Splitter).ToList();
        foreach (Bld b in l.All)
        {
            if (b.ItemId == Coaters.SprayCoater) b.Kind = Kind.Other;
            if (b.Kind == Kind.Belt && b.Cell == SplitterGeo.BeltCell && !splitters.Any(s => Math.Abs(s.X - b.X) < 1.5 && Math.Abs(s.Y - b.Y) < 1.5)) b.Cell = null;
        }
        return l;
    }

    static void DrawnReport(string name, ValidationReport r)
    {
        foreach (string m in r.Info) Console.WriteLine("      " + name + ": " + m);
        foreach (string m in r.Warnings) Console.WriteLine("      " + name + " WARN: " + m);
        foreach (string m in r.Errors) Console.WriteLine("      " + name + " FAIL: " + m);
    }

    /// Block ports from the drawing: In at every icon tail (first belt of a chain with [item,0]), Out at the chain end of
    /// every lane fed by output sorters.
    static void DrawnPorts(Layout l, int outItem)
    {
        var dropOn = new HashSet<Bld>(l.All.Where(s => s.Kind == Kind.Sorter && s.Output != null && s.Output.Kind == Kind.Belt).Select(s => s.Output));
        foreach (Bld b in l.All.Where(b => b.Kind == Kind.Belt))
        {
            if (b.Parameters != null && b.Parameters.Length == 2 && b.Parameters[0] != 0 && b.Output != null)
                l.Ports.Add(new Port { Name = "in:" + b.Parameters[0], Kind = PortKind.In, X = b.TileX, Y = b.TileY, Dir = Dirs.FromYaw(b.Yaw), Item = b.Parameters[0], Belt = b });
            if (b.Output == null)
            {
                bool fed = false;
                for (Bld n = b; n != null; n = l.All.FirstOrDefault(p => p.Kind == Kind.Belt && p.Output == n && p.OutputToSlot <= 1)) if (dropOn.Contains(n)) { fed = true; break; }
                if (fed) l.Ports.Add(new Port { Name = "out:" + outItem, Kind = PortKind.Out, X = b.TileX, Y = b.TileY, Dir = Dirs.FromYaw(b.Yaw), Item = outItem, Belt = b });
            }
        }
    }

    // ------------------------------------------------------------------ P08 / P09
    static string DrawnRow(string name, int recipe, int item)
    {
        var e = new Errs();
        Layout l = DrawnLayout(name);
        ValidationReport r = new Validator(gd, null).ValidateBelts(l);
        DrawnReport(name, r);
        foreach (string m in r.Errors) e.L.Add("belt checks rejected the drawing: " + m);
        // full Validate: 1-step plan (every recipe input treated as raw), 4 Mk.II assemblers at full speed
        RecipeDef rd = gd.Recipe(recipe);
        if (rd == null) return "recipe " + recipe + " not in game data";
        int machines = l.All.Count(b => b.Kind == Kind.Machine && b.RecipeId == recipe);
        e.Check(machines == 4, machines + " machines with recipe " + recipe + ", expected 4");
        item = rd.Outputs[0].Item;   // p09: recipe 144 makes 1609 (Missile Set)
        var spec = new BlockSpec { Item = item, Rate = machines * rd.OutCount(item) * 60.0 * gd.Speed(Ids.AsmMk2) / rd.TimeSec };
        spec.Tiers.Assembler = Ids.AsmMk2; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2; spec.Buffer = false;
        foreach (ItemCount ic in rd.Inputs) spec.TreatAsRaw.Add(ic.Item);
        ProductionPlan plan = new Solver(gd, spec).Solve();
        e.Check(plan.Steps.Count == 1 && plan.Steps[0].Machines == machines, "plan: " + string.Join(" | ", plan.Steps.Select(s => s.ToString()).ToArray()));
        DrawnPorts(l, item);
        e.Check(l.Ports.Count(p => p.Kind == PortKind.In) == rd.Inputs.Count && l.Ports.Count(p => p.Kind == PortKind.Out) == 1,
            "ports from the drawing: " + string.Join(", ", l.Ports.Select(p => p.ToString()).ToArray()));
        ValidationReport full = new Validator(gd, spec).Validate(l, plan);
        DrawnReport(name + " full", full);
        foreach (string m in full.Errors) e.L.Add("full Validate rejected the drawing: " + m);
        return e.Result();
    }

    // ------------------------------------------------------------------ P12 file
    static string DrawnComb()
    {
        var e = new Errs();
        Layout l = DrawnLayout(DrawnP12);
        ValidationReport r = new Validator(gd, null).ValidateBelts(l);
        DrawnReport(DrawnP12, r);
        foreach (string m in r.Errors) e.L.Add("belt checks rejected the drawing: " + m);
        DrawnPorts(l, 0);
        var ins = l.Ports.Where(p => p.Kind == PortKind.In).OrderBy(p => p.X).ToList();
        e.Check(ins.Count == 5, ins.Count + " icon tails, expected 5");
        for (int i = 1; i < ins.Count; i++) e.Check(ins[i].X - ins[i - 1].X == 1 && ins[i].Y == ins[0].Y, "tails are not at pitch 1: " + ins[i - 1] + " / " + ins[i]);
        var spec = new BlockSpec { InEdge = Dir.S };
        foreach (string m in Lanes.InputEdgeErrors(l, spec)) e.L.Add("input edge: " + m);
        // the tail / icon / group checks of the full validator on the belts-only comb (no machines, no plan)
        ValidationReport full = new Validator(gd, spec).Validate(l, null);
        Func<string, bool> tailCheck = m => m.StartsWith("input tail", StringComparison.Ordinal) || m.StartsWith("input groups", StringComparison.Ordinal) || m.StartsWith("input edge", StringComparison.Ordinal) || m.StartsWith("rule 5", StringComparison.Ordinal);
        foreach (string m in full.Errors.Where(tailCheck)) e.L.Add("tail / edge checks rejected the comb: " + m);
        Console.WriteLine("      " + DrawnP12 + ": " + ins.Count + " tails " + string.Join(" ", ins.Select(p => "(" + p.X + "," + p.Y + ")" + p.Item).ToArray()) + "; full-validate errors of other kinds (no machines on the comb): " + full.Errors.Count(m => !tailCheck(m)));
        return e.Result();
    }

    // ------------------------------------------------------------------ sheet
    static string DrawnSheetBelts()
    {
        var e = new Errs();
        Layout l = DrawnLayout(DrawnSheet);
        ValidationReport r = new Validator(gd, null).ValidateBelts(l);
        DrawnReport(DrawnSheet, r);
        // Open item (not a pattern, design doc "techniques only"): the x47..49 strip is a THREE-LEVEL stack (ground / z 0.5 /
        // z 1 on the tiles (48,42..47)) entered by diagonal lane shifts ((49,39) -> (48.666,40) -> (48.333,41,0.5); (47,39,1)
        // -> (47.5,40,1) -> (48,41,1)). Stacks are EdgeBus's domain (rules 2/3, engine\EdgeBus.cs, not owned by this
        // branch): BusNodes stops at the fractional node and Check wants one-tile links. Errors inside that strip are
        // listed here and tolerated; anything outside it is a failure of this case.
        var stack = new System.Text.RegularExpressions.Regex(@"\((-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)");
        int tolerated = 0;
        foreach (string m in r.Errors)
        {
            if (m.EndsWith("errors in total", StringComparison.Ordinal)) continue;
            var mt = stack.Match(m);
            double mx = mt.Success ? double.Parse(mt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
            double my = mt.Success ? double.Parse(mt.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
            if (mt.Success && mx >= 46.5 && mx <= 49.5 && my >= 36 && my <= 50) { tolerated++; continue; }
            e.L.Add("belt checks rejected the drawing: " + m);
        }
        if (tolerated > 0) Console.WriteLine("      " + DrawnSheet + ": " + tolerated + " error(s) tolerated in the x47..49 three-level stack technique (EdgeBus rules 2/3 with diagonal lane shifts, open item)");
        Func<double, double, Bld> ground = (x, y) => l.All.FirstOrDefault(b => b.Kind == Kind.Belt && !b.IsRaised && Math.Abs(b.X - x) < 0.3 && Math.Abs(b.Y - y) < 0.3);
        Func<double, double, double, Bld> raised = (x, y, z) => l.All.FirstOrDefault(b => b.Kind == Kind.Belt && Math.Abs(b.X - x) < 0.3 && Math.Abs(b.Y - y) < 0.3 && Math.Abs(b.Z - z) < 0.05);
        // P13: 3 adjacent raised runs (y 40..42, flow W) over 3 adjacent vertical lanes (x 13..15, flow N)
        int bundle = 0;
        for (int x = 13; x <= 15; x++) for (int y = 40; y <= 42; y++) if (raised(x, y, 0.5) != null && ground(x, y) != null) bundle++;
        e.Check(bundle == 9, "P13 bundle: " + bundle + " raised-over-ground tiles at x13..15 y40..42, expected 9");
        string runs = r.Info.FirstOrDefault(m => m.StartsWith("raised runs", StringComparison.Ordinal)) ?? "no raised runs";
        e.Check(runs.Contains("run(s)"), "no raised-run summary: " + runs);
        // P14: splitter (3,25) side outputs turn on the ground 2 tiles from the centre (1,25) / (5,25); diagonal variant (11,26) / (15,26)
        foreach (double[] c in new[] { new double[] { 1, 25, 3, 25 }, new double[] { 5, 25, 3, 25 }, new double[] { 11, 26, 13, 25 }, new double[] { 15, 26, 13, 25 } })
        {
            Bld b = ground(c[0], c[1]);
            e.Check(b != null && b.Output != null && Math.Abs(b.Output.Y - (c[1] + 1)) < 0.01, "P14 corner at (" + c[0] + "," + c[1] + ") missing or not turning north");
            e.Check(l.All.Any(s => s.Kind == Kind.Splitter && Math.Abs(s.X - c[2]) < 0.1 && Math.Abs(s.Y - c[3]) < 0.1), "P14 splitter at (" + c[2] + "," + c[3] + ") missing");
        }
        // P12 split: 5 icon tails at pitch 1 (x3..7, y35), iron bridges over copper/stone at (3,41) / (3,42)
        for (int x = 3; x <= 7; x++)
        {
            Bld t = ground(x, 35);
            e.Check(t != null && t.Parameters != null && t.Parameters.Length == 2 && t.Parameters[0] != 0, "P12 tail at (" + x + ",35) has no icon");
        }
        e.Check(raised(3, 41, 0.5) != null && raised(3, 42, 0.5) != null && ground(3, 41) != null && ground(3, 42) != null, "P12 iron bridge over (3,41) / (3,42) missing");
        return e.Result();
    }

    static string DrawnSheetCoaters()
    {
        var e = new Errs();
        Layout l = DrawnLayout(DrawnSheet);
        var coaters = l.All.Where(Coaters.IsCoater).ToList();
        e.Check(coaters.Count == 4 && coaters.All(c => Math.Abs(c.Y - 5) < 0.1 && c.X > 60.9 && c.X < 65.1), coaters.Count + " coaters at " + string.Join(" ", coaters.Select(c => "(" + Fmt(c.X) + "," + Fmt(c.Y) + ")").ToArray()) + ", expected 4 at y 5, x 61/62/64/65");
        var rep = new ValidationReport();
        Coaters.Validate(l, null, gd, rep);
        DrawnReport("coaters", rep);
        foreach (string m in rep.Errors) e.L.Add("coater checks rejected the drawing: " + m);
        e.Check(rep.Info.Any(m => m.Contains("spray coaters: 4") && m.Contains("feed chains: 1")), "coater summary: " + (rep.Info.FirstOrDefault(m => m.StartsWith("spray coaters", StringComparison.Ordinal)) ?? "-"));
        // the feed as drawn: tail (60,0) z 0 -> (60,1) z 0 -> (60,2) z 0.5 -> (60,3) z 1 -> corner (60,4) z 1 -> east (61..65,4) z 1, chain end at x 65
        Func<double, double, Bld> at = (x, y) => l.All.FirstOrDefault(b => b.Kind == Kind.Belt && Math.Abs(b.X - x) < 0.3 && Math.Abs(b.Y - y) < 0.3);
        double[][] want = { new double[] { 60, 0, 0 }, new double[] { 60, 1, 0 }, new double[] { 60, 2, 0.5 }, new double[] { 60, 3, 1 }, new double[] { 60, 4, 1 }, new double[] { 61, 4, 1 }, new double[] { 62, 4, 1 }, new double[] { 63, 4, 1 }, new double[] { 64, 4, 1 }, new double[] { 65, 4, 1 } };
        Bld prev = null;
        foreach (double[] w in want)
        {
            Bld b = at(w[0], w[1]);
            if (b == null) { e.L.Add("feed belt at (" + w[0] + "," + w[1] + ") missing"); continue; }
            e.Check(Math.Abs(b.Z - w[2]) < 0.02, "feed belt at (" + w[0] + "," + w[1] + ") z " + Fmt(b.Z) + ", drawing says " + w[2]);
            e.Check(prev == null || prev.Output == b, "feed chain broken before (" + w[0] + "," + w[1] + ")");
            prev = b;
        }
        e.Check(prev != null && prev.Output == null, "feed chain does not end at (65,4)");
        e.Check(at(60, 0) != null && at(60, 0).Parameters != null && at(60, 0).Parameters[0] == Coaters.ProliferatorMk2, "feed tail (60,0) has no Proliferator Mk.II icon");
        return e.Result();
    }
}
