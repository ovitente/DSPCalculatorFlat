// Bus.cs - BUS-FIRST kit mode (top-down). The floorplan comes first: a central bus of parallel north-running lanes,
// machine columns on both sides of it (rows parallel to the bus), every machine picks from the lanes beside it
// (L1, or L2 behind an L1 lane) and drops its product onto that product's lane. Lanes never cross (a sorter passes
// over a lane, belts never do). A lane column is reused along the bus: one item ends at its last pickup, the next
// item's lane starts two tiles further north in the same column.
// The floorplan is cut into horizontal bands (y ranges, south -> north) = kit modules. A module contains every
// building of its band, including the bus lane segments that cross it; segments start/end on the band edges, so
// modules placed one after another along the bus line up end to end. The assembled blueprint is the same buildings
// with the segments linked across the band edges.
//   BusFloor (declarative: lanes, machine groups, bands) -> Bus.Build -> assembled Layout + one Layout per band ->
//   Validator on each (own plans and ports) -> Emitter -> files.
// Floorplans: MotorFloor (Electric Motor; hand-designed column assignment, proven on motor 45). No automatic floorplan
// search yet. Pure model code except Export (game DLL through Emitter/GameBp).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DspFlat.Engine;

namespace DspFlat.Host
{

/// One bus lane segment: an item running north in column X from Y0 to Y1 (inclusive). BlockInput = raw lane entering
/// at the south edge (icon on its first tile).
public class BusLane
{
    public string Name; public int Item; public int X, Y0, Y1; public bool BlockInput, BlockOutput;
    public List<Bld> Belts = new List<Bld>();
}

/// A sorter link of a machine group: lane column, sorter length, lateral row offset relative to the machine centre.
public class BusLink
{
    public int Item; public int LaneX; public int Length; public int Lateral;
    public BusLink(int item, int laneX, int length, int lateral) { Item = item; LaneX = laneX; Length = length; Lateral = lateral; }
}

/// A column of identical machines beside the bus (flow N).
public class BusGroup
{
    public string Name; public Step Step; public int ColX; public int[] Ys; public double Yaw;
    public List<BusLink> Ins = new List<BusLink>(); public BusLink Out;
    public double RatePerMachine;               // output items/min per machine
    public double Load;                         // fraction of full speed
    public List<Bld> Machines = new List<Bld>();
    public int Count { get { return Ys.Length; } }
}

public class BusBand
{
    public int No; public string Title; public int Y0, Y1; public List<string> Bullets = new List<string>();
    /// 1 when the module carries overlap tails: every lane entering from the south starts one row below the band, on
    /// the previous module's last belt of that lane (the module's row 0 = assembled row Y0 - 1).
    public int Tail;
    public Layout Layout; public ValidationReport Report; public ExportResult Export; public ProductionPlan Plan;
    public string FileName;
}

public class BusFloor
{
    public string Name;
    public List<BusLane> Lanes = new List<BusLane>();
    public List<BusGroup> Groups = new List<BusGroup>();
    public List<BusBand> Bands = new List<BusBand>();
    public BusLane Product;                     // product lane (buffer depot at its end)
    public List<string> Notes = new List<string>();
}

public class Bus
{
    readonly GameData gd;
    readonly BlockSpec spec;
    public ProductionPlan Plan;                 // solver plan with the margin rule applied
    public BusFloor Floor;
    public Layout Assembled; public ValidationReport AssembledReport; public ExportResult AssembledExport;
    public readonly List<string> Errors = new List<string>();
    public readonly List<string> Notes = new List<string>();
    public Action<string> OnStep;

    public Bus(GameData gd, BlockSpec spec) { this.gd = gd; this.spec = spec; }

    string N(int item) { return gd.ItemName(item); }
    static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    void Say(string s) { if (OnStep != null) OnStep(s); }

    // ================================================================ 1. plan (margin rule)
    /// Margin rule (analysis\user-edit-missile90.md section 2): a step of one machine at exactly 100% that feeds a
    /// multi-machine consumer gets a second machine (both at 50%).
    public static void ApplyMarginRule(ProductionPlan plan, List<string> notes)
    {
        foreach (Step s in plan.Steps)
        {
            if (s.Machines != 1 || s.MachinesExact < 0.999) continue;
            bool many = s.ConsumerSteps.Any(ci => plan.Steps[ci].Machines > 1);
            if (!many) continue;
            s.Machines = 2;
            s.MaxOutputRate *= 2;
            if (notes != null) notes.Add("margin rule: " + (s.Recipe != null ? s.Recipe.Name : s.Item.ToString()) + " 1 machine at 100% feeds several consumers -> 2 machines at 50%");
        }
    }

