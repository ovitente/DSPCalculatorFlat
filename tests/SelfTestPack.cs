// SelfTestPack.cs - self tests of the packing rules (engine\Packing.cs: rules 14, 12, 8). Part of SelfTest (same runner,
// MAKEBLOCK_CASES filter: "micro pack"). Fixture: Assembling Machine Mk.I (iron ingot feeds 3 steps: the assembler, gear,
// circuit) - small enough to compose in seconds, same shape as the turret's iron (4 consumers) and copper (3) lanes.
using System;
using System.Collections.Generic;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const int AsmMk1Item = 2303;

    static partial void RunPackCases()
    {
        Case("micro pack: rule 14 GroupCounts - groups build Step.Machines, the extra machine goes to the least-margin group", MicroPackCounts);
        Case("micro pack: rules 8/12 Pack - turret iron (4 consumers) -> 1 group, 2-way + 2 tails; copper (3) -> 2-way + 1 tail; no lane room (cap 80) -> 3-way", MicroPackGroups);
        Case("micro pack: Assembler Mk.I 3/min - iron feeds 3 steps from one ore lane (2-way splitter + tail), valid, rules 8/12 ok", MicroPackAsm);
        Case("micro pack: rule 14 on Assembler Mk.I 3.75/min (iron 1.00 exact, 3 consumers) - CLI 2 smelters; injected PlanInput keeps 1; user count 2 is built", MicroPackMarginOption);
        Case("micro pack: rule 12 split mode (forced mode 3) - Assembler Mk.I 15/min: the 1-machine iron rows ride the gear lane end, fewer iron ore lanes than mode 35 (no tails)", MicroPackSplit);
    }

    static string MicroPackCounts()
    {
        var e = new Errs();
        Func<int[], string> s = a => string.Join(",", a.Select(x => x.ToString()).ToArray());
        int[] a1 = Packing.GroupCounts(4, 3.0, new[] { 1.0 });
        e.Check(s(a1) == "4", "copper 3.0 exact, 4 machines, 1 group -> " + s(a1));
        int[] a2 = Packing.GroupCounts(2, 1.0, new[] { 1.0 });
        e.Check(s(a2) == "2", "coil 1.0 exact, 2 machines -> " + s(a2));
        int[] a3 = Packing.GroupCounts(7, 6.75, new[] { 360 / 405.0, 45 / 405.0 });
        e.Check(s(a3) == "6,1", "iron 6.75 in 2 groups (360 + 45) -> " + s(a3) + " (expected the old split 6,1)");
        int[] a4 = Packing.GroupCounts(8, 6.75, new[] { 360 / 405.0, 45 / 405.0 });
        e.Check(s(a4) == "6,2" || s(a4) == "7,1", "8 machines -> " + s(a4));
        e.Check(a4[0] == 7, "extra machine should go to the 360 group (6 for 6.0 exact = zero margin): " + s(a4));
        int[] a5 = Packing.GroupCounts(1, 1.6, new[] { 1.0 });
        e.Check(s(a5) == "2", "total below the exact minimum keeps ceil(exact) -> " + s(a5));
        return e.Result();
    }

    static string MicroPackGroups()
    {
        var e = new Errs();
        var iron = new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(SteelId, 180), new KeyValuePair<int, double>(1203, 90),
            new KeyValuePair<int, double>(CircuitId, 90), new KeyValuePair<int, double>(GearId, 45) };
        var g = Packing.Pack(iron, 720, 720, null, x => x);
        e.Check(g.Count == 1, "iron: " + g.Count + " groups (expected 1: the gear joins the main line, rule 12)");
        if (g.Count == 1) e.Check(g[0].Mains.Count == 2 && g[0].Tails.Count == 2, "iron: " + g[0].Mains.Count + " mains / " + g[0].Tails.Count + " tails (expected 2-way + 2 tails, rule 8)");
        e.Check(g.All(x => x.Mains.Count <= 2), "iron: a group with 3 mains (3-way splitter)");
        var cu = new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(1407, 90), new KeyValuePair<int, double>(1202, 45), new KeyValuePair<int, double>(CircuitId, 45) };
        var c = Packing.Pack(cu, 720, 720, null, x => x);
        e.Check(c.Count == 1 && c[0].Mains.Count == 2 && c[0].Tails.Count == 1, "copper: " + c.Count + " groups, " + (c.Count > 0 ? c[0].Mains.Count + " mains / " + c[0].Tails.Count + " tails" : ""));
        if (c.Count == 1 && c[0].Tails.Count == 1)
        {
            var feeds = Packing.MainFeeds(c[0], 1.0);
            e.Check(Math.Abs(feeds.Sum(x => x.Value) - 180) < 1e-6, "copper main feeds carry " + feeds.Sum(x => x.Value) + "/min, expected 180 (tail included)");
        }
        // no lane room for a tail (lane cap 80: coil 45 + circuit 45 > 80): the rule 8 exception, a 3-way splitter
        var tight = Packing.Pack(cu, 720, 80, null, x => x);
        e.Check(tight.Count == 1 && tight[0].Mains.Count == 3 && tight[0].Tails.Count == 0, "lane cap 80: " + tight.Count + " groups, expected one 3-way group");
        // --own keeps its own group
        var own = Packing.Pack(iron, 720, 720, new HashSet<int> { SteelId }, x => x);
        e.Check(own.Any(x => x.Count == 1 && x.Mains[0].Key == SteelId), "--own Steel shares its group");
        return e.Result();
    }

    static BlockSpec AsmSpec(double rate)
    {
        var spec = new BlockSpec { Item = AsmMk1Item, Rate = rate };
        spec.Tiers.Smelter = Ids.ArcSmelter; spec.Tiers.Assembler = Ids.AsmMk1; spec.Tiers.Belt = Ids.BeltMk2; spec.Tiers.Sorter = Ids.SorterMk2;
        spec.Buffer = false;
        return spec;
    }

    static List<RuleResult> PackCompose(BlockSpec spec, ProductionPlan plan, Errs e, out Layout l, out Composer comp)
    {
        comp = new Composer(gd, spec);
        l = comp.Compose(plan);
        ValidationReport rep = new Validator(gd, spec).Validate(l, plan);
        foreach (string m in rep.Errors) e.L.Add("validate: " + m);
        var rs = Rules.Check(l, plan, plan.Steps[plan.Steps.Count - 1].Recipe.Id);
        Rect bx = l.Bounds();
        Console.WriteLine("      " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height + "; " + comp.Notes.LastOrDefault(n => n.StartsWith("used ")));
        foreach (RuleResult r in rs) if (r.Rule == 1 || r.Rule == 8 || r.Rule == 12 || r.Rule == 14) Console.WriteLine("      " + r);
        return rs;
    }

    static string MicroPackAsm()
    {
        var e = new Errs();
        BlockSpec spec = AsmSpec(3);
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Step iron = plan.StepFor(Ids.IronIngot);
        e.Check(iron != null && iron.ConsumerSteps.Distinct().Count() == 3, "fixture: iron ingot should feed 3 steps");
        Layout l; Composer comp;
        var rs = PackCompose(spec, plan, e, out l, out comp);
        RuleResult r8 = rs.First(r => r.Rule == 8), r12 = rs.First(r => r.Rule == 12);
        e.Check(r8.Ok, r8.ToString());
        e.Check(r12.Ok, r12.ToString());
        int ironLanes = l.All.Count(b => b.Kind == Kind.Belt && b.Parameters != null && b.Parameters.Length == 2 && b.Parameters[0] == Ids.IronOre);
        e.Check(ironLanes == 1, ironLanes + " iron ore input lanes, expected 1");
        e.Check(l.Count(Kind.Machine, Ids.ArcSmelter) == iron.Machines + plan.Steps.Where(s => s.MachineItem == Ids.ArcSmelter && s != iron).Sum(s => s.Machines), "smelter count differs from the plan");
        e.Check(!comp.Notes.Any(n => n.Contains("no tail pickup")), "fell back to the pre-rules packing: " + comp.Notes.LastOrDefault(n => n.StartsWith("used ")));
        return e.Result();
    }

    static string MicroPackMarginOption()
    {
        var e = new Errs();
        bool oldM = Rules.MarginInCompose;
        Rules.MarginInCompose = true;   // global switch is off (turret routing); the option logic is tested with it on
        try { MarginOption(e); } finally { Rules.MarginInCompose = oldM; }
        return e.Result();
    }

    static void MarginOption(Errs e)
    {
        // CLI: the engine's own plan gets the rule 14 margin
        BlockSpec spec = AsmSpec(3.75);
        ProductionPlan plan = new Solver(gd, spec).Solve();
        Step iron = plan.StepFor(Ids.IronIngot);
        e.Check(iron != null && iron.Machines == 1 && Math.Abs(iron.MachinesExact - 1) < 1e-9, "fixture: iron " + (iron == null ? "-" : iron.MachinesExact.ToString()) + " exact, expected 1.00");
        Layout l; Composer comp;
        var rs = PackCompose(spec, plan, e, out l, out comp);
        e.Check(plan.StepFor(Ids.IronIngot).Machines == 2, "CLI: iron " + plan.StepFor(Ids.IronIngot).Machines + " smelters, expected 2 (rule 14)");
        e.Check(rs.First(r => r.Rule == 14).Ok, rs.First(r => r.Rule == 14).ToString());
        int smelters = l.Count(Kind.Machine, Ids.ArcSmelter);
        // injected plan (DSPCalculator): counts are authoritative, no margin by default
        ProductionPlan p2 = new Solver(gd, AsmSpec(3.75)).Solve();
        PlanInput input = PlanMapper.FromPlan(p2, AsmSpec(3.75));
        input.Options.AddMargin = false;
        EngineResult res = FlatEngine.Generate(Source(), input, null, () => false);
        foreach (string m in res.Errors) e.L.Add("engine (no margin): " + m);
        if (res.Ok) e.Check(res.Plan.StepFor(Ids.IronIngot).Machines == 1 && res.Layout.Count(Kind.Machine, Ids.ArcSmelter) == smelters - 1,
            "injected: iron " + res.Plan.StepFor(Ids.IronIngot).Machines + ", smelters " + res.Layout.Count(Kind.Machine, Ids.ArcSmelter) + " (CLI " + smelters + ")");
        // the user asks for 2 iron smelters: built although 1.00 is exact
        PlanInput i3 = PlanMapper.FromPlan(new Solver(gd, AsmSpec(3.75)).Solve(), AsmSpec(3.75));
        i3.Options.AddMargin = false;
        foreach (PlanStep ps in i3.Steps) if (ps.RecipeId == 1) ps.Machines = 2;
        EngineResult r3 = FlatEngine.Generate(Source(), i3, null, () => false);
        foreach (string m in r3.Errors) e.L.Add("engine (user count): " + m);
        if (r3.Ok) e.Check(r3.Layout.Count(Kind.Machine, Ids.ArcSmelter) == smelters, "user count 2: " + r3.Layout.Count(Kind.Machine, Ids.ArcSmelter) + " smelters, expected " + smelters);
        e.Check(new LayoutOptions().AddMargin == false, "LayoutOptions.AddMargin must default to false (addon)");
    }

    static string MicroPackSplit()
    {
        var e = new Errs();
        int old = EngineTuning.ForcedMode;
        try
        {
            var lanes = new int[2];
            int k = 0;
            foreach (int mode in new[] { 3, 3 | 32 })
            {
                EngineTuning.ForcedMode = mode;
                BlockSpec spec = AsmSpec(15);
                ProductionPlan plan = new Solver(gd, spec).Solve();
                Layout l; Composer comp;
                var rs = PackCompose(spec, plan, e, out l, out comp);
                lanes[k++] = l.All.Count(b => b.Kind == Kind.Belt && b.Parameters != null && b.Parameters.Length == 2 && b.Parameters[0] == Ids.IronOre);
                if (mode == 3) { RuleResult r12 = rs.First(r => r.Rule == 12); e.Check(r12.Ok, "mode 3: " + r12); }
            }
            e.Check(lanes[0] < lanes[1], "iron ore lanes: tails " + lanes[0] + ", no tails " + lanes[1]);
        }
        finally { EngineTuning.ForcedMode = old; }
        return e.Result();
    }
}
