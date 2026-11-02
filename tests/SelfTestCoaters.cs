// SelfTestCoaters.cs - self tests of the spray coater cell (engine\Coaters.cs): proliferated final step -> one coater per
// input lane and one external proliferator lane with an icon tail on the input edge; no coaters without proliferator.
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