    // ================================================================ 2. floorplan: Electric Motor
    /// Column plan (x): -2 iron ore | W machines 0 | 2 iron ingot | 3 copper ore -> magnet -> gear | E machines 5 |
    /// 7 copper ingot -> coil | 8 motor (product, buffer depot east of it).
    /// Bands (south -> north): 01 ingots (3 iron smelters W + 1 copper smelter E), 02 magnets (2 W) + coil (1 E),
    /// 03 gears (W), 04 motors (E) + buffer depot. Sorter cells: see BuildGeometry (proven unless noted).
    public BusFloor MotorFloor(ProductionPlan plan)
    {
        Step fe = plan.StepFor(Ids.IronIngot), mg = plan.StepFor(Ids.Magnet), cu = plan.StepFor(Ids.CopperIngot),
             coil = plan.StepFor(Ids.MagneticCoil), gear = plan.StepFor(1201), motor = plan.StepFor(plan.TargetItem);
        if (fe == null || mg == null || cu == null || coil == null || gear == null || motor == null || motor.Item != 1203)
            throw new ArgumentException("MotorFloor needs the Electric Motor chain (iron ingot, magnet, copper ingot, coil, gear, motor)");
        if (fe.Machines > 6 || mg.Machines > 4 || cu.Machines > 3 || coil.Machines > 2 || gear.Machines > 4 || motor.Machines > 6)
            throw new ArgumentException("MotorFloor v1 is laid out for small rates (motor <= ~135/min); this plan needs more machines");
        var f = new BusFloor { Name = "motor" };
        const int W = 0, E = 5, Lore = -2, Lfe = 2, Lmid = 3, Lcu = 7, Lmot = 8;
        // band 01: smelters at pitch 3 from y 0, tail row y -2
        int y = 0;
        var feY = Enumerable.Range(0, fe.Machines).Select(i => y + 3 * i).ToArray();
        var cuY = Enumerable.Range(0, cu.Machines).Select(i => y + 3 * i).ToArray();
        int b1top = Math.Max(feY.Last(), cuY.Last()) + 1;
        // band 02: magnets W (pitch 3), coil E after the last magnet drop (pickup lat -1 > last magnet drop)
        int b2 = b1top + 1;
        var mgY = Enumerable.Range(0, mg.Machines).Select(i => b2 + 1 + 3 * i).ToArray();
        int coil0 = Math.Max(mgY.Last() + 2, b2 + 2);
        var coilY = Enumerable.Range(0, coil.Machines).Select(i => coil0 + 4 * i).ToArray();
        // the coil lane on x 7 starts at the first coil drop (lat +1); a second coil machine would drop into the Cu lane
        if (coil.Machines > 1) throw new ArgumentException("MotorFloor v1: one coil assembler only");
        int b2top = Math.Max(mgY.Last(), coilY.Last()) + 1;
        // band 03: gears W, pitch 4, tail row first
        int b3 = b2top + 1;
        var gearY = Enumerable.Range(0, gear.Machines).Select(i => b3 + 2 + 4 * i).ToArray();
        int b3top = gearY.Last() + 1;
        // band 04: motors E, pitch 4, tail row first; gear pickups (lat -1) must lie north of the last gear drop
        int b4 = b3top + 1;
        int m0 = Math.Max(b4 + 2, gearY.Last() + 2);
        var motY = Enumerable.Range(0, motor.Machines).Select(i => m0 + 4 * i).ToArray();
        int depotT0 = motY.Last() + 2;
        int b4top = depotT0 + 3;

        Func<string, Step, int, int[], double, BusGroup> G = (name, st, col, ys, yaw) =>
        {
            double full = st.MaxOutputRate / Math.Max(1, st.Machines);
            double per = st.Outputs[0].Rate / ys.Length;
            var g = new BusGroup { Name = name, Step = st, ColX = col, Ys = ys, Yaw = yaw, RatePerMachine = per, Load = per / full };
            f.Groups.Add(g); return g;
        };
        var gFe = G("iron smelters", fe, W, feY, 0); gFe.Ins.Add(new BusLink(Ids.IronOre, Lore, 1, -1)); gFe.Out = new BusLink(Ids.IronIngot, Lfe, 1, 0);
        var gCu = G("copper smelter", cu, E, cuY, 0); gCu.Ins.Add(new BusLink(Ids.CopperOre, Lmid, 1, -1)); gCu.Out = new BusLink(Ids.CopperIngot, Lcu, 1, 0);
        var gMg = G("magnet smelters", mg, W, mgY, 0); gMg.Ins.Add(new BusLink(Ids.IronOre, Lore, 1, 1)); gMg.Out = new BusLink(Ids.Magnet, Lmid, 2, 0);
        var gCoil = G("coil assembler", coil, E, coilY, 90); gCoil.Ins.Add(new BusLink(Ids.Magnet, Lmid, 1, -1)); gCoil.Ins.Add(new BusLink(Ids.CopperIngot, Lcu, 1, -1)); gCoil.Out = new BusLink(Ids.MagneticCoil, Lcu, 1, 1);
        var gGear = G("gear assemblers", gear, W, gearY, 90); gGear.Ins.Add(new BusLink(Ids.IronIngot, Lfe, 1, -1)); gGear.Out = new BusLink(1201, Lmid, 2, 0);
        var gMot = G("motor assemblers", motor, E, motY, 90);
        gMot.Ins.Add(new BusLink(1201, Lmid, 1, -1)); gMot.Ins.Add(new BusLink(Ids.IronIngot, Lfe, 2, 0)); gMot.Ins.Add(new BusLink(Ids.MagneticCoil, Lcu, 1, -1));
        gMot.Out = new BusLink(1203, Lmot, 2, 0);

        // lanes (column, from first drop / south edge to last pickup)
        Func<string, int, int, int, int, bool, BusLane> Ln = (name, item, x, y0, y1, input) => { var l = new BusLane { Name = name, Item = item, X = x, Y0 = y0, Y1 = y1, BlockInput = input }; f.Lanes.Add(l); return l; };
        Ln("iron ore", Ids.IronOre, Lore, -2, mgY.Last() + 1, true);
        Ln("copper ore", Ids.CopperOre, Lmid, -2, cuY.Last() - 1, true);
        Ln("iron ingot", Ids.IronIngot, Lfe, feY[0], motY.Last(), false);
        Ln("magnet", Ids.Magnet, Lmid, mgY[0], coilY.Last() - 1, false);
        Ln("copper ingot", Ids.CopperIngot, Lcu, cuY[0], coilY.Last() - 1, false);
        Ln("coil", Ids.MagneticCoil, Lcu, coilY[0] + 1, motY.Last() - 1, false);
        Ln("gear", 1201, Lmid, gearY[0], motY.Last() - 1, false);
        f.Product = Ln("motor", 1203, Lmot, motY[0], depotT0 + 2, false);
        f.Product.BlockOutput = true;

        f.Bands.Add(new BusBand { No = 1, Title = "Ingots", Y0 = -2, Y1 = b1top });
        f.Bands.Add(new BusBand { No = 2, Title = "Magnets + coil", Y0 = b2, Y1 = b2top });
        f.Bands.Add(new BusBand { No = 3, Title = "Gears", Y0 = b3, Y1 = b3top });
        f.Bands.Add(new BusBand { No = 4, Title = "Motors + buffer", Y0 = b4, Y1 = b4top });
        return f;
    }

