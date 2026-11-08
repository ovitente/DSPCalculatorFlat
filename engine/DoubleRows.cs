// DoubleRows.cs - two facing machine columns that share the middle output lane, as the user draws them on the pattern
// sheet refs\patterns\p-sheet-001.txt (docs\patterns-design.md: P10/P11 smelter double column x29..37, P01/P02 coil block
// x58..68). Pure model code (no game DLL, no I/O). Hooks in shared files are marked "PATTERNS HOOK: double rows"
// (Modules.cs forwarding entry Modules.DoubleRow, Composer.cs mode bit / row build / second raw lane port).
//
// Measured geometry (DumpBp.exe of the sheet; x east, y north; every machine yaw 0; numbers relative to the machine
// centre, world frame):
//   Smelters (12 Arc smelters, recipe not set in the file): columns x31 (west) and x35 (east), 4 apart, pitch 3, machine
//   rows y11..26. Ore lane x29 (west, 2 tiles outside the west column) and x37 (east): L1 input at lateral 0 (slots 10 /
//   4). Shared output lane x33 between the columns: west machines drop at lateral +1 (slot 3), east machines at lateral
//   -1 (slot 9), so the two columns use different tiles of the shared lane. Lanes run y9/y10..27 (output to y28); the two
//   ore tails (icon 1001) sit at (32,8) and (34,8) next to the output column and reach the outer lanes by runs along y9.
//   The six west ore sorters (#43 #48 #54 #64 #75 #82) were first recorded machine -> belt; the user re-drew them
//   belt -> machine on 2026-11-08 (same file), so the west input cell is a game cell like the east one.
//   Coil block (6 Assembler Mk.I, recipe 6): columns x61 / x65, pitch 4, rows y11..19. Per column: L2 lane 3 tiles out
//   (x58 / x68, magnet, lateral 0, slots 10 / 4), L1 lane 2 tiles out (x59 / x67, copper ingot, lateral -1, slots 9 / 5),
//   output lane x63 (west drops lateral +1 slot 3, east lateral -1 slot 9). Sorter ends sit 0.91..0.93 from the centre in
//   this file (coil-360-v3 cells: 0.874). Spray coaters (2313) at y5 on x61 x62 x64 x65: the four input lanes run as a
//   bundle x61..65 from their tails (y0) to y8/y9 and jog outward to x58/59/67/68; the proliferator lane x60 (tail y0,
//   ramp z0 y1 / 0.5 y2 / 1 y3, raised corner y4) runs east at y4 z1 one tile behind the coaters and ends after the last
//   one. The engine lays Coaters.AddToRow's COATER cell instead (straight lanes, feed row across all lanes from a ramp
//   column outside side A) - see docs\engine-api.md "Double rows" for what Coaters.cs would need to copy the drawing.
//   Record coordinates drift ~0.024 along a column (sphere grid); the cells take the middle machine's records (y17 /
//   y15) so every record of the sheet is within 0.015 of the cell.
//
// Fragment (Flow N frame, before spec.Flow rotation): output lane x 0, flowing N; column A (side A = E unless Mirror)
// at x +2, column B at x -2, both starting at y 0; column A has ceil(Count/2) machines, column B floor(Count/2); input
// lanes outside each column: L1 at 2 tiles, L2 at 3 tiles from the column. Ports: "in:<item>" (column A lane),
// "in:<item>#B" (column B lane, PortSuffixB), "out:<item>" (last tile of the shared lane). Port rates: each input lane
// carries its column's share of the row input; the output port the whole row.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

