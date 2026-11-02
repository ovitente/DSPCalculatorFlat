// SelfTestRules.cs - self tests of the user's 15 layout rules (engine\Rules.cs). Part of SelfTest (same runner,
// MAKEBLOCK_CASES filter: "micro rules"). Oracle: the user's game-proven edits of the Missile Turret 7.5 block.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const string UserTurret1 = @"refs\engine-missile-turret-7.5-v1-useredit.txt";
    const string UserTurret3 = @"refs\engine-missile-turret-7.5-v1-useredit3.txt";
    const string EngineTurret1 = @"work\out\engine-missile-turret-7.5-v1.txt";
    const int TurretRecipe = 129;

    static partial void RunRuleCases()
    {
        Case("micro rules: rule 14 margin on the turret 7.5 plan (coil 1 -> 2, copper 3 -> 4, iron 7, single-consumer steps unchanged)", MicroRulesMargin);
        Case("micro rules: useredit3 (game-proven) passes the hard rule checks 4, 6, 7, 12, 13, 14 and has the margin-rule machine counts", MicroRulesUserEdit3);
        Case("micro rules: engine v1 turret (before the user's edit) fails rules 6, 7, 12 (and has 3-way splitters, rule 8 metric)", MicroRulesEngineV1);
        Case("micro rules: rule 13 prunes useredit's 2 redundant towers (7 -> 5), coverage + links kept", MicroRulesTowers);
    }

    static ProductionPlan TurretPlan(bool margin)
    {
        var spec = new BlockSpec { Item = TurretId, Rate = 7.5 };
        ProductionPlan plan = new Solver(gd, spec).Solve();
        if (margin) Rules.ApplyMargin(plan);
        return plan;
    }

    static int Machines(ProductionPlan p, int item) { Step s = p.StepFor(item); return s == null ? -1 : s.Machines; }

    static string MicroRulesMargin()
    {
        var e = new Errs();
        ProductionPlan raw = TurretPlan(false), p = TurretPlan(true);
        e.Check(!Rules.CheckMargin(raw).Ok, "rule 14 check accepts the raw plan (coil 1.0 on a split lane)");
        e.Check(Rules.CheckMargin(p).Ok, "rule 14 check rejects the margin plan: " + Rules.CheckMargin(p).Detail);
        e.Check(Machines(p, Ids.MagneticCoil) == 2, "coil " + Machines(p, Ids.MagneticCoil) + " (useredit3: 2)");
        e.Check(Machines(p, Ids.CopperIngot) == 4, "copper ingot " + Machines(p, Ids.CopperIngot) + " (useredit3: 4)");
        e.Check(Machines(p, Ids.IronIngot) == 7, "iron ingot " + Machines(p, Ids.IronIngot) + " (useredit3: 7)");
        foreach (Step s in raw.Steps)
            if (s.ConsumerSteps.Distinct().Count() < 2) e.Check(Machines(p, s.Item) == s.Machines, s.Recipe.Name + " changed although it has one consumer");
        return e.Result();
    }

    static Layout UserFile(string rel)
    {
        string path = Cells.Resolve(bpDir, rel);
        if (!File.Exists(path)) path = Path.Combine(root, rel);
        if (!File.Exists(path)) throw new SkipException("missing " + path);
        return FullLayout(GameBp.Load(path), false);
    }

    static string MicroRulesUserEdit3()
    {
        var e = new Errs();
        Layout l = UserFile(UserTurret3);
        ProductionPlan p = TurretPlan(true);
        var rs = Rules.Check(l, p, TurretRecipe);
        foreach (int rule in new[] { 4, 6, 7, 12, 13, 14 })
        {
            RuleResult r = rs.FirstOrDefault(x => x.Rule == rule);
            e.Check(r != null && r.Hard && r.Ok, "rule " + rule + ": " + (r == null ? "not measured" : r.Detail));
        }
        // the margin plan's machine counts are what the user built (except the 4th engine, a buffer: rule 14 note)
        foreach (Step s in p.Steps)
        {
            int n = l.All.Count(b => b.Kind == Kind.Machine && b.RecipeId == s.Recipe.Id);
            if (s.Recipe.Id == 105) e.Check(n == s.Machines + 1, "engines " + n + " (user: plan + 1 buffer)");
            else e.Check(n == s.Machines, s.Recipe.Name + ": file " + n + ", plan " + s.Machines);
        }
        foreach (var r in rs) Console.WriteLine("      " + r);
        return e.Result();
    }

    static string MicroRulesEngineV1()
    {
        var e = new Errs();
        Layout l = UserFile(EngineTurret1);
        var rs = Rules.Check(l, TurretPlan(false), TurretRecipe);
        foreach (int rule in new[] { 6, 7, 12 })
        {
            RuleResult r = rs.FirstOrDefault(x => x.Rule == rule);
            e.Check(r != null && r.Hard && !r.Ok, "rule " + rule + " not violated: " + (r == null ? "not measured" : r.Detail));
        }
        RuleResult r8 = rs.FirstOrDefault(x => x.Rule == 8);
        e.Check(r8 != null && !r8.Ok && r8.Detail.Contains("2 with 3 outputs"), "rule 8 metric: " + (r8 == null ? "-" : r8.Detail) + " (analysis: iron and copper 3-way)");
        foreach (var r in rs) Console.WriteLine("      " + r);
        return e.Result();
    }

    static string MicroRulesTowers()
    {
        var e = new Errs();
        Layout l = UserFile(UserTurret1);
        int before = l.Count(Kind.Tesla, 0);
        e.Check(Rules.RedundantTowers(l).Count > 0, "no redundant tower found in useredit (analysis: (18,13), (27,6))");
        int cut = Rules.PruneTowers(l);
        e.Check(before == 7 && cut == 2 && l.Count(Kind.Tesla, 0) == 5, "towers " + before + " -> " + l.Count(Kind.Tesla, 0) + " (analysis: 7 -> 5)");
        e.Check(Rules.RedundantTowers(l).Count == 0, "still redundant after pruning");
        ValidationReport v = new Validator(gd, null).ValidatePower(l);
        foreach (string m in v.Errors) e.L.Add("power after pruning: " + m);
        return e.Result();
    }
}
