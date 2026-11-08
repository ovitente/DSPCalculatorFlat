// SelfTest.cs - self tests of the block generator (lib\CONTRACT.md "tests/SelfTest.cs"). Prints PASS/FAIL/SKIP per case.
// Writes only into <root>\work\selftest (never into the user's Blueprint folder), and only blueprints that passed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static int passed, failed, skipped;
    static partial void RunLabCases();   // feat-labs: implemented in SelfTestLabs.cs (SelfTest.exe only)
    static partial void RunBus2Cases();  // EDGEBUS HOOK: rules 2/3 stacked edge bus, tests\SelfTestBus2.cs (SelfTest.exe only)
    static partial void RunRuleCases();  // feat-rules: the user's 15 layout rules, implemented in SelfTestRules.cs (SelfTest.exe only)
    static partial void RunFinalCases(); // FINALCELL HOOK: final machine cell (rules 4-7, 9, 11), tests/SelfTestFinal.cs (SelfTest.exe only)
    static partial void RunPackCases();  // feat-rules-pack: packing rules 14/12/8, implemented in SelfTestPack.cs (SelfTest.exe only)
    static partial void RunDoubleCases(); // PATTERNS HOOK: double rows (engine\DoubleRows.cs), tests\SelfTestDouble.cs (SelfTest.exe only)
    static string root, bpDir;
    static GameData gd;
    static Cells cells;
    static string cellsError;

    const string RegressionFile = @"refs\coil-360-v3.txt";

    /// Signals a case that cannot run yet (e.g. the Composer is still a stub). Counted as SKIP, not as PASS.
    class SkipException : Exception { public SkipException(string m) : base(m) { } }

    /// Runs all self tests; returns 0 when everything passes. root = blueprint-tools folder, bpDir = user Blueprint folder.
    public static int Run(string rootDir, string blueprintDir)
    {
        root = rootDir; bpDir = blueprintDir; Cells.ToolsRoot = rootDir;
        passed = failed = skipped = 0;
        try { gd = GameDataFiles.Load(Path.Combine(root, @"analysis\game-data.json"), Path.Combine(root, "item-ids.json")); }
        catch (Exception e) { Console.WriteLine("FAIL  game data: " + Msg(e)); return 1; }

        Case("solver: Magnetic Coil 360/min -> magnet 9, copper ingot 3, coil asm 4", SolverCoil);
        Case("model: fragment rotation keeps machine slots", ModelRotation);
        Case("model: Footprints.TileRect of 3x3 machines", ModelFootprint);
        Case("modules: 1-input MachineRow = SMELT-COL-1 cells", ModulesSmelterRow);
        Case("modules: 2-input MachineRow = coil-360-v3 assembler cells", ModulesAssemblerRow);
        Case("validator: hard-coded coil-360-v3 model is valid", ValidatorPositive);
        Case("validator: rejects L2 input sorter on a 1-input machine", () => ValidatorNegative("l2"));
        Case("validator: rejects a Mk.III belt", () => ValidatorNegative("mk3"));
        Case("validator: rejects a missing input-belt icon", () => ValidatorNegative("icon"));
        Case("validator: rejects overlapping buildings", () => ValidatorNegative("overlap"));
        Case("emitter: header of the coil-360-v3 model", EmitterHeader);
        Case("emitter: step-1 regression, hard-coded coil-360-v3 model == refs\\coil-360-v3.txt", EmitterRegression);
        Case("emitter: link to a building outside the layout -> no export", EmitterNegative);
        Case("pipeline: --item \"Magnetic Coil\" --rate 360 vs refs\\coil-360-v3.txt", PipelineCoil);
        Case("modules: assembler rows use proven pitch 4, input lanes end at the last pickup", ModulesPitchAndLaneEnd);
        Case("validator: rejects assemblers at pitch 3 (game collider 3.04 tiles), accepts pitch 4", ValidatorCollider);
        Case("validator: rejects an input-lane tail beyond the last pickup", () => ValidatorNegative("tail"));
        Case("reference: refs\\engine-coil-360.txt (engine output built in game, re-saved by the user)", GameProvenEngineCoil);
        Case("reference: refs\\engine-electric-motor-120.txt (game: 'collides with another object') fails the collider rule", GameRejectedEngineMotor);
        Case("reference: refs\\engine-electric-motor-120-v2.txt (game: towers too close) fails the power-node distance rule", GameRejectedMotorV2Towers);
        Case("reference: game-proven files and the pipeline output keep power nodes >= the minimum distance", PowerNodeDistanceProven);
        Case("reference: refs\\engine-electric-motor-120-v3-user.txt (user's in-game edit, 5 towers) passes the power check, minus a tower fails", GameProvenUserMotorTowers);
        Case("validator: rejects an input icon on a pickup tile (no tail)", () => ValidatorNegative("notail"));
        Case("validator: rejects interleaved raw input lanes (iron, copper, iron)", ValidatorInterleave);
        Case("pipeline: input tails, grouped lanes, systematic towers (coil 360)", PipelineTailsAndTowers);
        Case("cells: Geo.SlotAt by sorter face is exact at all 48 smelter/assembler slot poses x 4 yaws", SlotAtByFace);
        Case("game data: colliders.json pitches (smelter 3, assembler 4, splitter 2 / 3 up to 60 deg) and the splitter's cross-shaped collider", CollidersData);
        Case("reference: refs\\bridges-splitters-user.txt (game-proven bridges + 1->3 splitter) passes the conveyor checks", GameProvenBridges);
        Case("modules: BeltRun bridge = cell BRIDGE of refs\\bridges-splitters-user.txt (C8, over 2 belts)", ModulesBridgeCell);
        Case("validator: rejects bad bridges (z 1.0, turn on a ramp foot, over a belt end, two-level)", ValidatorBadBridges);
        Case("validator: belt link limits (2.302 m, 36.87 deg) and power nodes incl. accumulators", ValidatorLinkLimits);
        Case("pipeline: coil 1080 = one copper row + splitter, bridge crossing, grouped raw lanes", PipelineCoil1080);
        Case("reference: game-proven splitter files (splitters, bridges-splitters-user, engine-coil-1080-v1) pass the splitter clearance", GameProvenSplitterClearance);
        Case("reference: refs\\engine-turbine-60-v1.txt / engine-missile-90-v1.txt (game: belts too close to a splitter) fail the splitter clearance", GameRejectedSplitterClearance);
        Case("game data: belt probe vs splitter arm (side tile collides, diagonal tile clear)", BeltProbeSplitter);
        Case("solver: --raw \"Iron Ingot@Steel\" = block input for steel only", SolverPerStepRaw);
        Case("composer: 4 consumer rows -> parallel producer rows (<= 3 per splitter), --own gets its own row", PackFourConsumers);
        Case("composer: rotated layouts (--in-edge W/N/E) turn the splitter side slots, splitter stays at yaw 0", RotatedSplitterSlots);
        Case("validator: per-row machine load (1-smelter row 45/min + 6-smelter row 360/min of one step)", ValidatorPerRowLoad);
        Case("modules/validator: output sorter vs full-speed machine output (circuit on Mk.II, 120 > L2 90)", OutputSorterFullSpeed);
        Case("kit: Missile Turret 22.5/min -> 11 validated modules, port standard, export round trip", KitTurret);

        Case("reference: refs\\engine-turbine-60-v2-userupgrade.txt (user's in-game edit: raised runs, depot buffer, m39/m40) passes the conveyor checks; its accidental stacked splitter fails the stacked-splitter rule", GameProvenUserTurbine);
        Case("modules: DepotBuffer = cell DEPOT_BUFFER of the user's file (depot #482, sorters #491-#493)", ModulesDepotCell);
        Case("modules: SplitterV m39 / m40 = the user's Tab variants (#279, #280: ports, heights, slots)", ModulesSplitterVariants);
        Case("validator: rejects an unused splitter (< 2 belts) and a raised node over a building / next to a sorter end", ValidatorSplitterAndRaised);
        Case("pipeline: coil 360 with the buffer depot (cell DEPOT_BUFFER, game orientation), validator + export ok", PipelineCoilBuffer);
        // micro fixtures (seconds): MAKEBLOCK_CASES=micro
        Case("micro a: raised run with raised corners 315/225 over a parallel same-direction belt", MicroRaisedParallel);
        Case("micro b+d: gear 120 = 3-machine row + buffer depot on a short product belt, validator + export", MicroGearBuffer);
        Case("micro c: splitter m39 / m40 with 2 outputs pass the belt checks", MicroSplitterVariants);
        Case("micro text: thumbnail icon tags (digits, item tags, 40-char cut) and bullet desc survive export + game header parse", MicroText);
        Case("micro text: START\\engine-coil-360-v3.txt model re-emitted with the new header -> work\\text\\coil-360-text.txt", MicroTextCoil);
        Case("micro bus: Electric Motor 45 bus = straight lanes, 4 modules with aligned bus seams, all valid + export", MicroBusMotor);
        // engine API (docs\engine-api.md): injected plan -> FlatEngine.Generate (also micro)
        Case("micro flat: gear 120 via PlanInput -> FlatEngine.Generate == Composer, records serialize + export check", MicroFlatGear);
        Case("micro flat: unsupported building / byproduct steps dropped, products become input lanes; target dropped -> error", MicroFlatDrop);
        Case("micro flat: cancel stops FlatEngine.Generate with an error, no buildings", MicroFlatCancel);
        // lanes (PLAN.md decisions 7, 8, 9; engine\Lanes.cs)
        Case("micro lanes: tier choice helpers (cheapest belt/sorter inside the cap, floor(lane / demand), pile cap -> Mk.III)", MicroLanesHelpers);
        Case("micro lanes: 13 smelters (11.9 exact, 714/min) on 720/min Mk.II -> 2 parallel lanes of 7 + 6, side by side on the edge", MicroLanesSplit13);
        Case("micro lanes: 1000/min stone with Mk.III allowed -> one Mk.III lane; Mk.II cap -> 2 parallel Mk.II lanes; Mk.III records export", MicroLanesMk3);
        Case("micro lanes: dropped-step product lane = icon tail on the input edge (S and W); a lane off the edge fails", MicroLanesDroppedTail);
        RunBus2Cases();  // EDGEBUS HOOK: engine\EdgeBus.cs (SelfTestBus2.cs)
        RunRuleCases();  // feat-rules: engine\Rules.cs against the user's game-proven turret edits (SelfTestRules.cs)
        RunFinalCases(); // FINALCELL HOOK
        RunPackCases();  // feat-rules-pack: engine\Packing.cs (SelfTestPack.cs)
        RunLabCases();   // feat-labs: Matrix Lab towers, collider rows (SelfTestLabs.cs; partial method, absent in MakeBlock.exe)
        RunDoubleCases(); // PATTERNS HOOK: double rows (SelfTestDouble.cs; partial method, absent in MakeBlock.exe)
        // spray coaters (engine\Coaters.cs, COATERS HOOK; cases in tests\SelfTestCoaters.cs)
        Case("micro coaters: 2-input final step incLevel 1 -> 2 coaters + 1 proliferator lane with icon tail, valid + export", MicroCoaters);
        Case("micro coaters: incLevel 0 -> no coaters, no proliferator lane; validator rejects a coater without its feed node", MicroCoatersNone);
        Console.WriteLine("selftest: " + passed + " passed, " + failed + " failed, " + skipped + " skipped");
        return failed == 0 && skipped == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ runner
    static void Case(string name, Func<string> body)
    {
        // MAKEBLOCK_CASES = comma-separated substrings: run only the matching cases (micro iteration)
        string filter = Environment.GetEnvironmentVariable("MAKEBLOCK_CASES");
        if (!string.IsNullOrEmpty(filter) && !filter.Split(',').Any(f => f.Length > 0 && name.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result;
        try { result = body(); }
        catch (SkipException e) { skipped++; Console.WriteLine("SKIP  " + name + ": " + e.Message); return; }
        catch (NotImplementedException e) { result = "not implemented yet: " + e.Message; }
        catch (Exception e) { result = Msg(e); }
        if (result == null) { passed++; Console.WriteLine("PASS  " + name); }
        else { failed++; Console.WriteLine("FAIL  " + name + ": " + result); }
    }

    static string Msg(Exception e)
    {
        while (e.InnerException != null && (e is System.Reflection.TargetInvocationException)) e = e.InnerException;
        if (e is NotImplementedException) return "not implemented yet: " + e.Message;
        return e.GetType().Name + ": " + e.Message;
    }

    static string Fmt(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static bool Near(double a, double b, double tol) { return Math.Abs(a - b) <= tol; }
    static bool NearYaw(double a, double b, double tol) { return Math.Abs(((a - b) % 360.0 + 540.0) % 360.0 - 180.0) <= tol; }

    /// Collects failures of one case; null when empty.
    class Errs
    {
        public readonly List<string> L = new List<string>();
        public void Check(bool ok, string m) { if (!ok) L.Add(m); }
        public string Result() { return L.Count == 0 ? null : string.Join("; ", L.Take(8).ToArray()) + (L.Count > 8 ? "; ... (" + L.Count + " problems)" : ""); }
    }

    static Cells GetCells()
    {
        if (cells != null) return cells;
        if (cellsError != null) throw new Exception(cellsError);
        try
        {
            var c = Cells.Load(bpDir, gd);
            if (c.Errors.Count > 0) { cellsError = "prototype errors: " + string.Join("; ", c.Errors.ToArray()); throw new Exception(cellsError); }
            cells = c;
            return c;
        }
        catch (NotImplementedException e) { cellsError = "not implemented yet: " + e.Message; throw new Exception(cellsError); }
    }

    static string OutDir()
    {
        string d = Path.Combine(root, @"work\selftest");
        string full = Path.GetFullPath(d), bp = Path.GetFullPath(bpDir);
        if (full.StartsWith(bp, StringComparison.OrdinalIgnoreCase)) throw new Exception("refusing to write into the Blueprint folder");
        Directory.CreateDirectory(full);
        return full;
    }

    // ------------------------------------------------------------------ 1. solver
    static BlockSpec CoilSpec()
    {
        var spec = new BlockSpec { Item = Ids.MagneticCoil, Rate = 360 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        spec.Buffer = false;   // regression vs the pre-buffer references (the buffer has its own case)
        return spec;
    }

    static string SolverCoil()
    {
        var plan = new Solver(gd, CoilSpec()).Solve();
        var e = new Errs();
        Action<int, int, int, int> step = (item, recipe, machine, n) =>
        {
            Step s = plan.StepFor(item);
            if (s == null) { e.L.Add("no step for " + item); return; }
            e.Check(s.Machines == n && Near(s.MachinesExact, n, 1e-6), "item " + item + ": " + s.Machines + " (" + Fmt(s.MachinesExact) + ") machines, expected " + n);
            e.Check(s.Recipe != null && s.Recipe.Id == recipe, "item " + item + ": recipe " + (s.Recipe != null ? s.Recipe.Id : 0) + ", expected " + recipe);
            e.Check(s.MachineItem == machine, "item " + item + ": machine " + s.MachineItem + ", expected " + machine);
        };
        step(Ids.Magnet, Ids.RecipeMagnet, Ids.ArcSmelter, 9);
        step(Ids.CopperIngot, Ids.RecipeCopperIngot, Ids.ArcSmelter, 3);
        step(Ids.MagneticCoil, Ids.RecipeMagneticCoil, Ids.AsmMk1, 4);
        e.Check(plan.Steps.Count == 3, plan.Steps.Count + " steps, expected 3");
        e.Check(plan.Steps.Count > 0 && plan.Steps[plan.Steps.Count - 1].Item == Ids.MagneticCoil, "target step is not last");
        Func<int, double> raw = id => plan.RawInputs.Where(r => r.Item == id).Sum(r => r.Rate);
        e.Check(Near(raw(Ids.IronOre), 360, 1e-6), "raw iron ore " + Fmt(raw(Ids.IronOre)) + ", expected 360");
        e.Check(Near(raw(Ids.CopperOre), 180, 1e-6), "raw copper ore " + Fmt(raw(Ids.CopperOre)) + ", expected 180");
        e.Check(plan.RawInputs.Count == 2, plan.RawInputs.Count + " raw inputs, expected 2");
        return e.Result();
    }

    // ------------------------------------------------------------------ 2. model
    static string ModelRotation()
    {
        var e = new Errs();
        var f = new Fragment("rot");
        Bld m = f.Add(Bld.Machine(Ids.ArcSmelter, 0, 0, 0, Ids.RecipeCopperIngot));
        Bld belt = f.Add(Bld.Belt(Ids.BeltMk2, 2, -1, 0, Ids.CopperOre, "c"));
        Bld s = f.Add(Bld.Sorter(Ids.SorterMk2, 2, -0.797, 0.811, -0.795, 270, belt, m, 5, 1, 4, Ids.CopperOre));
        f.AddPort(new Port { Name = "in", Kind = PortKind.In, X = 2, Y = -1, Dir = Dir.N, Belt = belt });
        f.RotateCW(1, 0, 0);
        e.Check(s.OutputToSlot == 5 && s.InputFromSlot == -1, "sorter slots changed: " + s.OutputToSlot + "/" + s.InputFromSlot);
        e.Check(NearYaw(m.Yaw, 90, 1e-9), "machine yaw " + Fmt(m.Yaw) + ", expected 90");
        e.Check(Near(s.X, -0.797, 1e-9) && Near(s.Y, -2, 1e-9), "sorter start (" + Fmt(s.X) + "," + Fmt(s.Y) + "), expected (-0.797,-2)");
        e.Check(Near(s.X2, -0.795, 1e-9) && Near(s.Y2, -0.811, 1e-9), "sorter end (" + Fmt(s.X2) + "," + Fmt(s.Y2) + "), expected (-0.795,-0.811)");
        e.Check(NearYaw(s.Yaw, 0, 1e-9), "sorter yaw " + Fmt(s.Yaw) + ", expected 0");
        e.Check(belt.TileX == -1 && belt.TileY == -2 && NearYaw(belt.Yaw, 90, 1e-9), "belt at (" + belt.TileX + "," + belt.TileY + ") yaw " + Fmt(belt.Yaw));
        Port p = f.Port("in");
        e.Check(p.X == -1 && p.Y == -2 && p.Dir == Dir.E, "port " + p);
        // four quarter turns are the identity
        f.RotateCW(3, 0, 0);
        e.Check(Near(s.X, 2, 1e-9) && Near(s.Y, -0.797, 1e-9) && NearYaw(s.Yaw, 270, 1e-9) && s.OutputToSlot == 5, "4 quarter turns are not the identity");
        return e.Result();
    }

    static string ModelFootprint()
    {
        var e = new Errs();
        var fp = new Footprints(gd);
        Rect r = fp.TileRect(Bld.Machine(Ids.ArcSmelter, 2, 2, 0, Ids.RecipeMagnet));
        e.Check(r.X0 == 1 && r.Y0 == 1 && r.X1 == 3 && r.Y1 == 3, "smelter at (2,2): " + r);
        r = fp.TileRect(Bld.Machine(Ids.AsmMk1, 2, 13, 90, Ids.RecipeMagneticCoil));
        e.Check(r.X0 == 1 && r.Y0 == 12 && r.X1 == 3 && r.Y1 == 14, "assembler at (2,13) yaw 90: " + r);
        r = fp.TileRect(Bld.Belt(Ids.BeltMk2, 4, 0, 0, 0, "x"));
        e.Check(r.Width == 1 && r.Height == 1 && r.X0 == 4 && r.Y0 == 0, "belt at (4,0): " + r);
        e.Check(fp.TileRect(Bld.Sorter(Ids.SorterMk2, 0, 0, 1, 0, 90, null, null, 0, 1, 0, 0)).IsEmpty, "sorter has a footprint");
        return e.Result();
    }

    // ------------------------------------------------------------------ 3. modules
    /// Expected sorter cell relative to the machine center (CONTRACT.md "Cells that MUST exist").
    class Cell
    {
        public string Name; public bool In; public int Bx, By; public double Sx, Sy, Ex, Ey; public int Slot, L; public int Carries;
        public Cell(string n, bool input, int bx, int by, double sx, double sy, double ex, double ey, int slot, int l, int carries)
        { Name = n; In = input; Bx = bx; By = by; Sx = sx; Sy = sy; Ex = ex; Ey = ey; Slot = slot; L = l; Carries = carries; }
    }

    static string CheckRow(Fragment f, int machineItem, double machineYaw, int count, Cell[] cellsWanted, string[] ports)
    {
        var e = new Errs();
        var machines = f.Blds.Where(b => b.Kind == Kind.Machine).ToList();
        e.Check(machines.Count == count, machines.Count + " machines, expected " + count);
        foreach (Bld m in machines)
        {
            e.Check(m.ItemId == machineItem && NearYaw(m.Yaw, machineYaw, 0.01), "machine " + m + ": expected item " + machineItem + " yaw " + machineYaw);
            var sorters = f.Blds.Where(s => s.Kind == Kind.Sorter && (s.Input == m || s.Output == m)).ToList();
            e.Check(sorters.Count == cellsWanted.Length, "machine at (" + Fmt(m.X) + "," + Fmt(m.Y) + "): " + sorters.Count + " sorters, expected " + cellsWanted.Length);
            foreach (Cell c in cellsWanted)
            {
                Bld hit = null;
                foreach (Bld s in sorters)
                {
                    bool input = s.Output == m;
                    Bld belt = input ? s.Input : s.Output;
                    if (input != c.In || belt == null || belt.Kind != Kind.Belt) continue;
                    if (!Near(belt.X - m.X, c.Bx, 0.01) || !Near(belt.Y - m.Y, c.By, 0.01)) continue;
                    hit = s;
                    e.Check(Near(s.X - m.X, c.Sx, 0.01) && Near(s.Y - m.Y, c.Sy, 0.01) && Near(s.X2 - m.X, c.Ex, 0.01) && Near(s.Y2 - m.Y, c.Ey, 0.01),
                        c.Name + ": start/end (" + Fmt(s.X - m.X) + "," + Fmt(s.Y - m.Y) + ")->(" + Fmt(s.X2 - m.X) + "," + Fmt(s.Y2 - m.Y) + ")");
                    int slot = input ? s.OutputToSlot : s.InputFromSlot;
                    e.Check(slot == c.Slot, c.Name + ": slot " + slot + ", expected " + c.Slot);
                    e.Check(s.SorterLength == c.L, c.Name + ": L " + s.SorterLength + ", expected " + c.L);
                    e.Check(belt.Carries == c.Carries, c.Name + ": lane carries " + belt.Carries + ", expected " + c.Carries);
                }
                e.Check(hit != null, "machine at (" + Fmt(m.X) + "," + Fmt(m.Y) + "): no sorter for cell " + c.Name);
            }
        }
        foreach (string p in ports) e.Check(f.Ports.Any(x => x.Name == p), "missing port " + p);
        return e.Result();
    }

    static string ModulesSmelterRow()
    {
        var spec = new RowSpec { Name = "copper", MachineItem = Ids.ArcSmelter, RecipeId = Ids.RecipeCopperIngot, Count = 3,
            InputItems = new[] { Ids.CopperOre }, InputRates = new[] { 180.0 }, OutputItem = Ids.CopperIngot, OutputRate = 180 };
        Fragment f = Modules.MachineRow(spec);
        return CheckRow(f, Ids.ArcSmelter, 0, 3, new[] {
            new Cell("smelter in E", true, 2, -1, 2, -0.797, 0.811, -0.795, 5, 1, Ids.CopperOre),
            new Cell("smelter out W", false, -2, 0, -0.811, 0, -2, 0, 10, 1, Ids.CopperIngot) },
            new[] { "in:" + Ids.CopperOre, "out:" + Ids.CopperIngot });
    }

    static string ModulesAssemblerRow()
    {
        var spec = new RowSpec { Name = "coil", MachineItem = Ids.AsmMk1, RecipeId = Ids.RecipeMagneticCoil, Count = 4,
            InputItems = new[] { Ids.Magnet, Ids.CopperIngot }, InputRates = new[] { 360.0, 180.0 }, OutputItem = Ids.MagneticCoil, OutputRate = 360 };
        Fragment f = Modules.MachineRow(spec);
        return CheckRow(f, Ids.AsmMk1, 90, 4, new[] {
            new Cell("asm in E L1", true, 2, -1, 2, -0.842, 0.874, -0.841, 2, 1, Ids.Magnet),
            new Cell("asm in W L1", true, -2, 1, -2, 0.842, -0.874, 0.841, 8, 1, Ids.CopperIngot),
            new Cell("asm out W L2", false, -3, 0, -0.874, 0, -3, 0, 7, 2, Ids.MagneticCoil) },
            new[] { "in:" + Ids.Magnet, "in:" + Ids.CopperIngot, "out:" + Ids.MagneticCoil });
    }

    // ------------------------------------------------------------------ hard-coded coil-360-v3 model (MakeCoilFactoryV3 coordinates)
    static int[][] Line(int x0, int y0, int x1, int y1)
    {
        var l = new List<int[]>();
        int dx = Math.Sign(x1 - x0), dy = Math.Sign(y1 - y0);
        for (int x = x0, y = y0; ; x += dx, y += dy) { l.Add(new[] { x, y }); if (x == x1 && y == y1) break; }
        return l.ToArray();
    }

    static int[][] Concat(params int[][][] parts)
    {
        var l = new List<int[]>();
        foreach (var p in parts) foreach (var q in p) { if (l.Count > 0 && l[l.Count - 1][0] == q[0] && l[l.Count - 1][1] == q[1]) continue; l.Add(q); }
        return l.ToArray();
    }

    static double DirYaw(int dx, int dy) { return dx > 0 ? 90 : dx < 0 ? 270 : dy > 0 ? 0 : 180; }

    /// Belt chain like MakeCoilFactoryV3.Chain: corner yaw = bisector of in/out, last tile outputToSlot 0.
    static List<Bld> Chain(Layout layout, string name, int carries, int[][] pts)
    {
        var made = new List<Bld>();
        for (int i = 0; i < pts.Length; i++)
        {
            int x = pts[i][0], y = pts[i][1];
            double yIn = i > 0 ? DirYaw(x - pts[i - 1][0], y - pts[i - 1][1]) : -1;
            double yOut = i < pts.Length - 1 ? DirYaw(pts[i + 1][0] - x, pts[i + 1][1] - y) : -1;
            double yaw = yIn < 0 ? yOut : yIn;
            if (yIn >= 0 && yOut >= 0 && yIn != yOut) { double diff = ((yOut - yIn + 540) % 360) - 180; yaw = (yIn + diff / 2 + 360) % 360; }
            Bld b = layout.Add(Bld.Belt(Ids.BeltMk2, x, y, yaw, carries, name));
            if (made.Count > 0) Layout.Link(made[made.Count - 1], b, 1);
            made.Add(b);
        }
        return made;
    }

    static void SmelterUnit(Layout l, int x, int y, int recipe, int ore, int product)
    {
        Bld m = l.Add(Bld.Machine(Ids.ArcSmelter, x, y, 0, recipe)); m.Carries = product; m.Role = "smelter";
        Bld si = l.Add(Bld.Sorter(Ids.SorterMk2, x + 2, y - 0.797, x + 0.811, y - 0.795, 270.001, l.BeltAt(x + 2, y - 1), m, 5, 1, 4, ore));
        si.Yaw2 = 270.059; si.Z = si.Z2 = -0.001;
        Bld so = l.Add(Bld.Sorter(Ids.SorterMk2, x - 0.811, y, x - 2, y, 269.942, m, l.BeltAt(x - 2, y), 10, 1, 0, product));
        so.Yaw2 = 269.999; so.Z = -0.003;
    }

    static void AssemblerUnit(Layout l, int x, int y)
    {
        Bld a = l.Add(Bld.Machine(Ids.AsmMk1, x, y, 90, Ids.RecipeMagneticCoil)); a.Carries = Ids.MagneticCoil; a.Role = "assembler";
        Bld s1 = l.Add(Bld.Sorter(Ids.SorterMk2, x + 2, y - 0.842, x + 0.874, y - 0.841, 270.004, l.BeltAt(x + 2, y - 1), a, 2, 1, 0, Ids.Magnet));
        s1.Yaw2 = 270.1; s1.Z = -0.001;
        Bld s2 = l.Add(Bld.Sorter(Ids.SorterMk2, x - 2, y + 0.842, x - 0.874, y + 0.841, 90.004, l.BeltAt(x - 2, y + 1), a, 8, 1, 0, Ids.CopperIngot));
        s2.Yaw2 = 90.1; s2.Z = -0.001;
        Bld s3 = l.Add(Bld.Sorter(Ids.SorterMk2, x - 0.874, y, x - 3, y, 270.001, a, l.BeltAt(x - 3, y), 7, 2, 0, Ids.MagneticCoil));
        s3.Yaw2 = 270; s3.Z = -0.001; s3.Z2 = -0.002;
    }

    /// The coil-360-v3 block as an abstract layout, in MakeCoilFactoryV3's building order.
    static Layout CoilV3Model()
    {
        var l = new Layout(gd);
        var copperOre = Chain(l, "in:" + Ids.CopperOre, Ids.CopperOre, Line(4, 0, 4, 7));
        var ironOre = Chain(l, "in:" + Ids.IronOre, Ids.IronOre, Line(10, 0, 10, 25));
        Chain(l, "copper", Ids.CopperIngot, Line(0, 2, 0, 26));
        Chain(l, "magnet", Ids.Magnet, Concat(Line(6, 2, 6, 27), Line(6, 27, 4, 27), Line(4, 27, 4, 12)));
        var coil = Chain(l, "out:" + Ids.MagneticCoil, Ids.MagneticCoil, Line(-1, 13, -1, 28));
        copperOre[0].Parameters = new[] { Ids.CopperOre, 0 };
        ironOre[0].Parameters = new[] { Ids.IronOre, 0 };
        foreach (int y in new[] { 2, 5, 8 }) SmelterUnit(l, 2, y, Ids.RecipeCopperIngot, Ids.CopperOre, Ids.CopperIngot);
        foreach (int y in new[] { 2, 5, 8, 11, 14, 17, 20, 23, 26 }) SmelterUnit(l, 8, y, Ids.RecipeMagnet, Ids.IronOre, Ids.Magnet);
        foreach (int y in new[] { 13, 17, 21, 25 }) AssemblerUnit(l, 2, y);
        foreach (var t in new[] { new[] { 5, 2 }, new[] { 5, 8 }, new[] { 2, 15 }, new[] { 2, 23 }, new[] { 11, 12 }, new[] { 11, 20 } })
            l.Add(Bld.Tesla(t[0], t[1]));
        l.Ports.Add(new Port { Name = "in:" + Ids.CopperOre, Kind = PortKind.In, X = 4, Y = 0, Dir = Dir.N, Item = Ids.CopperOre, Rate = 180, Belt = copperOre[0], Chain = copperOre[0].Chain });
        l.Ports.Add(new Port { Name = "in:" + Ids.IronOre, Kind = PortKind.In, X = 10, Y = 0, Dir = Dir.N, Item = Ids.IronOre, Rate = 360, Belt = ironOre[0], Chain = ironOre[0].Chain });
        Bld last = coil[coil.Count - 1];
        l.Ports.Add(new Port { Name = "out:" + Ids.MagneticCoil, Kind = PortKind.Out, X = -1, Y = 28, Dir = Dir.N, Item = Ids.MagneticCoil, Rate = 360, Belt = last, Chain = last.Chain });
        return l;
    }

    /// The plan coil-360-v3 implements (independent of the Solver): magnet 9, copper ingot 3, coil asm Mk.I 4.
    static ProductionPlan CoilPlan()
    {
        var p = new ProductionPlan { TargetItem = Ids.MagneticCoil, TargetRate = 360 };
        Func<int, int, int, int, double, Step> mk = (item, recipe, machine, n, rate) =>
        {
            RecipeDef r = gd.Recipe(recipe);
            var s = new Step { Item = item, Recipe = r, MachineItem = machine, Speed = gd.Speed(machine), MachinesExact = n, Machines = n, MaxOutputRate = rate };
            s.Outputs.Add(new ItemRate(item, rate));
            foreach (var i in r.Inputs) s.Inputs.Add(new ItemRate(i.Item, rate * i.Count / r.OutCount(item)));
            return s;
        };
        p.Steps.Add(mk(Ids.Magnet, Ids.RecipeMagnet, Ids.ArcSmelter, 9, 360));
        p.Steps.Add(mk(Ids.CopperIngot, Ids.RecipeCopperIngot, Ids.ArcSmelter, 3, 180));
        p.Steps.Add(mk(Ids.MagneticCoil, Ids.RecipeMagneticCoil, Ids.AsmMk1, 4, 360));
        p.Steps[0].ConsumerSteps.Add(2); p.Steps[1].ConsumerSteps.Add(2);
        p.RawInputs.Add(new ItemRate(Ids.IronOre, 360)); p.RawInputs.Add(new ItemRate(Ids.CopperOre, 180));
        return p;
    }

    // ------------------------------------------------------------------ 4. validator
    static string ValidatorPositive()
    {
        Layout l = CoilV3Model();
        if (l.Errors.Count > 0) return "model itself has layout errors: " + l.Errors[0];
        var rep = new Validator(gd, CoilSpec()).Validate(l, CoilPlan());
        return rep.Ok ? null : rep.Errors.Count + " errors, first: " + rep.Errors[0];
    }

    static string ValidatorNegative(string what)
    {
        Layout l = CoilV3Model();
        string change;
        switch (what)
        {
            case "l2":
            {
                // magnet smelter at (8,2): feed it from a new iron-ore tile at (11,1) with an L2 sorter over the L1 lane x=10
                Bld m = l.All.First(b => b.Kind == Kind.Machine && b.TileX == 8 && b.TileY == 2);
                Bld s = l.All.First(b => b.Kind == Kind.Sorter && b.Output == m);
                Bld nb = l.Add(Bld.Belt(Ids.BeltMk2, 11, 1, 0, Ids.IronOre, "in:" + Ids.IronOre + "#neg"));
                nb.Parameters = new[] { Ids.IronOre, 0 };
                s.Input = nb; s.X = 11; s.Parameters = new[] { 2 };
                change = "smelter (8,2) input sorter L2 from (11,1)";
                break;
            }
            case "mk3":
            {
                Bld b = l.All.First(x => x.Kind == Kind.Belt && x.TileX == 10 && x.TileY == 5);
                b.ItemId = Ids.BeltMk3; change = "belt (10,5) -> Mk.III";
                break;
            }
            case "icon":
                l.BeltAt(10, 0).Parameters = null; change = "icon removed from iron-ore belt (10,0)";
                break;
            case "notail":
            {
                // the iron-ore lane starts on its first pickup tile (10,1): drop the tail (10,0), icon onto the pickup
                Bld tail = l.BeltAt(10, 0), pick = l.BeltAt(10, 1);
                l.All.Remove(tail); l.Grid.Release(tail);
                pick.Parameters = new[] { Ids.IronOre, 0 };
                Port p = l.Ports.First(x => x.Kind == PortKind.In && x.Item == Ids.IronOre);
                p.Belt = pick; p.Y = 1;
                var r = new Validator(gd, CoilSpec()).Validate(l, CoilPlan());
                if (!r.Errors.Any(m => m.Contains("input tail"))) return "no input-tail error" + (r.Ok ? " (validator accepted)" : ", first error: " + r.Errors[0]);
                Console.WriteLine("      (" + r.Errors.First(m => m.Contains("input tail")) + ")");
                return null;
            }
            case "overlap":
                l.All.Add(Bld.Tesla(8, 5)); change = "tower inside the smelter at (8,5), added past Layout.Add";
                break;
            case "tail":
            {
                // copper-ore lane ends at its last pickup (4,7); one more tile north is a dead-end stub
                Bld end = l.BeltAt(4, 7);
                Bld nb = l.Add(Bld.Belt(Ids.BeltMk2, 4, 8, 0, Ids.CopperOre, end.Chain));
                Layout.Link(end, nb, 1); Layout.Link(nb, null, 0);
                change = "copper-ore lane extended to (4,8)";
                var r = new Validator(gd, CoilSpec()).Validate(l, CoilPlan());
                if (!r.Errors.Any(m => m.Contains("beyond its last pickup"))) return "no tail error for: " + change + (r.Ok ? " (validator accepted)" : ", first error: " + r.Errors[0]);
                Console.WriteLine("      (" + change + " -> " + r.Errors.First(m => m.Contains("beyond its last pickup")) + ")");
                return null;
            }
            default: throw new ArgumentException(what);
        }
        var rep = new Validator(gd, CoilSpec()).Validate(l, CoilPlan());
        if (rep.Ok) return "validator accepted: " + change;
        Console.WriteLine("      (" + change + " -> " + rep.Errors[0] + ")");
        return null;
    }

    // ------------------------------------------------------------------ 5. emitter
    static string EmitterHeader()
    {
        Layout l = CoilV3Model();
        BpHeader h = Emitter.HeaderFor(l, CoilPlan(), CoilSpec(), gd);
        var e = new Errs();
        e.Check(h.Width == 13 && h.Height == 29, "area " + h.Width + "x" + h.Height + ", expected 13x29");
        e.Check(h.Icons != null && h.Icons.Length == 5 && h.Icons[0] == Ids.MagneticCoil, "icon0 " + (h.Icons != null && h.Icons.Length > 0 ? h.Icons[0] : 0));
        e.Check(h.ShortDesc.StartsWith(@"\s604;\zzt1;") && h.ShortDesc.Length <= BpText.ThumbMax, "shortDesc '" + h.ShortDesc + "'");
        e.Check(h.Icons[1] == Ids.AsmMk1, "icon1 " + h.Icons[1]);
        e.Check(h.Desc.StartsWith("Magnetic Coil 360/min\n- in S: ") && h.Desc.Contains("\n- out N: Magnetic Coil 360") && h.Desc.Contains(" 180"), "desc '" + h.Desc + "'");
        string v = GameBp.InstalledGameVersion();
        e.Check(v != null && h.GameVersion == v, "game version '" + h.GameVersion + "', installed '" + v + "'");
        Console.WriteLine("      shortDesc: " + h.ShortDesc + " | version " + h.GameVersion);
        Console.WriteLine("      desc: " + h.Desc);
        return e.Result();
    }

    static string EmitterRegression()
    {
        Cells c = GetCells();
        Layout l = CoilV3Model();
        var e = new Errs();
        e.Check(l.Errors.Count == 0, "model layout errors: " + string.Join("; ", l.Errors.ToArray()));
        BpHeader h = Emitter.HeaderFor(l, CoilPlan(), CoilSpec(), gd);
        ExportResult x = new GameEmitter(gd, c).Emit(l, h);
        foreach (string m in x.Errors) e.L.Add("export: " + m);
        if (!x.Ok) return e.Result();

        object reference = GameBp.Load(Cells.Resolve(bpDir, RegressionFile));
        Array want = GameBp.Buildings(reference), got = GameBp.Buildings(x.Reimported);
        CompareCounts(got, want, e);
        CheckIcons(x.Reimported, e);
        int unmatched = CompareMultiset(got, want, e);
        object areaGot = ((Array)GameBp.Get(x.Reimported, "areas")).GetValue(0), areaWant = ((Array)GameBp.Get(reference, "areas")).GetValue(0);
        e.Check(GameBp.In(areaGot, "width") == GameBp.In(areaWant, "width") && GameBp.In(areaGot, "height") == GameBp.In(areaWant, "height"),
            "area " + GameBp.In(areaGot, "width") + "x" + GameBp.In(areaGot, "height") + ", reference " + GameBp.In(areaWant, "width") + "x" + GameBp.In(areaWant, "height"));
        Console.WriteLine("      " + got.Length + " buildings re-imported (reference " + want.Length + "), signature " + (x.SignatureValid ? "valid" : "INVALID")
            + ", " + unmatched + " records without an identical reference record");
        if (e.L.Count == 0)
        {
            string path = Path.Combine(OutDir(), "coil-360-v3-regen.txt");
            GameBp.Write(path, x.Text);
            Console.WriteLine("      written " + path);
        }
        return e.Result();
    }

    static string EmitterNegative()
    {
        Cells c = GetCells();
        Layout l = CoilV3Model();
        Bld s = l.All.First(b => b.Kind == Kind.Sorter);
        s.Output = Bld.Machine(Ids.ArcSmelter, 50, 50, 0, Ids.RecipeMagnet);   // never added to the layout
        ExportResult x = new GameEmitter(gd, c).Emit(l, Emitter.HeaderFor(l, CoilPlan(), CoilSpec(), gd));
        if (x.Ok || x.Text != null) return "emitter exported a layout with a dangling link";
        return null;
    }

    // ------------------------------------------------------------------ 6. full pipeline
    static string PipelineCoil()
    {
        BlockSpec spec = CoilSpec();
        ProductionPlan plan;
        Layout l;
        try { plan = new Solver(gd, spec).Solve(); l = new Composer(gd, spec).Compose(plan); }
        catch (NotImplementedException ex) { throw new SkipException("pipeline not available yet (" + ex.Message + " not implemented)"); }
        var e = new Errs();
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        Cells c = GetCells();
        BpHeader h = Emitter.HeaderFor(l, plan, spec, gd);
        ExportResult x = new GameEmitter(gd, c).Emit(l, h);
        foreach (string m in x.Errors) e.L.Add("export: " + m);
        if (!x.Ok) return e.Result();
        var check = new ValidationReport();
        GameEmitter.CheckExport(x, l, check);
        foreach (string m in check.Errors) e.L.Add("check-export: " + m);

        pipelineBp = x.Reimported;
        object reference = GameBp.Load(Cells.Resolve(bpDir, RegressionFile));
        Array want = GameBp.Buildings(reference), got = GameBp.Buildings(x.Reimported);
        // required: machines per (item, recipe) and sorters per (direction, machine, L); belts and towers may differ
        var req = new Errs();
        CompareCounts(got, want, req);
        foreach (string m in req.L) if (m.StartsWith("belt") || m.StartsWith("tesla") || m.StartsWith("total")) Console.WriteLine("      note: " + m); else e.L.Add(m);
        CheckIcons(x.Reimported, e);
        var desired = new Errs();
        int unmatched = CompareMultiset(got, want, desired);
        Console.WriteLine("      " + got.Length + " buildings (reference " + want.Length + "); identical multiset: " + (unmatched == 0 && got.Length == want.Length ? "yes" : "no, " + unmatched + " unmatched"));
        if (e.L.Count == 0)
        {
            string path = Path.Combine(OutDir(), "coil-360-pipeline.txt");
            GameBp.Write(path, x.Text);
            Console.WriteLine("      written " + path);
        }
        return e.Result();
    }

    static object pipelineBp;   // re-imported pipeline coil-360 (set by PipelineCoil)

    // ------------------------------------------------------------------ 7. pitch, input-lane ends, colliders, game-proven reference
    static string ModulesPitchAndLaneEnd()
    {
        var e = new Errs();
        var spec = new RowSpec { Name = "gear", MachineItem = Ids.AsmMk1, RecipeId = 5, Count = 3,
            InputItems = new[] { Ids.IronIngot }, InputRates = new[] { 120.0 }, OutputItem = 1201, OutputRate = 120 };
        Fragment f = Modules.MachineRow(spec);
        var ys = f.Blds.Where(b => b.Kind == Kind.Machine).Select(b => b.Y).OrderBy(y => y).ToList();
        for (int i = 1; i < ys.Count; i++) e.Check(Near(ys[i] - ys[i - 1], 4, 0.001), "1-input assembler row pitch " + Fmt(ys[i] - ys[i - 1]) + ", expected 4");
        // every input lane's last tile is a pickup tile; the output lane still spans the row
        foreach (Bld end in f.Blds.Where(b => b.Kind == Kind.Belt && b.Output == null))
        {
            bool pick = f.Blds.Any(s => s.Kind == Kind.Sorter && s.Input == end);
            bool isOut = f.Ports.Any(p => p.Kind == PortKind.Out && p.Belt == end);
            e.Check(pick || isOut, "lane " + end.Chain + " ends at (" + end.TileX + "," + end.TileY + ") without a pickup");
        }
        var bad = new RowSpec { Name = "gear3", MachineItem = Ids.AsmMk1, RecipeId = 5, Count = 3, Pitch = 3,
            InputItems = new[] { Ids.IronIngot }, InputRates = new[] { 120.0 }, OutputItem = 1201, OutputRate = 120 };
        try { Modules.MachineRow(bad); e.L.Add("assembler row at explicit pitch 3 was accepted"); }
        catch (ArgumentException) { }
        // smelters keep their proven pitch 3
        var sm = new RowSpec { Name = "copper", MachineItem = Ids.ArcSmelter, RecipeId = Ids.RecipeCopperIngot, Count = 2,
            InputItems = new[] { Ids.CopperOre }, InputRates = new[] { 120.0 }, OutputItem = Ids.CopperIngot, OutputRate = 120 };
        var sy = Modules.MachineRow(sm).Blds.Where(b => b.Kind == Kind.Machine).Select(b => b.Y).OrderBy(y => y).ToList();
        e.Check(Near(sy[1] - sy[0], 3, 0.001), "smelter row pitch " + Fmt(sy[1] - sy[0]) + ", expected 3");
        return e.Result();
    }

    static string ValidatorCollider()
    {
        var e = new Errs();
        foreach (int pitch in new[] { 3, 4 })
        {
            var l = new Layout(gd);
            l.Add(Bld.Machine(Ids.AsmMk1, 0, 0, 0, 5));
            l.Add(Bld.Machine(Ids.AsmMk1, 0, pitch, 0, 5));
            var rep = new Validator(gd, null).Validate(l, null);
            bool hit = rep.Errors.Any(m => m.Contains("collider overlap"));
            e.Check(hit == (pitch == 3), "assemblers at pitch " + pitch + ": collider error " + (hit ? "reported" : "missing"));
            if (hit) Console.WriteLine("      (pitch 3 -> " + rep.Errors.First(m => m.Contains("collider overlap")) + ")");
        }
        var s = new Layout(gd);
        s.Add(Bld.Machine(Ids.ArcSmelter, 0, 0, 0, Ids.RecipeMagnet));
        s.Add(Bld.Machine(Ids.ArcSmelter, 0, 3, 0, Ids.RecipeMagnet));
        e.Check(!new Validator(gd, null).Validate(s, null).Errors.Any(m => m.Contains("collider overlap")), "arc smelters at proven pitch 3 reported as colliding");
        return e.Result();
    }

    /// Dead-end belts (no output link) without an input sorter picking from them; the block product exit is one.
    static List<string> DeadEndsWithoutPickup(object bp)
    {
        var recs = GameBp.Buildings(bp);
        var picked = new HashSet<object>();
        foreach (object b in recs)
            if (Ids.IsSorter(GameBp.In(b, "itemId")))
            {
                object i = GameBp.Get(b, "inputObj");
                if (i != null && Ids.IsBelt(GameBp.In(i, "itemId"))) picked.Add(i);
            }
        var res = new List<string>();
        foreach (object b in recs)
            if (Ids.IsBelt(GameBp.In(b, "itemId")) && GameBp.Get(b, "outputObj") == null && !picked.Contains(b))
                res.Add("(" + Fmt(GameBp.Fl(b, "localOffset_x")) + "," + Fmt(GameBp.Fl(b, "localOffset_y")) + ")");
        return res;
    }

    /// Smallest collider gap between two machines (game-data sizes, yaw-aware), in tiles.
    static double MinMachineGap(object bp)
    {
        var ms = new List<object>();
        foreach (object b in GameBp.Buildings(bp)) { MachineDef md = gd.Machine(GameBp.In(b, "itemId")); if (md != null && md.RecipeType != null && md.FootprintTiles != null) ms.Add(b); }
        double min = double.MaxValue;
        for (int i = 0; i < ms.Count; i++)
            for (int j = i + 1; j < ms.Count; j++)
            {
                double[] a = gd.Machine(GameBp.In(ms[i], "itemId")).FootprintTiles, c = gd.Machine(GameBp.In(ms[j], "itemId")).FootprintTiles;
                double gx = Math.Abs(GameBp.Fl(ms[i], "localOffset_x") - GameBp.Fl(ms[j], "localOffset_x")) - (a[0] + c[0]) / 2;
                double gy = Math.Abs(GameBp.Fl(ms[i], "localOffset_y") - GameBp.Fl(ms[j], "localOffset_y")) - (a[1] + c[1]) / 2;
                min = Math.Min(min, Math.Max(gx, gy));
            }
        return min;
    }

    static string GameProvenEngineCoil()
    {
        string path = Cells.Resolve(bpDir, Geo.SourceFiles["eng360"]);
        if (!File.Exists(path)) return "missing " + path;
        var e = new Errs();
        object reference = GameBp.Load(path);
        Array want = GameBp.Buildings(reference);
        // the rules the engine now enforces hold in the game-proven file
        var dead = DeadEndsWithoutPickup(reference);
        e.Check(dead.Count == 1, "reference: dead ends without a pickup " + string.Join(" ", dead.ToArray()) + ", expected only the product exit");
        double gap = MinMachineGap(reference);
        e.Check(gap >= 0, "reference: machine colliders overlap by " + Fmt(-gap));
        Console.WriteLine("      reference " + want.Length + " buildings, min machine collider gap " + Fmt(gap) + " tile, dead ends " + string.Join(" ", dead.ToArray()));
        if (pipelineBp == null) { e.L.Add("pipeline case produced no blueprint"); return e.Result(); }
        // the pipeline output: same machines and sorter roles, no tails, not bigger than the reference
        Array got = GameBp.Buildings(pipelineBp);
        var req = new Errs();
        CompareCounts(got, want, req);
        foreach (string m in req.L) if (m.StartsWith("belt") || m.StartsWith("tesla") || m.StartsWith("total")) Console.WriteLine("      note: " + m); else e.L.Add(m);
        e.Check(got.Length <= want.Length, "pipeline " + got.Length + " buildings, reference " + want.Length);
        var deadGot = DeadEndsWithoutPickup(pipelineBp);
        e.Check(deadGot.Count == 1, "pipeline: dead ends without a pickup " + string.Join(" ", deadGot.ToArray()));
        object ag = ((Array)GameBp.Get(pipelineBp, "areas")).GetValue(0), aw = ((Array)GameBp.Get(reference, "areas")).GetValue(0);
        int wg = GameBp.In(ag, "width"), hg = GameBp.In(ag, "height"), ww = GameBp.In(aw, "width"), hw = GameBp.In(aw, "height");
        e.Check(wg * hg <= ww * hw, "pipeline area " + wg + "x" + hg + " > reference " + ww + "x" + hw);
        Console.WriteLine("      pipeline " + got.Length + " buildings, area " + wg + "x" + hg + " (reference " + ww + "x" + hw + ")");
        return e.Result();
    }

    static string GameRejectedEngineMotor()
    {
        string path = Cells.Resolve(bpDir, @"refs\engine-electric-motor-120.txt");
        if (!File.Exists(path)) throw new SkipException("missing " + path);
        double gap = MinMachineGap(GameBp.Load(path));
        Console.WriteLine("      min machine collider gap " + Fmt(gap) + " tile");
        return gap < 0 ? null : "the in-game rejected file passes the collider rule (min gap " + Fmt(gap) + ")";
    }

    /// Smallest distance (tiles) between two power nodes of a game blueprint record set (accumulator pairs exempt).
    static double MinPowerNodeDist(object bp)
    {
        var n = new List<object>();
        foreach (object b in GameBp.Buildings(bp)) if (gd.IsPowerNode(GameBp.In(b, "itemId"))) n.Add(b);
        double min = double.MaxValue;
        for (int i = 0; i < n.Count; i++)
            for (int j = i + 1; j < n.Count; j++)
            {
                if (GameData.IsAccumulator(GameBp.In(n[i], "itemId")) && GameData.IsAccumulator(GameBp.In(n[j], "itemId"))) continue;
                double dx = GameBp.Fl(n[i], "localOffset_x") - GameBp.Fl(n[j], "localOffset_x"), dy = GameBp.Fl(n[i], "localOffset_y") - GameBp.Fl(n[j], "localOffset_y");
                min = Math.Min(min, Math.Sqrt(dx * dx + dy * dy));
            }
        return min;
    }

    static string GameRejectedMotorV2Towers()
    {
        string path = Cells.Resolve(bpDir, @"refs\engine-electric-motor-120-v2.txt");
        if (!File.Exists(path)) throw new SkipException("missing " + path);
        object bp = GameBp.Load(path);
        double d = MinPowerNodeDist(bp), gap = MinMachineGap(bp);
        Console.WriteLine("      min power-node distance " + Fmt(d) + " tile (min " + Fmt(GameData.MinPowerNodeTiles) + "), min machine gap " + Fmt(gap));
        var e = new Errs();
        e.Check(d < GameData.MinPowerNodeTiles, "the in-game rejected file passes the power-node distance rule (" + Fmt(d) + ")");
        e.Check(gap >= 0, "motor-v2 machines were placeable in game, but the collider rule rejects them (" + Fmt(gap) + ")");
        return e.Result();
    }

    static string PowerNodeDistanceProven()
    {
        var e = new Errs();
        foreach (string rel in new[] { Geo.SourceFiles["eng360"], Geo.SourceFiles["v3"] })
        {
            string path = Cells.Resolve(bpDir, rel);
            if (!File.Exists(path)) { e.L.Add("missing " + path); continue; }
            double d = MinPowerNodeDist(GameBp.Load(path));
            Console.WriteLine("      " + rel + ": min power-node distance " + Fmt(d));
            e.Check(d >= GameData.MinPowerNodeTiles, rel + ": power nodes " + Fmt(d) + " tiles apart");
        }
        if (pipelineBp != null)
        {
            double d = MinPowerNodeDist(pipelineBp);
            Console.WriteLine("      pipeline coil 360: min power-node distance " + Fmt(d));
            e.Check(d >= GameData.MinPowerNodeTiles, "pipeline: power nodes " + Fmt(d) + " tiles apart");
        }
        return e.Result();
    }

    /// Machines, sorters and towers of a game blueprint as an abstract layout (positions only, no links): enough for
    /// the power check.
    static Layout PowerLayout(object bp, Func<object, bool> skip)
    {
        var l = new Layout(gd);
        foreach (object b in GameBp.Buildings(bp))
        {
            if (skip != null && skip(b)) continue;
            int item = GameBp.In(b, "itemId");
            double x = GameBp.Fl(b, "localOffset_x"), y = GameBp.Fl(b, "localOffset_y");
            if (item == Ids.Tesla) l.All.Add(Bld.Tesla(x, y));
            else if (Ids.IsSorter(item))
                l.All.Add(Bld.Sorter(item, x, y, GameBp.Fl(b, "localOffset_x2"), GameBp.Fl(b, "localOffset_y2"), GameBp.Fl(b, "yaw"), null, null, 0, 1, 0, 0));
            else if (!Ids.IsBelt(item) && item != Ids.Splitter)
                l.All.Add(Bld.Machine(item, x, y, GameBp.Fl(b, "yaw"), GameBp.In(b, "recipeId")));
        }
        return l;
    }

    static string GameProvenUserMotorTowers()
    {
        string path = Cells.Resolve(bpDir, @"refs\engine-electric-motor-120-v3-user.txt");
        if (!File.Exists(path)) return "missing " + path;
        object bp = GameBp.Load(path);
        var e = new Errs();
        Layout l = PowerLayout(bp, null);
        int towers = l.All.Count(b => b.Kind == Kind.Tesla);
        e.Check(towers == 5, towers + " towers, expected the user's 5");
        ValidationReport r = new Validator(gd, null).ValidatePower(l);
        foreach (string m in r.Errors) e.L.Add("user's tower set rejected: " + m);
        Console.WriteLine("      " + string.Join("; ", r.Info.ToArray()));
        // without the tower that powers the west smelters the same check must fail (the check is not vacuous)
        object drop = null; double bestD = double.MaxValue;
        foreach (object b in GameBp.Buildings(bp))
            if (GameBp.In(b, "itemId") == Ids.Tesla)
            {
                double d = Math.Abs(GameBp.Fl(b, "localOffset_x") - 8) + Math.Abs(GameBp.Fl(b, "localOffset_y") - 7);
                if (d < bestD) { bestD = d; drop = b; }
            }
        ValidationReport r2 = new Validator(gd, null).ValidatePower(PowerLayout(bp, b => b == drop));
        e.Check(!r2.Ok, "the user's set minus the tower near (8,7) still passes the power check");
        if (!r2.Ok) Console.WriteLine("      (minus one tower -> " + r2.Errors[0] + ")");
        // the engine's previous 10-tower version is valid too (more towers than needed, never fewer)
        string old = Cells.Resolve(bpDir, @"work\out\electric-motor-120-v3.txt");
        if (File.Exists(old)) e.Check(new Validator(gd, null).ValidatePower(PowerLayout(GameBp.Load(old), null)).Ok, "engine motor v3 (10 towers) rejected by the power check");
        return e.Result();
    }

    static string ValidatorInterleave()
    {
        var l = new Layout(gd);
        int[] items = { Ids.IronOre, Ids.CopperOre, Ids.IronOre };
        for (int i = 0; i < 3; i++)
        {
            Bld a = l.Add(Bld.Belt(Ids.BeltMk2, i * 2, 0, 0, items[i], "in#" + i));
            Bld b = l.Add(Bld.Belt(Ids.BeltMk2, i * 2, 1, 0, items[i], "in#" + i));
            Layout.Link(a, b, 1); Layout.Link(b, null, 0);
            a.Parameters = new[] { items[i], 0 };
            l.Ports.Add(new Port { Name = "in:" + items[i] + "#" + i, Kind = PortKind.In, X = i * 2, Y = 0, Dir = Dir.N, Item = items[i], Rate = 60, Belt = a, Chain = a.Chain });
        }
        var r = new Validator(gd, CoilSpec()).Validate(l, null);
        if (!r.Errors.Any(m => m.Contains("input groups"))) return "interleaved lanes accepted";
        Console.WriteLine("      (" + r.Errors.First(m => m.Contains("input groups")) + ")");
        // grouped order passes the group check
        l.Ports[1].X = 10; l.Ports[1].Belt.X = 10;
        var r2 = new Validator(gd, CoilSpec()).Validate(l, null);
        return r2.Errors.Any(m => m.Contains("input groups")) ? "grouped lanes (iron, iron, copper) rejected" : null;
    }

    static string PipelineTailsAndTowers()
    {
        BlockSpec spec = CoilSpec();
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Layout l = new Composer(gd, spec).Compose(plan);
        var e = new Errs();
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        Rect box = l.Bounds();
        foreach (Port p in l.Ports.Where(p => p.Kind == PortKind.In))
        {
            e.Check(p.Y == box.Y0, "input " + p.Item + " connection point at y " + p.Y + ", not on the input edge " + box.Y0);
            e.Check(!l.All.Any(s => s.Kind == Kind.Sorter && s.Input == p.Belt), "input " + p.Item + " icon belt is a pickup tile");
        }
        int towers = l.Count(Kind.Tesla, 0);
        e.Check(towers < 6, towers + " towers (coil-360-v3 and the first engine version had 6)");
        Console.WriteLine("      " + l.All.Count + " buildings, " + box.Width + "x" + box.Height + ", " + towers + " towers");
        return e.Result();
    }

    // ------------------------------------------------------------------ engine upgrade 2026-10-31 (colliders, slots, bridges, splitters)

    /// Game slot poses (analysis\buildings\slots.md section 2, tiles at yaw 0, index order 0..11) and faces.
    static readonly double[][] SmelterSlots = {
        new[] { -0.796, 0.796 }, new[] { 0, 0.796 }, new[] { 0.796, 0.796 }, new[] { 0.796, 0.796 }, new[] { 0.796, 0 }, new[] { 0.796, -0.796 },
        new[] { 0.796, -0.796 }, new[] { 0, -0.796 }, new[] { -0.796, -0.796 }, new[] { -0.796, -0.796 }, new[] { -0.796, 0 }, new[] { -0.796, 0.796 } };
    static readonly double[][] AsmSlots = {
        new[] { -0.796, 0.875 }, new[] { 0, 0.875 }, new[] { 0.796, 0.875 }, new[] { 0.875, 0.796 }, new[] { 0.875, 0 }, new[] { 0.875, -0.796 },
        new[] { 0.796, -0.875 }, new[] { 0, -0.875 }, new[] { -0.796, -0.875 }, new[] { -0.875, -0.796 }, new[] { -0.875, 0 }, new[] { -0.875, 0.796 } };
    static readonly Dir[] SlotFaces = { Dir.N, Dir.N, Dir.N, Dir.E, Dir.E, Dir.E, Dir.S, Dir.S, Dir.S, Dir.W, Dir.W, Dir.W };

    static string SlotAtByFace()
    {
        var e = new Errs();
        int oldWrong = 0, total = 0;
        foreach (var table in new[] { SmelterSlots, AsmSlots })
            foreach (int yaw in new[] { 0, 90, 180, 270 })
                for (int k = 0; k < 12; k++)
                {
                    double a = yaw * Math.PI / 180.0, x = table[k][0], z = table[k][1];
                    double wx = x * Math.Cos(a) + z * Math.Sin(a), wy = -x * Math.Sin(a) + z * Math.Cos(a);
                    Dir wf = Dirs.RotateCW(SlotFaces[k], yaw / 90);
                    int got = Geo.SlotAt(yaw, wx, wy, wf);
                    e.Check(got == k, "yaw " + yaw + " slot " + k + " -> " + got);
                    if (table == SmelterSlots && Geo.SlotAt(yaw, wx, wy) != k) oldWrong++;
                    total++;
                }
        Console.WriteLine("      " + total + " poses exact by face; position-only rule wrong at " + oldWrong + " of 48 smelter poses (slots.md: 16)");
        e.Check(oldWrong > 0, "the position-only rule is no longer ambiguous at smelter corners (test not exercising the fix)");
        // the re-yawed (inferred) cells use the face rule: every inferred cell's slot matches the face rule
        foreach (var g in Geo.All())
            foreach (int yaw in new[] { 0, 90, 180, 270 })
            {
                SorterGeo r = g.ReYawed(yaw);
                e.Check(r.Slot == Geo.SlotAt(yaw, r.BeltToMachine ? r.Ex : r.Sx, r.BeltToMachine ? r.Ey : r.Sy, r.Face), r.Name + ": slot " + r.Slot);
            }
        return e.Result();
    }

    static string CollidersData()
    {
        var e = new Errs();
        e.Check(gd.CollidersPath != null, "analysis\\buildings\\colliders.json not loaded");
        if (gd.CollidersPath == null) return e.Result();
        e.Check(gd.MinPitch(Ids.ArcSmelter, 0, false, false) == 3, "arc smelter N-S pitch " + gd.MinPitch(Ids.ArcSmelter, 0, false, false));
        e.Check(gd.MinPitch(Ids.AsmMk1, 0, false, false) == 4, "assembler N-S pitch " + gd.MinPitch(Ids.AsmMk1, 0, false, false));
        e.Check(gd.MinPitch(Ids.AsmMk1, 90, true, true) == 4, "assembler E-W pitch up to 60 deg " + gd.MinPitch(Ids.AsmMk1, 90, true, true));
        e.Check(gd.MinPitch(Ids.Splitter, 0, true, false) == 2 && gd.MinPitch(Ids.Splitter, 0, true, true) == 3,
            "splitter E-W pitch " + gd.MinPitch(Ids.Splitter, 0, true, false) + " / up to 60 deg " + gd.MinPitch(Ids.Splitter, 0, true, true));
        // splitter = two crossed arms (1.25 x 2.38 m): a tower on the diagonal tile is clear, on the side tile it collides
        Bld sp = Bld.Splitter(0, 0, false);
        double diag = gd.ColliderGap(sp, Bld.Tesla(1, 1), 1.0), side = gd.ColliderGap(sp, Bld.Tesla(1, 0), 1.0);
        e.Check(diag > 0 && side < 0, "splitter vs tower: diagonal gap " + Fmt(diag) + ", side gap " + Fmt(side));
        // assemblers at pitch 3 overlap 0.04 tile (the game-rejected motor file), at pitch 4 they are clear
        double p3 = gd.ColliderGap(Bld.Machine(Ids.AsmMk1, 0, 0, 0, 5), Bld.Machine(Ids.AsmMk1, 0, 3, 0, 5), 1.0);
        double p4 = gd.ColliderGap(Bld.Machine(Ids.AsmMk1, 0, 0, 0, 5), Bld.Machine(Ids.AsmMk1, 4, 0, 0, 5), GameData.EwFactorMin);
        e.Check(p3 < -0.03 && p3 > -0.05, "assemblers at pitch 3: gap " + Fmt(p3));
        e.Check(p4 > 0, "assemblers 4 tiles apart E-W at 60 deg: gap " + Fmt(p4));
        // row pitch comes from the collider data (Modules.ColliderPitch), safe60 E-W variant
        e.Check(Modules.ColliderPitch(gd, Ids.Splitter, 0, true, true) == 3, "Modules.ColliderPitch splitter safe60");
        Console.WriteLine("      smelter 3, assembler 4 (pitch 3 gap " + Fmt(p3) + "), splitter 2 / 3 up to 60 deg, splitter-tower diag " + Fmt(diag) + " side " + Fmt(side));
        return e.Result();
    }

    /// Belts and splitters of a game blueprint as a linked abstract layout (z, yaw, slots, links).
    static Layout BeltLayout(object bp)
    {
        var l = new Layout(gd);
        var byIdx = new Dictionary<int, Bld>();
        var recs = GameBp.Buildings(bp);
        foreach (object b in recs)
        {
            int item = GameBp.In(b, "itemId");
            double x = GameBp.Fl(b, "localOffset_x"), y = GameBp.Fl(b, "localOffset_y"), z = GameBp.Fl(b, "localOffset_z");
            Bld n;
            if (Ids.IsBelt(item)) n = Bld.Belt(item, x, y, GameBp.Fl(b, "yaw"), 0, null);
            else if (item == Ids.Splitter) n = Bld.Splitter(x, y, false);
            else continue;
            n.Z = n.Z2 = z;
            n.OutputToSlot = GameBp.In(b, "outputToSlot"); n.InputFromSlot = GameBp.In(b, "inputFromSlot");
            if (n.Kind == Kind.Belt && (Math.Abs(x - Math.Round(x)) > 0.01 || Math.Abs(y - Math.Round(y)) > 0.01)) n.Cell = SplitterGeo.BeltCell;
            byIdx[GameBp.In(b, "index")] = n;
            l.Add(n);
        }
        foreach (object b in recs)
        {
            Bld n;
            if (!byIdx.TryGetValue(GameBp.In(b, "index"), out n)) continue;
            object o = GameBp.Get(b, "outputObj"), i = GameBp.Get(b, "inputObj");
            Bld t;
            if (o != null && byIdx.TryGetValue(GameBp.In(o, "index"), out t)) n.Output = t;
            if (i != null && byIdx.TryGetValue(GameBp.In(i, "index"), out t)) n.Input = t;
        }
        return l;
    }

    static string GameProvenBridges()
    {
        string path = Cells.Resolve(bpDir, @"refs\bridges-splitters-user.txt");
        if (!File.Exists(path)) return "missing " + path;
        Layout l = BeltLayout(GameBp.Load(path));
        var e = new Errs();
        int raised = l.All.Count(b => b.IsRaised);
        e.Check(raised == 6, raised + " raised nodes, expected 6 (bridges over 1, 2, 3 belts)");
        ValidationReport r = new Validator(gd, null).ValidateBelts(l);
        foreach (string m in r.Errors) e.L.Add("game-proven file rejected: " + m);
        Console.WriteLine("      " + l.All.Count + " records, " + raised + " raised nodes; " + string.Join("; ", r.Info.Where(m => m.StartsWith("bridges")).ToArray()));
        return e.Result();
    }

    static string ModulesBridgeCell()
    {
        string path = Cells.Resolve(bpDir, @"refs\bridges-splitters-user.txt");
        if (!File.Exists(path)) return "missing " + path;
        object bp = GameBp.Load(path);
        // C8: (10,5) (11,5) (12,5,z0.5) (13,5,z0.5) (14,5) (15,5), flow E
        var tiles = new List<int[]> { new[] { 10, 5 }, new[] { 11, 5 }, new[] { 12, 5, 1 }, new[] { 13, 5, 1 }, new[] { 14, 5 }, new[] { 15, 5 } };
        Fragment f = Modules.BeltRun("c8", Ids.BeltMk2, 0, tiles, null);
        var e = new Errs();
        foreach (Bld b in f.Blds)
        {
            object rec = GameBp.Buildings(bp).Cast<object>().FirstOrDefault(r => Ids.IsBelt(GameBp.In(r, "itemId")) && Near(GameBp.Fl(r, "localOffset_x"), b.X, 0.01)
                && Near(GameBp.Fl(r, "localOffset_y"), b.Y, 0.01) && Near(GameBp.Fl(r, "localOffset_z"), b.Z, 0.01));
            e.Check(rec != null, "no record at " + b);
            if (rec != null) e.Check(NearYaw(GameBp.Fl(rec, "yaw"), b.Yaw, 1), "yaw " + Fmt(b.Yaw) + " vs " + Fmt(GameBp.Fl(rec, "yaw")) + " at " + b);
        }
        // shape rules of the cell
        Func<IList<int[]>, bool> throws = t => { try { Modules.BeltRun("x", Ids.BeltMk2, 0, t, null); return false; } catch (ArgumentException) { return true; } };
        e.Check(!throws(new List<int[]> { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 2, 0, 1 }, new[] { 3, 0, 1 }, new[] { 4, 0, 1 }, new[] { 5, 0, 1 }, new[] { 6, 0 }, new[] { 7, 0 } }), "long raised run (cell RAISED_RUN) rejected");
        e.Check(!throws(new List<int[]> { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 2, 0, 1 }, new[] { 3, 0, 1 }, new[] { 3, 1, 1 }, new[] { 3, 2, 1 }, new[] { 3, 3 }, new[] { 3, 4 } }), "raised corner inside a run rejected");
        e.Check(throws(new List<int[]> { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 1 }, new[] { 2, 2 }, new[] { 2, 3 } }), "turn on the first raised node accepted");
        e.Check(throws(new List<int[]> { new[] { 0, 1 }, new[] { 1, 1 }, new[] { 1, 0 }, new[] { 2, 0, 1 }, new[] { 3, 0 }, new[] { 4, 0 } }), "turn on the ramp foot accepted");
        e.Check(throws(new List<int[]> { new[] { 1, 0 }, new[] { 2, 0, 1 }, new[] { 3, 0 }, new[] { 4, 0 } }), "bridge without an approach node accepted");
        return e.Result();
    }

    /// n crossed belts (x = 1..n, flowing N, y -3..3) and a bridge flowing E along y 0 from x -2 to n + 3.
    /// endAtCross: the first crossed chain ends on the crossed tile (not straight through).
    /// turnFoot: the near ramp foot (0,0) is a corner (entered from (0,-1) flowing N, leaving E).
    static Layout BridgeModel(int n, double z, bool turnFoot, bool endAtCross)
    {
        var l = new Layout(gd);
        for (int c = 1; c <= n; c++)
        {
            var pts = new List<int[]>();
            for (int y = -3; y <= (endAtCross && c == 1 ? 0 : 3); y++) pts.Add(new[] { c, y });
            l.Add(Modules.BeltRun("cross" + c, Ids.BeltMk2, Ids.IronOre, pts, null));
        }
        var bridge = new List<Bld>();
        if (turnFoot)
        {
            bridge.Add(l.Add(Bld.Belt(Ids.BeltMk2, 0, -2, 0, Ids.CopperOre, "br")));
            bridge.Add(l.Add(Bld.Belt(Ids.BeltMk2, 0, -1, 0, Ids.CopperOre, "br")));
            bridge.Add(l.Add(Bld.Belt(Ids.BeltMk2, 0, 0, 45, Ids.CopperOre, "br")));
        }
        else for (int x = -2; x <= 0; x++) bridge.Add(l.Add(Bld.Belt(Ids.BeltMk2, x, 0, 90, Ids.CopperOre, "br")));
        for (int x = 1; x <= n + 3; x++)
        {
            Bld b = Bld.Belt(Ids.BeltMk2, x, 0, 90, Ids.CopperOre, "br");
            if (x <= n) b.Z = b.Z2 = z;
            bridge.Add(l.Add(b));
        }
        for (int i = 0; i + 1 < bridge.Count; i++) Layout.Link(bridge[i], bridge[i + 1], 1);
        Layout.Link(bridge[bridge.Count - 1], null, 0);
        return l;
    }

    static string ValidatorBadBridges()
    {
        var e = new Errs();
        Func<Layout, List<string>> errs = l => new Validator(gd, null).ValidateBelts(l).Errors;
        var ok1 = errs(BridgeModel(1, 0.5, false, false)); e.Check(ok1.Count == 0, "valid bridge over 1 rejected: " + string.Join(" | ", ok1.ToArray()));
        var ok3 = errs(BridgeModel(3, 0.5, false, false)); e.Check(ok3.Count == 0, "valid bridge over 3 rejected: " + string.Join(" | ", ok3.ToArray()));
        var z1 = errs(BridgeModel(1, 1.0, false, false)); e.Check(z1.Any(m => m.Contains("z ") || m.Contains("slope")), "z 1.0 bridge accepted");
        var n4 = errs(BridgeModel(4, 0.5, false, false)); e.Check(n4.Count == 0, "raised run over 4 belts rejected (cell RAISED_RUN has no span limit): " + string.Join(" | ", n4.ToArray()));
        var tf = errs(BridgeModel(1, 0.5, true, false)); e.Check(tf.Any(m => m.Contains("straight") || m.Contains("foot")), "turn on a ramp foot accepted");
        var cc = errs(BridgeModel(1, 0.5, false, true)); e.Check(cc.Any(m => m.Contains("not straight through")), "bridge over a belt end accepted");
        // two-level: a second raised node on an already crossed tile
        Layout two = BridgeModel(1, 0.5, false, false);
        Bld extra = two.Add(Bld.Belt(Ids.BeltMk2, 1, 0, 0, Ids.CopperOre, "second")); extra.Z = extra.Z2 = 0.5;
        var tw = errs(two); e.Check(tw.Any(m => m.Contains("two raised nodes")), "two-level crossing accepted");
        Console.WriteLine("      rejected: " + (z1.FirstOrDefault() ?? "-") + " / " + (tw.FirstOrDefault() ?? "-"));
        return e.Result();
    }

    static string ValidatorLinkLimits()
    {
        var e = new Errs();
        // a belt link 2 tiles long E-W at the widest E-W tile (1.1409 x 1.2566 m x 2 = 2.87 m) exceeds 2.302 m
        var l = new Layout(gd);
        Bld a = l.Add(Bld.Belt(Ids.BeltMk2, 0, 0, 90, Ids.IronOre, "x")), b = l.Add(Bld.Belt(Ids.BeltMk2, 2, 0, 90, Ids.IronOre, "x"));
        Layout.Link(a, b, 1); Layout.Link(b, null, 0);
        var r = new Validator(gd, null).ValidateBelts(l);
        e.Check(r.Errors.Any(m => m.Contains("2.302") || m.Contains("not adjacent")), "2-tile belt link accepted");
        // one tile up 1.5 levels: 3D 2.4 m > 2.302 m (TooFar) and too steep
        var l1 = new Layout(gd);
        Bld p = l1.Add(Bld.Belt(Ids.BeltMk2, 0, 0, 0, Ids.IronOre, "z")), q = l1.Add(Bld.Belt(Ids.BeltMk2, 0, 1, 0, Ids.IronOre, "z"));
        q.Z = q.Z2 = 1.5; Layout.Link(p, q, 1); Layout.Link(q, null, 0);
        e.Check(new Validator(gd, null).ValidateBelts(l1).Errors.Any(m => m.Contains("TooFar")), "belt link of 2.4 m accepted");
        // a full belt level (z 1.0) over one E-W tile is TooSteep (51 deg at 60 deg latitude)
        var l2 = new Layout(gd);
        Bld c = l2.Add(Bld.Belt(Ids.BeltMk2, 0, 0, 90, Ids.IronOre, "y")), d = l2.Add(Bld.Belt(Ids.BeltMk2, 1, 0, 90, Ids.IronOre, "y"));
        d.Z = d.Z2 = 1.0; Layout.Link(c, d, 1); Layout.Link(d, null, 0);
        var r2 = new Validator(gd, null).ValidateBelts(l2);
        e.Check(r2.Errors.Any(m => m.Contains("TooSteep")), "z 1.0 over one tile accepted");
        // accumulators are power nodes for the tower distance rule
        e.Check(gd.IsPowerNode(2206) && gd.IsPowerNode(2207), "accumulator not counted as a power node");
        var l3 = new Layout(gd);
        l3.All.Add(Bld.Tesla(0, 0)); l3.All.Add(new Bld { Kind = Kind.Storage, ItemId = 2206, X = 2, Y = 0, X2 = 2, Y2 = 0 });
        l3.All.Add(Bld.Machine(Ids.ArcSmelter, 0, 3, 0, 1));
        var r3 = new Validator(gd, null).ValidatePower(l3);
        e.Check(r3.Errors.Any(m => m.Contains("too close") || m.Contains("PowerTooClose")), "tower 2 tiles from an accumulator accepted");
        return e.Result();
    }

    static string PipelineCoil1080()
    {
        var spec = new BlockSpec { Item = Ids.MagneticCoil, Rate = 1080 };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var comp = new Composer(gd, spec);
        Layout l = comp.Compose(plan);
        var e = new Errs();
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        int copperRows = l.All.Count(b => b.Kind == Kind.Machine && b.RecipeId == Ids.RecipeCopperIngot);
        var copperIn = l.Ports.Where(p => p.Kind == PortKind.In && p.Item == Ids.CopperOre).ToList();
        e.Check(copperIn.Count == 1, copperIn.Count + " copper ore lanes (one consolidated copper row expected)");
        e.Check(copperRows == 9, copperRows + " copper smelters");
        e.Check(l.Count(Kind.Splitter, 0) >= 1, "no splitter distributes the copper lane");
        e.Check(!l.All.Any(b => b.ItemId == Ids.BeltMk3 || b.ItemId == Ids.SorterMk3), "Mk.III used");
        Rect box = l.Bounds();
        Console.WriteLine("      " + l.All.Count + " buildings, " + box.Width + "x" + box.Height + ", " + l.Count(Kind.Splitter, 0) + " splitter(s), "
            + l.All.Count(b => b.IsRaised) + " raised bridge node(s), " + l.Count(Kind.Tesla, 0) + " towers");
        return e.Result();
    }

    // ------------------------------------------------------------------ splitter clearance (in-game test 2026-10-31)
    static List<string> ClearanceErrors(string rel)
    {
        string path = Cells.Resolve(bpDir, rel);
        if (!File.Exists(path)) throw new Exception("missing " + path);
        ValidationReport r = new Validator(gd, null).ValidateBelts(BeltLayout(GameBp.Load(path)));
        string note = r.Info.FirstOrDefault(m => m.StartsWith("splitter clearance"));
        Console.WriteLine("      " + rel + ": " + (note ?? "no splitter"));
        return r.Errors.Where(m => m.Contains("splitter clearance")).ToList();
    }

    static string GameProvenSplitterClearance()
    {
        var e = new Errs();
        foreach (string rel in new[] { @"refs\splitters.txt", @"refs\bridges-splitters-user.txt", @"refs\engine-coil-1080-v1.txt" })
            foreach (string m in ClearanceErrors(rel)) e.L.Add(rel + ": " + m);
        return e.Result();
    }

    static string GameRejectedSplitterClearance()
    {
        var e = new Errs();
        foreach (string rel in new[] { @"refs\engine-turbine-60-v1.txt", @"refs\engine-missile-90-v1.txt" })
        {
            var errs = ClearanceErrors(rel);
            // turbine: foreign belts next to a splitter; missile: its own feed chain loops back past the splitter (more
            // than 3 hops away, so the game treats it as unconnected) - both are the game probe collision
            e.Check(errs.Any(m => m.Contains("probe hits")), rel + ": no game-collide (probe) error");
            if (errs.Count > 0) Console.WriteLine("      (" + errs[0] + ")");
        }
        return e.Result();
    }

    static string BeltProbeSplitter()
    {
        var e = new Errs();
        Bld sp = Bld.Splitter(0, 0, false);
        Func<int, int, double> g = (x, y) => gd.BeltProbeGapM(Bld.Belt(Ids.BeltMk2, x, y, 0, 0, null), sp);
        e.Check(g(1, 0) < 0 && g(0, 1) < 0 && g(-1, 0) < 0 && g(0, -1) < 0, "side tiles: " + Fmt(g(1, 0)) + " / " + Fmt(g(0, 1)));
        e.Check(g(1, 1) > 0 && g(2, 0) > 0 && g(2, 1) > 0, "diagonal / 2 tiles: " + Fmt(g(1, 1)) + " / " + Fmt(g(2, 0)));
        return e.Result();
    }

    // ------------------------------------------------------------------ engine fixes 2026-10-31 (Missile Turret 7.5 runs) + kit mode
    static readonly int SteelId = 1103, GearId = 1201, CircuitId = 1301, TurretId = 3005;

    static string SolverPerStepRaw()
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = TurretId, Rate = 7.5 };
        spec.RawFor[SteelId] = new HashSet<int> { Ids.IronIngot };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Step steel = plan.StepFor(SteelId), ingot = plan.StepFor(Ids.IronIngot);
        e.Check(steel != null && steel.External.Contains(Ids.IronIngot), "steel does not take iron ingot from outside");
        e.Check(ingot != null && !ingot.ConsumerSteps.Contains(plan.Steps.IndexOf(steel)), "iron ingot step still feeds steel");
        e.Check(ingot != null && Near(ingot.Outputs[0].Rate, 225, 1e-6), "iron ingot step " + (ingot != null ? Fmt(ingot.Outputs[0].Rate) : "-") + "/min, expected 405 - 180 = 225");
        var raw = plan.RawInputs.FirstOrDefault(x => x.Item == Ids.IronIngot);
        e.Check(raw != null && Near(raw.Rate, 180, 1e-6), "raw iron ingot " + (raw != null ? Fmt(raw.Rate) : "-") + ", expected 180");
        e.Check(steel != null && steel.IsExternal(plan, Ids.IronIngot) && !plan.StepFor(GearId).IsExternal(plan, Ids.IronIngot), "IsExternal per consumer");
        return e.Result();
    }

    static string PackFourConsumers()
    {
        var e = new Errs();
        // iron ingot at turret 7.5: steel 180, motor 90, circuit 90, gear 45 (4 consumer rows)
        var slots = new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(SteelId, 180), new KeyValuePair<int, double>(1203, 90),
            new KeyValuePair<int, double>(CircuitId, 90), new KeyValuePair<int, double>(GearId, 45) };
        var g = Composer.PackConsumers(slots, 720, null, x => x);
        e.Check(g.Count == 2 && g.All(x => x.Count <= 3) && g.Sum(x => x.Count) == 4, "4 consumers -> " + g.Count + " groups (" + string.Join("/", g.Select(x => x.Count.ToString()).ToArray()) + "), expected 2 groups of <= 3");
        var own = Composer.PackConsumers(slots, 720, new HashSet<int> { SteelId }, x => x);
        var sg = own.FirstOrDefault(x => x.Any(kv => kv.Key == SteelId));
        e.Check(sg != null && sg.Count == 1, "--own Steel: steel shares its group");
        return e.Result();
    }

    static string RotatedSplitterSlots()
    {
        var e = new Errs();
        for (int q = 1; q <= 3; q++)
        {
            Fragment f = Modules.Splitter(0, 0, Dir.S, new[] { Dir.N, Dir.E, Dir.W }, Ids.BeltMk2, Ids.IronIngot, false);
            var good = new Layout(gd);
            var blds = f.Blds.ToList();
            Composer.RotateLayout(blds, q);
            foreach (var b in blds) good.Add(b);
            var bad = new Layout(gd);
            Fragment f2 = Modules.Splitter(0, 0, Dir.S, new[] { Dir.N, Dir.E, Dir.W }, Ids.BeltMk2, Ids.IronIngot, false);
            foreach (var b in f2.Blds) { b.RotateCW(q, 0, 0); bad.Add(b); }   // the old Finish(): rigid rotation only
            var rg = new Validator(gd, null).ValidateBelts(good).Errors.Where(m => m.Contains("rule 7")).ToList();
            // the rigidly rotated splitter is self-consistent in its own frame (slots are prefab-local), but its yaw is
            // unproven: the Validator warns
            var rb = new Validator(gd, null).ValidateBelts(bad).Warnings.Where(m => m.Contains("only yaw 0 is proven")).ToList();
            e.Check(rg.Count == 0, "q " + q + ": " + (rg.Count > 0 ? rg[0] : ""));
            e.Check(rb.Count > 0, "q " + q + ": a rotated splitter yaw is not flagged");
            e.Check(good.All.Where(b => b.Kind == Kind.Splitter).All(b => NearYaw(b.Yaw, 0, 1e-6)), "q " + q + ": splitter yaw not 0");
        }
        return e.Result();
    }

    /// Two rows of one step (7 smelters for 405/min: a 6-smelter row for 360 and a 1-smelter row for 45, the turret 7.5
    /// iron-ingot split): with per-row loads the Validator agrees with the row ports; the step average (405/7) reports the
    /// false 'short by 12.857'.
    static string ValidatorPerRowLoad()
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = Ids.IronIngot, Rate = 405 };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Step st = plan.Steps[0];
        Func<bool, Layout> make = withLoad =>
        {
            var l = new Layout(gd);
            int x = 0;
            foreach (int[] row in new[] { new[] { 6, 360 }, new[] { 1, 45 } })
            {
                var rs = new RowSpec { Name = "ingot" + row[0], MachineItem = Ids.ArcSmelter, RecipeId = Ids.RecipeIronIngot, Count = row[0],
                    InputItems = new[] { Ids.IronOre }, InputRates = new double[] { row[1] }, OutputItem = Ids.IronIngot, OutputRate = row[1],
                    LaneIcons = true, TailItems = new[] { Ids.IronOre }, Data = gd };
                Fragment f = Modules.MachineRow(rs);
                Rect b = f.Bounds(l.Footprints);
                f.Translate(x - b.X0, -b.Y0);
                x += b.Width + 3;
                if (withLoad) foreach (var m in f.Blds) if (m.Kind == Kind.Machine) m.Load = Composer.RowLoad(gd, st, row[0], row[1]);
                foreach (var bb in f.Blds) l.Add(bb);
                Port pi = f.Ports.First(p => p.Kind == PortKind.In), po = f.Ports.First(p => p.Kind == PortKind.Out);
                pi.Rate = row[1]; po.Rate = row[1];
                l.Ports.Add(pi); l.Ports.Add(po);
            }
            foreach (var t in Modules.TeslaGrid(l, gd, true)) l.Add(t);
            return l;
        };
        var ok = new Validator(gd, spec).Validate(make(true), plan);
        e.Check(ok.Ok, "per-row load: " + (ok.Errors.Count > 0 ? ok.Errors[0] : ""));
        var old = new Validator(gd, spec).Validate(make(false), plan);
        e.Check(old.Errors.Any(m => m.Contains("runs short")), "step-average rate no longer reproduces the false shortage");
        return e.Result();
    }

    static string OutputSorterFullSpeed()
    {
        var e = new Errs();
        // circuit board on Mk.II assemblers: 120/min per machine at full speed, one L2 Mk.II sorter moves 90/min
        Func<GameData, RowSpec> rs = d => new RowSpec { Name = "circuit", MachineItem = Ids.AsmMk2, RecipeId = 50, Count = 2,
            InputItems = new[] { Ids.IronIngot, Ids.CopperIngot }, InputRates = new double[] { 240, 120 }, OutputItem = CircuitId, OutputRate = 240,
            LaneIcons = true, TailItems = new[] { Ids.IronIngot, Ids.CopperIngot }, Data = d };
        RowSpec checkd = rs(gd);
        Fragment f = Modules.MachineRow(checkd);
        int outs = f.Blds.Count(b => b.Kind == Kind.Sorter && b.Input != null && b.Input.Kind == Kind.Machine);
        e.Check(checkd.Problems.Count > 0 || outs >= 4, "MachineRow neither adds a 2nd output sorter nor reports the 120 > 90 problem (" + outs + " output sorters)");
        // the Validator fails a single L2 output sorter even when the planned rate fits (machine at 75%: 90/min)
        RowSpec plain = rs(null); plain.OutputRate = 180; plain.InputRates = new double[] { 180, 90 };
        Fragment g = Modules.MachineRow(plain);
        var l = new Layout(gd);
        foreach (var b in g.Blds) { if (b.Kind == Kind.Machine) b.Load = 0.75; l.Add(b); }
        foreach (var p in g.Ports) { p.Rate = p.Kind == PortKind.Out ? 180 : p.Item == Ids.IronIngot ? 180 : 90; l.Ports.Add(p); }
        foreach (var t in Modules.TeslaGrid(l, gd, true)) l.Add(t);
        var spec = new BlockSpec { Item = CircuitId, Rate = 180 }; spec.Tiers.Assembler = Ids.AsmMk2;
        spec.TreatAsRaw.Add(Ids.IronIngot); spec.TreatAsRaw.Add(Ids.CopperIngot);
        var plan = new Solver(gd, spec).Solve();
        var rep = new Validator(gd, spec).Validate(l, plan);
        e.Check(rep.Errors.Any(m => m.Contains("output sorter") && m.Contains("full speed")), "single L2 output sorter at 120/min full speed not rejected: " + string.Join(" | ", rep.Errors.Take(3).ToArray()));
        return e.Result();
    }

    /// Bus kit (lib\Bus.cs): assembled layout and every module validate; bus lanes are straight (no corner, no raised
    /// belt, no splitter); all modules share the assembled width; at every band seam each lane leaving module k at x
    /// enters module k+1 at the same x; module belts add up to the assembled belts; negative: a module without the
    /// port of a lane entering from the south fails.
    static string MicroBusMotor()
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = 1203, Rate = 45 };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var bus = new Bus(gd, spec);
        bus.Build(plan);
        foreach (string m in bus.Errors) e.L.Add(m);
        if (e.L.Count > 0) return e.Result();
        var A = bus.Assembled;
        e.Check(A.All.Where(b => b.Kind == Kind.Belt).All(b => NearYaw(b.Yaw, 0, 0.01) && !b.IsRaised), "a bus belt is not straight north on the ground");
        e.Check(A.Count(Kind.Splitter, 0) == 0, "splitter in the bus layout");
        e.Check(A.Count(Kind.Machine, 0) == 11, A.Count(Kind.Machine, 0) + " machines, expected 11 (gear 2 by the margin rule)");
        var bands = bus.Floor.Bands;
        e.Check(bands.Count == 4, bands.Count + " modules, expected 4");
        // overlap tails: every module after the first repeats the previous module's last belt of each crossing lane
        e.Check(bands[0].Tail == 0 && bands.Skip(1).All(b => b.Tail == 1), "tails: module 01 must have none, every later module one row");
        e.Check(bands.Sum(b => b.Layout.Count(Kind.Belt, 0)) == A.Count(Kind.Belt, 0) + bands.Skip(1).Sum(b => b.Layout.Ports.Count(p => p.Kind == PortKind.In)), "module belts != assembled belts + one overlap tail per crossing lane");
        var icons = bands.SelectMany(b => b.Layout.All.Where(x => x.Kind == Kind.Belt && x.Parameters != null && x.Parameters.Length > 0 && x.Parameters[0] != 0)).Count();
        e.Check(icons == bus.Floor.Lanes.Count(l => l.BlockInput), icons + " icon belts in the modules, expected one per raw input lane");
        for (int i = 0; i + 1 < bands.Count; i++)
        {
            BusBand lo = bands[i], hi = bands[i + 1];
            int h = lo.Y1 - lo.Y0 + lo.Tail;
            // out ends of module i in assembled rows / x; in ports (tails) of module i+1 in assembled rows / x
            var top = lo.Layout.Ports.Where(p => p.Kind == PortKind.Out && p.Belt.TileY == h && p.Belt.Output == null).Select(p => p.X + "," + (p.Y + lo.Y0 - lo.Tail)).OrderBy(s => s).ToList();
            var bot = hi.Layout.Ports.Where(p => p.Kind == PortKind.In).Select(p => p.X + "," + (p.Y + hi.Y0 - hi.Tail)).OrderBy(s => s).ToList();
            e.Check(top.SequenceEqual(bot), "seam " + (i + 1) + "/" + (i + 2) + ": out ends " + string.Join(" ", top.ToArray()) + " vs tails " + string.Join(" ", bot.ToArray()) + " (must be the same tiles)");
            foreach (var p in hi.Layout.Ports.Where(p => p.Kind == PortKind.In))
                e.Check(p.Y == 0 && p.Belt.Input == null && p.Belt.Output != null && p.Belt.Parameters == null, "module " + (i + 2) + " input " + p + " not an icon-less chain head on row 0");
        }
        // negative: drop the iron ore port of module 02
        var b2 = bands[1];
        var saved = b2.Layout.Ports.ToList();
        b2.Layout.Ports.RemoveAll(p => p.Kind == PortKind.In && p.Item == Ids.IronOre);
        var neg = new Validator(gd, spec).Validate(b2.Layout, b2.Plan);
        e.Check(!neg.Ok, "module 02 without its iron ore port still validates");
        b2.Layout.Ports.Clear(); b2.Layout.Ports.AddRange(saved);
        bus.Export(GetCells());
        foreach (string m in bus.Errors) e.L.Add(m);
        e.Check(bus.AssembledExport != null && bus.AssembledExport.Ok, "assembled export failed");
        foreach (var b in bands) e.Check(b.Export != null && b.Export.Ok && b.Report.Ok, b.FileName + ": export failed");
        Rect r = A.Bounds();
        Console.WriteLine("      assembled " + A.All.Count + " buildings " + r.Width + "x" + r.Height + "; " + string.Join(", ", bands.Select(b => b.FileName).ToArray()));
        return e.Result();
    }

    static string KitTurret()
    {
        var e = new Errs();
        var spec = new BlockSpec { Item = TurretId, Rate = 22.5 };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var kit = new Kit(gd, spec);
        kit.MakeRows(plan); kit.MakeModules(); kit.BuildLayouts();
        foreach (string m in kit.Errors) e.L.Add(m);
        e.Check(kit.Rows.Sum(r => r.Count) == plan.Steps.Sum(s => s.Machines), "kit machines " + kit.Rows.Sum(r => r.Count) + " != plan " + plan.Steps.Sum(s => s.Machines));
        e.Check(kit.Modules.Count == 11, kit.Modules.Count + " modules, expected 11");
        e.Check(kit.Rows.All(r => r.Feeds == null || r.Feeds.Count <= 3), "a row feeds more than 3 rows");
        foreach (var m in kit.Modules)
        {
            if (m.Layout == null) continue;
            string id = m.FileName + ": ";
            e.Check(m.W % 4 == 0, id + "width " + m.W + " not a multiple of 4");
            foreach (var p in m.InPorts) e.Check(p.Y == 0 && p.X % 2 == 1 && p.Belt.Parameters != null && p.Belt.Parameters[0] == p.Item, id + "input port " + p);
            var xs = m.InPorts.Select(p => p.X).OrderBy(v => v).ToList();
            for (int i = 1; i < xs.Count; i++) e.Check(xs[i] - xs[i - 1] >= 2, id + "input ports " + xs[i - 1] + "/" + xs[i] + " closer than 2");
            e.Check(m.OutPort.Y == m.H - 1 && m.OutPort.X % 2 == 1 && m.OutPort.Belt.Output == null, id + "output port " + m.OutPort);
            foreach (var b in m.Layout.All)
            {
                if (b.Kind == Kind.Tesla) e.Check(b.TileX >= 2 && b.TileX <= m.W - 3 && b.TileY >= 2 && b.TileY <= m.H - 3, id + "tower in a keep-out " + b);
                bool strip = b.TileY <= 1 || b.TileY >= m.H - 2;
                bool buffer = b.Kind == Kind.Storage || (b.Role != null && b.Role.StartsWith("depot buffer"));   // final module: buffer depot beside the output strip
                if (strip && b.Kind != Kind.Sorter && !buffer) e.Check(b.Kind == Kind.Belt && NearYaw(b.Yaw, 0, 0.01), id + "port strip holds " + b);
            }
        }
        if (e.L.Count == 0)
        {
            kit.Export(GetCells());
            foreach (string m in kit.Errors) e.L.Add(m);
            foreach (var m in kit.Modules) e.Check(m.Export != null && m.Export.Ok && m.Report.Ok, m.FileName + ": export/re-import failed");
            string intro = kit.IntroText();
            e.Check(intro.StartsWith("BPBOOK:0,1," + TurretId + ","), "_intro_ header '" + intro.Substring(0, Math.Min(30, intro.Length)) + "'");
            e.Check(kit.KitMd().Contains("split 1->3"), "_kit.md has no split instruction");
        }
        Console.WriteLine("      " + string.Join(", ", kit.Modules.Select(m => m.No.ToString("00", CultureInfo.InvariantCulture) + " " + m.Slug + " x" + m.Proto.Count + (m.Rows.Count > 1 ? " (" + m.Rows.Count + "x)" : "")).ToArray()));
        return e.Result();
    }

    // ------------------------------------------------------------------ comparison helpers (game records)
    static string Role(object s)
    {
        object i = GameBp.Get(s, "inputObj"), o = GameBp.Get(s, "outputObj");
        bool input = i != null && Ids.IsBelt(GameBp.In(i, "itemId"));
        object m = input ? o : i;
        int[] p = (int[])GameBp.Get(s, "parameters");
        return "sorter " + GameBp.In(s, "itemId") + (input ? " in " : " out ") + (m != null ? GameBp.In(m, "itemId").ToString() : "?") + " L" + (p != null && p.Length > 0 ? p[0] : 0);
    }

    static SortedDictionary<string, int> Histogram(Array recs)
    {
        var h = new SortedDictionary<string, int>();
        Action<string> add = k => h[k] = h.ContainsKey(k) ? h[k] + 1 : 1;
        foreach (object b in recs)
        {
            int item = GameBp.In(b, "itemId");
            add("total");
            if (Ids.IsBelt(item)) add("belt " + item);
            else if (Ids.IsSorter(item)) add(Role(b));
            else if (item == Ids.Tesla) add("tesla " + item);
            else add("machine " + item + " recipe " + GameBp.In(b, "recipeId"));
        }
        return h;
    }

    static void CompareCounts(Array got, Array want, Errs e)
    {
        var g = Histogram(got); var w = Histogram(want);
        foreach (string k in g.Keys.Union(w.Keys).OrderBy(k => k))
        {
            int a = g.ContainsKey(k) ? g[k] : 0, b = w.ContainsKey(k) ? w[k] : 0;
            if (a != b) e.L.Add(k + ": " + a + ", reference " + b);
        }
    }

    static void CheckIcons(object bp, Errs e)
    {
        var icons = new List<string>();
        foreach (object b in GameBp.Buildings(bp))
        {
            if (!Ids.IsBelt(GameBp.In(b, "itemId"))) continue;
            int[] p = (int[])GameBp.Get(b, "parameters");
            if (p != null && p.Length > 0 && p[0] != 0) icons.Add(p[0] + "@" + Fmt(GameBp.Fl(b, "localOffset_x")) + "," + Fmt(GameBp.Fl(b, "localOffset_y")));
        }
        bool copper = icons.Any(s => s.StartsWith(Ids.CopperOre + "@")), iron = icons.Any(s => s.StartsWith(Ids.IronOre + "@"));
        e.Check(copper && iron && icons.Count == 2, "input-belt icons after re-import: [" + string.Join(" ", icons.ToArray()) + "], expected one 1002 and one 1001");
    }

    static string Key(object b)
    {
        var sb = new StringBuilder();
        foreach (string f in new[] { "itemId", "modelIndex", "recipeId", "filterId", "outputToSlot", "inputFromSlot", "outputFromSlot", "inputToSlot", "outputOffset", "inputOffset" })
            sb.Append(GameBp.In(b, f)).Append('|');
        int[] p = (int[])GameBp.Get(b, "parameters");
        int n = p == null ? 0 : p.Length; while (n > 0 && p[n - 1] == 0) n--;
        for (int k = 0; k < n; k++) sb.Append(p[k]).Append(',');
        return sb.ToString();
    }

    static bool SamePos(object a, object b, double tol)
    {
        foreach (string f in new[] { "localOffset_x", "localOffset_y", "localOffset_z", "localOffset_x2", "localOffset_y2", "localOffset_z2" })
            if (!Near(GameBp.Fl(a, f), GameBp.Fl(b, f), tol)) return false;
        return NearYaw(GameBp.Fl(a, "yaw"), GameBp.Fl(b, "yaw"), 0.01) && NearYaw(GameBp.Fl(a, "yaw2"), GameBp.Fl(b, "yaw2"), 0.01);
    }

    static bool SameLink(object a, object b, string f)
    {
        object x = GameBp.Get(a, f), y = GameBp.Get(b, f);
        if (x == null || y == null) return x == null && y == null;
        return GameBp.In(x, "itemId") == GameBp.In(y, "itemId") && Near(GameBp.Fl(x, "localOffset_x"), GameBp.Fl(y, "localOffset_x"), 0.01)
            && Near(GameBp.Fl(x, "localOffset_y"), GameBp.Fl(y, "localOffset_y"), 0.01);
    }

    /// Greedy one-to-one match of records (fields, positions within 0.002, yaw within 0.01, links by target item+position).
    /// Returns the number of records of 'got' without a partner and adds the first differences to e.
    static int CompareMultiset(Array got, Array want, Errs e)
    {
        var pool = new List<object>(); foreach (object b in want) pool.Add(b);
        int unmatched = 0;
        foreach (object g in got)
        {
            string k = Key(g);
            int hit = -1;
            for (int i = 0; i < pool.Count; i++)
                if (Key(pool[i]) == k && SamePos(g, pool[i], 0.002) && SameLink(g, pool[i], "outputObj") && SameLink(g, pool[i], "inputObj")) { hit = i; break; }
            if (hit >= 0) { pool.RemoveAt(hit); continue; }
            unmatched++;
            if (unmatched <= 3)
                e.L.Add("no identical reference record for item " + GameBp.In(g, "itemId") + " at (" + Fmt(GameBp.Fl(g, "localOffset_x")) + "," + Fmt(GameBp.Fl(g, "localOffset_y"))
                    + ") yaw " + Fmt(GameBp.Fl(g, "yaw")) + " key " + k);
        }
        if (pool.Count > 0) e.L.Add(pool.Count + " reference records not reproduced");
        return unmatched;
    }

    // ------------------------------------------------------------------ user's in-game edit of turbine 60 (2026-10-31)
    const string UserTurbine = @"refs\engine-turbine-60-v2-userupgrade.txt";

    /// Every building of a game blueprint as a linked abstract layout (belts, splitters with model + z, sorters, machines,
    /// depots, towers). dropStacked: leave out a splitter stacked on another splitter without belts (the user's accidental
    /// one at (28,22) z 2; analysis\user-upgrade-turbine.md section 4).
    static Layout FullLayout(object bp, bool dropStacked)
    {
        var l = new Layout(gd);
        var byIdx = new Dictionary<int, Bld>();
        var recs = GameBp.Buildings(bp).Cast<object>().ToList();
        var linkedTo = new HashSet<object>();
        foreach (object r in recs) { object o = GameBp.Get(r, "outputObj"), i = GameBp.Get(r, "inputObj"); if (o != null && Ids.IsBelt(GameBp.In(r, "itemId"))) linkedTo.Add(o); if (i != null && Ids.IsBelt(GameBp.In(r, "itemId"))) linkedTo.Add(i); }
        foreach (object r in recs)
        {
            int item = GameBp.In(r, "itemId");
            double x = GameBp.Fl(r, "localOffset_x"), y = GameBp.Fl(r, "localOffset_y"), z = GameBp.Fl(r, "localOffset_z");
            double yaw = GameBp.Fl(r, "yaw");
            int[] par = GameBp.Get(r, "parameters") as int[];
            Bld n;
            if (Ids.IsBelt(item))
            {
                n = Bld.Belt(item, x, y, yaw, 0, null);
                if (Math.Abs(x - Math.Round(x)) > 0.01 || Math.Abs(y - Math.Round(y)) > 0.01) n.Cell = SplitterGeo.BeltCell;
                if (par != null && par.Length == 2 && par[0] != 0) n.Parameters = (int[])par.Clone();
            }
            else if (item == Ids.Splitter)
            {
                object inp = GameBp.Get(r, "inputObj");
                if (dropStacked && z > 1.5 && inp != null && GameBp.In(inp, "itemId") == Ids.Splitter && !linkedTo.Contains(r)) continue;
                n = Bld.Splitter(x, y, par != null && par.Length > 0 && par[0] == 1);
                n.ModelIndex = GameBp.In(r, "modelIndex");
            }
            else if (Ids.IsSorter(item))
                n = new Bld { Kind = Kind.Sorter, ItemId = item, X = x, Y = y, X2 = GameBp.Fl(r, "localOffset_x2"), Y2 = GameBp.Fl(r, "localOffset_y2"), Z2 = GameBp.Fl(r, "localOffset_z2"),
                    Yaw = yaw, Yaw2 = GameBp.Fl(r, "yaw2"), Parameters = par != null ? (int[])par.Clone() : new[] { 1 },
                    InputOffset = GameBp.In(r, "inputOffset"), OutputOffset = GameBp.In(r, "outputOffset") };
            else if (item == Ids.DepotMk1) n = Bld.Depot(x, y);
            else if (item == Ids.Tesla) n = Bld.Tesla(x, y);
            else n = Bld.Machine(item, x, y, yaw, GameBp.In(r, "recipeId"));
            n.Z = z; if (n.Kind != Kind.Sorter) n.Z2 = z;
            n.OutputToSlot = GameBp.In(r, "outputToSlot"); n.InputFromSlot = GameBp.In(r, "inputFromSlot");
            n.OutputFromSlot = GameBp.In(r, "outputFromSlot"); n.InputToSlot = GameBp.In(r, "inputToSlot");
            byIdx[GameBp.In(r, "index")] = n;
            l.Add(n);
        }
        foreach (object r in recs)
        {
            Bld n, t;
            if (!byIdx.TryGetValue(GameBp.In(r, "index"), out n)) continue;
            object o = GameBp.Get(r, "outputObj"), i = GameBp.Get(r, "inputObj");
            if (o != null && byIdx.TryGetValue(GameBp.In(o, "index"), out t)) n.Output = t;
            if (i != null && byIdx.TryGetValue(GameBp.In(i, "index"), out t)) n.Input = t;
        }
        return l;
    }

    static string GameProvenUserTurbine()
    {
        string path = Cells.Resolve(bpDir, UserTurbine);
        if (!File.Exists(path)) return "missing " + path;
        object bp = GameBp.Load(path);
        var e = new Errs();
        Layout clean = FullLayout(bp, true);
        ValidationReport r = new Validator(gd, null).ValidateBelts(clean);
        foreach (string m in r.Errors) e.L.Add("game-proven file rejected: " + m);
        string runs = r.Info.FirstOrDefault(m => m.StartsWith("raised runs")) ?? "no raised runs";
        e.Check(runs.Contains("raised corner"), "no raised runs found: " + runs);
        e.Check(clean.Count(Kind.Storage, 0) == 1 && clean.All.Count(b => b.Kind == Kind.Sorter && (b.Output != null && b.Output.Kind == Kind.Storage || b.Input != null && b.Input.Kind == Kind.Storage)) == 3, "depot buffer (1 depot, 3 sorters) not found");
        Layout raw = FullLayout(bp, false);
        var stacked = new Validator(gd, null).ValidateBelts(raw).Errors.Where(m => m.Contains("stacked splitter")).ToList();
        e.Check(stacked.Count > 0, "the stacked splitter at (28,22) z 2 was accepted");
        Console.WriteLine("      " + clean.All.Count + " records (stacked splitter dropped); " + runs + "; with it: " + (stacked.FirstOrDefault() ?? "-"));
        return e.Result();
    }

    static object UserRec(object bp, int idx) { return GameBp.ByIndex(bp)[idx]; }

    static string ModulesDepotCell()
    {
        string path = Cells.Resolve(bpDir, UserTurbine);
        if (!File.Exists(path)) return "missing " + path;
        object bp = GameBp.Load(path);
        var e = new Errs();
        Bld t0 = Bld.Belt(Ids.BeltMk2, 5, 42, 90, 0, "p"), t1 = Bld.Belt(Ids.BeltMk2, 6, 42, 90, 0, "p"), t2 = Bld.Belt(Ids.BeltMk2, 7, 42, 90, 0, "p");
        Fragment f = Modules.DepotBuffer(t0, t1, t2, Dir.E, true, Ids.SorterMk2, 0);
        Bld dep = f.Blds.First(b => b.Kind == Kind.Storage);
        object rd = UserRec(bp, 482);
        e.Check(Near(dep.X, GameBp.Fl(rd, "localOffset_x"), 0.01) && Near(dep.Y, GameBp.Fl(rd, "localOffset_y"), 0.01), "depot at " + dep + ", game (6,40)");
        e.Check(Modules.IsProvenDepot(Dir.E, true), "flow E / depot right not marked proven");
        var sorters = f.Blds.Where(b => b.Kind == Kind.Sorter).ToList();
        int[] idx = { 491, 492, 493 };
        for (int i = 0; i < 3; i++)
        {
            Bld s = sorters[i]; object g = UserRec(bp, idx[i]);
            string w = "sorter #" + idx[i] + ": ";
            e.Check(Near(s.X, GameBp.Fl(g, "localOffset_x"), 0.006) && Near(s.Y, GameBp.Fl(g, "localOffset_y"), 0.006) && Near(s.X2, GameBp.Fl(g, "localOffset_x2"), 0.006) && Near(s.Y2, GameBp.Fl(g, "localOffset_y2"), 0.006),
                w + "geometry " + s + " vs (" + Fmt(GameBp.Fl(g, "localOffset_x")) + "," + Fmt(GameBp.Fl(g, "localOffset_y")) + ")->(" + Fmt(GameBp.Fl(g, "localOffset_x2")) + "," + Fmt(GameBp.Fl(g, "localOffset_y2")) + ")");
            e.Check(s.OutputToSlot == GameBp.In(g, "outputToSlot") && s.InputFromSlot == GameBp.In(g, "inputFromSlot"), w + "slots " + s.OutputToSlot + "/" + s.InputFromSlot);
            e.Check(s.InputOffset == GameBp.In(g, "inputOffset") && s.OutputOffset == GameBp.In(g, "outputOffset"), w + "offsets " + s.InputOffset + "/" + s.OutputOffset);
            e.Check(NearYaw(s.Yaw, GameBp.Fl(g, "yaw"), 1), w + "yaw " + Fmt(s.Yaw));
            e.Check(s.SorterLength == 1, w + "length " + s.SorterLength);
        }
        return e.Result();
    }

    static string ModulesSplitterVariants()
    {
        string path = Cells.Resolve(bpDir, UserTurbine);
        if (!File.Exists(path)) return "missing " + path;
        object bp = GameBp.Load(path);
        var e = new Errs();
        var all = GameBp.Buildings(bp).Cast<object>().Where(r => Ids.IsBelt(GameBp.In(r, "itemId"))).ToList();
        Action<Fragment, string> cmp = (f, name) =>
        {
            foreach (Bld b in f.Blds.Where(b => b.Kind == Kind.Belt))
            {
                object g = all.FirstOrDefault(r => Near(GameBp.Fl(r, "localOffset_x"), b.X, 0.006) && Near(GameBp.Fl(r, "localOffset_y"), b.Y, 0.006) && Near(GameBp.Fl(r, "localOffset_z"), b.Z, 0.003));
                if (g == null) { e.L.Add(name + ": no game belt at " + b + " z " + Fmt(b.Z)); continue; }
                if (b.Output != null && b.Output.Kind == Kind.Splitter) e.Check(GameBp.In(g, "outputToSlot") == b.OutputToSlot, name + ": input slot " + b.OutputToSlot + " vs " + GameBp.In(g, "outputToSlot"));
                if (b.Input != null && b.Input.Kind == Kind.Splitter) e.Check(GameBp.In(g, "inputFromSlot") == b.InputFromSlot, name + ": output slot " + b.InputFromSlot + " vs " + GameBp.In(g, "inputFromSlot"));
                e.Check(NearYaw(GameBp.Fl(g, "yaw"), b.Yaw, 1), name + ": yaw at " + b);
            }
        };
        var o39 = new List<KeyValuePair<Dir, bool>> { new KeyValuePair<Dir, bool>(Dir.N, false), new KeyValuePair<Dir, bool>(Dir.N, true) };
        var o40 = new List<KeyValuePair<Dir, bool>> { new KeyValuePair<Dir, bool>(Dir.N, true), new KeyValuePair<Dir, bool>(Dir.S, true) };
        Fragment f39 = Modules.SplitterV(18, 35, 39, Dir.S, o39, Ids.BeltMk2, 0, false);
        Fragment f40 = Modules.SplitterV(21, 35, 40, Dir.E, o40, Ids.BeltMk2, 0, false);
        cmp(f39, "m39"); cmp(f40, "m40");
        e.Check(Modules.IsProvenSplitterConfig(39, Dir.S, false, o39) && Modules.IsProvenSplitterConfig(40, Dir.E, false, o40), "user's configurations not marked proven");
        e.Check(!Modules.IsProvenSplitterConfig(40, Dir.W, false, o40), "m40 in W marked proven");
        e.Check(Modules.SplitterSlot(39, Dir.E, false) < 0 && Modules.SplitterSlot(40, Dir.N, false) < 0, "port table: m39 E or m40 N low exists");
        return e.Result();
    }

    static string ValidatorSplitterAndRaised()
    {
        var e = new Errs();
        // unused splitter: one incoming belt only
        var l = new Layout(gd);
        Fragment sp = Modules.Splitter(0, 0, Dir.S, new[] { Dir.N }, Ids.BeltMk2, Ids.IronOre, false);
        foreach (Bld b in sp.Blds) if (!(b.Input != null && b.Input.Kind == Kind.Splitter) && !(b.Kind == Kind.Belt && b.Role != null && b.Role.Contains(" N"))) l.Add(b);
        var r1 = new Validator(gd, null).ValidateBelts(l).Errors;
        e.Check(r1.Any(m => m.Contains("unused splitter")), "splitter with 1 belt accepted: " + string.Join(" | ", r1.ToArray()));
        // raised run over free ground with a tower under it, and a sorter end next to it
        Func<bool, bool, List<string>> run = (tower, sorter) =>
        {
            var g = new Layout(gd);
            var chain = new List<Bld>();
            for (int x = -2; x <= 6; x++) { Bld b = Bld.Belt(Ids.BeltMk2, x, 0, 90, Ids.IronOre, "r"); if (x >= 1 && x <= 3) b.Z = b.Z2 = 0.5; chain.Add(g.Add(b)); }
            for (int i = 0; i + 1 < chain.Count; i++) Layout.Link(chain[i], chain[i + 1], 1);
            if (tower) g.Add(Bld.Tesla(2, 0));
            if (sorter) g.All.Add(new Bld { Kind = Kind.Sorter, ItemId = Ids.SorterMk2, X = 2, Y = 0.2, X2 = 2, Y2 = 1.4, Parameters = new[] { 1 } });
            return new Validator(gd, null).ValidateBelts(g).Errors;
        };
        var ok = run(false, false); e.Check(ok.Count == 0, "raised run over free ground rejected: " + string.Join(" | ", ok.ToArray()));
        e.Check(run(true, false).Any(m => m.Contains("over a building")), "raised node over a tower accepted");
        e.Check(run(false, true).Any(m => m.Contains("from a sorter")), "raised node on a sorter end accepted");
        return e.Result();
    }

    /// Shape of the user's C1/C3 edit (refs\engine-turbine-60-v2-userupgrade.txt), mirrored to the origin.
    static List<string> RaisedParallelErrors(bool opposite)
    {
        var g = new Layout(gd);
        var a = new List<Bld>();
        for (int x = 10; x >= 0; x--) a.Add(g.Add(Bld.Belt(Ids.BeltMk2, opposite ? 10 - x : x, 0, opposite ? 90 : 270, Ids.IronIngot, "a")));
        for (int i = 0; i + 1 < a.Count; i++) Layout.Link(a[i], a[i + 1], 1);
        var pts = new List<double[]>   // x, y, z, yaw
        {
            new double[] { 8, -4, 0, 0 }, new double[] { 8, -3, 0, 0 }, new double[] { 8, -2, 0.5, 0 }, new double[] { 8, -1, 0.5, 0 }, new double[] { 8, 0, 0.5, 315 },
        };
        for (int x = 7; x >= 3; x--) pts.Add(new double[] { x, 0, 0.5, 270 });
        pts.Add(new double[] { 2, 0, 0.5, 225 }); pts.Add(new double[] { 2, -1, 0.5, 180 }); pts.Add(new double[] { 2, -2, 0, 180 }); pts.Add(new double[] { 2, -3, 0, 180 });
        var c = new List<Bld>();
        foreach (var p in pts) { Bld b = Bld.Belt(Ids.BeltMk2, (int)p[0], (int)p[1], p[3], 1201, "c"); b.Z = b.Z2 = p[2]; c.Add(g.Add(b)); }
        for (int i = 0; i + 1 < c.Count; i++) Layout.Link(c[i], c[i + 1], 1);
        return new Validator(gd, null).ValidateBelts(g).Errors;
    }

    static string MicroRaisedParallel()
    {
        var e = new Errs();
        var ok = RaisedParallelErrors(false);
        e.Check(ok.Count == 0, "user's raised run shape rejected: " + string.Join(" | ", ok.ToArray()));
        var bad = RaisedParallelErrors(true);
        e.Check(bad.Count > 0, "raised run over an opposite-direction belt accepted");
        return e.Result();
    }

    static string MicroGearBuffer()
    {
        var spec = new BlockSpec { Item = 1201, Rate = 120 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        spec.Buffer = true;
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var comp = new Composer(gd, spec);
        Layout l = comp.Compose(plan);
        var e = new Errs();
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        e.Check(l.Count(Kind.Machine, Ids.AsmMk1) == 3, l.Count(Kind.Machine, Ids.AsmMk1) + " assemblers, expected a 3-machine row");
        e.Check(l.Count(Kind.Storage, Ids.DepotMk1) == 1, l.Count(Kind.Storage, 0) + " depots");
        if (e.L.Count == 0)
        {
            ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, Emitter.HeaderFor(l, plan, spec, gd));
            var check = new ValidationReport();
            GameEmitter.CheckExport(x, l, check);
            foreach (string m in x.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
        }
        Rect bx = l.Bounds();
        Console.WriteLine("      " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height);
        return e.Result();
    }

    // ------------------------------------------------------------------ engine API (FlatEngine)
    class TestSource : IGameSource
    {
        public GameData Data { get; set; }
        public PrototypeCells Cells { get; set; }
        public string GameVersion { get; set; }
    }

    static TestSource Source() { return new TestSource { Data = gd, Cells = GetCells().Prototypes, GameVersion = GameBp.InstalledGameVersion() }; }

    static BlockSpec GearSpec()
    {
        var spec = new BlockSpec { Item = 1201, Rate = 120 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        spec.Buffer = true;
        return spec;
    }

    static string MicroFlatGear()
    {
        BlockSpec spec = GearSpec();
        ProductionPlan plan = new Solver(gd, spec).Solve();
        PlanInput input = PlanMapper.FromPlan(plan, spec);
        input.Steps.Reverse();   // any order: the engine sorts topologically
        int calls = 0;
        EngineResult res = FlatEngine.Generate(Source(), input, (f, s) => calls++, () => false);
        var e = new Errs();
        foreach (string m in res.Errors) e.L.Add("engine: " + m);
        if (!res.Ok) return e.Result();
        e.Check(calls > 0, "no progress reported");
        e.Check(!res.Warnings.Any(w => w.Contains("dropped")), "unexpected drop: " + string.Join("; ", res.Warnings.ToArray()));
        // the mapped plan equals the solver's plan
        e.Check(res.Plan.Steps.Count == plan.Steps.Count, res.Plan.Steps.Count + " mapped steps, solver " + plan.Steps.Count);
        for (int i = 0; i < Math.Min(res.Plan.Steps.Count, plan.Steps.Count); i++)
        {
            Step a = res.Plan.Steps[i], b = plan.Steps[i];
            e.Check(a.Item == b.Item && a.Recipe.Id == b.Recipe.Id && a.MachineItem == b.MachineItem && a.Machines == b.Machines
                && Math.Abs(a.MachinesExact - b.MachinesExact) < 1e-9 && a.Speed == b.Speed, "step " + i + ": " + a + " vs " + b);
            e.Check(string.Join(",", a.Inputs.Select(x => x.ToString()).ToArray()) == string.Join(",", b.Inputs.Select(x => x.ToString()).ToArray()), "step " + i + " inputs");
            e.Check(string.Join(",", a.ConsumerSteps.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) == string.Join(",", b.ConsumerSteps.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()), "step " + i + " consumers");
        }
        e.Check(string.Join(",", res.Plan.RawInputs.Select(x => x.ToString()).ToArray()) == string.Join(",", plan.RawInputs.Select(x => x.ToString()).ToArray()), "raw inputs");
        // same layout as the composer run directly
        Layout direct = new Composer(gd, spec).Compose(plan);
        e.Check(direct.All.Count == res.Layout.All.Count && direct.Bounds().Width == res.Layout.Bounds().Width && direct.Bounds().Height == res.Layout.Bounds().Height,
            "engine layout " + res.Layout.All.Count + " buildings " + res.AreaWidth + "x" + res.AreaHeight + ", composer " + direct.All.Count);
        e.Check(res.Buildings.Count == res.Layout.All.Count, res.Buildings.Count + " records for " + res.Layout.All.Count + " buildings");
        e.Check(res.Icons[0] == 1201 && res.GameVersion == GameBp.InstalledGameVersion(), "icon0 " + res.Icons[0] + ", version " + res.GameVersion);
        for (int i = 0; i < res.Buildings.Count; i++)
        {
            BpBuilding r = res.Buildings[i]; Bld b = res.Layout.All[i];
            int wantOut = b.Output != null ? b.Output.Index : -1, wantIn = b.Input != null ? b.Input.Index : -1;
            if (r.index != i || r.itemId != b.ItemId || r.tempOutputObjIdx != wantOut || r.tempInputObjIdx != wantIn) { e.L.Add("record " + i + " does not mirror " + b); break; }
        }
        // the records serialize through the game and survive the export check
        ExportResult xr = new GameEmitter(gd, GetCells()).Serialize(res.Buildings, res.Header, res.Layout);
        var check = new ValidationReport();
        GameEmitter.CheckExport(xr, res.Layout, check);
        foreach (string m in xr.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
        Console.WriteLine("      " + res.Buildings.Count + " records, " + res.AreaWidth + "x" + res.AreaHeight + ", " + res.Warnings.Count + " warning(s), export " + (xr.Ok ? "ok" : "FAILED"));
        return e.Result();
    }

    static string MicroFlatDrop()
    {
        BlockSpec spec = GearSpec();
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var e = new Errs();
        // iron ingot step on a chemical plant (not placeable) -> dropped, iron ingot arrives on an input lane
        PlanInput input = PlanMapper.FromPlan(plan, spec);
        PlanStep ingot = input.Steps.First(s => s.OutputsPerMin.ContainsKey(Ids.IronIngot));
        ingot.BuildingItemId = Ids.ChemicalPlant;
        EngineResult res = FlatEngine.Generate(Source(), input, null, null);
        foreach (string m in res.Errors) e.L.Add("engine: " + m);
        if (!res.Ok) return e.Result();
        e.Check(res.Warnings.Any(w => w.Contains("dropped") && w.Contains("Iron Ingot")), "no drop warning: " + string.Join("; ", res.Warnings.ToArray()));
        e.Check(res.Plan.Steps.Count == 1 && res.Plan.RawInputs.Any(r => r.Item == Ids.IronIngot), "plan: " + res.Plan.Describe(gd));
        e.Check(res.Layout.Count(Kind.Machine, Ids.ArcSmelter) == 0, "smelters placed for a dropped step");
        e.Check(res.Layout.Ports.Any(p => p.Kind == PortKind.In && p.Item == Ids.IronIngot), "no iron ingot input lane");
        // byproduct recipe on an assembler -> dropped with its products listed (mapping only)
        RecipeDef by = gd.Recipes.Values.OrderBy(r => r.Id).First(r => r.Outputs.Count > 1);
        PlanInput input2 = PlanMapper.FromPlan(plan, spec);
        input2.Steps.Add(new PlanStep { RecipeId = by.Id, BuildingItemId = Ids.AsmMk1, MachineCount = 1, Machines = 1 });
        PlanMapper.Result m2 = PlanMapper.Map(gd, input2);
        e.Check(m2.Errors.Count == 0, "byproduct mapping errors: " + string.Join("; ", m2.Errors.ToArray()));
        e.Check(m2.Warnings.Any(w => w.Contains("byproducts")) && by.Outputs.All(o => m2.DroppedItems.Contains(o.Item)), "recipe " + by.Id + " " + by.Name + " not dropped as byproduct");
        // dropping a step removes the steps that only fed it (motor: coil dropped -> magnet + copper ingot steps go too)
        int motor = gd.ResolveItem("Electric Motor");
        var mspec = new BlockSpec { Item = motor, Rate = 45 };
        PlanInput input4 = PlanMapper.FromPlan(new Solver(gd, mspec).Solve(), mspec);
        input4.Steps.First(s => s.OutputsPerMin.ContainsKey(Ids.MagneticCoil)).BuildingItemId = Ids.ChemicalPlant;
        PlanMapper.Result m4 = PlanMapper.Map(gd, input4);
        e.Check(m4.Errors.Count == 0 && m4.Plan.StepFor(Ids.Magnet) == null && m4.Plan.StepFor(Ids.CopperIngot) == null
            && m4.Plan.RawInputs.Any(r => r.Item == Ids.MagneticCoil) && m4.Plan.StepFor(Ids.IronIngot) != null
            && m4.Warnings.Count(w => w.Contains("removed")) == 2, "motor without coil: " + string.Join("; ", m4.Errors.Concat(m4.Warnings).ToArray()));
        // target step not placeable -> error, nothing built
        PlanInput input3 = PlanMapper.FromPlan(plan, spec);
        input3.Steps.First(s => s.OutputsPerMin.ContainsKey(1201)).BuildingItemId = Ids.Refinery;
        EngineResult r3 = FlatEngine.Generate(Source(), input3, null, null);
        e.Check(!r3.Ok && r3.Errors.Any(m => m.Contains("target")) && r3.Buildings.Count == 0, "dropped target not reported: " + string.Join("; ", r3.Errors.ToArray()));
        Console.WriteLine("      drop: " + res.Buildings.Count + " records, warnings: " + string.Join(" | ", res.Warnings.Where(w => w.Contains("dropped")).ToArray()));
        return e.Result();
    }

    static string MicroFlatCancel()
    {
        BlockSpec spec = GearSpec();
        PlanInput input = PlanMapper.FromPlan(new Solver(gd, spec).Solve(), spec);
        EngineResult res = FlatEngine.Generate(Source(), input, null, () => true);
        var e = new Errs();
        e.Check(!res.Ok && res.Errors.Any(m => m.Contains("cancelled")) && res.Buildings.Count == 0, "cancel ignored: ok " + res.Ok + ", " + string.Join("; ", res.Errors.ToArray()));
        return e.Result();
    }

    static string MicroSplitterVariants()
    {
        var e = new Errs();
        var o39 = new List<KeyValuePair<Dir, bool>> { new KeyValuePair<Dir, bool>(Dir.N, false), new KeyValuePair<Dir, bool>(Dir.N, true) };
        var o40 = new List<KeyValuePair<Dir, bool>> { new KeyValuePair<Dir, bool>(Dir.N, true), new KeyValuePair<Dir, bool>(Dir.S, true) };
        foreach (var f in new[] { Modules.SplitterV(0, 0, 39, Dir.S, o39, Ids.BeltMk2, Ids.IronOre, false), Modules.SplitterV(0, 0, 40, Dir.E, o40, Ids.BeltMk2, Ids.IronOre, false) })
        {
            var l = new Layout(gd);
            foreach (Bld b in f.Blds) l.Add(b);
            var sp = f.Blds.First(b => b.Kind == Kind.Splitter);
            int outs = f.Blds.Count(b => b.Kind == Kind.Belt && b.Input == sp);
            e.Check(outs == 2, "m" + sp.ModelIndex + ": " + outs + " output belts");
            var r = new Validator(gd, null).ValidateBelts(l).Errors;
            e.Check(r.Count == 0, "m" + sp.ModelIndex + ": " + string.Join(" | ", r.ToArray()));
        }
        return e.Result();
    }

    /// Header fields as the game's BlueprintData.HeaderFromBase64String reads them (that method itself cannot run under
    /// .NET Framework: it calls String.Split(char, StringSplitOptions)): split at ',', [2..6] icons, [10] shortDesc,
    /// desc = [14] when there are >= 15 fields else [11], text fields through Uri.UnescapeDataString.
    static string[] GameHeader(string text)
    {
        string[] f = text.Substring(0, text.IndexOf('"')).Split(',');
        if (f.Length < 12 || !f[0].StartsWith("BLUEPRINT:")) throw new Exception("not a blueprint header");
        return new[] { f[2], f[3], Uri.UnescapeDataString(f[10]), Uri.UnescapeDataString(f.Length >= 15 ? f[14] : f[11]) };
    }

    /// shortDesc / desc / icons written == what the game reads back from the text; the raw header holds "%0A" breaks.
    static void CheckHeaderText(string text, BpHeader h, Errs e)
    {
        string[] back = GameHeader(text);
        e.Check(back[2] == h.ShortDesc, "header shortDesc '" + back[2] + "', written '" + h.ShortDesc + "'");
        e.Check(back[3] == h.Desc, "header desc differs: '" + back[3] + "'");
        e.Check(back[0] == h.Icons[0].ToString(CultureInfo.InvariantCulture) && back[1] == h.Icons[1].ToString(CultureInfo.InvariantCulture), "header icons " + back[0] + "," + back[1]);
        string head = text.Substring(0, text.IndexOf('"'));
        e.Check(!h.Desc.Contains("\n") || head.Contains("%0A"), "no %0A line break in the raw header");
        int last = text.LastIndexOf('"');
        e.Check(GameBp.Md5F(text.Substring(0, last)) == text.Substring(last + 1), "MD5F mismatch");
    }

    static string MicroText()
    {
        var e = new Errs();
        e.Check(BpText.Digits(4, 2) == @"\s600;\s604;" && BpText.Digits(11) == @"\s601;\s601;", "digits " + BpText.Digits(4, 2) + " " + BpText.Digits(11));
        e.Check(BpText.Icon(gd, Ids.AsmMk1) == @"\zzt1;" && BpText.Icon(gd, Ids.BeltMk2) == @"\csd2;", "icon tags " + BpText.Icon(gd, Ids.AsmMk1) + " " + BpText.Icon(gd, Ids.BeltMk2));
        e.Check(BpText.BlockThumb(gd, 8, Ids.AsmMk1, 1, Ids.BeltMk2) == @"\s608;\zzt1;\s601;\csd2;", "user template 8x1 not reproduced: " + BpText.BlockThumb(gd, 8, Ids.AsmMk1, 1, Ids.BeltMk2));
        string longT = BpText.Thumb(BpText.Digits(123456), @"\zzt1;", @"\csd2;");
        e.Check(longT == BpText.Digits(123456) && longT.Length <= BpText.ThumbMax, "40-char cut: '" + longT + "'");

        var spec = new BlockSpec { Item = 1201, Rate = 120 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        spec.Buffer = true;
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Layout l = new Composer(gd, spec).Compose(plan);
        BpHeader h = Emitter.HeaderFor(l, plan, spec, gd);
        int lanes = l.Ports.Count(p => p.Kind == PortKind.In);
        e.Check(h.ShortDesc == @"\s603;\zzt1;" + BpText.Digits(lanes) + @"\csd2;", "gear shortDesc '" + h.ShortDesc + "'");
        string[] lines = h.Desc.Split('\n');
        e.Check(lines[0] == "Gear 120/min" && lines.Skip(1).All(s => s.StartsWith("- ")) && lines.All(s => s.Length <= 60), "desc lines: " + h.Desc.Replace("\n", " | "));
        e.Check(lines.Contains("- buffer depot at the end") && lines.Any(s => s.StartsWith("- out N: Gear 120")), "desc facts: " + h.Desc.Replace("\n", " | "));
        ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, h);
        foreach (string m in x.Errors) e.L.Add("export: " + m);
        if (x.Ok) CheckHeaderText(x.Text, h, e);
        Console.WriteLine("      shortDesc " + h.ShortDesc + " | desc " + h.Desc.Replace("\n", " | "));
        return e.Result();
    }

    static string MicroTextCoil()
    {
        var e = new Errs();
        string start = Path.Combine(bpDir, @"START\engine-coil-360-v3.txt");
        if (!File.Exists(start)) throw new SkipException("no " + start);
        BlockSpec spec = CoilSpec();
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Layout l = new Composer(gd, spec).Compose(plan);
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        BpHeader h = Emitter.HeaderFor(l, plan, spec, gd);
        ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, h);
        foreach (string m in x.Errors) e.L.Add("export: " + m);
        if (!x.Ok) return e.Result();
        var check = new ValidationReport();
        GameEmitter.CheckExport(x, l, check);
        foreach (string m in check.Errors) e.L.Add("check-export: " + m);
        // same model as the START file: every record identical (fields, positions, links)
        object reference = GameBp.Load(start);
        int unmatched = CompareMultiset(GameBp.Buildings(x.Reimported), GameBp.Buildings(reference), e);
        CheckHeaderText(x.Text, h, e);
        if (e.L.Count > 0) return e.Result();
        string dir = Path.Combine(root, @"work\text");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "coil-360-text.txt");
        GameBp.Write(path, x.Text);
        // the file on disk: payload re-import + header parse again
        string disk = File.ReadAllText(path);
        e.Check(GameBp.Buildings(GameBp.Load(path)).Length == GameBp.Buildings(reference).Length, "building count on disk");
        CheckHeaderText(disk, h, e);
        Console.WriteLine("      " + GameBp.Buildings(reference).Length + " records identical to START v3 (" + unmatched + " unmatched); written " + path);
        Console.WriteLine("      shortDesc " + h.ShortDesc + " | desc " + h.Desc.Replace("\n", " | "));
        return e.Result();
    }

    static string PipelineCoilBuffer()
    {
        BlockSpec spec = CoilSpec(); spec.Buffer = true;
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var comp = new Composer(gd, spec);
        Layout l = comp.Compose(plan);
        var e = new Errs();
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        e.Check(l.Count(Kind.Storage, Ids.DepotMk1) == 1, l.Count(Kind.Storage, 0) + " depots");
        var ds = l.All.Where(b => b.Kind == Kind.Sorter && (b.Output != null && b.Output.Kind == Kind.Storage || b.Input != null && b.Input.Kind == Kind.Storage)).ToList();
        e.Check(ds.Count(s => s.Output != null && s.Output.Kind == Kind.Storage) == 1 && ds.Count(s => s.Input != null && s.Input.Kind == Kind.Storage) == 2, "depot sorters: " + ds.Count);
        Port outP = l.Ports.FirstOrDefault(p => p.Kind == PortKind.Out);
        e.Check(outP != null && outP.Belt.Output == null && ds.Any(s => s.Output == outP.Belt), "the product chain does not end on the last out-sorter tile");
        if (e.L.Count == 0)
        {
            ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, Emitter.HeaderFor(l, plan, spec, gd));
            var check = new ValidationReport();
            GameEmitter.CheckExport(x, l, check);
            foreach (string m in x.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
        }
        Rect bx = l.Bounds();
        Console.WriteLine("      " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height + "; " + string.Join("; ", comp.Notes.Where(n => n.Contains("buffer")).ToArray()));
        return e.Result();
    }

    // ------------------------------------------------------------------ lanes (PLAN.md decisions 7, 8, 9; engine\Lanes.cs)
    /// One-step plan: Glass (2 stone -> 1 glass, arc smelter) with the given exact/built machine count and tier caps.
    static PlanInput GlassInput(double exact, int machines, int beltCap, int sorterCap, bool cheapest)
    {
        int glass = gd.ResolveItem("Glass");
        RecipeDef r = gd.DefaultRecipe(glass);
        if (r == null || r.Inputs.Count != 1 || r.Outputs.Count != 1 || !r.Buildings.Contains(Ids.ArcSmelter)) throw new Exception("glass recipe is not a 1-input arc smelter recipe");
        double crafts = exact * 60.0 * gd.Speed(Ids.ArcSmelter) / r.TimeSec;
        var ps = new PlanStep { RecipeId = r.Id, BuildingItemId = Ids.ArcSmelter, MachineCount = exact, Machines = machines };
        ps.OutputsPerMin[glass] = crafts * r.OutCount(glass);
        foreach (ItemCount x in r.Inputs) ps.InputsPerMin[x.Item] = crafts * x.Count;
        var input = new PlanInput { TargetItemId = glass, TargetPerMin = ps.OutputsPerMin[glass] };
        input.Steps.Add(ps);
        foreach (ItemCount x in r.Inputs) input.ExternalItems.Add(x.Item);
        input.Options.BeltItemMax = beltCap; input.Options.SorterItemMax = sorterCap; input.Options.CheapestTiers = cheapest;
        input.Options.Buffer = false;
        return input;
    }

    /// Belts of the chain starting at a belt (following Output links).
    static List<Bld> LaneFrom(Bld start)
    {
        var lane = new List<Bld>();
        for (Bld b = start; b != null && b.Kind == Kind.Belt && !lane.Contains(b); b = b.Output) lane.Add(b);
        return lane;
    }

    static int PickupsOn(Layout l, Bld start)
    {
        var lane = new HashSet<Bld>(LaneFrom(start));
        return l.All.Count(s => s.Kind == Kind.Sorter && s.Input != null && lane.Contains(s.Input));
    }

    static void ExportCheck(EngineResult res, List<BpBuilding> recs, Errs e, string what)
    {
        ExportResult xr = new GameEmitter(gd, GetCells()).Serialize(recs, res.Header, res.Layout);
        var check = new ValidationReport();
        GameEmitter.CheckExport(xr, res.Layout, check);
        foreach (string m in xr.Errors.Concat(check.Errors)) e.L.Add(what + " export: " + m);
    }

    static string MicroLanesHelpers()
    {
        var e = new Errs();
        e.Check(gd.BeltRate(Ids.BeltMk1) == 360 && gd.BeltRate(Ids.BeltMk2) == 720 && gd.BeltRate(Ids.BeltMk3) == 1800, "belt rates " + gd.BeltRate(Ids.BeltMk1) + "/" + gd.BeltRate(Ids.BeltMk2) + "/" + gd.BeltRate(Ids.BeltMk3));
        e.Check(Near(gd.SorterRate(Ids.SorterMk1, 1), 90, 1e-6) && Near(gd.SorterRate(Ids.SorterMk2, 2), 90, 1e-6) && Near(gd.SorterRate(Ids.SorterMk3, 1), 360, 1e-6) && Near(gd.SorterRate(Ids.SorterMk3, 2), 180, 1e-6),
            "sorter rates Mk.I L1 " + Fmt(gd.SorterRate(Ids.SorterMk1, 1)) + ", Mk.III L1 " + Fmt(gd.SorterRate(Ids.SorterMk3, 1)));
        e.Check(Lanes.BeltFor(gd, 300, Ids.BeltMk3, null) == Ids.BeltMk1, "300/min -> " + Lanes.BeltFor(gd, 300, Ids.BeltMk3, null));
        e.Check(Lanes.BeltFor(gd, 360, Ids.BeltMk3, null) == Ids.BeltMk1, "360/min (exactly Mk.I) -> " + Lanes.BeltFor(gd, 360, Ids.BeltMk3, null));
        e.Check(Lanes.BeltFor(gd, 500, Ids.BeltMk3, null) == Ids.BeltMk2, "500/min -> " + Lanes.BeltFor(gd, 500, Ids.BeltMk3, null));
        e.Check(Lanes.BeltFor(gd, 1000, Ids.BeltMk3, null) == Ids.BeltMk3, "1000/min, Mk.III cap -> " + Lanes.BeltFor(gd, 1000, Ids.BeltMk3, null));
        e.Check(Lanes.BeltFor(gd, 1000, Ids.BeltMk2, null) == Ids.BeltMk2, "1000/min, Mk.II cap -> " + Lanes.BeltFor(gd, 1000, Ids.BeltMk2, null));
        e.Check(Lanes.BeltFor(gd, 300, Ids.BeltMk3, new HashSet<int> { Ids.BeltMk1 }) == Ids.BeltMk2, "300/min without a Mk.I prototype");
        e.Check(Lanes.SorterFor(gd, 60, 1, Ids.SorterMk3, null) == Ids.SorterMk1, "sorter 60/min L1 -> " + Lanes.SorterFor(gd, 60, 1, Ids.SorterMk3, null));
        e.Check(Lanes.SorterFor(gd, 100, 1, Ids.SorterMk3, null) == Ids.SorterMk2, "sorter 100/min L1 -> " + Lanes.SorterFor(gd, 100, 1, Ids.SorterMk3, null));
        e.Check(Lanes.SorterFor(gd, 200, 1, Ids.SorterMk3, null) == Ids.SorterMk3, "sorter 200/min L1 -> " + Lanes.SorterFor(gd, 200, 1, Ids.SorterMk3, null));
        e.Check(Lanes.SorterFor(gd, 100, 2, Ids.SorterMk3, null) == Ids.SorterMk3, "sorter 100/min L2 -> " + Lanes.SorterFor(gd, 100, 2, Ids.SorterMk3, null));
        e.Check(Lanes.SorterFor(gd, 200, 1, Ids.SorterMk2, null) == Ids.SorterMk2, "sorter 200/min L1, Mk.II cap -> " + Lanes.SorterFor(gd, 200, 1, Ids.SorterMk2, null));
        e.Check(Lanes.MachinesPerLane(720, 60) == 12 && Lanes.MachinesPerLane(1800, 60) == 30 && Lanes.MachinesPerLane(720, 50) == 14 && Lanes.MachinesPerLane(360, 40) == 9,
            "floor(lane / demand): " + Lanes.MachinesPerLane(720, 60) + " " + Lanes.MachinesPerLane(1800, 60) + " " + Lanes.MachinesPerLane(720, 50) + " " + Lanes.MachinesPerLane(360, 40));
        var w = new List<string>(); var t = new Tiers();
        Lanes.ApplyCaps(gd, new LayoutOptions { BeltItemMax = Ids.BeltMk3, SorterItemMax = Lanes.PileSorter }, t, w);
        e.Check(t.Belt == Ids.BeltMk3 && t.Sorter == Ids.SorterMk3 && w.Count == 1, "caps Mk.III / pile -> " + t.Belt + " / " + t.Sorter + ", warnings: " + string.Join("; ", w.ToArray()));
        // prototypes: Mk.III belt = Mk.II record with the game's model index, Mk.III sorter = the COL-PAIR-3 template record
        Cells c = GetCells();
        BpBuilding b3 = c.Prototypes.ProtoFor(Ids.BeltMk3), s3 = c.Prototypes.ProtoFor(Ids.SorterMk3);
        e.Check(b3 != null && b3.itemId == Ids.BeltMk3 && b3.modelIndex == gd.Machine(Ids.BeltMk3).ModelIndex, "belt Mk.III prototype: " + (b3 == null ? "missing" : b3.itemId + " model " + b3.modelIndex));
        e.Check(s3 != null && s3.itemId == Ids.SorterMk3 && s3.modelIndex == gd.Machine(Ids.SorterMk3).ModelIndex, "sorter Mk.III prototype: " + (s3 == null ? "missing" : s3.itemId + " model " + s3.modelIndex));
        foreach (string m in c.Log.Where(m => m.Contains("mk3"))) Console.WriteLine("      " + m);
        return e.Result();
    }

    static string MicroLanesSplit13()
    {
        var e = new Errs();
        // 11.9 machines' worth of stone (714/min) fits one Mk.II lane by rate, but 13 built machines at full speed draw
        // 780/min: machines per lane = floor(720 / 60) = 12 -> 2 parallel lanes
        PlanInput input = GlassInput(11.9, 13, Ids.BeltMk2, Ids.SorterMk2, false);
        int stone = input.ExternalItems[0];
        EngineResult res = FlatEngine.Generate(Source(), input, null, null);
        foreach (string m in res.Errors) e.L.Add("engine: " + m);
        if (!res.Ok) return e.Result();
        Layout l = res.Layout;
        e.Check(l.Count(Kind.Machine, Ids.ArcSmelter) == 13, l.Count(Kind.Machine, Ids.ArcSmelter) + " smelters");
        var ins = l.Ports.Where(p => p.Kind == PortKind.In && p.Item == stone).OrderBy(p => p.X).ToList();
        e.Check(ins.Count == 2, ins.Count + " stone lanes, expected 2");
        var per = ins.Select(p => PickupsOn(l, p.Belt)).OrderByDescending(x => x).ToList();
        e.Check(per.Count == 2 && per[0] == 7 && per[1] == 6, "machines per lane " + string.Join("+", per.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", expected 7+6");
        var all = l.Ports.Where(p => p.Kind == PortKind.In).OrderBy(p => p.X).ToList();
        e.Check(ins.Count == 2 && Math.Abs(all.IndexOf(ins[0]) - all.IndexOf(ins[1])) == 1, "stone lanes are not neighbours on the edge");
        foreach (Port p in ins)
        {
            e.Check(p.Y == 0 && p.Dir == Dir.N, "stone lane at (" + p.X + "," + p.Y + ") not on the S edge");
            e.Check(p.Belt.Parameters != null && p.Belt.Parameters[0] == stone && !l.All.Any(s => s.Kind == Kind.Sorter && s.Input == p.Belt), "stone lane at x " + p.X + " has no icon tail");
        }
        Console.WriteLine("      " + res.Buildings.Count + " records, " + res.AreaWidth + "x" + res.AreaHeight + ", stone lanes at x " + string.Join(",", ins.Select(p => p.X.ToString(CultureInfo.InvariantCulture)).ToArray()));
        return e.Result();
    }

    static string MicroLanesMk3()
    {
        var e = new Errs();
        // Mk.III allowed: 17 smelters (1000/min stone) on one Mk.III lane (floor(1800 / 60) = 30 per lane)
        PlanInput a = GlassInput(1000.0 / 60.0, 17, Ids.BeltMk3, Ids.SorterMk3, true);
        int stone = a.ExternalItems[0];
        EngineResult ra = FlatEngine.Generate(Source(), a, null, null);
        foreach (string m in ra.Errors) e.L.Add("Mk.III cap: " + m);
        e.Check(!ra.Warnings.Any(w => w.Contains("prototype")), "Mk.III cap clamped: " + string.Join("; ", ra.Warnings.ToArray()));
        if (ra.Ok)
        {
            Layout l = ra.Layout;
            var ins = l.Ports.Where(p => p.Kind == PortKind.In && p.Item == stone).ToList();
            e.Check(ins.Count == 1, ins.Count + " stone lanes with Mk.III allowed, expected 1");
            if (ins.Count == 1)
            {
                var lane = LaneFrom(ins[0].Belt);
                e.Check(lane.All(b => b.ItemId == Ids.BeltMk3), "stone lane tiers " + string.Join(",", lane.Select(b => b.ItemId.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray()));
            }
            // cheapest tiers elsewhere: glass 500/min -> Mk.II; sorters 60 and 30/min at L1 -> Mk.I
            Port outP = l.Ports.FirstOrDefault(p => p.Kind == PortKind.Out);
            e.Check(outP != null && outP.Belt.ItemId == Ids.BeltMk2, "product lane belt " + (outP != null ? outP.Belt.ItemId : 0) + ", expected Mk.II");
            var sorterTiers = l.All.Where(b => b.Kind == Kind.Sorter).Select(s => s.ItemId).Distinct().ToList();
            e.Check(sorterTiers.Count == 1 && sorterTiers[0] == Ids.SorterMk1, "sorter tiers " + string.Join(",", sorterTiers.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
            ExportCheck(ra, ra.Buildings, e, "Mk.III belt");
            // Mk.III sorter records: every sorter raised to Mk.III (within the cap) still emits and re-imports
            foreach (Bld s in l.All.Where(b => b.Kind == Kind.Sorter)) s.ItemId = Ids.SorterMk3;
            var errs = new List<string>();
            List<BpBuilding> recs = new Emitter(gd, Source().Cells).Records(l, errs);
            foreach (string m in errs) e.L.Add("Mk.III sorter records: " + m);
            if (recs != null)
            {
                e.Check(recs.Any(r => r.itemId == Ids.SorterMk3 && r.modelIndex == gd.Machine(Ids.SorterMk3).ModelIndex) && recs.Any(r => r.itemId == Ids.BeltMk3 && r.modelIndex == gd.Machine(Ids.BeltMk3).ModelIndex), "Mk.III records/model indices missing");
                ExportCheck(ra, recs, e, "Mk.III sorter");
            }
            Console.WriteLine("      Mk.III cap: " + ra.Buildings.Count + " records, " + ra.AreaWidth + "x" + ra.AreaHeight + "; " + string.Join("; ", ra.Notes.Where(n => n.StartsWith("tiers")).ToArray()));
        }
        // Mk.II cap: 2 parallel Mk.II lanes, nothing above Mk.II
        PlanInput b2 = GlassInput(1000.0 / 60.0, 17, Ids.BeltMk2, Ids.SorterMk2, true);
        EngineResult rb = FlatEngine.Generate(Source(), b2, null, null);
        foreach (string m in rb.Errors) e.L.Add("Mk.II cap: " + m);
        if (rb.Ok)
        {
            var ins = rb.Layout.Ports.Where(p => p.Kind == PortKind.In && p.Item == stone).OrderBy(p => p.X).ToList();
            e.Check(ins.Count == 2, ins.Count + " stone lanes with a Mk.II cap, expected 2");
            e.Check(!rb.Layout.All.Any(x => x.ItemId == Ids.BeltMk3 || x.ItemId == Ids.SorterMk3), "Mk.III used under a Mk.II cap");
            var all = rb.Layout.Ports.Where(p => p.Kind == PortKind.In).OrderBy(p => p.X).ToList();
            e.Check(ins.Count == 2 && Math.Abs(all.IndexOf(ins[0]) - all.IndexOf(ins[1])) == 1, "stone lanes are not neighbours");
            ExportCheck(rb, rb.Buildings, e, "Mk.II cap");
            Console.WriteLine("      Mk.II cap: " + rb.Buildings.Count + " records, " + rb.AreaWidth + "x" + rb.AreaHeight + ", " + ins.Count + " stone lanes; " + string.Join("; ", rb.Notes.Where(n => n.StartsWith("tiers")).ToArray()));
        }
        return e.Result();
    }

    static string MicroLanesDroppedTail()
    {
        var e = new Errs();
        foreach (Dir edge in new[] { Dir.S, Dir.W })
        {
            BlockSpec spec = GearSpec();
            PlanInput input = PlanMapper.FromPlan(new Solver(gd, spec).Solve(), spec);
            input.Steps.First(s => s.OutputsPerMin.ContainsKey(Ids.IronIngot)).BuildingItemId = Ids.ChemicalPlant;   // dropped
            input.Options.InEdge = edge; input.Options.OutEdge = Dirs.Opposite(edge);
            EngineResult res = FlatEngine.Generate(Source(), input, null, null);
            foreach (string m in res.Errors) e.L.Add(edge + ": engine: " + m);
            if (!res.Ok) continue;
            Layout l = res.Layout;
            Port p = l.Ports.FirstOrDefault(x => x.Kind == PortKind.In && x.Item == Ids.IronIngot);
            if (p == null) { e.L.Add(edge + ": no iron ingot input lane"); continue; }
            Rect box = Rect.Empty;
            foreach (Bld b in l.All) if (b.Kind != Kind.Sorter && b.Kind != Kind.Tesla) box = box.Union(l.Footprints.TileRect(b));
            bool onEdge = edge == Dir.S ? p.Y == box.Y0 : p.X == box.X0;
            e.Check(onEdge && p.Dir == Dirs.Opposite(edge), edge + ": iron ingot lane starts at (" + p.X + "," + p.Y + ") flowing " + p.Dir + ", block " + box.X0 + ".." + box.X1 + " x " + box.Y0 + ".." + box.Y1);
            e.Check(p.Belt.Parameters != null && p.Belt.Parameters.Length == 2 && p.Belt.Parameters[0] == Ids.IronIngot, edge + ": no icon [1101,0] on the lane's first belt");
            e.Check(p.Belt.Output != null && !l.All.Any(s => s.Kind == Kind.Sorter && s.Input == p.Belt), edge + ": the icon belt is not a tail (pickup tile or dead end)");
            var edgeErr = Lanes.InputEdgeErrors(l, new BlockSpec { InEdge = edge });
            e.Check(edgeErr.Count == 0, edge + ": " + string.Join("; ", edgeErr.ToArray()));
            Console.WriteLine("      " + edge + ": iron ingot lane at (" + p.X + "," + p.Y + "), " + res.AreaWidth + "x" + res.AreaHeight);
            if (edge != Dir.S) continue;
            // negative: the port moved one belt into the block (off the edge) -> "input edge" failure
            Bld second = p.Belt.Output;
            second.Parameters = p.Belt.Parameters; p.Belt.Parameters = null;
            p.Belt = second; p.X = second.TileX; p.Y = second.TileY;
            ValidationReport rep = new Validator(gd, new BlockSpec { Item = 1201, Rate = 120, InEdge = Dir.S }).Validate(l, res.Plan);
            e.Check(rep.Errors.Any(m => m.Contains("input edge")), "lane off the edge accepted: " + (rep.Ok ? "validator ok" : rep.Errors[0]));
        }
        return e.Result();
    }
}
