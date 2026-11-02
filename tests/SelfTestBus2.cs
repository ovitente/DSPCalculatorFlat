// SelfTestBus2.cs - self tests of the stacked edge bus (engine\EdgeBus.cs, the user's rules 2 and 3). Part of SelfTest
// (same runner, MAKEBLOCK_CASES filter: "micro bus2"). Oracle: the user's game-proven useredit3 top bus (row y=25).
using System;
using System.Collections.Generic;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static partial void RunBus2Cases()
    {
        Case("micro bus2: useredit3 top bus (z 0 / 0.5 / 1 on row y=25) is a stacked bus and passes EdgeBus.Check + the Validator's raised checks on that row", MicroBus2UserEdit3);
        Case("micro bus2: negative - z 1 -> 0 jump, a turn on a ramp node and two belts on one level fail EdgeBus.Check", MicroBus2Negative);
        Case("micro bus2: gate - 2-lane turret sub-block whose separate routes stay inside: no bus, valid", MicroBus2Gate);
        Case("micro bus2: forced 2-lane bus (engine + motor lanes, z 0 / 0.5) on the turret sub-block, valid", () => MicroBus2Forced(false));
        // a 3-lane fixture (+ steel row) is not routable even without the bus (steel lane enters the turret row from below the edge)
    }

    static List<Bld> BeltsOf(Layout l) { return l.All.Where(b => b.Kind == Kind.Belt).ToList(); }

    static string MicroBus2UserEdit3()
    {
        var e = new Errs();
        Layout l = UserFile(UserTurret3);
        var belts = BeltsOf(l);
        // the top bus: row y=25 and its x<=1 column; the coil run on x=26 (rule 15: z 0 -> 1 in one tile #459 -> #458, z 1 over
        // sorters #84 #465) is a stacked run too, but outside the proven bus geometry (not this test's subject)
        var bus = new HashSet<Bld>(EdgeBus.BusNodes(belts).Where(b => Near(b.Y, 25, 0.01) || b.X < 1.5));
        int z1 = bus.Count(b => b.Z > 0.75), z05 = bus.Count - z1;
        // records: p z 0.5 #478 (21,25) .. #67 (5,25) = 17 nodes; s #488 (28,25,0.5), z 1 #487 (27,25) .. #557 (5,25) = 23,
        // #558 (4,25,0.5) .. #436 (1,25,0.5) corner, x=1 column at z 0.5 down to (1,13)
        e.Check(z1 == 23, z1 + " bus nodes at z 1, useredit3 has 23 ((27..5,25,1))");
        e.Check(z05 >= 17 + 5, z05 + " bus nodes at z 0.5, expected at least 22");
        Func<double, double, double, bool> has = (x, y, z) => bus.Any(b => Near(b.X, x, 0.01) && Near(b.Y, y, 0.01) && Near(b.Z, z, 0.01));
        e.Check(has(21, 25, 0.5) && has(28, 25, 0.5) && has(27, 25, 1) && has(4, 25, 0.5), "ramp nodes #478 #488 #487 #558 not in the bus set");
        var warns = new List<string>();
        var fails = EdgeBus.Check(belts, bus, null, l.All.Where(b => b.Kind == Kind.Sorter).ToList(), warns.Add);
        foreach (string m in fails.Take(8)) e.L.Add(m);
        // the whole Validator: no raised-run error left on the user's file (other user-file findings are not this test's)
        ValidationReport rep = new Validator(gd, null).Validate(l, null);
        foreach (string m in rep.Errors.Where(m => (m.Contains("raised") || m.Contains("edge bus")) && (m.Contains(",25") || m.Contains("(1,") || m.Contains("(0,"))).Take(8)) e.L.Add("validator: " + m);
        Console.WriteLine("      useredit3 bus: " + z05 + " nodes at z 0.5, " + z1 + " at z 1; " + EdgeBus.Measure(l));
        return e.Result();
    }

    static string MicroBus2Negative()
    {
        var e = new Errs();
        Func<Layout, List<string>> check = l => { var bl = BeltsOf(l); return EdgeBus.Check(bl, EdgeBus.BusNodes(bl), null, null, null); };
        // 1. #558 (4,25,0.5) between z 1 (#557) and z 0.5 -> z 0: z 1 -> 0 step
        Layout a = UserFile(UserTurret3);
        Bld n558 = a.All.First(b => b.Kind == Kind.Belt && Near(b.X, 4, 0.01) && Near(b.Y, 25, 0.01) && Near(b.Z, 0.5, 0.01));
        Bld n5 = a.All.First(b => b.Kind == Kind.Belt && Near(b.X, 5, 0.01) && Near(b.Y, 25, 0.01) && Near(b.Z, 1, 0.01));
        n558.Z = n558.Z2 = 0;
        // the z-0 node is no longer raised; its z 1 predecessor now drops a whole level
        e.Check(check(a).Any(m => m.Contains("z step")), "z 1 -> 0 step accepted at " + n5);
        // 2. #487 (27,25,1) yaw turned to a corner flowing S: the next node is no longer in line -> turn on a ramp / long link
        Layout b2 = UserFile(UserTurret3);
        Bld r = b2.All.First(b => b.Kind == Kind.Belt && Near(b.X, 28, 0.01) && Near(b.Y, 25, 0.01) && Near(b.Z, 0.5, 0.01));
        Bld s = b2.All.First(b => b.Kind == Kind.Belt && Near(b.X, 27, 0.01) && Near(b.Y, 25, 0.01) && Near(b.Z, 1, 0.01));
        s.Y = s.Y2 = 24;   // the up-ramp now turns S on its z 1 node
        e.Check(check(b2).Any(m => m.Contains("turn on a ramp") || m.Contains("longer than one tile")), "turn on the up-ramp accepted at " + r);
        // 3. a z 0.5 node moved down to z 0... on the same level as the ground belt under it
        Layout c = UserFile(UserTurret3);
        Bld p = c.All.First(b => b.Kind == Kind.Belt && Near(b.X, 12, 0.01) && Near(b.Y, 25, 0.01) && Near(b.Z, 1, 0.01));
        p.Z = p.Z2 = 0.5;   // now on the level of copper #100 (12,25,0.5)
        e.Check(check(c).Any(m => m.Contains("two belts on one tile and level")), "two bus belts at (12,25,0.5) accepted");
        return e.Result();
    }

    /// Small composed fixtures: Missile Turret 7.5 with some parts bought in. raw2 = steel, circuit, iron, copper, coil, gear
    /// (rows turret, engine, motor: 2 long lanes into the turret); raw3 = the same without steel (+ steel row: 3 lanes).
    static Layout Bus2Fixture(bool steelRow, bool force, out List<string> notes, Errs e)
    {
        var spec = new BlockSpec { Item = TurretId, Rate = 7.5 };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        foreach (int it in new[] { SteelId, CircuitId, Ids.IronIngot, Ids.CopperIngot, Ids.MagneticCoil, GearId }) if (!(steelRow && it == SteelId)) spec.TreatAsRaw.Add(it);
        ProductionPlan plan = new Solver(gd, spec).Solve();
        var comp = new Composer(gd, spec);
        comp.TimeBudgetMs = 60000;
        bool was = EdgeBus.Enabled, wf = EdgeBus.Force;
        EdgeBus.Enabled = Environment.GetEnvironmentVariable("BUS2_OFF") == null;   // the bus is off by default in the engine
        EdgeBus.Force = force;
        Layout l;
        try { l = comp.Compose(plan); } finally { EdgeBus.Enabled = was; EdgeBus.Force = wf; }
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors.Take(8)) e.L.Add("validate: " + m);
        notes = comp.Notes;
        Rect bx = l.Bounds();
        Console.WriteLine("      " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height + ", " + EdgeBus.Measure(l));
        if (Environment.GetEnvironmentVariable("BUS2_MAP") != null) Console.Write(Composer.Map(l));
        return l;
    }

    /// Gate: the separate routes of the 2-lane fixture stay inside the layout, so no bus is built (it would add a row).
    static string MicroBus2Gate()
    {
        var e = new Errs();
        List<string> notes;
        Layout l = Bus2Fixture(false, false, out notes, e);
        e.Check(!notes.Any(n => n.Contains("edge bus (rules 2/3)")), "bus built although the separate routes are shorter");
        e.Check(EdgeBus.BusNodes(BeltsOf(l)).Count == 0, "stacked bus nodes without a bus");
        return e.Result();
    }

    /// Forced bus (EdgeBus.Force): 2 lanes z 0 / 0.5, or 3 lanes z 0 / 0.5 / 1; the Validator accepts the layout.
    static string MicroBus2Forced(bool steelRow)
    {
        var e = new Errs();
        List<string> notes;
        Layout l = Bus2Fixture(steelRow, true, out notes, e);
        e.Check(notes.Any(n => n.Contains("edge bus (rules 2/3)")), "no stacked edge bus in the chosen attempt: " + string.Join(" | ", notes.Where(n => n.Contains("edge bus")).Distinct().Take(3).ToArray()));
        var bus = EdgeBus.BusNodes(BeltsOf(l));
        e.Check(bus.Count > 0, "no stacked bus node");
        if (steelRow) e.Check(bus.Any(b => b.Z > 0.75), "3 lanes but no z 1 tier");
        return e.Result();
    }
}