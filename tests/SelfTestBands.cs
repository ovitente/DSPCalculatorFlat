// SelfTestBands.cs - self tests of the floorplan belt layer (engine\Bands.cs, docs\floorplan-design.md "bands").
// Hand-made FpPlans from Modules.MachineRow fragments placed by hand; Bands.Lay; then Validator.ValidateBelts must be
// clean and the geometry is asserted against the drawn patterns (refs\patterns\p-sheet-001.txt: P12 x3 y40..42 straight
// lane raised over turning lanes, P13 x13..15 y40..42 adjacent bridges, P14 splitter corners 2 tiles out).
// Filter: MAKEBLOCK_CASES="micro bands".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    static partial void RunBandCases()
    {
        Case("micro bands a: band iron ingot over two adjacent product lanes of unrelated rows = 2 adjacent raised nodes (P13 shape), straight feet, band y = BandY0 + Slot, ValidateBelts clean, export ok", MicroBandsCrossing);
        Case("micro bands b: comb of 3 tails pitch 1, the middle lane turns east along the edge, the straight lane it crosses bridges over it (P12 shape), ValidateBelts clean", MicroBandsComb);
        Case("micro bands c1: fork, both consumers east: one splitter on the band row (in W, out S/E), P14 corner geometry, clearance, ValidateBelts clean", () => MicroBandsFork(false));
        Case("micro bands c2: fork, consumers west and east: splitter on top of the rise (in S, out W/E), corners 2 tiles out, ValidateBelts clean", () => MicroBandsFork(true));
        Case("micro bands c3: fork with a third consumer fed from the second consumer's lane end, ValidateBelts clean", MicroBandsForkThird);
        Case("micro bands f: merge, two iron producers into one consumer: the second rise side-joins the first run's band row (rule 6), ValidateBelts clean", MicroBandsMerge);
        Case("micro bands g: two bands on adjacent slots: the slot 0 row bridges over the slot 1 rise whose top tile lies on that row, ValidateBelts clean", MicroBandsAdjacentSlots);
        Case("micro bands h: a band row blocked by a tower reports an error and rolls back completely (no belts, source open, target icon restored)", MicroBandsRollback);
        Case("micro bands e: straight stacked rows (producer under its consumer) link port to port; an impossible band reports an error and lays nothing", MicroBandsStraightAndError);
    }

    // ------------------------------------------------------------------ fixtures
    const int BandGearRecipe = 5;   // Gear (1201) from iron ingot on Assembler Mk.I (SelfTest.cs ModulesPitchAndLaneEnd)

    static RowSpec BandRow(string name, int machine, int recipe, int count, int[] ins, int outItem, Dir flow, bool mirror)
    {
        var rates = ins.Select(i => 60.0 * count).ToArray();
        return new RowSpec { Name = name, MachineItem = machine, RecipeId = recipe, Count = count, InputItems = ins, InputRates = rates,
            OutputItem = outItem, OutputRate = 60.0 * count, Flow = flow, Mirror = mirror, Data = gd };
    }

    /// Row fragment translated so that its port `portName` lands on tile (px, py).
    static FpRow PlaceByPort(FpPlan fp, Layout work, RowSpec spec, string portName, int px, int py, int column)
    {
        Fragment f = Modules.MachineRow(spec);
        foreach (string p in spec.Problems) throw new Exception(spec.Name + ": " + p);
        Port port = f.Port(portName);
        f.Translate(px - port.X, py - port.Y);
        Rect b = f.Bounds(work.Footprints);
        var row = new FpRow { Name = spec.Name, Frag = f, Y0 = b.Y0, Y1 = b.Y1 };
        FpColumn col = fp.Columns.FirstOrDefault(c => c.Index == column);
        if (col == null) { col = new FpColumn { Index = column, X0 = b.X0, Width = b.Width }; fp.Columns.Add(col); }
        col.Rows.Add(row); row.Column = col;
        fp.Rows.Add(row);
        work.Add(f);
        if (work.Errors.Count > 0) throw new Exception("fixture overlap: " + work.Errors[0]);
        return row;
    }

    static FpConnection Conn(FpPlan fp, FpRoute route, int item, FpRow from, string outPort, FpRow to, string inPort, int slot)
    {
        var c = new FpConnection { Item = item, Rate = 60, From = from, To = to, Route = route, Slot = slot,
            Source = from != null ? from.Frag.Port(outPort) : null, Target = to.Frag.Port(inPort) };
        fp.Connections.Add(c);
        return c;
    }

    static string BeltsReport(string tag, Layout l, Errs e, List<string> errors, FpPlan fp)
    {
        foreach (string m in errors) e.L.Add("lay: " + m);
        ValidationReport r = new Validator(gd, null).ValidateBelts(l);
        foreach (string m in r.Errors) e.L.Add("belts: " + m);
        foreach (string m in r.Warnings.Where(w => w.Contains("UNPROVEN"))) e.L.Add("warning: " + m);
        foreach (string m in fp.Notes) Console.WriteLine("      " + m);
        foreach (string m in r.Info.Where(m => m.StartsWith("raised runs", StringComparison.Ordinal) || m.StartsWith("splitter", StringComparison.Ordinal))) Console.WriteLine("      " + tag + ": " + m);
        Rect bx = l.Bounds();
        Console.WriteLine("      " + tag + ": " + l.All.Count + " buildings, " + bx.Width + "x" + bx.Height + ", " + l.All.Count(b => b.IsRaised) + " raised node(s), " + l.Count(Kind.Splitter, 0) + " splitter(s)");
        return r.Info.FirstOrDefault(m => m.StartsWith("raised runs", StringComparison.Ordinal));
    }

    static Bld GroundBelt(Layout l, int x, int y) { return l.All.FirstOrDefault(b => b.Kind == Kind.Belt && !b.IsRaised && b.TileX == x && b.TileY == y && Math.Abs(b.X - x) < 0.3 && Math.Abs(b.Y - y) < 0.3); }
    static Bld RaisedBelt(Layout l, int x, int y) { return l.All.FirstOrDefault(b => b.Kind == Kind.Belt && b.IsRaised && b.TileX == x && b.TileY == y); }

    // ------------------------------------------------------------------ (a) band with two crossings
    /// Producer A (2 iron smelters, flow N) west; unrelated B (2 copper smelters) and D (2 magnet smelters, mirrored so
    /// that its product lane is right next to B's) between; consumer C (2 gear assemblers, flow S: its iron ingot in port
    /// at the north end) east. B's and D's product lanes continue north past the band row (product tails to the north
    /// edge), so the band crosses two adjacent vertical lanes = the P13 bundle shape (adjacent raised nodes, one run).
    /// Inside a machine column a band cannot cross (sorters on every lane tile at the machine rows, footprints between):
    /// the first attempt of this fixture with a 4-smelter row B failed exactly so.
    static string MicroBandsCrossing()
    {
        var e = new Errs();
        string chosen = null;
        List<string> last = null;
        const int bandY = 8, laneTop = 16;
        foreach (int variant in new[] { 0, 1, 2, 3 })
        {
            if (chosen != null) break;
            bool mirrorD = variant % 2 == 1; int dxD = variant < 2 ? -1 : 1;
            var work = new Layout(gd);
            var fp = new FpPlan { BandY0 = bandY, BandLanes = 1, CombY = -3 };
            FpRow a = PlaceByPort(fp, work, BandRow("iron", Ids.ArcSmelter, Ids.RecipeIronIngot, 2, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, 0, -1, 0);
            FpRow b = PlaceByPort(fp, work, BandRow("copper", Ids.ArcSmelter, Ids.RecipeCopperIngot, 2, new[] { Ids.CopperOre }, Ids.CopperIngot, Dir.N, false), "in:" + Ids.CopperOre, 12, -1, 1);
            Port bo = b.Frag.Port("out:" + Ids.CopperIngot);
            Fragment df = Modules.MachineRow(BandRow("magnet", Ids.ArcSmelter, Ids.RecipeMagnet, 2, new[] { Ids.IronOre }, Ids.Magnet, Dir.N, mirrorD));
            Port dOut = df.Port("out:" + Ids.Magnet);
            df.Translate(bo.X + dxD - dOut.X, bo.Y - dOut.Y);
            Rect db = df.Bounds(work.Footprints);
            if (!work.Grid.IsFree(db)) continue;   // D would overlap A or B: try the other side / mirror
            var d = new FpRow { Name = "magnet", Frag = df, Y0 = db.Y0, Y1 = db.Y1 };
            fp.Rows.Add(d); work.Add(df);
            FpRow c = PlaceByPort(fp, work, BandRow("gear", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, bo.X + 10, 3, 2);
            // product lanes of B and D continue north to the block edge (laid by hand, linked from the out ports)
            foreach (Port po in new[] { bo, dOut })
            {
                var tiles = new List<int[]>();
                for (int y = po.Y + 1; y <= laneTop; y++) tiles.Add(new[] { po.X, y });
                Fragment ext = Modules.BeltRun(po.Chain + "-ext", Ids.BeltMk2, po.Item, tiles, null);
                work.Add(ext);
                Layout.Link(po.Belt, ext.Port("start").Belt, 1);
            }
            if (work.Errors.Count > 0) { e.L.Add("fixture: " + work.Errors[0]); break; }
            FpConnection conn = Conn(fp, FpRoute.Band, Ids.IronIngot, a, "out:" + Ids.IronIngot, c, "in:" + Ids.IronIngot, 0);
            List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
            last = errors;
            if (errors.Count > 0) { Console.WriteLine("      variant " + variant + ": " + errors[0]); break; }
            chosen = "variant " + variant + " (D lane at x" + (bo.X + dxD) + ", B lane at x" + bo.X + ")";
            string runs = BeltsReport("a", work, e, errors, fp);
            // geometry: band row = BandY0 + Slot, 2 adjacent raised nodes over the two product lanes, straight feet
            var raised = work.All.Where(x => x.IsRaised).OrderBy(x => x.TileX).ToList();
            var bLanes = new List<int> { Math.Min(bo.X, bo.X + dxD), Math.Max(bo.X, bo.X + dxD) };
            e.Check(raised.Count == 2, raised.Count + " raised nodes, expected 2 (one per crossed lane x " + string.Join(",", bLanes.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")");
            e.Check(raised.Count == 2 && raised[1].TileX == raised[0].TileX + 1 && raised[0].Output == raised[1], "raised nodes are not one adjacent run (P13)");
            foreach (Bld r in raised)
            {
                e.Check(r.TileY == bandY, "raised node " + r + " not on the band row y" + bandY);
                e.Check(bLanes.Contains(r.TileX), "raised node " + r + " not over a lane of row B");
                e.Check(Near(r.Z, GameData.BridgeZ, 0.01) && NearYaw(r.Yaw, 90, 1), "raised node " + r + " z/yaw");
                Bld under = GroundBelt(work, r.TileX, r.TileY);
                e.Check(under != null && under.Output != null && NearYaw(under.Yaw, 0, 1), "no straight N ground belt under " + r);
            }
            if (raised.Count == 2)
            {
                Bld f0 = GroundBelt(work, raised[0].TileX - 1, bandY), f1 = GroundBelt(work, raised[1].TileX + 1, bandY);
                e.Check(f0 != null && NearYaw(f0.Yaw, 90, 1) && f0.Output == raised[0], "west foot missing or not straight E");
                e.Check(f1 != null && NearYaw(f1.Yaw, 90, 1) && raised[1].Output == f1, "east foot missing or not straight E");
                Bld a0 = GroundBelt(work, raised[0].TileX - 2, bandY), a1 = GroundBelt(work, raised[1].TileX + 2, bandY);
                e.Check(a0 != null && NearYaw(a0.Yaw, 90, 1) && a1 != null && NearYaw(a1.Yaw, 90, 1), "approach nodes missing or not straight E");
            }
            e.Check(conn.Laid && conn.Bridges == 2 && conn.Chain == "band:" + Ids.IronIngot + "#0", "connection record: laid " + conn.Laid + ", bridges " + conn.Bridges + ", chain " + conn.Chain);
            Port src = a.Frag.Port("out:" + Ids.IronIngot), dst = c.Frag.Port("in:" + Ids.IronIngot);
            e.Check(src.Belt.Output != null && src.Belt.Output.TileX == src.X && src.Belt.Output.TileY == src.Y + 1, "source port not linked into the rise");
            Bld feeder = work.All.FirstOrDefault(x => x.Kind == Kind.Belt && x.Output == dst.Belt);
            e.Check(feeder != null && feeder.TileX == dst.X && feeder.TileY == dst.Y + 1 && NearYaw(feeder.Yaw, 180, 1), "target port not fed from the north: " + (feeder == null ? "no feeder" : feeder.ToString()));
            e.Check(dst.Belt.Parameters == null, "target lane kept its input icon (rule 5: icon only on a block input tail)");
            e.Check(runs != null && runs.Contains("1 run(s), 2 node(s)"), "raised-run summary: " + (runs ?? "-"));
            // corner tiles of the band: bisector yaws
            Bld cNW = GroundBelt(work, src.X, bandY), cNE = GroundBelt(work, dst.X, bandY);
            e.Check(cNW != null && NearYaw(cNW.Yaw, 45, 1), "rise corner at (" + src.X + "," + bandY + ") yaw " + (cNW == null ? "-" : Fmt(cNW.Yaw)) + ", expected 45");
            e.Check(cNE != null && NearYaw(cNE.Yaw, 135, 1), "descent corner at (" + dst.X + "," + bandY + ") yaw " + (cNE == null ? "-" : Fmt(cNE.Yaw)) + ", expected 135");
            // (d) export round trip
            if (e.L.Count == 0)
            {
                ExportResult x = new GameEmitter(gd, GetCells()).Emit(work, Emitter.HeaderFor(work, null, new BlockSpec { Item = 1201, Rate = 120 }, gd));
                var check = new ValidationReport();
                GameEmitter.CheckExport(x, work, check);
                foreach (string m in x.Errors.Concat(check.Errors)) e.L.Add("export: " + m);
            }
        }
        if (chosen == null) e.L.Add("band could not be laid: " + (last != null && last.Count > 0 ? last[0] : "fixture did not fit"));
        else Console.WriteLine("      a: band row y" + bandY + ", fixture " + chosen);
        return e.Result();
    }

    // ------------------------------------------------------------------ (b) comb with a turning lane
    /// Three single-input smelter rows: R1 (in lane x0), R2 (in lane x0+2, mirrored), R3 (in lane x0+8, mirrored). Tails
    /// at x0 / x0+1 / x0+2 on CombY: the middle tail feeds R3 two columns east, so it turns along the edge and the straight
    /// lane x0+2 bridges over it (P12 drawing: iron straight lane raised at (3,41)/(3,42) over copper/stone turning W).
    static string MicroBandsComb()
    {
        var e = new Errs();
        var work = new Layout(gd);
        const int x0 = 10, combY = -6;
        var fp = new FpPlan { CombY = combY, CombTails = 3, BandY0 = 20 };
        FpRow r1 = PlaceByPort(fp, work, BandRow("iron", Ids.ArcSmelter, Ids.RecipeIronIngot, 2, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, x0, -1, 0);
        FpRow r2 = PlaceByPort(fp, work, BandRow("copper", Ids.ArcSmelter, Ids.RecipeCopperIngot, 2, new[] { Ids.CopperOre }, Ids.CopperIngot, Dir.N, true), "in:" + Ids.CopperOre, x0 + 2, -1, 1);
        FpRow r3 = PlaceByPort(fp, work, BandRow("magnet", Ids.ArcSmelter, Ids.RecipeMagnet, 2, new[] { Ids.IronOre }, Ids.Magnet, Dir.N, true), "in:" + Ids.IronOre, x0 + 8, -1, 2);
        FpConnection c1 = Conn(fp, FpRoute.Comb, Ids.IronOre, null, null, r1, "in:" + Ids.IronOre, x0);
        FpConnection c3 = Conn(fp, FpRoute.Comb, Ids.IronOre, null, null, r3, "in:" + Ids.IronOre, x0 + 1);
        FpConnection c2 = Conn(fp, FpRoute.Comb, Ids.CopperOre, null, null, r2, "in:" + Ids.CopperOre, x0 + 2);
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        string runs = BeltsReport("b", work, e, errors, fp);
        // tails: icons on (x0..x0+2, combY), pitch 1, block ports registered
        for (int x = x0; x <= x0 + 2; x++)
        {
            Bld t = GroundBelt(work, x, combY);
            e.Check(t != null && t.Parameters != null && t.Parameters.Length == 2 && t.Parameters[0] != 0, "tail at (" + x + "," + combY + ") missing or without icon");
            e.Check(work.Ports.Any(p => p.Kind == PortKind.In && p.X == x && p.Y == combY && p.Belt == t), "no block In port on the tail (" + x + "," + combY + ")");
        }
        e.Check(c1.Laid && c2.Laid && c3.Laid, "not every comb lane laid: " + c1.Laid + "/" + c2.Laid + "/" + c3.Laid);
        // the turning lane: corner at (x0+1, yt), E along yt, corner at (x0+8, yt), N into R3's port
        int yt = combY + 2;
        Bld k1 = GroundBelt(work, x0 + 1, yt), k2 = GroundBelt(work, x0 + 8, yt);
        e.Check(k1 != null && NearYaw(k1.Yaw, 45, 1) && k1.Chain == c3.Chain, "turning lane corner at (" + (x0 + 1) + "," + yt + "): " + (k1 == null ? "missing" : k1.ToString()));
        e.Check(k2 != null && NearYaw(k2.Yaw, 45, 1) && k2.Chain == c3.Chain, "turning lane corner at (" + (x0 + 8) + "," + yt + "): " + (k2 == null ? "missing" : k2.ToString()));
        // P12 shape: the straight lane x0+2 is raised over the turning lane's tile, ground belt under it flows E, feet straight N
        Bld rz = RaisedBelt(work, x0 + 2, yt);
        e.Check(rz != null && rz.Chain == c2.Chain && NearYaw(rz.Yaw, 0, 1) && Near(rz.Z, GameData.BridgeZ, 0.01), "straight lane not raised over (" + (x0 + 2) + "," + yt + "): " + (rz == null ? "missing" : rz.ToString()));
        Bld under = GroundBelt(work, x0 + 2, yt);
        e.Check(under != null && under.Chain == c3.Chain && NearYaw(under.Yaw, 90, 1), "ground belt under the bridge is not the turning lane flowing E");
        Bld f0 = GroundBelt(work, x0 + 2, yt - 1), f1 = GroundBelt(work, x0 + 2, yt + 1);
        e.Check(f0 != null && NearYaw(f0.Yaw, 0, 1) && f0.Output == rz, "south foot of the bridge missing or not straight N");
        e.Check(f1 != null && NearYaw(f1.Yaw, 0, 1) && rz != null && rz.Output == f1, "north foot of the bridge missing or not straight N");
        e.Check(work.All.Count(b => b.IsRaised) == 1 && c2.Bridges == 1 && c3.Bridges == 0 && c1.Bridges == 0, "raised nodes: " + work.All.Count(b => b.IsRaised) + ", expected exactly 1 (on the straight copper lane)");
        e.Check(runs != null && runs.Contains("1 run(s), 1 node(s)"), "raised-run summary: " + (runs ?? "-"));
        // tail / edge checks of the full validator on the comb
        var spec = new BlockSpec { InEdge = Dir.S };
        foreach (string m in Lanes.InputEdgeErrors(work, spec)) e.L.Add("input edge: " + m);
        return e.Result();
    }

    // ------------------------------------------------------------------ (c) fork
    /// Producer P (3 iron smelters, flow N); consumers C1 / C2 (2 gear assemblers each, flow S) east of P (same side:
    /// splitter at C1's column, in W, out S / E) or on both sides (splitter on the rise, in S, out W / E).
    static string MicroBandsFork(bool bothSides)
    {
        var e = new Errs();
        var work = new Layout(gd);
        const int bandY = 12;
        var fp = new FpPlan { BandY0 = bandY, BandLanes = 1, CombY = -3 };
        FpRow p = PlaceByPort(fp, work, BandRow("iron", Ids.ArcSmelter, Ids.RecipeIronIngot, 3, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, bothSides ? 14 : 0, -1, 1);
        FpRow c1 = PlaceByPort(fp, work, BandRow("gear1", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, bothSides ? 2 : 10, 3, bothSides ? 0 : 2);
        FpRow c2 = PlaceByPort(fp, work, BandRow("gear2", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, bothSides ? 22 : 18, 3, bothSides ? 2 : 3);
        FpConnection k1 = Conn(fp, FpRoute.Band, Ids.IronIngot, p, "out:" + Ids.IronIngot, c1, "in:" + Ids.IronIngot, 0);
        FpConnection k2 = Conn(fp, FpRoute.Band, Ids.IronIngot, p, "out:" + Ids.IronIngot, c2, "in:" + Ids.IronIngot, 0);
        k1.Group = k2.Group = 0;
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        BeltsReport(bothSides ? "c2" : "c1", work, e, errors, fp);
        var splitters = work.All.Where(b => b.Kind == Kind.Splitter).ToList();
        e.Check(splitters.Count == 1, splitters.Count + " splitters, expected 1");
        if (splitters.Count != 1) return e.Result();
        Bld s = splitters[0];
        Port src = p.Frag.Port("out:" + Ids.IronIngot), d1 = c1.Frag.Port("in:" + Ids.IronIngot), d2 = c2.Frag.Port("in:" + Ids.IronIngot);
        e.Check(s.TileY == bandY, "splitter " + s + " not on the band row y" + bandY);
        e.Check(s.TileX == (bothSides ? src.X : d1.X), "splitter " + s + " at x" + s.TileX + ", expected x" + (bothSides ? src.X : d1.X));
        e.Check(k1.Splitter == s && k2.Splitter == s && k1.Laid && k2.Laid, "connections do not record the splitter / not laid");
        // P14: the splitter's side-output runs may turn on the tile 2 steps from the centre; every belt within 2 tiles of the
        // centre belongs to the splitter's own chains (clearance) - checked by the Validator above; here: the corner positions
        if (bothSides)
        {
            Bld w = GroundBelt(work, s.TileX - 2, bandY), ea = GroundBelt(work, s.TileX + 2, bandY);
            e.Check(w != null && w.Output != null && w.Output.TileY == bandY - 1 || d1.X != s.TileX - 2, "west output does not turn S 2 tiles after the splitter");
            e.Check(ea != null && ea.Output != null && (ea.Output.TileY == bandY - 1 || ea.Output.TileX == s.TileX + 3), "east output run missing 2 tiles after the splitter");
            Bld riseTop = GroundBelt(work, s.TileX, bandY - 2);
            e.Check(riseTop != null && riseTop.Output != null && Math.Abs(riseTop.Output.Y - (bandY - 1.073)) < 0.01, "rise does not end 2 tiles under the splitter linking into its input belt");
        }
        else
        {
            Bld down = GroundBelt(work, s.TileX, bandY - 2);
            e.Check(down != null && NearYaw(down.Yaw, 180, 1) && down.Chain == k1.Chain, "S output descent does not start 2 tiles under the splitter");
            Bld feed = GroundBelt(work, s.TileX - 2, bandY);
            e.Check(feed != null && feed.Output != null && Math.Abs(feed.Output.X - (s.TileX - 1.073)) < 0.01, "band row does not end 2 tiles west of the splitter linking into its input belt");
            Bld ea = GroundBelt(work, s.TileX + 2, bandY);
            e.Check(ea != null && ea.Chain == k2.Chain, "east output run missing 2 tiles after the splitter");
        }
        // both consumers fed from the north
        foreach (Port d in new[] { d1, d2 })
        {
            Bld feeder = work.All.FirstOrDefault(x => x.Kind == Kind.Belt && x.Output == d.Belt);
            e.Check(feeder != null && feeder.TileX == d.X && feeder.TileY == d.Y + 1, "port " + d + " not fed from the north");
        }
        e.Check(s.Parameters != null && s.Parameters[0] == 0, "splitter priority flag set");
        return e.Result();
    }

    /// Fork plus a third consumer C3 (flow N, in port at the south end) fed from C2's lane end (rule 8 / 12).
    static string MicroBandsForkThird()
    {
        var e = new Errs();
        var work = new Layout(gd);
        const int bandY = 12;
        var fp = new FpPlan { BandY0 = bandY, BandLanes = 1, CombY = -20 };
        FpRow p = PlaceByPort(fp, work, BandRow("iron", Ids.ArcSmelter, Ids.RecipeIronIngot, 3, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, 0, -1, 0);
        FpRow c1 = PlaceByPort(fp, work, BandRow("gear1", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, 10, 3, 1);
        FpRow c2 = PlaceByPort(fp, work, BandRow("gear2", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, 18, 3, 2);
        Port d2 = c2.Frag.Port("in:" + Ids.IronIngot);
        // C2's lane ends at its last pickup (south); C3 flows N with its in port 6 tiles below that end, 10 columns east
        Bld end = d2.Belt; while (end.Output != null && end.Output.Kind == Kind.Belt) end = end.Output;
        FpRow c3 = PlaceByPort(fp, work, BandRow("gear3", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.N, false), "in:" + Ids.IronIngot, end.TileX + 10, end.TileY - 6, 3);
        FpConnection k1 = Conn(fp, FpRoute.Band, Ids.IronIngot, p, "out:" + Ids.IronIngot, c1, "in:" + Ids.IronIngot, 0);
        FpConnection k2 = Conn(fp, FpRoute.Band, Ids.IronIngot, p, "out:" + Ids.IronIngot, c2, "in:" + Ids.IronIngot, 0);
        FpConnection k3 = Conn(fp, FpRoute.Band, Ids.IronIngot, p, "out:" + Ids.IronIngot, c3, "in:" + Ids.IronIngot, 0);
        k1.Group = k2.Group = k3.Group = 0;
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        BeltsReport("c3", work, e, errors, fp);
        e.Check(work.Count(Kind.Splitter, 0) == 1, work.Count(Kind.Splitter, 0) + " splitters, expected 1 (rule 8: the third consumer takes from the lane tail)");
        e.Check(k1.Laid && k2.Laid && k3.Laid, "laid: " + k1.Laid + "/" + k2.Laid + "/" + k3.Laid);
        Port d3 = c3.Frag.Port("in:" + Ids.IronIngot);
        e.Check(end.Output != null && end.Output.Kind == Kind.Belt && end.Output.TileY == end.TileY - 1, "C2's lane end does not continue south");
        Bld feeder = work.All.FirstOrDefault(x => x.Kind == Kind.Belt && x.Output == d3.Belt);
        e.Check(feeder != null && feeder.TileX == d3.X && feeder.TileY == d3.Y - 1 && feeder.Chain == k3.Chain, "C3's port not fed from the south by the tail feed");
        return e.Result();
    }

    // ------------------------------------------------------------------ (f) merge
    /// P1 (west) and P2 (between P1 and the consumer) both make iron ingot for C (flow S). P1's run is the primary band;
    /// P2's rise ends one tile under the band row and side-joins north into it (Modules.SideMerge, slot 2).
    static string MicroBandsMerge()
    {
        var e = new Errs();
        var work = new Layout(gd);
        const int bandY = 10;
        var fp = new FpPlan { BandY0 = bandY, BandLanes = 1, CombY = -3 };
        FpRow p1 = PlaceByPort(fp, work, BandRow("iron1", Ids.ArcSmelter, Ids.RecipeIronIngot, 2, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, 0, -1, 0);
        FpRow p2 = PlaceByPort(fp, work, BandRow("iron2", Ids.ArcSmelter, Ids.RecipeIronIngot, 2, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, 10, -1, 1);
        FpRow c = PlaceByPort(fp, work, BandRow("gear", Ids.AsmMk1, BandGearRecipe, 3, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, 20, 3, 2);
        FpConnection k1 = Conn(fp, FpRoute.Band, Ids.IronIngot, p1, "out:" + Ids.IronIngot, c, "in:" + Ids.IronIngot, 0);
        FpConnection k2 = Conn(fp, FpRoute.Band, Ids.IronIngot, p2, "out:" + Ids.IronIngot, c, "in:" + Ids.IronIngot, 0);
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        BeltsReport("f", work, e, errors, fp);
        e.Check(k1.Laid && k2.Laid, "laid: " + k1.Laid + "/" + k2.Laid);
        e.Check(k2.Chain != null && k2.Chain.StartsWith("merge:", StringComparison.Ordinal), "merge chain name: " + k2.Chain);
        Port o2 = p2.Frag.Port("out:" + Ids.IronIngot);
        Bld top = GroundBelt(work, o2.X, bandY - 1), band = GroundBelt(work, o2.X, bandY);
        e.Check(top != null && band != null && top.Output == band && top.OutputToSlot == 2 && band.Chain == k1.Chain, "second rise does not side-join (slot 2) into the primary band row at (" + o2.X + "," + bandY + ")");
        e.Check(work.Count(Kind.Splitter, 0) == 0, "a merge must not use a splitter (rule 6)");
        return e.Result();
    }

    // ------------------------------------------------------------------ (g) adjacent band slots
    /// A -> C on slot 0, B -> D on slot 1, B between A and C: B's rise ends on (x, BandY0), the tile under its corner on
    /// slot 1's row, and that tile is exactly where slot 0's row has to bridge over it.
    static string MicroBandsAdjacentSlots()
    {
        var e = new Errs();
        var work = new Layout(gd);
        const int bandY = 10;
        var fp = new FpPlan { BandY0 = bandY, BandLanes = 2, CombY = -3 };
        FpRow a = PlaceByPort(fp, work, BandRow("iron", Ids.ArcSmelter, Ids.RecipeIronIngot, 2, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, 0, -1, 0);
        FpRow b = PlaceByPort(fp, work, BandRow("copper", Ids.ArcSmelter, Ids.RecipeCopperIngot, 2, new[] { Ids.CopperOre }, Ids.CopperIngot, Dir.N, false), "in:" + Ids.CopperOre, 12, -1, 1);
        FpRow c = PlaceByPort(fp, work, BandRow("gear", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, 24, 3, 2);
        FpRow d = PlaceByPort(fp, work, BandRow("wire", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.CopperIngot }, 1201, Dir.S, false), "in:" + Ids.CopperIngot, 34, 3, 3);
        FpConnection k0 = Conn(fp, FpRoute.Band, Ids.IronIngot, a, "out:" + Ids.IronIngot, c, "in:" + Ids.IronIngot, 0);
        FpConnection k1 = Conn(fp, FpRoute.Band, Ids.CopperIngot, b, "out:" + Ids.CopperIngot, d, "in:" + Ids.CopperIngot, 1);
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        BeltsReport("g", work, e, errors, fp);
        e.Check(k0.Laid && k1.Laid, "laid: " + k0.Laid + "/" + k1.Laid);
        Port bo = b.Frag.Port("out:" + Ids.CopperIngot);
        Bld r = RaisedBelt(work, bo.X, bandY), under = GroundBelt(work, bo.X, bandY);
        e.Check(r != null && r.Chain == k0.Chain, "slot 0 row is not raised over the slot 1 rise at (" + bo.X + "," + bandY + ")");
        e.Check(under != null && under.Chain == k1.Chain && NearYaw(under.Yaw, 0, 1), "no straight slot 1 rise under the bridge");
        e.Check(work.All.Count(x => x.IsRaised) == 1, work.All.Count(x => x.IsRaised) + " raised nodes, expected 1");
        return e.Result();
    }

    // ------------------------------------------------------------------ (h) rollback
    static string MicroBandsRollback()
    {
        var e = new Errs();
        var work = new Layout(gd);
        const int bandY = 10;
        var fp = new FpPlan { BandY0 = bandY, BandLanes = 1, CombY = -3 };
        FpRow a = PlaceByPort(fp, work, BandRow("iron", Ids.ArcSmelter, Ids.RecipeIronIngot, 2, new[] { Ids.IronOre }, Ids.IronIngot, Dir.N, false), "in:" + Ids.IronOre, 0, -1, 0);
        FpRow c = PlaceByPort(fp, work, BandRow("gear", Ids.AsmMk1, BandGearRecipe, 2, new[] { Ids.IronIngot }, 1201, Dir.S, false), "in:" + Ids.IronIngot, 15, 3, 1);
        work.Add(Bld.Tesla(8, bandY));
        FpConnection k = Conn(fp, FpRoute.Band, Ids.IronIngot, a, "out:" + Ids.IronIngot, c, "in:" + Ids.IronIngot, 0);
        Port src = k.Source, dst = k.Target;
        e.Check(dst.Belt.Parameters != null, "fixture: the target lane has no icon to restore");
        int before = work.All.Count;
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        e.Check(errors.Count == 1 && errors[0].Contains("band row"), "errors: " + string.Join(" | ", errors.ToArray()));
        e.Check(work.All.Count == before && !k.Laid, (work.All.Count - before) + " belts left behind, laid " + k.Laid);
        e.Check(src.Belt.Output == null, "source port still linked");
        e.Check(dst.Belt.Parameters != null && dst.Belt.Parameters[0] == Ids.IronIngot, "target lane icon not restored");
        e.Check(work.Grid.IsFree(src.X, src.Y + 1) && work.Grid.IsFree(dst.X, dst.Y + 1), "rolled-back tiles still occupied");
        foreach (string m in new Validator(gd, null).ValidateBelts(work).Errors) e.L.Add("belts: " + m);
        return e.Result();
    }

    // ------------------------------------------------------------------ (e) straight + error path
    static string MicroBandsStraightAndError()
    {
        var e = new Errs();
        var work = new Layout(gd);
        var fp = new FpPlan { BandY0 = 30, BandLanes = 1, CombY = -3 };
        // magnet row under a coil row: magnet out port directly under the coil's magnet in port -> ports linked, no new belt
        FpRow mag = PlaceByPort(fp, work, BandRow("magnet", Ids.ArcSmelter, Ids.RecipeMagnet, 3, new[] { Ids.IronOre }, Ids.Magnet, Dir.N, false), "in:" + Ids.IronOre, 0, -1, 0);
        Port mo = mag.Frag.Port("out:" + Ids.Magnet);
        var coilSpec = BandRow("coil", Ids.AsmMk1, Ids.RecipeMagneticCoil, 2, new[] { Ids.Magnet, Ids.CopperIngot }, Ids.MagneticCoil, Dir.N, false);
        Fragment cf = Modules.MachineRow(coilSpec);
        Port ci = cf.Port("in:" + Ids.Magnet);
        int gap = 2;   // 2 joining tiles between the rows
        FpRow coil = PlaceByPort(fp, work, coilSpec, "in:" + Ids.Magnet, mo.X, mo.Y + 1 + gap, 0);
        FpConnection s1 = Conn(fp, FpRoute.Straight, Ids.Magnet, mag, "out:" + Ids.Magnet, coil, "in:" + Ids.Magnet, -1);
        // impossible band: the target port lies above the band row (E / W lane ends are valid targets now, floorplan final cell)
        var bogus = new FpConnection { Item = Ids.CopperIngot, From = mag, To = coil, Route = FpRoute.Band, Slot = 0, Source = mo,
            Target = new Port { Name = "in:x", Kind = PortKind.In, X = 40, Y = 40, Dir = Dir.S, Item = Ids.CopperIngot, Belt = Bld.Belt(Ids.BeltMk2, 40, 40, 180, Ids.CopperIngot, "x") } };
        fp.Connections.Add(bogus);
        int before = work.All.Count;
        List<string> errors = Bands.Lay(gd, new BlockSpec(), fp, work);
        e.Check(errors.Count == 1 && errors[0].Contains("below the target port"), "errors: " + string.Join(" | ", errors.ToArray()) + " (expected exactly the bogus band)");
        e.Check(s1.Laid && mo.Belt.Output != null && work.All.Count == before + gap, "straight link: laid " + s1.Laid + ", " + (work.All.Count - before) + " new belts, expected " + gap);
        e.Check(coil.Frag.Port("in:" + Ids.Magnet).Belt.Parameters == null, "stacked consumer lane kept its icon");
        e.Check(!bogus.Laid && bogus.Belts.Count == 0 && !work.All.Any(b => b.Chain != null && b.Chain.StartsWith("band:", StringComparison.Ordinal)), "the failed band left belts behind");
        ValidationReport r = new Validator(gd, null).ValidateBelts(work);
        foreach (string m in r.Errors) e.L.Add("belts: " + m);
        return e.Result();
    }
}
