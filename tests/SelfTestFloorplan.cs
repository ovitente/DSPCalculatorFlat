// SelfTestFloorplan.cs - self tests of the pattern-driven floorplan (engine\Floorplan.cs -> FpPlan). Part of SelfTest
// (MAKEBLOCK_CASES filter "micro floorplan"). Oracle: the user's turret 7.5 edit (docs\floorplan-design.md): final cell
// west, steel next to it, magnet under coil, 3 comb tails, a flat band of <= 4 lanes. No belts are laid, no Validator run;
// the only geometric check is that the placed fragments do not overlap (tile occupancy).
using System;
using System.Collections.Generic;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static partial void RunFloorplanCases()
    {
        Case("micro floorplan: turret 7.5 plan -> <= 9 columns, final cell west, steel adjacent + lane aligned, magnet under coil, 3 comb tails, band <= 5 flat rows (<= 4 with the rule-2 bus), fragments disjoint", MicroFloorplanTurret);
        Case("micro floorplan: motor 120 (magnet, copper ingot raw) and coil 360 plans -> 2-3 columns, Ok, stacked producer under its only consumer", MicroFloorplanSmall);
    }

    /// Common checks: Ok, every non-final row has a column and a fragment, fragments do not overlap, ports and slots set.
    static void FloorplanCommon(Errs e, FpPlan fp, string tag)
    {
        foreach (string m in fp.Errors) e.L.Add(tag + " error: " + m);
        foreach (FpRow r in fp.Rows.Where(r => !r.FinalCell))
            e.Check(r.Column != null && r.Frag != null, tag + " row " + r.Name + " has no column / fragment");
        foreach (FpColumn c in fp.Columns)
        {
            e.Check(c.Rows.Count > 0, tag + " column " + c.Index + " is empty");
            for (int i = 1; i < c.Rows.Count; i++) e.Check(c.Rows[i].Y0 > c.Rows[i - 1].Y1, tag + " column " + c.Index + ": " + c.Rows[i].Name + " not above " + c.Rows[i - 1].Name);
        }
        // fragments disjoint: tile occupancy of all distinct fragments (an absorbed row shares the final cell's fragment)
        var l = new Layout(gd);
        var seen = new HashSet<Fragment>();
        foreach (FpRow r in fp.Rows) if (r.Frag != null && seen.Add(r.Frag)) l.Add(r.Frag);
        foreach (string m in l.Errors.Take(4)) e.L.Add(tag + " " + m);
        // no two rows' bounds overlap unless they share the fragment
        var rows = fp.Rows.Where(r => r.Frag != null).ToList();
        for (int i = 0; i < rows.Count; i++)
            for (int j = i + 1; j < rows.Count; j++)
            {
                if (rows[i].Frag == rows[j].Frag) continue;
                Rect a = rows[i].Frag.Bounds(l.Footprints), b = rows[j].Frag.Bounds(l.Footprints);
                bool overlap = a.X0 <= b.X1 && b.X0 <= a.X1 && a.Y0 <= b.Y1 && b.Y0 <= a.Y1;
                // the rule-5 aligned column shares one lane x with the final cell (tiles are disjoint, checked above)
                bool aligned = (rows[i].FinalCell || rows[j].FinalCell) && (rows[i].Column.Index == 1 || rows[j].Column.Index == 1) && l.Errors.Count == 0;
                e.Check(!overlap || aligned, tag + " rows overlap: " + rows[i].Name + " " + a + " vs " + rows[j].Name + " " + b);
            }
        foreach (FpConnection k in fp.Connections)
        {
            e.Check(k.To != null && k.Slot >= 0, tag + " connection " + k + " has no target / slot");
            if (k.Route == FpRoute.MachineToMachine) continue;
            e.Check(k.Target != null, tag + " connection " + k + " has no target port");
            if (k.Route != FpRoute.Comb) e.Check(k.Source != null, tag + " connection " + k + " has no source port");
            if (k.Route == FpRoute.Band) e.Check(k.Slot < fp.BandLanes && k.Group >= 0 && k.TargetFlow == Dir.S || k.To.FinalCell, tag + " band connection " + k + ": slot " + k.Slot + " group " + k.Group + " flow " + k.TargetFlow);
            if (k.Route == FpRoute.Straight) e.Check(k.Source.X == k.Target.X && k.Target.Y > k.Source.Y, tag + " straight connection " + k + " is not aligned");
        }
        int top = fp.Rows.Max(r => r.Y1);
        e.Check(fp.BandY0 == top + 1 && fp.CombY == 0, tag + " band y" + fp.BandY0 + " (top row " + top + "), comb y" + fp.CombY);
    }

    static FpRow FpRowOf(FpPlan fp, int item) { return fp.Rows.FirstOrDefault(r => r.Step != null && r.Step.Item == item); }

    static string MicroFloorplanTurret()
    {
        var e = new Errs();
        ProductionPlan plan = TurretPlan(true);
        var spec = new BlockSpec { Item = TurretId, Rate = 7.5 };
        FpPlan fp = Floorplan.Build(gd, spec, plan);
        Console.WriteLine("      --- turret 7.5 floorplan ---");
        foreach (string line in fp.Summary(gd).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("      " + line.TrimEnd('\r'));
        foreach (string n in fp.Notes) Console.WriteLine("      note: " + n);
        FloorplanCommon(e, fp, "turret");
        e.Check(fp.Columns.Count <= 9, fp.Columns.Count + " columns (want <= 9)");
        FpRow fin = fp.Rows.FirstOrDefault(r => r.FinalCell && !r.Absorbed);
        e.Check(fin != null && fin.Column != null && fin.Column.Index == 0, "final cell column " + (fin == null || fin.Column == null ? "none" : fin.Column.Index.ToString()) + " (want 0)");
        FpRow steel = FpRowOf(fp, SteelId);
        e.Check(steel != null && steel.Column != null && steel.Column.Index == 1, "steel column " + (steel == null || steel.Column == null ? "none" : steel.Column.Index.ToString()) + " (want 1, rule 5)");
        FpConnection steelLink = fp.Connections.FirstOrDefault(k => k.From == steel && k.To == fin);
        e.Check(steelLink != null && steelLink.Route == FpRoute.Straight, "steel -> turret link " + (steelLink == null ? "missing" : steelLink.Route.ToString()) + " (want Straight: lane aligned with the E face)");
        FpRow magnet = FpRowOf(fp, Ids.Magnet), coil = FpRowOf(fp, Ids.MagneticCoil);
        e.Check(magnet != null && coil != null && magnet.Column == coil.Column && magnet.Y1 < coil.Y0, "magnet not stacked under the coil row");
        FpConnection mc = fp.Connections.FirstOrDefault(k => k.From == magnet && k.To == coil);
        e.Check(mc != null && mc.Route == FpRoute.Straight, "magnet -> coil link " + (mc == null ? "missing" : mc.Route.ToString()));
        FpRow circuit = fp.Rows.FirstOrDefault(r => r.Absorbed);
        e.Check(circuit != null && fp.Connections.Any(k => k.Route == FpRoute.MachineToMachine && k.From == circuit && k.To == fin), "circuit not absorbed into the final cell (rule 4)");
        var comb = fp.Connections.Where(k => k.Route == FpRoute.Comb).ToList();
        e.Check(comb.Count == 3 && fp.CombTails == 3, comb.Count + " comb tails (want 3: iron ore x2, copper ore)");
        e.Check(comb.Count(k => k.Item == Ids.IronOre) == 2 && comb.Count(k => k.Item == Ids.CopperOre) == 1, "comb items: " + string.Join(", ", comb.Select(k => gd.ItemName(k.Item)).ToArray()));
        e.Check(comb.Select(k => k.Slot).Distinct().Count() == comb.Count, "comb tails share an x");
        foreach (var g in comb.GroupBy(k => k.To.Column))
        {
            var xs = g.Select(k => k.Slot).OrderBy(x => x).ToList();
            for (int i = 1; i < xs.Count; i++) e.Check(xs[i] == xs[i - 1] + 1, "comb tails of column " + g.Key.Index + " not at pitch 1: " + string.Join(",", xs.Select(x => x.ToString()).ToArray()));
        }
        var ordered = comb.OrderBy(k => k.Slot).Select(k => k.To.Column.X0).ToList();
        for (int i = 1; i < ordered.Count; i++) e.Check(ordered[i] >= ordered[i - 1], "comb tails not ordered by column x");
        // band: five items cross the block horizontally in the oracle too (iron ingot, copper ingot, coil, motor, engine); the
        // user's z-stacked bus (rule 2) merges the lanes that only go to the final machine (motor, engine) into one row -> 4
        var bandItems = fp.Connections.Where(k => k.Route == FpRoute.Band).Select(k => k.Item).Distinct().ToList();
        int busItems = bandItems.Count(it => fp.Connections.Where(k => k.Route == FpRoute.Band && k.Item == it).All(k => k.To.FinalCell));
        int rule2Rows = fp.BandLanes - Math.Max(0, busItems - 1);
        e.Check(fp.BandLanes >= 1 && fp.BandLanes <= 5 && rule2Rows <= 4, fp.BandLanes + " flat band rows, " + rule2Rows + " with the rule-2 bus (want <= 5 flat, <= 4 stacked)");
        // steel-like dedicated feeders are not band lanes; iron ingot and copper ingot have several consumers -> band
        e.Check(fp.Connections.Any(k => k.Route == FpRoute.Band && k.Item == Ids.IronIngot) && fp.Connections.Any(k => k.Route == FpRoute.Band && k.Item == Ids.CopperIngot), "iron / copper ingot not on the band");
        // a column's gap is 0 everywhere except a possible corridor after the final cell
        for (int i = 1; i < fp.Columns.Count; i++)
        {
            FpColumn a = fp.Columns[i - 1], b = fp.Columns[i];
            if (i == 1) continue;   // the aligned steel column shares a lane x with the cell
            e.Check(b.X0 == a.X0 + a.Width + a.GapAfter, "column " + b.Index + " x" + b.X0 + " not packed after column " + a.Index + " (x" + a.X0 + " w" + a.Width + " gap " + a.GapAfter + ")");
        }
        return e.Result();
    }

    static string MicroFloorplanSmall()
    {
        var e = new Errs();
        // motor 120 (magnet, copper ingot raw): gear and coil both feed only the motor row; the gear (step order on a rate
        // tie) is stacked under it, the coil and the iron ingot smelters (feeding gear + motor) are their own columns. The
        // motor row has no block input lane, so stacking is allowed (a row with a tail is never stacked).
        var ms = new BlockSpec { Item = MotorId, Rate = 120 };
        foreach (int r in new[] { MagnetId, CopperIngotId }) ms.TreatAsRaw.Add(r);
        ProductionPlan mp = new Solver(gd, ms).Solve();
        FpPlan mf = Floorplan.Build(gd, ms, mp);
        Console.WriteLine("      --- motor 120 floorplan ---");
        foreach (string line in mf.Summary(gd).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("      " + line.TrimEnd('\r'));
        FloorplanCommon(e, mf, "motor");
        e.Check(mf.Columns.Count >= 2 && mf.Columns.Count <= 3, "motor: " + mf.Columns.Count + " columns (want 2-3)");
        e.Check(mf.Connections.Count(k => k.Route == FpRoute.Straight) == 1, "motor: " + mf.Connections.Count(k => k.Route == FpRoute.Straight) + " straight link(s) (want 1: gear or coil under the motor row)");
        FpRow motor = FpRowOf(mf, MotorId);
        e.Check(motor != null && motor.Column != null && motor.Column.Index == 0, "motor: target row not in column 0");
        e.Check(mf.Connections.Count(k => k.Route == FpRoute.Comb) == 3, "motor: " + mf.Connections.Count(k => k.Route == FpRoute.Comb) + " comb tails (want 3: iron ore, magnet, copper ingot)");
        e.Check(mf.Connections.Count(k => k.Route == FpRoute.Band && k.Item == IronIngotId) == 2, "motor: iron ingot should reach gear and motor through the band");

        // coil 360 (coil-360-v3 plan): magnet under coil, copper its own column
        ProductionPlan cp = CoilPlan();
        BlockSpec cs = CoilSpec();
        FpPlan cf = Floorplan.Build(gd, cs, cp);
        Console.WriteLine("      --- coil 360 floorplan ---");
        foreach (string line in cf.Summary(gd).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("      " + line.TrimEnd('\r'));
        FloorplanCommon(e, cf, "coil");
        e.Check(cf.Columns.Count >= 2 && cf.Columns.Count <= 3, "coil: " + cf.Columns.Count + " columns (want 2-3)");
        FpRow magnet = FpRowOf(cf, Ids.Magnet), coil = FpRowOf(cf, Ids.MagneticCoil);
        e.Check(magnet != null && coil != null && magnet.Column == coil.Column && magnet.Y1 < coil.Y0 && coil.Column.Index == 0, "coil: magnet not stacked under the coil row in column 0");
        e.Check(cf.Connections.Any(k => k.Route == FpRoute.Band && k.Item == Ids.CopperIngot), "coil: copper ingot not a band link");
        e.Check(cf.Connections.Count(k => k.Route == FpRoute.Comb) == 2 && cf.CombTails == 2, "coil: " + cf.CombTails + " comb tails (want 2)");
        return e.Result();
    }
}
