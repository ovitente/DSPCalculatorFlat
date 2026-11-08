// SelfTestCoaters.cs - self tests of the spray coater cell (engine\Coaters.cs): proliferated final step -> one coater per
// input lane and one external proliferator lane with an icon tail on the input edge; no coaters without proliferator;
// the cell has the shape the user drew on refs\patterns\p-sheet-001.txt x58..68 (CheckDrawnCoaterShape, also used by
// SelfTestDouble.cs MicroDoubleCoilBlock).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    /// Magnetic Coil 180/min on 2 assemblers Mk.I (recipe 6: 2 magnet + 1 copper ingot -> 2 coils, 1 s), both inputs
    /// arrive on lanes; incLevel / proliferator as given.
    static PlanInput CoaterPlan(int incLevel, int prolif)
    {
        var p = new PlanInput { TargetItemId = Ids.MagneticCoil, TargetPerMin = 180 };
        var s = new PlanStep { RecipeId = Ids.RecipeMagneticCoil, BuildingItemId = Ids.AsmMk1, MachineCount = 2, Machines = 2, IncLevel = incLevel };
        s.InputsPerMin[Ids.Magnet] = 180; s.InputsPerMin[Ids.CopperIngot] = 90; s.OutputsPerMin[Ids.MagneticCoil] = 180;
        p.Steps.Add(s);
        p.ExternalItems.Add(Ids.Magnet); p.ExternalItems.Add(Ids.CopperIngot);
        p.Options.ProliferatorItemId = prolif;
        return p;
    }

    static string MicroCoaters()
    {
        var e = new Errs();
        e.Check(Coaters.Sprays(gd, Coaters.ProliferatorMk1) == 12 && Coaters.Sprays(gd, Coaters.ProliferatorMk2) == 24 && Coaters.Sprays(gd, Coaters.ProliferatorMk3) == 60,
            "sprays per item (raw-protos HpMax) " + Coaters.Sprays(gd, 1141) + "/" + Coaters.Sprays(gd, 1142) + "/" + Coaters.Sprays(gd, 1143) + ", host loaded " + gd.SprayCounts.Count);
        EngineResult res = FlatEngine.Generate(Source(), CoaterPlan(1, Coaters.ProliferatorMk1), null, null);
        foreach (string m in res.Errors) e.L.Add("engine: " + m);
        if (res.Layout == null) return e.Result();
        Layout l = res.Layout;
        var coaters = l.All.Where(Coaters.IsCoater).ToList();
        e.Check(coaters.Count == 2, coaters.Count + " coaters, expected 2");
        // one coater on each input lane, on a belt carrying that item
        var lanesCoated = new HashSet<int>();
        foreach (Bld c in coaters)
        {
            Bld under = l.BeltAt(c.TileX, c.TileY);
            e.Check(under != null && Coaters.PassesUnder(c, under), "coater at " + Fmt(c.X) + "," + Fmt(c.Y) + " is not on a lane belt along its axis");
            if (under != null) lanesCoated.Add(under.Carries);
        }
        e.Check(lanesCoated.SetEquals(new[] { Ids.Magnet, Ids.CopperIngot }), "coated lanes carry " + string.Join(",", lanesCoated.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
        // exactly one proliferator lane, icon tail on the input edge, rate = (180 + 90) / 12
        var pPorts = l.Ports.Where(p => p.Kind == PortKind.In && p.Item == Coaters.ProliferatorMk1).ToList();
        e.Check(pPorts.Count == 1, pPorts.Count + " proliferator input ports, expected 1");
        Rect box = l.Bounds();
        foreach (Port p in pPorts)
        {
            e.Check(p.Belt != null && p.Belt.Parameters != null && p.Belt.Parameters.Length == 2 && p.Belt.Parameters[0] == Coaters.ProliferatorMk1, "proliferator lane first belt has no icon [1141,0]");
            e.Check(p.Y == box.Y0 && p.Dir == Dir.N, "proliferator port at (" + p.X + "," + p.Y + ") " + p.Dir + " is not on the S input edge (y " + box.Y0 + ")");
            e.Check(Math.Abs(p.Rate - 22.5) < 1e-6, "proliferator rate " + Fmt(p.Rate) + "/min, expected 22.5");
            e.Check(Coaters.IsFeedPort(p), "port is not recognised as a feed port");
        }
        // every external lane (and the proliferator lane) starts with its icon belt
        foreach (Port p in l.Ports.Where(x => x.Kind == PortKind.In))
            e.Check(p.Belt != null && p.Belt.Parameters != null && p.Belt.Parameters.Length == 2 && p.Belt.Parameters[0] == p.Item, "input " + p.Item + " has no icon tail");
        // feed nodes: z 1 one tile behind each coater; ramp z 0.5
        var feeds = l.All.Where(Coaters.IsFeed).ToList();
        foreach (Bld c in coaters)
        {
            int[] ft = Coaters.FeedTile(c);
            e.Check(feeds.Any(f => f.TileX == ft[0] && f.TileY == ft[1] && Math.Abs(f.Z - 1.0) < 1e-6), "no z 1 feed node behind the coater at " + Fmt(c.X) + "," + Fmt(c.Y));
        }
        e.Check(feeds.Count(f => Math.Abs(f.Z - 0.5) < 1e-6) == 1, feeds.Count(f => Math.Abs(f.Z - 0.5) < 1e-6) + " ramp nodes at z 0.5, expected 1");
        e.Check(!res.Warnings.Any(w => w.Contains("not placed yet")), "old 'not placed yet' warning still there");
        e.Check(res.Report != null && res.Report.Info.Any(m => m.StartsWith("spray coaters: 2", StringComparison.Ordinal)), "validator did not see the coaters");
        if (!res.Ok) return e.Result();
        // records + export through the game (coater prototype from the user's 7-logi\L1.txt)
        e.Check(res.Buildings.Count(b => b.itemId == Coaters.SprayCoater && b.modelIndex == 120) == 2, "coater records (item 2313, model 120)");
        ExportResult xr = new GameEmitter(gd, GetCells()).Serialize(res.Buildings, res.Header, res.Layout);
        var check = new ValidationReport();
        GameEmitter.CheckExport(xr, res.Layout, check);
        foreach (string m in xr.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
        Console.WriteLine("      coaters: " + res.Buildings.Count + " records, " + res.AreaWidth + "x" + res.AreaHeight + ", " + coaters.Count + " coaters, proliferator "
            + (pPorts.Count > 0 ? Fmt(pPorts[0].Rate) : "-") + "/min, export " + (xr.Ok ? "ok" : "FAILED"));
        if (e.L.Count == 0 && xr.Ok)
        {
            string dir = System.IO.Path.Combine(root, @"work\selftest");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "coil-180-coaters.txt"), xr.Text);
            Console.WriteLine(Composer.Map(res.Layout));
        }
        return e.Result();
    }

    /// The drawn coater shape (refs\patterns\p-sheet-001.txt x58..68, engine\Coaters.cs header) on a Flow-N fragment or
    /// layout: all coaters in one row at (lowest coated tail y) + Coaters.CoaterRowOffset; exactly one feed chain whose
    /// tail sits level with the coated tails and carries the proliferator icon, in the column right next to the bundle
    /// of coated lanes (-x side, or +x side when a lane takes the -x tile); profile z 0 / 0 / 0.5 / 1 / 1 straight up
    /// that column, the corner at the coater row - 1, the feed on z 1 across every coated lane, dead end over the far
    /// coated lane. Returns the ramp side as text for the case's console line ("-x" / "+x" / "?").
    static string CheckDrawnCoaterShape(Errs e, IEnumerable<Bld> all, int prolif, string tag)
    {
        var blds = all.ToList();
        var coaters = blds.Where(Coaters.IsCoater).ToList();
        if (coaters.Count == 0) { e.L.Add(tag + ": no coaters"); return "?"; }
        var rows = coaters.Select(k => k.TileY).Distinct().ToList();
        e.Check(rows.Count == 1, tag + ": coaters on " + rows.Count + " rows (y " + string.Join(",", rows.Select(y => y.ToString(CultureInfo.InvariantCulture)).ToArray()) + "), drawn: one row");
        int c = rows.Min();
        var pred = new Dictionary<Bld, Bld>();
        foreach (Bld b in blds) if (b.Kind == Kind.Belt && b.Output != null && b.OutputToSlot <= 1 && !pred.ContainsKey(b.Output)) pred[b.Output] = b;
        // coated lanes: ground belt under each coater, walked back to the chain start (the tail)
        var coatedX = new List<int>(); int tailY = int.MaxValue;
        foreach (Bld k in coaters)
        {
            Bld under = blds.FirstOrDefault(b => b.Kind == Kind.Belt && !b.IsRaised && b.TileX == k.TileX && b.TileY == k.TileY);
            if (under == null) { e.L.Add(tag + ": no lane belt under the coater at (" + k.TileX + "," + k.TileY + ")"); continue; }
            coatedX.Add(k.TileX);
            Bld t = under; var seen = new HashSet<Bld>();
            while (seen.Add(t)) { Bld p; if (!pred.TryGetValue(t, out p) || Coaters.IsFeed(p)) break; t = p; }
            tailY = Math.Min(tailY, t.TileY);
        }
        if (coatedX.Count == 0) return "?";
        e.Check(c == tailY + Coaters.CoaterRowOffset, tag + ": coater row y " + c + ", lowest coated tail y " + tailY + ", drawn offset " + Coaters.CoaterRowOffset + " (sheet tails y0, coaters y5)");
        // one feed chain
        var feeds = blds.Where(Coaters.IsFeed).ToList();
        var starts = feeds.Where(b => { Bld p; return !pred.TryGetValue(b, out p) || !Coaters.IsFeed(p); }).ToList();
        e.Check(starts.Count == 1, tag + ": " + starts.Count + " feed chain(s), drawn: 1");
        if (starts.Count != 1) return "?";
        var chain = new List<Bld>();
        for (Bld n = starts[0]; n != null && chain.Count < 200; n = n.Output) chain.Add(n);
        int xr = chain[0].TileX;
        int lo = coatedX.Min(), hi = coatedX.Max();
        string side = xr == lo - 1 ? "-x" : xr == hi + 1 ? "+x" : "?";
        e.Check(side != "?", tag + ": ramp column x " + xr + " is not next to the coated lanes " + lo + ".." + hi + " (sheet: x60 next to the bundle x61..65)");
        int far = side == "-x" ? hi : lo;
        e.Check(chain[0].TileY == tailY && chain[0].Parameters != null && chain[0].Parameters.Length == 2 && chain[0].Parameters[0] == prolif,
            tag + ": feed tail at (" + chain[0].TileX + "," + chain[0].TileY + ") is not level with the coated tails (y " + tailY + ") with the icon [" + prolif + ",0]");
        // profile up the ramp column (sheet #96..#92), then the feed row (sheet #91..#87)
        double[][] want = { new double[] { xr, c - 5, 0 }, new double[] { xr, c - 4, 0 }, new double[] { xr, c - 3, 0.5 }, new double[] { xr, c - 2, 1 }, new double[] { xr, c - 1, 1 } };
        var expect = new List<double[]>(want);
        int step = side == "-x" ? 1 : -1;
        for (int x = xr + step; side == "-x" ? x <= far : x >= far; x += step) expect.Add(new double[] { x, c - 1, 1 });
        e.Check(chain.Count == expect.Count, tag + ": feed chain has " + chain.Count + " belts, drawn shape needs " + expect.Count + " (5 up the ramp column + " + (expect.Count - 5) + " across)");
        for (int i = 0; i < Math.Min(chain.Count, expect.Count); i++)
            e.Check(chain[i].TileX == (int)expect[i][0] && chain[i].TileY == (int)expect[i][1] && Near(chain[i].Z, expect[i][2], 1e-6),
                tag + ": feed belt " + i + " at (" + chain[i].TileX + "," + chain[i].TileY + ") z " + Fmt(chain[i].Z) + ", drawn (" + expect[i][0] + "," + expect[i][1] + ") z " + expect[i][2]);
        e.Check(chain[chain.Count - 1].Output == null && chain[chain.Count - 1].TileX == far, tag + ": feed does not end dead over the far coated lane x " + far);
        foreach (Bld k in coaters)
        {
            int[] ft = Coaters.FeedTile(k);
            e.Check(chain.Any(b => b.TileX == ft[0] && b.TileY == ft[1] && Near(b.Z, Coaters.FeedZ, 1e-6)), tag + ": no z 1 feed node behind the coater at (" + k.TileX + "," + k.TileY + ")");
        }
        return side;
    }

    /// Magnetic Coil 270/min on 3 assemblers Mk.I (the sheet's coil column count), magnet + copper ingot on tail lanes,
    /// both coated, proliferator Mk.II (1142, as drawn).
    static RowSpec CoilRowSpec()
    {
        return new RowSpec { Name = "coil", MachineItem = Ids.AsmMk1, RecipeId = Ids.RecipeMagneticCoil, Count = 3,
            InputItems = new[] { Ids.Magnet, Ids.CopperIngot }, InputRates = new[] { 270.0, 135.0 }, OutputItem = Ids.MagneticCoil, OutputRate = 270,
            TailItems = new[] { Ids.Magnet, Ids.CopperIngot }, CoatItems = new[] { Ids.Magnet, Ids.CopperIngot }, ProliferatorItem = Coaters.ProliferatorMk2, Data = gd };
    }

    static string MicroCoatersDrawnRow()
    {
        var e = new Errs();
        RowSpec spec = CoilRowSpec();
        Fragment f = Modules.MachineRow(spec);
        foreach (string p in spec.Problems) e.L.Add("problem: " + p);
        var coaters = f.Blds.Where(Coaters.IsCoater).ToList();
        e.Check(coaters.Count == 2, coaters.Count + " coaters, expected 2 (one per input lane)");
        string side = CheckDrawnCoaterShape(e, f.Blds, Coaters.ProliferatorMk2, "coil row");
        // the 2-input row: in1 L1 on side A (x +2), in2 L1 on side B (x -2), output L2 on side B (x -3) takes the -x tile
        // next to the bundle, so the ramp column goes to the +x side (x +3)
        var laneX = f.Blds.Where(b => b.Kind == Kind.Belt && !Coaters.IsFeed(b)).Select(b => b.TileX).Distinct().OrderBy(x => x).ToList();
        int xr = f.Blds.Where(Coaters.IsFeed).Select(b => b.TileX).DefaultIfEmpty(int.MinValue).Min();
        Console.WriteLine("      coil row: lanes x " + string.Join(",", laneX.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ", coaters at y "
            + string.Join(",", coaters.Select(k => k.TileY.ToString(CultureInfo.InvariantCulture)).Distinct().ToArray()) + ", ramp column on the " + side + " side; notes: " + string.Join(" | ", spec.Notes.ToArray()));
        Port fp = f.Ports.FirstOrDefault(Coaters.IsFeedPort);
        e.Check(fp != null && fp.Y == f.Ports.Where(p => p.Kind == PortKind.In && !Coaters.IsFeedPort(p)).Min(p => p.Y), "feed port " + (fp == null ? "missing" : fp.ToString()) + " is not level with the input tails");
        e.Check(fp != null && Math.Abs(fp.Rate - (270 + 135) / 24.0) < 1e-6, "proliferator rate " + (fp == null ? "-" : Fmt(fp.Rate)) + "/min, expected " + Fmt((270 + 135) / 24.0) + " (24 sprays per Mk.II item)");
        // belt-level and coater checks on the bare fragment
        var l0 = new Layout(gd); l0.Add(f); l0.Ports.AddRange(f.Ports);
        ValidationReport belts = new Validator(gd, null).ValidateBelts(l0);
        foreach (string m in belts.Errors) e.L.Add("belt checks: " + m);
        var cr = new ValidationReport();
        Coaters.Validate(l0, null, gd, cr);
        foreach (string m in cr.Errors) e.L.Add("coater checks: " + m);
        // full Validate + export with a 1-step plan (same steps as SelfTestDouble.cs ValidateFragment, which MakeBlock.exe does
        // not compile): fresh fragment in a layout, tails to the edge, towers, PlanMapper plan, Validator, GameEmitter
        var input = new PlanInput { TargetItemId = Ids.MagneticCoil, TargetPerMin = 270 };
        var s = new PlanStep { RecipeId = Ids.RecipeMagneticCoil, BuildingItemId = Ids.AsmMk1, MachineCount = 3, Machines = 3, IncLevel = 2 };
        s.InputsPerMin[Ids.Magnet] = 270; s.InputsPerMin[Ids.CopperIngot] = 135; s.OutputsPerMin[Ids.MagneticCoil] = 270;
        input.Steps.Add(s);
        input.ExternalItems.Add(Ids.Magnet); input.ExternalItems.Add(Ids.CopperIngot);
        input.Options.ProliferatorItemId = Coaters.ProliferatorMk2;
        Fragment f2 = Modules.MachineRow(CoilRowSpec());
        var l = new Layout(gd);
        l.Add(f2);
        l.Ports.AddRange(f2.Ports);
        var errs = new List<string>();
        Lanes.TailsToEdge(l, l.Ports, (x, y) => l.Grid.IsFree(x, y), errs);
        foreach (string m in errs) e.L.Add("tails: " + m);
        foreach (Bld t in Modules.TeslaGrid(l, gd)) l.Add(t);
        foreach (string m in l.Errors) e.L.Add("layout: " + m);
        PlanMapper.Result pm = PlanMapper.Map(gd, input);
        foreach (string m in pm.Errors) e.L.Add("plan: " + m);
        if (pm.Plan == null) return e.Result();
        ValidationReport rep = new Validator(gd, pm.Spec).Validate(l, pm.Plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        string exportState = "skipped";
        if (rep.Ok)
        {
            ExportResult x = new GameEmitter(gd, GetCells()).Emit(l, Emitter.HeaderFor(l, pm.Plan, pm.Spec, gd));
            var check = new ValidationReport();
            GameEmitter.CheckExport(x, l, check);
            foreach (string m in x.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
            exportState = x.Ok && check.Ok ? "ok" : "FAILED";
            if (x.Ok && check.Ok)
            {
                string dir = System.IO.Path.Combine(root, @"work\selftest");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "coil-row-3-coaters-drawn.txt"), x.Text);
            }
        }
        Rect bx = l.Bounds();
        Console.WriteLine("      coil row 3 + coaters: " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height + ", validator " + (rep.Ok ? "ok" : rep.Errors.Count + " error(s)") + ", export " + exportState);
        if (rep.Ok) Console.WriteLine(Composer.Map(l));
        return e.Result();
    }

    static string MicroCoatersNone()
    {
        var e = new Errs();
        EngineResult res = FlatEngine.Generate(Source(), CoaterPlan(0, Coaters.ProliferatorMk1), null, null);
        foreach (string m in res.Errors) e.L.Add("engine: " + m);
        if (!res.Ok) return e.Result();
        e.Check(!res.Layout.All.Any(Coaters.IsCoater), "coaters placed for incLevel 0");
        e.Check(!res.Layout.All.Any(Coaters.IsFeed), "feed belts placed for incLevel 0");
        e.Check(!res.Layout.Ports.Any(p => Coaters.IsProliferator(p.Item)), "proliferator lane placed for incLevel 0");
        // negative: the incLevel-1 layout without the feed node of one coater fails the validator
        EngineResult r1 = FlatEngine.Generate(Source(), CoaterPlan(1, Coaters.ProliferatorMk1), null, null);
        if (r1.Layout == null || !r1.Ok) { e.L.Add("incLevel 1 layout failed: " + string.Join("; ", r1.Errors.ToArray())); return e.Result(); }
        Bld end = r1.Layout.All.FirstOrDefault(b => Coaters.IsFeed(b) && b.Output == null && b.IsRaised);
        Bld pred = end == null ? null : r1.Layout.All.FirstOrDefault(b => b.Output == end);
        if (end == null || pred == null) { e.L.Add("no feed chain end found"); return e.Result(); }
        var cut = new Layout(gd);
        foreach (Bld b in r1.Layout.All) if (b != end) cut.Add(b);
        cut.Ports.AddRange(r1.Layout.Ports);
        Bld keepOut = pred.Output; int keepSlot = pred.OutputToSlot;
        pred.Output = null; pred.OutputToSlot = 0;
        var spec = new BlockSpec { Item = Ids.MagneticCoil, Rate = 180, ProliferatorItem = Coaters.ProliferatorMk1 };
        ValidationReport rep = new Validator(gd, spec).Validate(cut, r1.Plan);
        pred.Output = keepOut; pred.OutputToSlot = keepSlot;
        e.Check(!rep.Ok && rep.Errors.Any(m => m.StartsWith("coater:", StringComparison.Ordinal) && m.Contains("feed node")), "validator accepted a coater without its feed node: " + string.Join("; ", rep.Errors.Take(3).ToArray()));
        // and a coater moved off its lane (no belt under it) fails too
        Bld c0 = r1.Layout.All.First(Coaters.IsCoater);
        c0.Translate(0, -20);
        ValidationReport rep2 = new Validator(gd, spec).Validate(r1.Layout, r1.Plan);
        c0.Translate(0, 20);
        e.Check(!rep2.Ok && rep2.Errors.Any(m => m.StartsWith("coater:", StringComparison.Ordinal)), "validator accepted a coater off its lane");
        return e.Result();
    }
}