    // ================================================================ 3. geometry
    /// Builds all buildings of the floor (lanes as one chain per lane, machines, sorters, depot). Returns null or an error.
    public string BuildGeometry(BusFloor f)
    {
        int belt = spec.Tiers.Belt, sorter = spec.Tiers.Sorter;
        // lane chains
        var tiles = new Dictionary<long, Bld>();
        foreach (var l in f.Lanes)
        {
            if (l.Y1 < l.Y0) return "lane " + l.Name + " is empty (" + l.Y0 + ".." + l.Y1 + ")";
            Bld prev = null;
            for (int yy = l.Y0; yy <= l.Y1; yy++)
            {
                long k = K(l.X, yy);
                if (tiles.ContainsKey(k)) return "lane " + l.Name + " runs into " + tiles[k].Chain + " at (" + l.X + "," + yy + ")";
                Bld b = Bld.Belt(belt, l.X, yy, 0, l.Item, "bus:" + l.Name);
                b.Role = "bus " + l.Name;
                if (prev != null) Layout.Link(prev, b, 1);
                tiles[k] = b; l.Belts.Add(b); prev = b;
            }
            // a lane end must not point into the next lane's first tile in the same column
            Bld after; if (tiles.TryGetValue(K(l.X, l.Y1 + 1), out after)) return "lane " + l.Name + " ends right before " + after.Chain + " at (" + l.X + "," + (l.Y1 + 1) + ")";
            if (l.BlockInput) l.Belts[0].Parameters = new[] { l.Item, 0 };
        }
        foreach (var l in f.Lanes) { Bld after; if (tiles.TryGetValue(K(l.X, l.Y0 - 1), out after) && after.Output == null && after.Chain != "bus:" + l.Name) return "lane " + after.Chain + " ends right before " + l.Name; }
        // machines + sorters
        foreach (var g in f.Groups)
        {
            g.Machines.Clear();
            foreach (int yy in g.Ys)
            {
                Bld m = Bld.Machine(g.Step.MachineItem, g.ColX, yy, g.Yaw, g.Step.Recipe.Id);
                m.Carries = g.Step.Item; m.Load = Math.Min(1.0, g.Load); m.Role = g.Name;
                g.Machines.Add(m);
                foreach (var ln in g.Ins.Concat(new[] { g.Out }))
                {
                    bool input = ln != g.Out;
                    Dir side = ln.LaneX > g.ColX ? Dir.E : Dir.W;
                    SorterGeo geo = Geo.FindAny(g.Step.MachineItem, input, side, ln.Length, ln.Lateral, g.Yaw);
                    if (geo == null) return g.Name + ": no sorter cell " + (input ? "in " : "out ") + side + " L" + ln.Length + " lat " + ln.Lateral;
                    if (!geo.Proven) Note("UNPROVEN sorter cell " + geo.Name + " [" + geo.Status + "] (" + g.Name + ", " + N(ln.Item) + ")");
                    Bld bb; tiles.TryGetValue(K(g.ColX + geo.BeltDx, yy + geo.BeltDy), out bb);
                    if (bb == null) return g.Name + " at y " + yy + ": no " + N(ln.Item) + " belt at (" + (g.ColX + geo.BeltDx) + "," + (yy + geo.BeltDy) + ")";
                    if (bb.Carries != ln.Item) return g.Name + " at y " + yy + ": belt at (" + bb.TileX + "," + bb.TileY + ") carries " + N(bb.Carries) + ", not " + N(ln.Item);
                    Bld s = geo.Make(m, bb, sorter, ln.Item);
                    extra.Add(s);
                }
            }
        }
        // buffer depot beside the last 3 product tiles (cell DEPOT_BUFFER turned to flow N, depot east)
        if (f.Product != null && spec.Buffer)
        {
            var pb = f.Product.Belts; int n = pb.Count;
            if (n < 4) return "product lane too short for the buffer depot";
            Fragment dep = Modules.DepotBuffer(pb[n - 3], pb[n - 2], pb[n - 1], Dir.N, true, sorter, f.Product.Item);
            depot.AddRange(dep.Blds);
            Note("buffer depot: cell DEPOT_BUFFER turned to flow N (derived orientation; game cell is flow E)");
        }
        return null;
    }
    readonly List<Bld> extra = new List<Bld>();   // sorters
    readonly List<Bld> depot = new List<Bld>();
    void Note(string s) { if (!Notes.Contains(s)) Notes.Add(s); }
    static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }

