// SelfTestDouble.cs - self tests of the double rows (engine\DoubleRows.cs, PATTERNS HOOK): two facing machine columns
// sharing the middle output lane, measured on the user's pattern sheet refs\patterns\p-sheet-001.txt (x29..37 smelters,
// x58..68 coil block). Cases: (a) fragment geometry == sheet records, (b) fragment validates + exports, (c) coil block with
// coaters, (d) composer run with / without double rows. Filter: MAKEBLOCK_CASES="micro double".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static partial void RunDoubleCases()
    {
        Case("micro double a: DoubleRow 12 arc smelters iron ingot == sheet x29..37 (pitch 3, lanes x29/x33/x37, sorter records within 0.04)", MicroDoubleSmelterGeometry);
        Case("micro double b: the 12-smelter double row in a layout passes the full Validator and the export check", MicroDoubleSmelterValidate);
        Case("micro double c: coil block (6 Asm Mk.I, 2 inputs, coaters on 4 lanes) == sheet x58..68, validates (rule 10 is a warning)", MicroDoubleCoilBlock);
        Case("micro double d: composer 12 smelters iron ingot 720/min with / without DoubleRows.Enabled: both valid, double not larger", MicroDoubleComposer);
    }

    const string SheetPath = DoubleRows.SheetFile;

    static Layout LoadSheet(Errs e)
    {
        string path = Cells.Resolve(bpDir, SheetPath);
        if (!File.Exists(path)) { e.L.Add("missing " + path); return null; }
        return FullLayout(GameBp.Load(path), false);
    }

    /// 12 Arc smelters iron ingot (recipe 1), one ore lane per column, Mk.II belts/sorters, tail on the ore lanes.
    static RowSpec SmelterDoubleSpec()
    {
        return new RowSpec { Name = "iron", MachineItem = Ids.ArcSmelter, RecipeId = Ids.RecipeIronIngot, Count = 12,
            InputItems = new[] { Ids.IronOre }, InputRates = new[] { 720.0 }, OutputItem = Ids.IronIngot, OutputRate = 720,
            TailItems = new[] { Ids.IronOre }, Data = gd };
    }

    /// Compares every sorter of the fragment (translated into sheet coordinates) with the sheet record on the same machine
    /// and belt tile: start/end within tol (a record drawn in the opposite direction counts as "reversed"), slot, length.
    static int CompareSorters(Errs e, Fragment f, Layout sheet, double tol, string tag, out int reversed)
    {
        reversed = 0;
        var sheetSorters = sheet.All.Where(b => b.Kind == Kind.Sorter).ToList();
        var sheetMachines = sheet.All.Where(b => b.Kind == Kind.Machine).ToList();
        int matched = 0;
        foreach (Bld m in f.Blds.Where(b => b.Kind == Kind.Machine))
        {
            Bld sm = sheetMachines.FirstOrDefault(x => Near(x.X, m.X, 0.01) && Near(x.Y, m.Y, 0.01));
            e.Check(sm != null, tag + ": no sheet machine at (" + Fmt(m.X) + "," + Fmt(m.Y) + ")");
            if (sm == null) continue;
            e.Check(sm.ItemId == m.ItemId && NearYaw(sm.Yaw, m.Yaw, 1.0), tag + ": sheet machine at (" + Fmt(m.X) + "," + Fmt(m.Y) + ") is item " + sm.ItemId + " yaw " + Fmt(sm.Yaw) + ", fragment " + m.ItemId + " yaw " + Fmt(m.Yaw));
            foreach (Bld s in f.Blds.Where(b => b.Kind == Kind.Sorter && (b.Input == m || b.Output == m)))
            {
                bool input = s.Output == m;
                Bld belt = input ? s.Input : s.Output;
                Bld t = sheetSorters.FirstOrDefault(x =>
                {
                    Bld tm = x.Input != null && x.Input.Kind == Kind.Machine ? x.Input : x.Output != null && x.Output.Kind == Kind.Machine ? x.Output : null;
                    Bld tb = tm == x.Input ? x.Output : x.Input;
                    return tm == sm && tb != null && tb.Kind == Kind.Belt && tb.TileX == belt.TileX && tb.TileY == belt.TileY;
                });
                string w = tag + " machine (" + Fmt(m.X) + "," + Fmt(m.Y) + ") sorter to belt (" + belt.TileX + "," + belt.TileY + ")";
                e.Check(t != null, w + ": no sheet sorter on that machine and belt tile");
                if (t == null) continue;
                matched++;
                bool same = Near(t.X, s.X, tol) && Near(t.Y, s.Y, tol) && Near(t.X2, s.X2, tol) && Near(t.Y2, s.Y2, tol);
                bool rev = Near(t.X2, s.X, tol) && Near(t.Y2, s.Y, tol) && Near(t.X, s.X2, tol) && Near(t.Y, s.Y2, tol);
                if (rev && !same) reversed++;
                e.Check(same || rev, w + ": fragment (" + Fmt(s.X) + "," + Fmt(s.Y) + ")->(" + Fmt(s.X2) + "," + Fmt(s.Y2) + ") vs sheet (" + Fmt(t.X) + "," + Fmt(t.Y) + ")->(" + Fmt(t.X2) + "," + Fmt(t.Y2) + ")");
                int mySlot = input ? s.OutputToSlot : s.InputFromSlot;
                int tSlot = Math.Max(t.OutputToSlot, t.InputFromSlot);
                e.Check(mySlot == tSlot, w + ": slot " + mySlot + ", sheet " + tSlot);
                e.Check(s.SorterLength == t.SorterLength, w + ": L" + s.SorterLength + ", sheet L" + t.SorterLength);
            }
        }
        return matched;
    }

    static string MicroDoubleSmelterGeometry()
    {
        var e = new Errs();
        Layout sheet = LoadSheet(e);
        if (sheet == null) return e.Result();
        RowSpec spec = SmelterDoubleSpec();
        Fragment f = Modules.DoubleRow(spec);
        foreach (string p in spec.Problems) e.L.Add("problem: " + p);
        var machines = f.Blds.Where(b => b.Kind == Kind.Machine).ToList();
        e.Check(machines.Count == 12, machines.Count + " machines");
        // column B (west, x -2) first machine -> sheet (31,11)
        Bld b0 = machines.Where(m => m.X < 0).OrderBy(m => m.Y).FirstOrDefault();
        if (b0 == null) { e.L.Add("no west column"); return e.Result(); }
        f.Translate((int)Math.Round(31 - b0.X), (int)Math.Round(11 - b0.Y));
        var xs = machines.Select(m => (int)Math.Round(m.X)).Distinct().OrderBy(x => x).ToList();
        e.Check(xs.Count == 2 && xs[0] == 31 && xs[1] == 35, "machine columns at x " + string.Join(",", xs.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", sheet 31/35");
        var ys = machines.Select(m => (int)Math.Round(m.Y)).Distinct().OrderBy(y => y).ToList();
        e.Check(ys.SequenceEqual(new[] { 11, 14, 17, 20, 23, 26 }), "machine rows y " + string.Join(",", ys.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", sheet 11..26 pitch 3");
        var laneX = f.Blds.Where(b => b.Kind == Kind.Belt).Select(b => b.TileX).Distinct().OrderBy(x => x).ToList();
        e.Check(laneX.SequenceEqual(new[] { 29, 33, 37 }), "lanes at x " + string.Join(",", laneX.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", sheet 29/33/37");
        var sheetLaneX = sheet.All.Where(b => b.Kind == Kind.Belt && b.TileY >= 11 && b.TileY <= 26 && b.TileX >= 29 && b.TileX <= 37).Select(b => b.TileX).Distinct().OrderBy(x => x).ToList();
        e.Check(sheetLaneX.SequenceEqual(laneX), "sheet lanes in the machine rows at x " + string.Join(",", sheetLaneX.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
        int reversed;
        int matched = CompareSorters(e, f, sheet, 0.04, "smelters", out reversed);
        var sheetMach = new HashSet<Bld>(sheet.All.Where(b => b.Kind == Kind.Machine && b.TileX >= 29 && b.TileX <= 37 && b.TileY >= 10 && b.TileY <= 27));
        int sheetCount = sheet.All.Count(b => b.Kind == Kind.Sorter && ((b.Input != null && sheetMach.Contains(b.Input)) || (b.Output != null && sheetMach.Contains(b.Output))));
        e.Check(matched == 24 && sheetCount == 24, matched + " sorters matched, the sheet's 12 machines have " + sheetCount + " sorters, expected 24");
        // the user re-drew the six west ore sorters belt -> machine (2026-11-08; before they were machine -> belt): none reversed now
        e.Check(reversed == 0, reversed + " sorter(s) reversed against the sheet, expected 0 (west ore lane fixed by the user 2026-11-08)");
        foreach (Bld s in f.Blds.Where(b => b.Kind == Kind.Sorter && b.Output != null && b.Output.Kind == Kind.Machine && b.Output.X < 33))
            e.Check(s.Input != null && s.Input.Kind == Kind.Belt && s.Input.Carries == Ids.IronOre && s.OutputToSlot == 10, "west input sorter " + s + " does not take iron ore into slot 10");
        // ports: column A (east) "in:1001" at x37, column B "in:1001#B" at x29, output at the lane end (33, 27); sheet lane runs to y28
        Port pa = f.Ports.FirstOrDefault(p => p.Name == "in:" + Ids.IronOre), pb = f.Ports.FirstOrDefault(p => p.Name == "in:" + Ids.IronOre + DoubleRows.PortSuffixB), po = f.Ports.FirstOrDefault(p => p.Name == "out:" + Ids.IronIngot);
        e.Check(pa != null && pa.X == 37 && pa.Y == 10 && pa.Dir == Dir.N && Near(pa.Rate, 360, 1e-9), "port in:1001 " + (pa == null ? "missing" : pa.ToString()) + ", expected (37,10) N 360/min");
        e.Check(pb != null && pb.X == 29 && pb.Y == 10 && pb.Dir == Dir.N && Near(pb.Rate, 360, 1e-9), "port in:1001#B " + (pb == null ? "missing" : pb.ToString()) + ", expected (29,10) N 360/min");
        e.Check(po != null && po.X == 33 && po.Y == 27 && Near(po.Rate, 720, 1e-9), "port out:1101 " + (po == null ? "missing" : po.ToString()) + ", expected (33,27) 720/min");
        e.Check(pa != null && pa.Belt.Parameters != null && pa.Belt.Parameters[0] == Ids.IronOre && !f.Blds.Any(s => s.Kind == Kind.Sorter && s.Input == pa.Belt), "column A tail belt has no icon or is a pickup tile");
        // sheet tails: (32,8) and (34,8) with runs along y9 to the outer lanes (the fragment keeps straight lanes, tails at y10)
        var tails = sheet.All.Where(b => b.Kind == Kind.Belt && b.Parameters != null && b.Parameters.Length == 2 && b.Parameters[0] == Ids.IronOre && b.TileY == 8 && b.TileX >= 29 && b.TileX <= 37).Select(b => b.TileX).OrderBy(x => x).ToList();
        e.Check(tails.SequenceEqual(new[] { 32, 34 }), "sheet ore tails at x " + string.Join(",", tails.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + " y8, expected 32/34");
        Console.WriteLine("      sheet x29..37: columns x31/x35 pitch 3, ore lanes x29/x37 (L1 lateral 0, slots 10/4), shared out x33 (west lat +1 slot 3, east lat -1 slot 9); "
            + matched + " sorters within 0.04, " + reversed + " drawn machine->belt in the sheet; notes: " + string.Join(" | ", spec.Notes.ToArray()));
        return e.Result();
    }

    /// Fragment -> layout with block ports, tails to the edge, towers; full Validate with a one-step plan; export check.
    static string ValidateFragment(Errs e, Fragment f, PlanInput input, string tag, string fileName, Func<string, bool> expectedError)
    {
        var l = new Layout(gd);
        l.Add(f);
        l.Ports.AddRange(f.Ports);
        // rule 5: the Composer's RouteRaw puts the icon on the first tile of every block input chain (lane tails and the
        // proliferator feed port alike); do the same here
        foreach (Port p in l.Ports.Where(p => p.Kind == PortKind.In && p.Belt != null && p.Belt.Parameters == null)) p.Belt.Parameters = new[] { p.Item, 0 };
        var errs = new List<string>();
        Lanes.TailsToEdge(l, l.Ports, (x, y) => l.Grid.IsFree(x, y), errs);
        foreach (string m in errs) e.L.Add(tag + " tails: " + m);
        foreach (Bld t in Modules.TeslaGrid(l, gd)) l.Add(t);
        foreach (string m in l.Errors) e.L.Add(tag + " layout: " + m);
        PlanMapper.Result pm = PlanMapper.Map(gd, input);
        foreach (string m in pm.Errors) e.L.Add(tag + " plan: " + m);
        if (pm.Plan == null) return null;
        ValidationReport rep = new Validator(gd, pm.Spec).Validate(l, pm.Plan);
        var unexpected = rep.Errors.Where(m => expectedError == null || !expectedError(m)).ToList();
        var expected = rep.Errors.Where(m => expectedError != null && expectedError(m)).ToList();
        foreach (string m in unexpected) e.L.Add(tag + " validate: " + m);
        string exportState = "skipped";
        if (unexpected.Count == 0)
        {
            ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, Emitter.HeaderFor(l, pm.Plan, pm.Spec, gd));
            var check = new ValidationReport();
            GameEmitter.CheckExport(x, l, check);
            foreach (string m in x.Errors.Concat(check.Errors)) e.L.Add(tag + " export: " + m);
            exportState = x.Ok && check.Ok ? "ok" : "FAILED";
            if (x.Ok && check.Ok && fileName != null)
            {
                string dir = Path.Combine(root, @"work\selftest");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, fileName), x.Text);
            }
        }
        Rect bx = l.Bounds();
        Console.WriteLine("      " + tag + ": " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height + ", " + l.Count(Kind.Tesla, 0) + " towers, validator "
            + (rep.Ok ? "ok" : rep.Errors.Count + " error(s)") + (expected.Count > 0 ? " (expected: " + expected[0] + ")" : "") + ", export " + exportState
            + (fileName != null && exportState == "ok" ? " -> work\\selftest\\" + fileName : ""));
        return expected.Count > 0 ? expected[0] : null;
    }

    static PlanInput IronPlan(int machines)
    {
        var p = new PlanInput { TargetItemId = Ids.IronIngot, TargetPerMin = 60.0 * machines };
        var s = new PlanStep { RecipeId = Ids.RecipeIronIngot, BuildingItemId = Ids.ArcSmelter, MachineCount = machines, Machines = machines };
        s.InputsPerMin[Ids.IronOre] = 60.0 * machines; s.OutputsPerMin[Ids.IronIngot] = 60.0 * machines;
        p.Steps.Add(s);
        p.ExternalItems.Add(Ids.IronOre);
        return p;
    }

    static string MicroDoubleSmelterValidate()
    {
        var e = new Errs();
        RowSpec spec = SmelterDoubleSpec();
        Fragment f = Modules.DoubleRow(spec);
        foreach (string p in spec.Problems) e.L.Add("problem: " + p);
        ValidateFragment(e, f, IronPlan(12), "smelters 2x6", "double-smelters-12.txt", null);
        return e.Result();
    }

    /// Coil block as drawn: copper ingot on the L1 lane (inner), magnet on the L2 lane (outer), coaters on all four input
    /// lanes with proliferator Mk.I. 480 coils/min on 6 machines (2/3 load: the sheet's magnet-on-L2 Mk.II sorter carries
    /// 90/min, a full-speed Mk.I assembler draws 120/min of magnet).
    static RowSpec CoilDoubleSpec()
    {
        return new RowSpec { Name = "coil", MachineItem = Ids.AsmMk1, RecipeId = Ids.RecipeMagneticCoil, Count = 6,
            InputItems = new[] { Ids.CopperIngot, Ids.Magnet }, InputRates = new[] { 240.0, 480.0 }, OutputItem = Ids.MagneticCoil, OutputRate = 480,
            TailItems = new[] { Ids.CopperIngot, Ids.Magnet }, CoatItems = new[] { Ids.CopperIngot, Ids.Magnet }, ProliferatorItem = Coaters.ProliferatorMk1, Data = gd };
    }

    static string MicroDoubleCoilBlock()
    {
        var e = new Errs();
        Layout sheet = LoadSheet(e);
        if (sheet == null) return e.Result();
        RowSpec spec = CoilDoubleSpec();
        Fragment f = Modules.DoubleRow(spec);
        foreach (string p in spec.Problems) e.L.Add("problem: " + p);
        var machines = f.Blds.Where(b => b.Kind == Kind.Machine).ToList();
        e.Check(machines.Count == 6, machines.Count + " machines");
        var coaters = f.Blds.Where(Coaters.IsCoater).ToList();
        e.Check(coaters.Count == 4, coaters.Count + " coaters, expected 4 (one per input lane)");
        var feeds = f.Blds.Where(Coaters.IsFeed).ToList();
        foreach (Bld c in coaters)
        {
            int[] ft = Coaters.FeedTile(c);
            e.Check(feeds.Any(x => x.TileX == ft[0] && x.TileY == ft[1] && Near(x.Z, Coaters.FeedZ, 1e-6)), "no z 1 feed node behind the coater at (" + Fmt(c.X) + "," + Fmt(c.Y) + ")");
        }
        e.Check(f.Ports.Count(p => Coaters.IsFeedPort(p)) == 1, "proliferator feed ports: " + f.Ports.Count(p => Coaters.IsFeedPort(p)) + ", expected 1");
        // geometry vs the sheet: column B (west) first machine -> (61,11)
        Bld b0 = machines.Where(m => m.X < 0).OrderBy(m => m.Y).FirstOrDefault();
        if (b0 == null) { e.L.Add("no west column"); return e.Result(); }
        Fragment g = Modules.DoubleRow(CoilDoubleSpec());   // untouched copy for the comparison (the first one is validated below)
        g.Translate((int)Math.Round(61 - b0.X), (int)Math.Round(11 - b0.Y));
        var xs = g.Blds.Where(b => b.Kind == Kind.Machine).Select(m => (int)Math.Round(m.X)).Distinct().OrderBy(x => x).ToList();
        e.Check(xs.SequenceEqual(new[] { 61, 65 }), "machine columns at x " + string.Join(",", xs.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", sheet 61/65");
        var ys = g.Blds.Where(b => b.Kind == Kind.Machine).Select(m => (int)Math.Round(m.Y)).Distinct().OrderBy(y => y).ToList();
        e.Check(ys.SequenceEqual(new[] { 11, 15, 19 }), "machine rows y " + string.Join(",", ys.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", sheet 11/15/19 pitch 4");
        var laneX = g.Blds.Where(b => b.Kind == Kind.Belt && !Coaters.IsFeed(b)).Select(b => b.TileX).Distinct().OrderBy(x => x).ToList();
        e.Check(laneX.SequenceEqual(new[] { 58, 59, 63, 67, 68 }), "lanes at x " + string.Join(",", laneX.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", sheet 58/59/63/67/68");
        foreach (var kv in new Dictionary<int, int> { { 58, Ids.Magnet }, { 59, Ids.CopperIngot }, { 67, Ids.CopperIngot }, { 68, Ids.Magnet }, { 63, Ids.MagneticCoil } })
        {
            Bld lane = g.Blds.FirstOrDefault(b => b.Kind == Kind.Belt && !Coaters.IsFeed(b) && b.TileX == kv.Key);
            e.Check(lane != null && lane.Carries == kv.Value, "lane x" + kv.Key + " carries " + (lane == null ? "nothing" : lane.Carries.ToString(CultureInfo.InvariantCulture)) + ", sheet " + kv.Value);
        }
        int reversed;
        int matched = CompareSorters(e, g, sheet, 0.04, "coil", out reversed);
        e.Check(matched == 18 && reversed == 0, matched + " sorters matched (" + reversed + " reversed), expected 18 / 0");
        var cy = g.Blds.Where(Coaters.IsCoater).Select(c => c.TileY).Distinct().ToList();
        var sheetCoaters = sheet.All.Where(b => Coaters.IsCoater(b) && b.TileX >= 58 && b.TileX <= 68).ToList();
        e.Check(sheetCoaters.Count == 4 && sheetCoaters.All(c => c.TileY == 5), "sheet coaters: " + sheetCoaters.Count + " at y " + string.Join(",", sheetCoaters.Select(c => c.TileY.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray()));
        Console.WriteLine("      sheet x58..68: columns x61/x65 pitch 4, L2 magnet x58/x68 (lat 0, slots 10/4), L1 copper x59/x67 (lat -1, slots 9/5), out x63; " + matched
            + " sorters within 0.04; engine coaters at y " + string.Join(",", cy.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray()) + " (sheet frame), sheet coaters at y5 on the bundled lanes x61/62/64/65");
        // full validation: the sheet itself runs magnet, copper, copper, magnet along the edge (tails x61 magnet, x62 copper,
        // x64 copper, x65 magnet); rule 10 only warns about it since 2026-11-08
        var input = new PlanInput { TargetItemId = Ids.MagneticCoil, TargetPerMin = 480 };
        var s = new PlanStep { RecipeId = Ids.RecipeMagneticCoil, BuildingItemId = Ids.AsmMk1, MachineCount = 4, Machines = 6, IncLevel = 1 };
        s.InputsPerMin[Ids.Magnet] = 480; s.InputsPerMin[Ids.CopperIngot] = 240; s.OutputsPerMin[Ids.MagneticCoil] = 480;
        input.Steps.Add(s);
        input.ExternalItems.Add(Ids.Magnet); input.ExternalItems.Add(Ids.CopperIngot);
        input.Options.ProliferatorItemId = Coaters.ProliferatorMk1;
        // rule 10 is a warning since 2026-11-08 (the user drew exactly this order), so the block must validate cleanly
        ValidateFragment(e, f, input, "coil block 2x3 + coaters", "double-coil-6-coaters.txt", null);
        Console.WriteLine("      notes: " + string.Join(" | ", spec.Notes.ToArray()));
        return e.Result();
    }

    static string MicroDoubleComposer()
    {
        var e = new Errs();
        bool keep = DoubleRows.Enabled;
        EngineResult plain, dbl;
        try
        {
            DoubleRows.Enabled = false;
            plain = FlatEngine.Generate(Source(), IronPlan(12), null, null);
            DoubleRows.Enabled = true;
            dbl = FlatEngine.Generate(Source(), IronPlan(12), null, null);
        }
        finally { DoubleRows.Enabled = keep; }
        foreach (string m in plain.Errors) e.L.Add("plain: " + m);
        foreach (string m in dbl.Errors) e.L.Add("double: " + m);
        if (!plain.Ok || !dbl.Ok) return e.Result();
        long aPlain = (long)plain.AreaWidth * plain.AreaHeight, aDbl = (long)dbl.AreaWidth * dbl.AreaHeight;
        e.Check(aDbl <= aPlain, "with double rows " + dbl.AreaWidth + "x" + dbl.AreaHeight + " is larger than without " + plain.AreaWidth + "x" + plain.AreaHeight);
        e.Check(!plain.Layout.All.Any(b => b.Role != null && b.Role.Contains("(shared)")), "double row built although DoubleRows.Enabled was false");
        string usedPlain = plain.Notes.FirstOrDefault(n => n.StartsWith("used ", StringComparison.Ordinal)) ?? "?";
        string usedDbl = dbl.Notes.FirstOrDefault(n => n.StartsWith("used ", StringComparison.Ordinal)) ?? "?";
        bool chosen = dbl.Layout.All.Any(b => b.Role != null && b.Role.Contains("(shared)"));
        var cols = dbl.Layout.All.Where(b => b.Kind == Kind.Machine).Select(b => (int)Math.Round(b.X)).Distinct().Count();
        e.Check(plain.Report != null && plain.Report.Ok && dbl.Report != null && dbl.Report.Ok, "validator not ok");
        Console.WriteLine("      without double rows: " + plain.AreaWidth + "x" + plain.AreaHeight + " (" + plain.Buildings.Count + " records) - " + usedPlain);
        Console.WriteLine("      with double rows:    " + dbl.AreaWidth + "x" + dbl.AreaHeight + " (" + dbl.Buildings.Count + " records), " + (chosen ? "double row chosen, " + cols + " machine columns" : "plain row kept") + " - " + usedDbl);
        if (chosen)
        {
            ExportResult xr = new GameEmitter(gd, GetCells()).Serialize(dbl.Buildings, dbl.Header, dbl.Layout);
            var check = new ValidationReport();
            GameEmitter.CheckExport(xr, dbl.Layout, check);
            foreach (string m in xr.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
            if (xr.Ok && check.Ok)
            {
                string dir = Path.Combine(root, @"work\selftest");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "double-composer-iron-720.txt"), xr.Text);
                Console.WriteLine(Composer.Map(dbl.Layout));
            }
        }
        return e.Result();
    }
}