public static class DoubleRows
{
    /// Master switch of the Composer side: false (default) = the Composer never builds double rows and behaves as before
    /// this file existed; Modules.DoubleRow itself works regardless. Opt-in because a double row has two lanes per raw item
    /// separated by the output column, which the lane-split expectations of decision 9 (one lane per 720/min, lanes of one
    /// item adjacent on the edge; selftests "micro lanes") and rule 10 for two raw items do not allow yet; the sheet draws
    /// converging tails (x32/x34 at y8) to bring the two ore inputs together, which the fragment does not do.
    public static bool Enabled = false;
    /// Composer row-mode bit: the same modes once more with two-column raw rows (Composer.Compose, PATTERNS HOOK).
    public const int ModeBit = 64;
    /// Rows below this many machines stay plain MachineRows in the Composer.
    public const int MinCount = 4;
    /// Column B input port name suffix ("in:<item>#B").
    public const string PortSuffixB = "#B";
    public const string SheetFile = @"refs\patterns\p-sheet-001.txt";
    /// Machine columns are this far apart (sheet: x31/x35, x61/x65); the shared lane sits in the middle.
    public const int ColumnGap = 4;

    class Req
    {
        public string Role; public int Item; public bool In; public Dir Side; public int L; public int Index; public double PerMachine;
        public List<SorterGeo> Geos = new List<SorterGeo>();
    }

    class Column
    {
        public string Tag; public int X; public int Count; public Dir Outer, Inner; public bool IsA;
        public List<Req> Reqs = new List<Req>();
    }

    // ================================================================ sheet cells
    static List<SorterGeo> sheet;

    static void Add(int item, bool toMachine, int bdx, int bdy, double sx, double sy, double ex, double ey, int slot, int len, int rec, string status, string note)
    {
        var g = new SorterGeo
        {
            MachineItem = item, MachineYaw = 0, BeltToMachine = toMachine, BeltDx = bdx, BeltDy = bdy, Sx = sx, Sy = sy, Ex = ex, Ey = ey,
            Slot = slot, Length = len, Status = status, SourceKey = null, SourceIndex = rec, Pattern = "double column (pattern sheet)", Note = note
        };
        g.Yaw = Dirs.Yaw(Dirs.FromYaw(Math.Atan2(ex - sx, ey - sy) * 180.0 / Math.PI));
        g.Source = SheetFile + " #" + rec;
        g.Name = "sheet " + Geo.MakeName(g);
        sheet.Add(g);
    }

    static void Build()
    {
        if (sheet != null) return;
        sheet = new List<SorterGeo>();
        const bool IN = true, OUT = false;
        // ---- arc smelter double column, records of the middle machines (31,17) #26 west and (35,17) #25 east
        Add(Ids.ArcSmelter, IN, -2, 0, -2, 0.004, -0.833, 0.001, 10, 1, 64, "game",
            "west ore lane, lateral 0 (re-drawn belt -> machine by the user 2026-11-08)");
        Add(Ids.ArcSmelter, IN, 2, 0, 2, 0.004, 0.833, 0.001, 4, 1, 63, "game", "east ore lane, lateral 0");
        Add(Ids.ArcSmelter, OUT, 2, 1, 0.831, 0.796, 2, 0.799, 3, 1, 62, "game", "west machine drops on the shared lane at lateral +1 (game offset -5)");
        Add(Ids.ArcSmelter, OUT, -2, -1, -0.834, -0.794, -2, -0.791, 9, 1, 66, "game", "east machine drops on the shared lane at lateral -1 (game offset 4)");
        // ---- assembler Mk.I coil block, records of the middle machines (61,15) #7 west and (65,15) #6 east
        Add(Ids.AsmMk1, IN, -3, 0, -3, 0.008, -0.919, 0.001, 10, 2, 69, "game", "west L2 lane (magnet in the sheet), lateral 0 behind the L1 lane");
        Add(Ids.AsmMk1, IN, -2, -1, -2, -0.791, -0.921, -0.794, 9, 1, 73, "game", "west L1 lane (copper ingot in the sheet), lateral -1");
        Add(Ids.AsmMk1, OUT, 2, 1, 0.918, 0.796, 2, 0.799, 3, 1, 67, "game", "west machine drops on the shared lane at lateral +1");
        Add(Ids.AsmMk1, IN, 3, 0, 3, 0.008, 0.919, 0.001, 4, 2, 68, "game", "east L2 lane, lateral 0 behind the L1 lane");
        Add(Ids.AsmMk1, IN, 2, -1, 2, -0.791, 0.921, -0.794, 5, 1, 71, "game", "east L1 lane, lateral -1");
        Add(Ids.AsmMk1, OUT, -2, -1, -0.921, -0.794, -2, -0.791, 9, 1, 72, "game", "east machine drops on the shared lane at lateral -1");
    }