    IEnumerable<Bld> AllBuildings(BusFloor f)
    {
        foreach (var l in f.Lanes) foreach (var b in l.Belts) yield return b;
        foreach (var g in f.Groups) foreach (var m in g.Machines) yield return m;
        foreach (var s in extra) yield return s;
        foreach (var d in depot) yield return d;
    }

    // ================================================================ 4. rates per lane at a band edge
    /// Items/min on lane l just north of row y: drops minus pickups south of (and on) y; block input = all its pickups.
    double LaneRateAbove(BusFloor f, BusLane l, int y)
    {
        double r = 0;
        foreach (var g in f.Groups)
            foreach (var m in g.Machines)
                foreach (var s in extra.Where(x => x.Input == m || x.Output == m))
                {
                    Bld belt = s.Input == m ? s.Output : s.Input;
                    if (!l.Belts.Contains(belt)) continue;
                    double rate = RateOf(g, s.Carries, s.Input == m);
                    bool south = belt.TileY <= y;
                    if (s.Input == m) { if (south) r += rate; }
                    else if (l.BlockInput) { if (!south) r += rate; }
                    else { if (south) r -= rate; }
                }
        return Math.Max(0, r);
    }

    static double RateOf(BusGroup g, int item, bool output)
    {
        if (output) return g.RatePerMachine;
        var inp = g.Step.Inputs.FirstOrDefault(x => x.Item == item);
        return inp == null ? 0 : inp.Rate / g.Count;
    }

    // ================================================================ 5. layouts: assembled + bands
    static readonly FieldInfo[] BldFields = typeof(Bld).GetFields(BindingFlags.Public | BindingFlags.Instance);
    static Bld Copy(Bld b) { var c = new Bld(); foreach (var fi in BldFields) fi.SetValue(c, fi.GetValue(b)); if (b.Parameters != null) c.Parameters = (int[])b.Parameters.Clone(); c.Game = null; c.Index = -1; return c; }

    /// Copies the given buildings into a new layout, links remapped; links to buildings outside the set are cut.
    static Dictionary<Bld, Bld> CopySet(IEnumerable<Bld> set)
    {
        var map = new Dictionary<Bld, Bld>();
        foreach (var b in set) map[b] = Copy(b);
        foreach (var kv in map)
        {
            Bld o; Bld c = kv.Value;
            c.Output = kv.Key.Output != null && map.TryGetValue(kv.Key.Output, out o) ? o : null;
            c.Input = kv.Key.Input != null && map.TryGetValue(kv.Key.Input, out o) ? o : null;
            if (c.Kind == Kind.Belt && c.Output == null) c.OutputToSlot = 0;
        }
        return map;
    }

    static bool InBand(Bld b, BusBand band)
    {
        double y = b.Kind == Kind.Sorter ? (b.Input != null && b.Input.Kind != Kind.Belt ? b.Input.Y : b.Output != null ? b.Output.Y : b.Y) : b.Y;
        return y > band.Y0 - 0.5 && y < band.Y1 + 0.5;
    }