    /// The sheet's sorter cells (copies, native machine yaw 0).
    public static List<SorterGeo> SheetCells() { Build(); var l = new List<SorterGeo>(); foreach (var g in sheet) l.Add(g.Copy()); return l; }

    /// 1 = smelter class (arc / plane), 2 = assembler class (Mk.I-III), 0 = no double-column cells.
    static int ClassOf(int item)
    {
        if (item == Ids.ArcSmelter || item == Ids.PlaneSmelter) return 1;
        if (item == Ids.AsmMk1 || item == Ids.AsmMk2 || item == Ids.AsmMk3) return 2;
        return 0;
    }

    /// Machines a double row can be built for (3x3 smelter / assembler classes; Matrix Labs and colliders have their own rows).
    public static bool Supports(int machineItem) { return ClassOf(machineItem) != 0 && !SlotRows.Handles(machineItem); }

    /// Cell for a machine at yaw 0: the sheet's own cells first (same machine class), then the proven Geo cells turned to
    /// machine yaw 0 (e.g. smelter L2 inputs of smelter-arc-2 for a 2-input smelter row). null = none.
    static SorterGeo Lookup(int machineItem, bool input, Dir side, int length, int lateral)
    {
        Build();
        int cls = ClassOf(machineItem);
        foreach (var g in sheet)
        {
            if (ClassOf(g.MachineItem) != cls || g.BeltToMachine != input || g.Side != side || g.Length != length || g.Lateral != lateral) continue;
            SorterGeo c = g.Copy();
            if (c.MachineItem != machineItem) { c.Note = (c.Note == null ? "" : c.Note + "; ") + "geometry of machine " + c.MachineItem + " used for " + machineItem + " (same class)"; c.MachineItem = machineItem; }
            return Sane(c, side, length);
        }
        SorterGeo p = Geo.Find(machineItem, input, side, length, lateral, 0.0);
        if (p == null || Math.Abs(Dirs.Norm(p.MachineYaw)) > 0.5) return null;
        return Sane(p, side, length);
    }

    static SorterGeo Sane(SorterGeo g, Dir side, int length)
    {
        if (side != Dir.E && side != Dir.W) return null;
        int sign = side == Dir.E ? 1 : -1;
        if (g.BeltDx != sign * (length + 1) || Math.Abs(g.BeltDy) > 1) return null;
        return g;
    }