    public void Build(ProductionPlan solverPlan)
    {
        Plan = solverPlan;
        ApplyMarginRule(Plan, Notes);
        try { Floor = MotorFloor(Plan); }
        catch (ArgumentException e) { Errors.Add(e.Message); return; }
        string err = BuildGeometry(Floor);
        if (err != null) { Errors.Add("geometry: " + err); return; }
        var all = AllBuildings(Floor).ToList();
        // band membership + towers per band (band interior only, so neighbouring modules' towers stay >= 4 apart)
        foreach (var band in Floor.Bands)
        {
            var set = all.Where(b => InBand(b, band)).ToList();
            var bl = new Layout(gd);
            foreach (var b in set) bl.Add(b);
            if (bl.Errors.Count > 0) { Errors.Add("band " + band.No + ": " + bl.Errors[0]); return; }
            int y0 = band.Y0, y1 = band.Y1;
            Modules.TeslaAllowed = (x, yy) => yy >= y0 + 2 && yy <= y1 - 2;
            List<Bld> towers;
            try { towers = Modules.TeslaGrid(bl, gd, true); }
            finally { Modules.TeslaAllowed = null; }
            if (bl.Errors.Count > 0) { Errors.Add("band " + band.No + " towers: " + bl.Errors[0]); return; }
            all.AddRange(towers);
        }
        int all0 = all.Count;
        // assembled
        var amap = CopySet(all);
        Assembled = new Layout(gd);
        foreach (var b in all) Assembled.Add(amap[b]);
        AddPorts(Assembled, amap, null);
        // common frame: every file starts at (0,0); all modules have the assembled width, so the bus columns sit at the
        // same offset in every module (cursor = frame centre) and modules pasted one above the other line up
        Rect fr = Assembled.Bounds();
        FrameX0 = fr.X0; FrameW = fr.Width;
        Shift(Assembled, -fr.X0, -fr.Y0);
        AssembledReport = new Validator(gd, spec).Validate(Assembled, Plan);
        Say("assembled: " + Assembled.All.Count + " buildings, " + (AssembledReport.Ok ? "valid" : AssembledReport.Errors.Count + " error(s)"));
        if (!AssembledReport.Ok) foreach (var e in AssembledReport.Errors.Take(12)) Errors.Add("assembled: " + e);
        // bands
        foreach (var band in Floor.Bands)
        {
            var set = all.Where(b => InBand(b, band)).ToList();
            // overlap tails: a lane entering from the south also takes the previous band's last belt of that lane
            // (row Y0 - 1). Pasted N of the previous module, this belt is the chain head (no input, has an output) on
            // an existing same-tier belt with a free output, which the paste covers and joins
            // (BuildTool_BlueprintPaste.CheckBuildConditions IL_1aaa..IL_1ae8, conditions.md F21).
            var tails = new HashSet<Bld>();
            foreach (var lane in Floor.Lanes)
                if (lane.Y0 < band.Y0 && lane.Y1 >= band.Y0) tails.Add(lane.Belts[band.Y0 - 1 - lane.Y0]);
            band.Tail = tails.Count > 0 ? 1 : 0;
            set.AddRange(tails);
            var map = CopySet(set);
            var l = new Layout(gd);
            foreach (var b in set) l.Add(map[b]);
            // item icons only on the block's raw inputs (the first tile of a block input lane)
            foreach (var lane in Floor.Lanes)
                if (lane.BlockInput && map.ContainsKey(lane.Belts[0])) map[lane.Belts[0]].Parameters = new[] { lane.Item, 0 };
            foreach (var t in tails) map[t].Parameters = null;
            AddPorts(l, map, band, tails);
            Shift(l, -FrameX0, -(band.Y0 - band.Tail));
            Rect br = l.Bounds();
            if (br.X0 < 0 || br.X1 >= FrameW || br.Y0 < 0 || br.Y1 > band.Y1 - band.Y0 + band.Tail) Errors.Add("module " + band.No + ": buildings outside its frame (" + br + ")");
            band.Layout = l;
            band.Plan = BandPlan(band);
            band.Report = new Validator(gd, spec).Validate(l, band.Plan);
            Say("module " + band.No.ToString("00", CultureInfo.InvariantCulture) + " " + band.Title + ": " + l.All.Count + " buildings, " + (band.Report.Ok ? "valid" : band.Report.Errors.Count + " error(s)"));
            if (!band.Report.Ok) foreach (var e in band.Report.Errors.Take(8)) Errors.Add("module " + band.No + ": " + e);
            band.FileName = band.No.ToString("00", CultureInfo.InvariantCulture) + "-" + Kit.Slug(band.Title) + ".txt";
            Bullets(band);
        }
        if (all.Count != all0) Errors.Add("internal: building list changed");
    }

    public int FrameX0, FrameW;

    static void Shift(Layout l, int dx, int dy)
    {
        foreach (var b in l.All) b.Translate(dx, dy);
        foreach (var p in l.Ports) { p.X += dx; p.Y += dy; }
    }

    /// Ports: block inputs (raw lanes at the south edge), the product at the lane end; for a band also every lane
    /// segment crossing its south (In) or north (Out) edge, with the lane rate at that edge.
    void AddPorts(Layout l, Dictionary<Bld, Bld> map, BusBand band, HashSet<Bld> tails = null)
    {
        foreach (var lane in Floor.Lanes)
        {
            var seg = lane.Belts.Where(b => band == null || InBand(b, band) || (tails != null && tails.Contains(b))).ToList();
            if (seg.Count == 0) continue;
            Bld first = seg[0], last = seg[seg.Count - 1];
            bool tail = tails != null && tails.Contains(first);
            bool inEdge = tail || (first == lane.Belts[0] ? lane.BlockInput : band != null);
            bool outEdge = lane.BlockOutput ? last == lane.Belts[lane.Belts.Count - 1] : band != null && last != lane.Belts[lane.Belts.Count - 1];
            if (inEdge)
                l.Ports.Add(new Port { Name = "in:" + lane.Name, Kind = PortKind.In, X = first.TileX, Y = first.TileY, Dir = Dir.N, Item = lane.Item,
                    Rate = LaneRateAbove(Floor, lane, tail ? band.Y0 - 1 : first.TileY - 1), Belt = map[first], Chain = first.Chain, Seam = tail });
            if (outEdge)
                l.Ports.Add(new Port { Name = "out:" + lane.Name, Kind = PortKind.Out, X = last.TileX, Y = last.TileY, Dir = Dir.N, Item = lane.Item,
                    Rate = lane.BlockOutput ? Plan.TargetRate : LaneRateAbove(Floor, lane, last.TileY), Belt = map[last], Chain = last.Chain });
        }
    }

    /// One plan per band: a step per machine group in the band (its share), raw inputs = its In ports.
    ProductionPlan BandPlan(BusBand band)
    {
        var p = new ProductionPlan { TargetItem = Plan.TargetItem, TargetRate = Plan.TargetRate };
        foreach (var g in Floor.Groups.Where(x => x.Machines.Any(m => InBand(m, band))))
        {
            Step s = g.Step;
            var st = new Step { Item = s.Item, Recipe = s.Recipe, MachineItem = s.MachineItem, Speed = s.Speed, Machines = g.Count,
                MachinesExact = g.Count * g.Load, MaxOutputRate = g.RatePerMachine * g.Count / Math.Max(1e-9, g.Load) };
            foreach (var i in s.Inputs) st.Inputs.Add(new ItemRate(i.Item, i.Rate));
            st.Outputs.Add(new ItemRate(s.Item, s.Outputs[0].Rate));
            p.Steps.Add(st);
        }
        foreach (var port in band.Layout.Ports.Where(x => x.Kind == PortKind.In)) p.RawInputs.Add(new ItemRate(port.Item, port.Rate));
        // the band's "product" = the last item it makes that leaves through its north edge
        var outs = band.Layout.Ports.Where(x => x.Kind == PortKind.Out).ToList();
        var made = p.Steps.Select(s => s.Item).Reverse().FirstOrDefault(it => outs.Any(o => o.Item == it));
        if (made != 0) { p.TargetItem = made; p.TargetRate = outs.First(o => o.Item == made).Rate; }
        return p;
    }

    void Bullets(BusBand band)
    {
        band.Bullets.Clear();
        foreach (var g in Floor.Groups.Where(x => x.Machines.Any(m => InBand(m, band))))
            band.Bullets.Add(g.Count + " " + Short(g.Step.MachineItem) + " " + N(g.Step.Item) + " " + F(g.Step.Outputs[0].Rate) + "/min (" + (g.ColX < 4 ? "W" : "E") + ")");
        var ins = band.Layout.Ports.Where(x => x.Kind == PortKind.In).Select(x => N(x.Item)).ToArray();
        var outs = band.Layout.Ports.Where(x => x.Kind == PortKind.Out).Select(x => N(x.Item)).ToArray();
        if (ins.Length > 0) band.Bullets.Add("S in: " + string.Join(", ", ins));
        if (outs.Length > 0) band.Bullets.Add("N out: " + string.Join(", ", outs));
        if (band.Layout.All.Any(b => b.Kind == Kind.Storage)) band.Bullets.Add("buffer depot on the product");
        band.Bullets.Add("place " + (band.No == 1 ? "first, ore enters S" : "N of module " + (band.No - 1).ToString("00", CultureInfo.InvariantCulture) + ", bottom row on its top bus belts (overlap 1 row)"));
    }

    string Short(int machine) { return machine == Ids.ArcSmelter ? "smelter" : machine == Ids.AsmMk1 ? "asm Mk.I" : N(machine); }