    // ================================================================ the row
    /// Two facing columns of spec.Count machines (ceil/floor halves) sharing the middle output lane; 1 or 2 inputs, own
    /// input lanes per column outside it (L1 at 2 tiles, L2 at 3 tiles). Ports "in:<item>", "in:<item>#B", "out:<item>".
    /// Tails (spec.TailItems), icons (spec.LaneIcons), coaters (spec.CoatItems, Coaters.AddToRow) and the per-sorter
    /// throughput check (spec.Data) follow Modules.MachineRow. Throws ArgumentException for a bad spec and
    /// InvalidOperationException when no cell combination exists for the machine.
    public static Fragment DoubleRow(RowSpec spec)
    {
        if (spec == null) throw new ArgumentNullException("spec");
        if (spec.Count < 2) throw new ArgumentException("double row " + spec.Name + ": Count must be >= 2");
        if (spec.InputItems == null || spec.InputItems.Length < 1 || spec.InputItems.Length > 2)
            throw new ArgumentException("double row " + spec.Name + ": 1..2 input items required");
        if (spec.InputRates != null && spec.InputRates.Length != spec.InputItems.Length)
            throw new ArgumentException("double row " + spec.Name + ": InputRates length differs from InputItems");
        if (spec.InputItems.Distinct().Count() != spec.InputItems.Length || spec.InputItems.Contains(spec.OutputItem))
            throw new ArgumentException("double row " + spec.Name + ": duplicate item among inputs/output");
        if (!Supports(spec.MachineItem))
            throw new InvalidOperationException("double row " + spec.Name + ": machine " + spec.MachineItem + " has no double-column cells (sheet: arc smelter, assembler Mk.I)");
        spec.Problems.Clear(); spec.Notes.Clear();

        int n = spec.InputItems.Length;
        Dir sideA = spec.Mirror ? Dir.W : Dir.E, sideB = Dirs.Opposite(sideA);
        int signA = sideA == Dir.E ? 1 : -1;
        var cols = new List<Column>
        {
            new Column { Tag = "A", IsA = true, X = signA * ColumnGap / 2, Count = (spec.Count + 1) / 2, Outer = sideA, Inner = sideB },
            new Column { Tag = "B", IsA = false, X = -signA * ColumnGap / 2, Count = spec.Count / 2, Outer = sideB, Inner = sideA }
        };
        RecipeDef rdef = spec.Data != null ? spec.Data.Recipe(spec.RecipeId) : null;
        double outFull = spec.OutputRate / spec.Count;
        if (rdef != null && rdef.TimeSec > 0)
            outFull = Math.Max(outFull, rdef.OutCount(spec.OutputItem) * 60.0 * spec.Data.Speed(spec.MachineItem) / rdef.TimeSec);

        // ---- cells per column: inputs on the outer side (L1, then L2 behind it), the output on the inner side
        foreach (Column c in cols)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                var r = new Req { Role = "in" + (i + 1), Item = spec.InputItems[i], In = true, Side = c.Outer, L = i == 0 ? 1 : 2, Index = i,
                    PerMachine = spec.InputRates != null ? spec.InputRates[i] / spec.Count : 0 };
                int[] pref = i == 0 ? (n == 1 ? new[] { 0, -1, 1 } : new[] { -1, 1, 0 }) : new[] { 0, -1, 1 };
                foreach (int lat in pref)
                {
                    if (used.Contains(lat)) continue;
                    SorterGeo g = Lookup(spec.MachineItem, true, c.Outer, r.L, lat);
                    if (g == null) continue;
                    r.Geos.Add(g); used.Add(lat); break;
                }
                if (r.Geos.Count == 0)
                    throw new InvalidOperationException("double row " + spec.Name + ": no L" + r.L + " input cell on side " + c.Outer + " for machine " + spec.MachineItem + " (column " + c.Tag + ")");
                c.Reqs.Add(r);
            }
            var o = new Req { Role = "out", Item = spec.OutputItem, In = false, Side = c.Inner, L = 1, Index = -1, PerMachine = outFull };
            // sheet: the column west of the shared lane drops at lateral +1, the column east of it at lateral -1
            int first = c.Inner == Dir.E ? 1 : -1;
            foreach (int lat in new[] { first, -first, 0 })
            {
                SorterGeo g = Lookup(spec.MachineItem, false, c.Inner, 1, lat);
                if (g == null) continue;
                o.Geos.Add(g); break;
            }
            if (o.Geos.Count == 0)
                throw new InvalidOperationException("double row " + spec.Name + ": no L1 output cell on side " + c.Inner + " for machine " + spec.MachineItem + " (column " + c.Tag + ")");
            c.Reqs.Add(o);
        }

        // ---- throughput: extra sorters on free laterals of the same lane (same column), else report
        if (spec.Data == null) spec.Notes.Add("sorter throughput not checked (RowSpec.Data is null)");
        else
            foreach (Column c in cols)
                foreach (Req r in c.Reqs)
                {
                    double cap = spec.Data.SorterRate(spec.SorterItem, r.L);
                    if (cap <= 0) { spec.Problems.Add("double row " + spec.Name + ": no sorter rate for item " + spec.SorterItem + " L" + r.L); continue; }
                    int need = (int)Math.Ceiling(r.PerMachine / cap - 1e-9);
                    while (r.Geos.Count < need)
                    {
                        SorterGeo extra = null;
                        foreach (int lat in new[] { -1, 0, 1 })
                        {
                            if (c.Reqs.Any(q => q.Side == r.Side && q.Geos.Any(qg => qg.Lateral == lat))) continue;
                            SorterGeo g = Lookup(spec.MachineItem, r.In, r.Side, r.L, lat);
                            if (g == null || g.BeltDx != r.Geos[0].BeltDx) continue;
                            extra = g; break;
                        }
                        if (extra == null) break;
                        r.Geos.Add(extra);
                    }
                    if (r.Geos.Count < need)
                        spec.Problems.Add(string.Format(CultureInfo.InvariantCulture,
                            "double row {0} column {1}: {2} item {3} needs {4:0.##}/min per machine, {5} sorter(s) L{6} carry {7:0.##}/min",
                            spec.Name, c.Tag, r.Role, r.Item, r.PerMachine, r.Geos.Count, r.L, cap * r.Geos.Count));
                    else if (r.Geos.Count > 1 && c.IsA) spec.Notes.Add(r.Role + ": " + r.Geos.Count + " sorters per machine for throughput");
                }
        foreach (Column c in cols)
            foreach (Req r in c.Reqs)
                foreach (SorterGeo g in r.Geos)
                    if (!g.Proven) spec.Notes.Add("column " + c.Tag + " " + r.Role + " uses unproven cell " + g.Name + " [" + g.Status + "] - needs an in-game test" + (g.Note != null ? " (" + g.Note + ")" : ""));

        bool onlyL1 = cols.All(c => c.Reqs.All(r => r.L == 1));
        int pitch = ChoosePitch(spec, onlyL1 ? 3 : 4);

        // ---- build in the Flow-N frame
        var f = new Fragment(spec.Name);
        int nMax = cols.Max(c => c.Count);
        int y0 = -1, y1 = (nMax - 1) * pitch + 1;              // shared lane covers every machine's footprint rows
        var lanes = new Dictionary<int, List<Bld>>();          // lane x -> tiles (index = y - laneStart[x])
        var laneStart = new Dictionary<int, int>();
        var laneItem = new Dictionary<int, int>();
        // COATERS HOOK: coater line of a proliferated row (Coaters.cs cell COATER); coated lanes start lower
        int coatY = Coaters.RowCoaterY(spec, cols.SelectMany(c => c.Reqs).Where(r => r.In && Coaters.Coats(spec, r.Item)).Select(r => r.Geos.Min(g => g.BeltDy)));

        // shared output lane
        {
            string chain = spec.Name + ":out:" + spec.OutputItem;
            var tiles = new List<Bld>();
            for (int y = y0; y <= y1; y++)
            {
                Bld b = f.Add(Bld.Belt(spec.BeltItem, 0, y, 0, spec.OutputItem, chain));
                b.Role = spec.Name + " out lane (shared)";
                if (tiles.Count > 0) Layout.Link(tiles[tiles.Count - 1], b, 1);
                tiles.Add(b);
            }
            Layout.Link(tiles[tiles.Count - 1], null, 0);
            lanes[0] = tiles; laneStart[0] = y0;
            f.AddPort(new Port { Name = "out:" + spec.OutputItem, Kind = PortKind.Out, X = 0, Y = y1, Dir = Dir.N, Item = spec.OutputItem,
                Rate = spec.OutputRate, Belt = tiles[tiles.Count - 1], Chain = chain });
        }
        // input lanes per column
        foreach (Column c in cols)
            foreach (Req r in c.Reqs.Where(q => q.In))
            {
                int lx = c.X + r.Geos[0].BeltDx;
                if (lanes.ContainsKey(lx)) throw new InvalidOperationException("double row " + spec.Name + ": two lanes on x=" + lx);
                string suffix = c.IsA ? "" : PortSuffixB;
                string chain = spec.Name + ":in:" + r.Item + suffix;
                // lanes end at the column's last pickup tile (no dead-end stub, as MachineRow; the sheet runs one tile further)
                int yEnd = (c.Count - 1) * pitch + r.Geos.Max(g => g.BeltDy);
                int yStart = y0;
                bool tail = spec.TailItems != null && spec.TailItems.Contains(r.Item);
                if (tail) yStart = Math.Min(y0, r.Geos.Min(g => g.BeltDy) - 1);
                yStart = Coaters.LaneStart(spec, r.Item, tail, yStart, coatY);   // COATERS HOOK
                laneStart[lx] = yStart; laneItem[lx] = r.Item;
                var tiles = new List<Bld>();
                for (int y = yStart; y <= yEnd; y++)
                {
                    Bld b = f.Add(Bld.Belt(spec.BeltItem, lx, y, 0, r.Item, chain));
                    b.Role = spec.Name + " " + c.Tag + " " + r.Role + " lane";
                    if (tiles.Count > 0) Layout.Link(tiles[tiles.Count - 1], b, 1);
                    tiles.Add(b);
                }
                Layout.Link(tiles[tiles.Count - 1], null, 0);
                if (spec.LaneIcons) tiles[0].Parameters = new[] { r.Item, 0 };
                lanes[lx] = tiles;
                f.AddPort(new Port { Name = "in:" + r.Item + suffix, Kind = PortKind.In, X = lx, Y = yStart, Dir = Dir.N, Item = r.Item,
                    Rate = spec.InputRates != null ? spec.InputRates[r.Index] * c.Count / spec.Count : 0, Belt = tiles[0], Chain = chain });
            }
        // machines and sorters
        foreach (Column c in cols)
            for (int k = 0; k < c.Count; k++)
            {
                int my = k * pitch;
                Bld m = f.Add(Bld.Machine(spec.MachineItem, c.X, my, 0, spec.RecipeId));
                m.Carries = spec.OutputItem; m.Role = spec.Name + " machine " + c.Tag + (k + 1);
                foreach (Req r in c.Reqs)
                    foreach (SorterGeo g in r.Geos)
                    {
                        int lx = c.X + g.BeltDx;
                        Bld belt = lanes[lx][my + g.BeltDy - laneStart[lx]];
                        Bld s = g.Make(m, belt, spec.SorterItem, r.Item);
                        s.Role = spec.Name + " " + c.Tag + " " + r.Role + " L" + r.L + " (" + g.Name + ")";
                        f.Add(s);
                    }
            }

        Coaters.AddToRow(spec, f, coatY, lanes, laneStart, laneItem, signA);   // COATERS HOOK

        spec.Notes.Add("double row: " + cols[0].Count + " + " + cols[1].Count + " machines in two facing columns, pitch " + pitch
            + ", shared output lane, input lanes " + string.Join(",", lanes.Keys.Where(x => x != 0).OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())
            + " (pattern sheet " + SheetFile + ")");

        int quarters = (int)spec.Flow;
        if (quarters != 0) f.RotateCW(quarters, 0, 0);
        return f;
    }

    /// Pitch along the columns: what the sorters need (3 with only L1, 4 with L2) and the collider pitch (Modules.ColliderPitch;
    /// sheet: smelters 3, assemblers 4), else the proven pitches (Modules.ProvenPitches). Same rule as MachineRow.
    static int ChoosePitch(RowSpec spec, int need)
    {
        int col = Modules.ColliderPitch(spec.Data, spec.MachineItem, 90.0 * spec.FinalQuarters, spec.AlongEW, spec.Safe60);
        if (col > 0)
        {
            int min = Math.Max(need, col);
            if (spec.Pitch > 0)
            {
                if (spec.Pitch < min) throw new ArgumentException("double row " + spec.Name + ": pitch " + spec.Pitch + " < " + min + " (sorters need " + need + ", collider pitch " + col + ")");
                return spec.Pitch;
            }
            return min;
        }
        int[] ok = Modules.ProvenPitches(spec.MachineItem);
        if (spec.Pitch > 0)
        {
            if (spec.Pitch < need || !ok.Contains(spec.Pitch))
                throw new ArgumentException("double row " + spec.Name + ": pitch " + spec.Pitch + " is not a proven pitch >= " + need + " for machine " + spec.MachineItem);
            return spec.Pitch;
        }
        foreach (int p in ok.OrderBy(p => p)) if (p >= need) return p;
        throw new ArgumentException("double row " + spec.Name + ": no proven machine pitch >= " + need + " for machine " + spec.MachineItem);
    }

    // ================================================================ ports (Composer side)
    /// Column B input lane port of a double row ("in:<item>#B").
    public static bool IsSecondLane(Port p)
    {
        return p != null && p.Kind == PortKind.In && p.Name != null && p.Name.StartsWith("in:", StringComparison.Ordinal) && p.Name.EndsWith(PortSuffixB, StringComparison.Ordinal);
    }

    /// Every input lane port of a fragment for an item: "in:<item>" (MachineRow: the only one) and "in:<item>#B".
    public static List<Port> InPorts(Fragment f, int item)
    {
        var l = new List<Port>();
        if (f == null) return l;
        string a = "in:" + item, b = a + PortSuffixB;
        foreach (Port p in f.Ports) if (p.Name == a) { l.Add(p); break; }
        foreach (Port p in f.Ports) if (p.Name == b) { l.Add(p); break; }
        return l;
    }

    // ================================================================ Composer side
    /// A Composer row becomes a double row when: enabled, 1-2 inputs, >= MinCount machines, a supported machine class,
    /// every input is a block input lane (spec.TailItems covers them: the Composer feeds two tails per item, nothing routes
    /// a producer to two lane ports) and the whole row's full-speed output fits the shared lane (beltRate; 0 = unknown).
    public static bool Applies(RowSpec spec, double beltRate)
    {
        if (!Enabled || spec == null || spec.Count < MinCount) return false;
        if (spec.InputItems == null || spec.InputItems.Length < 1 || spec.InputItems.Length > 2) return false;
        if (!Supports(spec.MachineItem)) return false;
        if (spec.TailItems == null || spec.InputItems.Any(it => !spec.TailItems.Contains(it))) return false;
        if (beltRate > 0 && beltRate < double.MaxValue)
        {
            double outFull = spec.OutputRate;
            if (spec.Data != null)
            {
                RecipeDef rd = spec.Data.Recipe(spec.RecipeId);
                if (rd != null && rd.TimeSec > 0) outFull = Math.Max(outFull, spec.Count * rd.OutCount(spec.OutputItem) * 60.0 * spec.Data.Speed(spec.MachineItem) / rd.TimeSec);
            }
            if (outFull > beltRate + 1e-9) return false;
        }
        return true;
    }

    /// Composer entry: the double row when wanted (mode bit) and applicable, else Modules.MachineRow. A double row that
    /// cannot be built (no cell combination) falls back to MachineRow with a note.
    public static Fragment BuildRow(RowSpec spec, bool wantDouble, double beltRate)
    {
        if (wantDouble && Applies(spec, beltRate))
        {
            string why = null;
            try { return DoubleRow(spec); }
            catch (InvalidOperationException e) { why = e.Message; }
            Fragment f = Modules.MachineRow(spec);
            spec.Notes.Add("double row not possible, plain row built: " + why);
            return f;
        }
        return Modules.MachineRow(spec);
    }

    /// True when a plan has a step a double row could serve (1-2 inputs, all from block input lanes, >= MinCount machines,
    /// supported machine): the Composer then tries its row modes once more with the double-row bit.
    public static bool PlanHasCandidate(ProductionPlan plan)
    {
        if (!Enabled || plan == null) return false;
        foreach (Step s in plan.Steps)
        {
            if (s.Recipe == null || s.Machines < MinCount || s.Inputs.Count < 1 || s.Inputs.Count > 2 || !Supports(s.MachineItem)) continue;
            if (s.Inputs.Any(x => !s.IsExternal(plan, x.Item))) continue;
            return true;
        }
        return false;
    }
}
}