    // ================================================================ 6. export + files
    public void Export(Cells cells)
    {
        var lb = Assembled.Bounds();
        BpHeader h = Emitter.HeaderFor(Assembled, Plan, spec, gd);
        h.Desc = BpText.Lines(N(Plan.TargetItem) + " " + F(Plan.TargetRate) + "/min, bus layout", new[] {
            "bus N: iron ore W, ingots + intermediates in the middle, motor E",
            "ore in S: " + string.Join(", ", Plan.RawInputs.Select(r => N(r.Item) + " " + F(r.Rate)).ToArray()),
            string.Join(", ", Floor.Groups.Select(g => g.Count + " " + N(g.Step.Item)).ToArray()),
            "buffer depot at the motor lane end", "belts Mk.II, sorters Mk.II", "kit: " + KitName + " (" + Floor.Bands.Count + " modules)" });
        AssembledExport = ExportOne(cells, Assembled, h, AssembledReport, "assembled");
        foreach (var band in Floor.Bands)
        {
            BpHeader bh = Emitter.HeaderFor(band.Layout, band.Plan, spec, gd);
            bh.ShortDesc = BpText.Digits(band.No, 2);
            bh.Width = FrameW; bh.Height = band.Y1 - band.Y0 + 1 + band.Tail;
            var icons = new List<int>();
            foreach (var g in Floor.Groups.Where(x => x.Machines.Any(m => InBand(m, band)))) if (!icons.Contains(g.Step.Item)) icons.Add(g.Step.Item);
            while (icons.Count < 5) icons.Add(0);
            bh.Icons = icons.Take(5).ToArray();
            bh.Desc = BpText.Lines(band.No.ToString("00", CultureInfo.InvariantCulture) + " " + band.Title + " (kit " + N(Plan.TargetItem) + " " + F(Plan.TargetRate) + "/min)", band.Bullets);
            band.Export = ExportOne(cells, band.Layout, bh, band.Report, "module " + band.No);
        }
    }

    ExportResult ExportOne(Cells cells, Layout l, BpHeader h, ValidationReport rep, string what)
    {
        var ex = new GameEmitter(gd, cells).Emit(l, h);
        GameEmitter.CheckExport(ex, l, rep);
        foreach (var e in ex.Errors) rep.Fail(e);
        if (!rep.Ok) Errors.Add(what + ": export: " + rep.Errors[0]);
        Say(what + ": " + (rep.Ok ? "exported, re-import + hash ok" : "export FAILED"));
        return ex;
    }

    public string KitName { get { return "kit-" + Kit.Slug(N(spec.Item)) + "-" + F(spec.Rate); } }

    /// Writes the assembled blueprint and the kit folder (modules + _intro_). Never deletes anything.
    public void Write(string assembledPath, string kitDir)
    {
        if (assembledPath != null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(assembledPath))); GameBp.Write(assembledPath, AssembledExport.Text); }
        if (kitDir == null) return;
        Directory.CreateDirectory(kitDir);
        foreach (var band in Floor.Bands) GameBp.Write(Path.Combine(kitDir, band.FileName), band.Export.Text);
        File.WriteAllText(Path.Combine(kitDir, "_intro_"), IntroText(), new UTF8Encoding(false));
    }

    public string IntroText()
    {
        var ic = new List<int> { Plan.TargetItem };
        foreach (var r in Plan.RawInputs) if (ic.Count < 5) ic.Add(r.Item);
        while (ic.Count < 5) ic.Add(0);
        var sb = new StringBuilder();
        sb.Append("Bus kit ").Append(N(Plan.TargetItem)).Append(' ').Append(F(Plan.TargetRate)).Append("/min, ").Append(Floor.Groups.Sum(g => g.Count)).Append(" machines. ")
          .Append("Place modules in number order, each directly north of the previous one with its bottom row on the previous module's top bus belts (1 row overlap): the pasted bus tails cover those belt ends and join them. Ore enters at the S edge of 01. ");
        sb.Append(string.Join("; ", Floor.Bands.Select(b => b.No.ToString("00", CultureInfo.InvariantCulture) + " " + b.Title).ToArray()));
        return "BPBOOK:0,1," + string.Join(",", ic.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + ",0," + Uri.EscapeDataString(sb.ToString());
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        if (Assembled != null) { var r = Assembled.Bounds(); sb.AppendLine("  assembled: " + Assembled.All.Count + " buildings, " + r.Width + "x" + r.Height + ", " + Assembled.Count(Kind.Tesla, 0) + " towers"); }
        if (Floor != null)
            foreach (var b in Floor.Bands)
                if (b.Layout != null) { var r = b.Layout.Bounds(); sb.AppendLine("  " + b.FileName + ": rows " + b.Y0 + ".." + b.Y1 + ", " + b.Layout.All.Count + " buildings, " + r.Width + "x" + r.Height + ", " + b.Layout.Count(Kind.Tesla, 0) + " towers" + (b.Report != null ? ", " + b.Report.Warnings.Count + " warn" : "")); }
        foreach (var n in Notes) sb.AppendLine("  note: " + n);
        return sb.ToString();
    }
}
}
