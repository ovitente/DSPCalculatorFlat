// Cells.cs - named unit cells of the block generator. Owner: cells agent (lib\CONTRACT.md).
// Two parts:
//  * SorterGeo / Geo / BeltGeo / SplitterGeo: pure geometry tables (no game DLL, used by Modules). Every number is
//    copied from a user blueprint (relative to the machine center, world frame, at the cell's native machine yaw) and
//    every cell names its source record. Cells.Load re-reads those records and self-checks the table.
//  * PrototypeCells (Api.cs): plain-value prototype records (BpBuilding) for the Emitter. The CLI host fills them from
//    the user's blueprints through the game DLL (DspFlat.Host.Cells, engine.host\GameCells.cs); the addon from LDB.
// Status of a cell: "game"     = record of a blueprint built in the game (user template or in-game confirmed block),
//                   "code"     = record of a generated blueprint validated by code only (coil-360-v3, assemblers-8x4),
//                   "derived"  = reversed proven input cell (output sorter on the same segment), no blueprint has it,
//                   "inferred" = proven geometry with the machine yaw changed and the slot recomputed (3x3 machines).
// Find returns "game" and "code" cells only; FindAny also returns "derived" and "inferred" cells (needs in-game test).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DspFlat.Engine
{

/// One sorter placement relative to a machine center, world frame, for a machine at MachineYaw.
public class SorterGeo
{
    public string Name;              // e.g. "smelter y0 in E L1 lat-1"
    public int MachineItem;          // geometry is per machine class (edge 0.79-0.84 smelter, 0.84-0.89 assembler)
    public bool BeltToMachine;       // true = input sorter
    public int BeltDx, BeltDy;       // belt tile relative to the machine center
    public double Sx, Sy, Ex, Ey;    // sorter start/end relative to the machine center
    public double Yaw;               // direction start -> end (0/90/180/270)
    public int Slot;                 // machine slot in the machine's LOCAL frame (patterns.md section 0)
    public int Length;               // sorter grid length 1..3
    public string Source;            // blueprint (and record) the numbers come from

    // ---- extra metadata (cells agent) ----
    public double MachineYaw;        // machine yaw this geometry belongs to; Make requires the same yaw
    public string Status = "game";   // game | code | derived | inferred (see file header)
    public string SourceKey;         // Cells source key ("arc1", "v3", ...) for the self-check; null = not checkable
    public int SourceIndex = -1;     // sorter record index in that source
    public string Pattern;           // patterns.md module id, e.g. "SMELT-COL-1"
    public string Note;

    public bool Proven { get { return Status == "game" || Status == "code"; } }
    /// World side of the machine the belt is on.
    public Dir Side { get { return Math.Abs(BeltDx) >= Math.Abs(BeltDy) ? (BeltDx > 0 ? Dir.E : Dir.W) : (BeltDy > 0 ? Dir.N : Dir.S); } }
    /// Belt tile offset along the side (dy for E/W sides, dx for N/S sides): -1, 0 or +1.
    public int Lateral { get { Dir s = Side; return s == Dir.E || s == Dir.W ? BeltDy : BeltDx; } }
    /// World face of the machine the sorter crosses (the slot's outward direction): opposite of travel for an input,
    /// along travel for an output (analysis\buildings\slots.md section 5: choose the slot by face, then lateral).
    public Dir Face
    {
        get
        {
            double vx = Ex - Sx, vy = Ey - Sy;
            Dir travel = Math.Abs(vx) >= Math.Abs(vy) ? (vx > 0 ? Dir.E : Dir.W) : (vy > 0 ? Dir.N : Dir.S);
            return BeltToMachine ? Dirs.Opposite(travel) : travel;
        }
    }

    public SorterGeo Copy() { return (SorterGeo)MemberwiseClone(); }

    /// Rigid rotation by q quarter turns clockwise (machine, belt and sorter turn together; slot unchanged).
    public SorterGeo Rotated(int q)
    {
        q = ((q % 4) + 4) % 4;
        SorterGeo g = Copy();
        for (int i = 0; i < q; i++)
        {
            int bx = g.BeltDx; g.BeltDx = g.BeltDy; g.BeltDy = -bx;
            double t = g.Sx; g.Sx = g.Sy; g.Sy = -t;
            t = g.Ex; g.Ex = g.Ey; g.Ey = -t;
        }
        g.Yaw = Dirs.Norm(Yaw + 90.0 * q);
        g.MachineYaw = Dirs.Norm(MachineYaw + 90.0 * q);
        if (q != 0) g.Name = Name + " rot" + (90 * q);
        return g;
    }

    /// The opposite direction on the same segment (input -> output). Status becomes "derived".
    public SorterGeo Reversed()
    {
        SorterGeo g = Copy();
        g.BeltToMachine = !BeltToMachine;
        g.Sx = Ex; g.Sy = Ey; g.Ex = Sx; g.Ey = Sy;
        g.Yaw = Dirs.Norm(Yaw + 180.0);
        g.Status = "derived"; g.SourceKey = null; g.SourceIndex = -1;
        g.Note = "reversed " + Name + " (" + Source + "); not seen in any blueprint";
        g.Name = Geo.MakeName(g);
        return g;
    }

    /// Same world geometry for a machine at another yaw, slot recomputed (3x3 symmetric machines only). "inferred".
    public SorterGeo ReYawed(double machineYaw)
    {
        SorterGeo g = Copy();
        g.MachineYaw = Dirs.Norm(machineYaw);
        double mx = BeltToMachine ? Ex : Sx, my = BeltToMachine ? Ey : Sy;
        g.Slot = Geo.SlotAt(g.MachineYaw, mx, my, Face);
        g.Status = "inferred";
        g.Note = "re-yawed " + Name + " to machine yaw " + g.MachineYaw + ", slot " + Slot + " -> " + g.Slot;
        g.Name = Geo.MakeName(g) + " (inferred)";
        return g;
    }

    /// Creates the sorter between machine m and belt b (b must sit on (m.X+BeltDx, m.Y+BeltDy), m.Yaw == MachineYaw).
    /// Belt offset = max(0, floor(20*d)), d =displacement of the belt-side end along the belt flow (patterns.md 0).
    public Bld Make(Bld machine, Bld belt, int sorterItem, int carries) { return Make(machine, belt, sorterItem, carries, false); }

    /// laneContinuesBehind: the belt tile has a predecessor along its flow. The game then stores a NEGATIVE output
    /// offset for an output sorter whose belt-side end lies before the tile centre (refs\patterns\p08-asm-row-3in.txt
    /// #5/#9/#13: d = -0.201 -> -5; the 3-input template #127: d = -0.002 -> -1), and 0 where the lane starts at
    /// that tile (p08 #17). Input sorters keep the clamp at 0 (coil-360-v3 assembler cells: d = -0.158, offset 0).
    public Bld Make(Bld machine, Bld belt, int sorterItem, int carries, bool laneContinuesBehind)
    {
        if (machine == null || belt == null) throw new ArgumentNullException(machine == null ? "machine" : "belt");
        if (belt.Kind != Kind.Belt) throw new ArgumentException(Name + ": belt argument is a " + belt.Kind);
        if (!Ids.IsSorter(sorterItem)) throw new ArgumentException(Name + ": item " + sorterItem + " is not a sorter");
        double dyaw = Math.Abs(((machine.Yaw - MachineYaw) % 360.0 + 540.0) % 360.0 - 180.0);
        if (dyaw > 1.0)
            throw new InvalidOperationException(Name + ": machine yaw " + machine.Yaw + " but the cell is for yaw " + MachineYaw
                + " (use Geo.Find(..., machineYaw))");
        double mx = Math.Round(machine.X), my = Math.Round(machine.Y);
        if (Math.Abs(machine.X - mx) > 0.01 || Math.Abs(machine.Y - my) > 0.01)
            throw new InvalidOperationException(Name + ": machine not on a tile center: " + machine);
        if (belt.TileX != (int)mx + BeltDx || belt.TileY != (int)my + BeltDy || Math.Abs(belt.X - belt.TileX) > 0.01 || Math.Abs(belt.Y - belt.TileY) > 0.01)
            throw new InvalidOperationException(Name + ": belt at (" + belt.X + "," + belt.Y + ") but the cell needs (" + (mx + BeltDx) + "," + (my + BeltDy) + ")");
        double sx = mx + Sx, sy = my + Sy, ex = mx + Ex, ey = my + Ey;
        double bx = BeltToMachine ? sx : ex, by = BeltToMachine ? sy : ey;
        double fy = belt.Yaw * Math.PI / 180.0;
        double d = (bx - belt.X) * Math.Sin(fy) + (by - belt.Y) * Math.Cos(fy);
        int off = (int)Math.Floor(20.0 * d + 1e-6);
        if (BeltToMachine || !laneContinuesBehind) off = Math.Max(0, off);   // proven files store 0 for a negative input displacement
        Bld s = BeltToMachine
            ? Bld.Sorter(sorterItem, sx, sy, ex, ey, Yaw, belt, machine, Slot, Length, off, carries)
            : Bld.Sorter(sorterItem, sx, sy, ex, ey, Yaw, machine, belt, Slot, Length, off, carries);
        s.Role = Name;
        return s;
    }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture, "{0} [{1}] item {2} yaw {3} belt ({4},{5}) ({6:0.###},{7:0.###})->({8:0.###},{9:0.###}) dir {10} slot {11} L{12} src {13}",
            Name, Status, MachineItem, MachineYaw, BeltDx, BeltDy, Sx, Sy, Ex, Ey, Yaw, Slot, Length, Source);
    }
}

/// Registry of sorter geometries (no game DLL).
public static class Geo
{
    // source keys -> blueprint path relative to the Blueprint folder (alternatives separated by '|')
    public static readonly Dictionary<string, string> SourceFiles = new Dictionary<string, string>
    {
        { "arc1", @"t-tpl\Smelters\smelter-arc-1.txt" },
        { "arc2", @"t-tpl\Smelters\smelter-arc-2.txt" },
        { "arc3", @"t-tpl\Smelters\smelter-arc-3.txt" },
        { "a8x1", @"t-tpl\Assemblers\assemblers-8x1.txt|t-tpl\Assemblers\assemblers-8.txt" },
        { "a8x2", @"t-tpl\Assemblers\assemblers-8x2.txt|t-tpl\Assemblers\assemblers-8-2L.txt" },
        { "a8x4", @"t-tpl\Assemblers\assemblers-8x4.txt" },
        // distrubution\<2-input> / <3-input> assembler templates (Cyrillic file names, escaped)
        { "in2", "t-tpl\\Assemblers\\distrubution\\\u0421\u0431\u043e\u0440\u0449\u0438\u043a-2-\u0432\u0445\u043e\u0434\u0430.txt" },
        { "in3", "t-tpl\\Assemblers\\distrubution\\\u0421\u0431\u043e\u0440\u0449\u0438\u043a-3-\u0432\u0445\u043e\u0434\u0430.txt" },
        // "refs\..." = copies in blueprint-tools\refs (the user's START test folder is not a dependency)
        { "v3", @"refs\coil-360-v3.txt" },
        // engine output placed in game and re-saved by the user (2026-10-31): game-proven reference of the engine
        { "eng360", @"refs\engine-coil-360.txt" },
        { "split", @"refs\splitters.txt" },
        // user's in-game edit of engine-turbine-60-v2 (raised runs, depot buffer, splitter variants m39 / m40)
        { "turb2u", @"refs\engine-turbine-60-v2-userupgrade.txt" },
        { "lent", @"refs\lent-begin.txt" },
        { "mk3", @"t-tpl\Assemblers mk III.txt" },
        // the user's Blueprint folder was reorganised (2026-11): the old path first, the new location as the alternative
        { "plane", @"2-smelt\Smelters.txt|1-t1\Smelters.txt" },
        { "fuel", @"5-chem\Fuel.txt|1-t1\Fuel.txt" },
        // feat-labs: Matrix Lab tower cells (LabTowers.cs) and Miniature Particle Collider cells (Collider.cs)
        { LabTowers.SourceKey, LabTowers.SourceFile },
        { Collider.SourceKey, Collider.SourceFile },
        { Collider.SourceKey2, Collider.SourceFile2 },
        // pattern rows drawn by the user in game (docs\patterns-design.md P08 / P09): copies in blueprint-tools\refs\patterns,
        // originals in the user's Blueprint\t-tpl folder
        { "p08", @"refs\patterns\p08-asm-row-3in.txt|t-tpl\p08-asm-row-3in.txt" },
        { "p09", @"refs\patterns\p09-asm-row-4in.txt|t-tpl\p09-asm-row-4in.txt" },
    };

    static Dictionary<string, SorterGeo> table;
    static List<SorterGeo> ordered;

    /// All sorter cells (proven and unproven, native machine yaw), keyed by Name.
    public static Dictionary<string, SorterGeo> Sorters()
    {
        Build();
        var d = new Dictionary<string, SorterGeo>();
        foreach (var kv in table) d[kv.Key] = kv.Value.Copy();
        return d;
    }

    /// Cells in table (preference) order.
    public static List<SorterGeo> All() { Build(); var l = new List<SorterGeo>(); foreach (var g in ordered) l.Add(g.Copy()); return l; }

    /// PATTERNS HOOK (feat-pat-loader): external cells read from the user's pattern drawings (Patterns.RowCells).
    /// preferred = consulted before the table (Find/Search walk `ordered` in order), else after it. A cell whose Name is
    /// already registered replaces the earlier one. The built-in table itself is not touched.
    public static void Register(IEnumerable<SorterGeo> cells, bool preferred)
    {
        Build();
        if (cells == null) return;
        var add = new List<SorterGeo>(cells);
        for (int i = 0; i < add.Count; i++)
        {
            SorterGeo g = add[i];
            if (g == null || g.Name == null) continue;
            SorterGeo old;
            if (table.TryGetValue(g.Name, out old)) ordered.Remove(old);
            table[g.Name] = g;
            if (preferred) ordered.Insert(Math.Min(i, ordered.Count), g); else ordered.Add(g);
        }
    }

    /// Lookup by machine class, direction, side and length among the cells at their NATIVE machine yaw (the result's
    /// MachineYaw tells which yaw to give the machine, e.g. 90 for the coil-360-v3 assembler cells). Proven cells only.
    /// side: world side of the machine the belt is on; lateral: belt tile offset along that side (dy for E/W, dx for N/S).
    /// null if no proven cell exists.
    public static SorterGeo Find(int machineItem, bool beltToMachine, Dir side, int length, int lateral)
    {
        Build();
        foreach (int item in Candidates(machineItem))
            foreach (var g in ordered)
                if (g.Proven && g.MachineItem == item && Match(g, beltToMachine, side, length, lateral)) return Tag(g.Copy(), machineItem);
        return null;
    }

    /// Lookup for a machine at the given yaw: native cells at that yaw and rigid rotations of every proven cell
    /// (rotating a whole cell is what the game does with a pasted blueprint). Proven cells only; null if none.
    public static SorterGeo Find(int machineItem, bool beltToMachine, Dir side, int length, int lateral, double machineYaw)
    {
        return Search(machineItem, beltToMachine, side, length, lateral, machineYaw, false);
    }

    /// Like Find(..., machineYaw) but falls back to unproven cells: "derived" (reversed inputs) and "inferred"
    /// (re-yawed). Check SorterGeo.Status / Proven on the result.
    public static SorterGeo FindAny(int machineItem, bool beltToMachine, Dir side, int length, int lateral, double machineYaw)
    {
        return Search(machineItem, beltToMachine, side, length, lateral, machineYaw, true);
    }

    static SorterGeo Search(int machineItem, bool beltToMachine, Dir side, int length, int lateral, double machineYaw, bool any)
    {
        Build();
        double my = Dirs.Norm(machineYaw);
        if (Math.Abs(my / 90.0 - Math.Round(my / 90.0)) > 0.01) return null;
        // pass 1: proven (native + rigid rotations); pass 2: derived; pass 3: inferred re-yaw of proven + derived
        for (int pass = 1; pass <= (any ? 3 : 1); pass++)
            foreach (int item in Candidates(machineItem))
                foreach (var g in ordered)
                {
                    if (g.MachineItem != item) continue;
                    if (pass == 1 && !g.Proven) continue;
                    if (pass == 2 && g.Status != "derived") continue;
                    if (pass == 3)
                    {
                        if (!Square(item)) continue;
                        SorterGeo r = g.ReYawed(my);
                        if (Match(r, beltToMachine, side, length, lateral)) return Tag(r, machineItem);
                        continue;
                    }
                    int q = (int)Math.Round(Dirs.Norm(my - g.MachineYaw) / 90.0) % 4;
                    SorterGeo c = g.Rotated(q);
                    if (Match(c, beltToMachine, side, length, lateral)) return Tag(c, machineItem);
                }
        return null;
    }

    static bool Match(SorterGeo g, bool beltToMachine, Dir side, int length, int lateral)
    {
        return g.BeltToMachine == beltToMachine && g.Side == side && g.Length == length && g.Lateral == lateral;
    }

    static SorterGeo Tag(SorterGeo g, int machineItem)
    {
        if (g.MachineItem != machineItem)
        {
            g.Note = (g.Note == null ? "" : g.Note + "; ") + "geometry of machine " + g.MachineItem + " used for " + machineItem + " (same class)";
            g.MachineItem = machineItem;
        }
        return g;
    }

    /// Exact item first, then the other members of its class (same 3x3 footprint).
    static int[] Candidates(int item)
    {
        if (item == Ids.ArcSmelter) return new[] { Ids.ArcSmelter, Ids.PlaneSmelter };
        if (item == Ids.PlaneSmelter) return new[] { Ids.PlaneSmelter, Ids.ArcSmelter };
        if (item == Ids.AsmMk1) return new[] { Ids.AsmMk1, Ids.AsmMk2, Ids.AsmMk3 };
        if (item == Ids.AsmMk2) return new[] { Ids.AsmMk2, Ids.AsmMk1, Ids.AsmMk3 };
        if (item == Ids.AsmMk3) return new[] { Ids.AsmMk3, Ids.AsmMk2, Ids.AsmMk1 };
        return new[] { item };
    }

    static bool Square(int item)
    {
        return item == Ids.ArcSmelter || item == Ids.PlaneSmelter || item == Ids.AsmMk1 || item == Ids.AsmMk2 || item == Ids.AsmMk3;
    }

    /// Machine slot implied by a point (dx,dy) relative to a 3x3 machine center (world frame) for a machine at
    /// machineYaw: 3x3 table, clockwise from the NW corner, in the machine's local frame (patterns.md section 0).
    /// Corner points belong to the face they are closest to (as MakeAssemblers8x4.SlotAt). AMBIGUOUS at smelter-class
    /// corners (slots 3/5/9/11 sit on the same point as 2/6/8/0, analysis\buildings\slots.md section 5: 16 of 48 wrong
    /// for exact game coordinates); use the overload with the sorter's face whenever the direction is known.
    public static int SlotAt(double machineYaw, double dx, double dy)
    {
        double a = machineYaw * Math.PI / 180.0;
        double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
        if (Math.Abs(ly) >= Math.Abs(lx))
        {
            if (ly > 0) return lx < -0.4 ? 0 : lx > 0.4 ? 2 : 1;
            return lx > 0.4 ? 6 : lx < -0.4 ? 8 : 7;
        }
        if (lx > 0) return ly > 0.4 ? 3 : ly < -0.4 ? 5 : 4;
        return ly < -0.4 ? 9 : ly > 0.4 ? 11 : 10;
    }

    /// Slot by the sorter direction first (worldFace = the machine face the sorter crosses, i.e. the slot's outward
    /// direction), then by the lateral offset along that face (|lateral| > 0.4 = corner slot). Exact at every corner.
    public static int SlotAt(double machineYaw, double dx, double dy, Dir worldFace)
    {
        double a = machineYaw * Math.PI / 180.0;
        double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
        Dir face = Dirs.RotateCW(worldFace, -(int)Math.Round(Dirs.Norm(machineYaw) / 90.0));
        switch (face)
        {
            case Dir.N: return lx < -0.4 ? 0 : lx > 0.4 ? 2 : 1;
            case Dir.E: return ly > 0.4 ? 3 : ly < -0.4 ? 5 : 4;
            case Dir.S: return lx > 0.4 ? 6 : lx < -0.4 ? 8 : 7;
            default: return ly < -0.4 ? 9 : ly > 0.4 ? 11 : 10;
        }
    }

    internal static string MakeName(SorterGeo g)
    {
        string m = g.MachineItem == Ids.ArcSmelter ? "smelter" : g.MachineItem == Ids.PlaneSmelter ? "plane" :
                   g.MachineItem == Ids.AsmMk1 ? "asm1" : g.MachineItem == Ids.AsmMk2 ? "asm2" : g.MachineItem == Ids.AsmMk3 ? "asm3" : g.MachineItem.ToString();
        return m + " y" + (int)Math.Round(g.MachineYaw) + " " + (g.BeltToMachine ? "in" : "out") + " " + g.Side + " L" + g.Length
            + " lat" + (g.Lateral > 0 ? "+" : "") + g.Lateral;
    }

    static void Add(int item, double myaw, bool toMachine, int bdx, int bdy, double sx, double sy, double ex, double ey,
                    int slot, int len, string status, string key, int idx, string pattern, string note)
    {
        Add(item, myaw, toMachine, bdx, bdy, sx, sy, ex, ey, slot, len, status, key, idx, pattern, note, null);
    }

    /// nameTag: appended to the generated name when the same geometry class (side / length / lateral) already has a
    /// cell from another blueprint (e.g. " (p08)"); lookups match by geometry, so the first cell in table order wins.
    static void Add(int item, double myaw, bool toMachine, int bdx, int bdy, double sx, double sy, double ex, double ey,
                    int slot, int len, string status, string key, int idx, string pattern, string note, string nameTag)
    {
        var g = new SorterGeo
        {
            MachineItem = item, MachineYaw = myaw, BeltToMachine = toMachine, BeltDx = bdx, BeltDy = bdy,
            Sx = sx, Sy = sy, Ex = ex, Ey = ey, Slot = slot, Length = len, Status = status, SourceKey = key, SourceIndex = idx,
            Pattern = pattern, Note = note
        };
        g.Yaw = Dirs.Yaw(Dirs.FromYaw(Math.Atan2(ex - sx, ey - sy) * 180.0 / Math.PI));
        g.Source = SourceFiles[key].Split('|')[0] + " #" + idx;
        g.Name = MakeName(g) + (nameTag ?? "");
        if (table.ContainsKey(g.Name)) throw new InvalidOperationException("duplicate sorter cell " + g.Name);
        table[g.Name] = g; ordered.Add(g);
    }

    static void Build()
    {
        if (table != null) return;
        table = new Dictionary<string, SorterGeo>(); ordered = new List<SorterGeo>();
        const int Sm = Ids.ArcSmelter, A1 = Ids.AsmMk1, A2 = Ids.AsmMk2;
        const bool IN = true, OUT = false;

        // ---- arc smelter, yaw 0 (patterns.md SMELT-COL-1, SMELT-COL-PAIR-1/2, COL-PAIR-3 smelter variant) ----
        Add(Sm, 0, IN, 2, -1, 2, -0.797, 0.811, -0.795, 5, 1, "game", "arc1", 40, "SMELT-COL-1", "same numbers in coil-360-v3 #119");
        Add(Sm, 0, OUT, -2, 0, -0.811, 0, -2, 0, 10, 1, "game", "arc1", 42, "SMELT-COL-1", "same numbers in coil-360-v3 #120");
        Add(Sm, 0, IN, -2, 1, -2, 0.793, -0.812, 0.795, 11, 1, "game", "arc1", 37, "SMELT-COL-1 mirror", null);
        Add(Sm, 0, OUT, 2, 0, 0.811, 0, 2, 0, 4, 1, "game", "arc1", 33, "SMELT-COL-1 mirror", null);
        Add(Sm, 0, IN, 2, 1, 2, 0.792, 0.818, 0.795, 3, 1, "game", "arc2", 36, "SMELT-COL-PAIR-2", null);
        Add(Sm, 0, IN, 3, 0, 3, -0.006, 0.817, 0, 4, 2, "game", "arc2", 13, "SMELT-COL-PAIR-2", "L2 lane behind an L1 lane (rule 2)");
        Add(Sm, 0, IN, -2, -1, -2, -0.798, -0.815, -0.795, 9, 1, "game", "arc2", 10, "SMELT-COL-PAIR-2", null);
        Add(Sm, 0, IN, -3, 0, -3, -0.006, -0.816, 0, 10, 2, "game", "arc2", 29, "SMELT-COL-PAIR-2", "L2 lane behind an L1 lane (rule 2)");
        Add(Sm, 0, IN, -4, 1, -4, 0.78, -0.841, 0.794, 11, 3, "game", "arc3", 14, "COL-PAIR-3 (smelter-arc-3 right column)", null);
        // smelter-arc-3 left column is yaw 180 (local slots 9/10/11 face east); template left column has no output sorter
        Add(Sm, 180, IN, 4, -1, 4, -0.81, 0.836, -0.796, 11, 3, "game", "arc3", 13, "COL-PAIR-3 (smelter-arc-3 left column)", "machine yaw 180; template column unfinished (no output)");

        // ---- assembler Mk.I, yaw 90 (coil-360 layout: belts flow N, lanes east/west) ----
        Add(A1, 90, IN, 2, -1, 2, -0.842, 0.874, -0.841, 2, 1, "game", "v3", 155, "coil-360 asm (ROW-2IN-FACING row A near, rotated CW)",
            "same cell built in game in coil-360-v2; numbers from coil-360-v3");
        Add(A1, 90, IN, -2, 1, -2, 0.842, -0.874, 0.841, 8, 1, "game", "v3", 156, "coil-360-v3 asm west lane",
            "built in game in refs\\engine-coil-360.txt (#42, slot 8; the game re-snapped the ends to dy 0.796/0.795)");
        Add(A1, 90, OUT, -3, 0, -0.874, 0, -3, 0, 7, 2, "game", "v3", 157, "coil-360-v3 asm output",
            "built in game in refs\\engine-coil-360.txt (#41, slot 7, L2)");
        // ---- assembler Mk.I, yaw 0 (rows, belts flow E/W, lanes north/south) ----
        Add(A1, 0, IN, 1, 2, 0.795, 2, 0.795, 0.874, 2, 1, "game", "a8x2", 19, "ROW-2IN-OWN-BELTS", null);
        Add(A1, 0, IN, 0, 3, 0, 3, 0, 0.874, 1, 2, "game", "a8x2", 16, "ROW-2IN-OWN-BELTS", "L2 lane behind an L1 lane (rule 2)");
        Add(A1, 0, IN, 1, -2, 0.795, -2, 0.795, -0.874, 6, 1, "game", "a8x2", 38, "ROW-2IN-OWN-BELTS", null);
        Add(A1, 0, IN, 0, -3, 0, -3, 0, -0.874, 7, 2, "game", "a8x2", 25, "ROW-2IN-OWN-BELTS", "L2 lane behind an L1 lane (rule 2)");
        Add(A1, 0, OUT, -1, 2, -0.795, 0.875, -0.795, 2, 0, 1, "game", "a8x2", 30, "ROW-PAIR-SHARED-OUT", null);
        Add(A1, 0, OUT, 1, -2, 0.795, -0.875, 0.795, -2, 6, 1, "game", "a8x2", 32, "ROW-PAIR-SHARED-OUT", null);
        Add(A1, 0, IN, 0, -2, 0, -2, 0, -0.874, 7, 1, "game", "a8x1", 15, "ROW-PAIR-SHARED-OUT", null);
        Add(A1, 0, IN, 0, 2, 0, 2, 0, 0.874, 1, 1, "game", "a8x1", 16, "ROW-PAIR-SHARED-OUT", null);
        Add(A1, 0, OUT, -1, 4, -0.798, 0.875, -0.797, 4, 0, 3, "code", "a8x4", 4, "assemblers-8x4 output", "L1 output stretched to L3; validated by code only");

        // ---- assembler Mk.II, yaw 0 (Sorter Mk.II / Mk.III in the source; geometry is tier independent) ----
        Add(A2, 0, IN, 1, 2, 0.842, 2, 0.841, 0.874, 2, 1, "game", "in2", 27, "ROW-2IN-FACING row A", null);
        Add(A2, 0, IN, 0, 3, 0, 3, 0, 0.874, 1, 2, "game", "in2", 21, "ROW-2IN-FACING row A", "far lane; coil-360-v2 used it rotated (in game)");
        Add(A2, 0, OUT, 0, -2, 0, -0.875, 0, -2, 7, 1, "game", "in2", 102, "ROW-2IN-FACING row A", null);
        Add(A2, 0, IN, -1, -2, -0.846, -2, -0.847, -0.875, 8, 1, "game", "in2", 14, "ROW-2IN-FACING row B", null);
        Add(A2, 0, IN, 0, -3, 0, -3, 0, -0.874, 7, 2, "game", "in2", 19, "ROW-2IN-FACING row B", null);
        Add(A2, 0, OUT, 0, 2, 0, 0.874, 0, 2, 1, 1, "game", "in2", 100, "ROW-2IN-FACING row B", null);
        Add(A2, 0, IN, 2, 1, 2, 0.792, 0.886, 0.795, 3, 1, "game", "in3", 3, "COL-PAIR-3 left", "Sorter Mk.III in the source");
        Add(A2, 0, IN, 3, 0, 3, -0.008, 0.885, 0, 4, 2, "game", "in3", 11, "COL-PAIR-3 left", "Sorter Mk.III in the source");
        Add(A2, 0, IN, 4, -1, 4, -0.81, 0.885, -0.795, 5, 3, "game", "in3", 5, "COL-PAIR-3 left", "Sorter Mk.III in the source");
        Add(A2, 0, OUT, -2, 0, -0.885, 0, -2, -0.002, 10, 1, "game", "in3", 127, "COL-PAIR-3 left", "Sorter Mk.III in the source");
        Add(A2, 0, IN, -4, 1, -4, 0.781, -0.881, 0.795, 11, 3, "game", "in3", 19, "COL-PAIR-3 right", "Sorter Mk.III in the source");
        Add(A2, 0, IN, -3, 0, -3, -0.007, -0.88, 0, 10, 2, "game", "in3", 24, "COL-PAIR-3 right", "Sorter Mk.III in the source");
        Add(A2, 0, IN, -2, -1, -2, -0.798, -0.88, -0.795, 9, 1, "game", "in3", 31, "COL-PAIR-3 right", "Sorter Mk.III in the source");
        Add(A2, 0, OUT, 2, 0, 0.88, 0, 2, -0.001, 4, 1, "game", "in3", 1, "COL-PAIR-3 right", "Sorter Mk.III in the source");

        // ======== PATTERN ROWS (rows agent, docs\patterns-design.md): keep this region at the END of the table ========
        // ---- P08 refs\patterns\p08-asm-row-3in.txt: 4 Assembler Mk.II yaw 0, Electric Motor, lanes flow N, pitch 4.
        // Numbers are relative to the first record's machine (#0 at (4,17)). West: x2 L1 in (slot 9), x1 L2 in (slot 10),
        // x0 L3 OUT (slot 11); east: x6 L1 in (slot 5). Same geometry class as the COL-PAIR-3 cells above where the
        // names collide (" (p08)" tag): those stay first in table order, the drawn row is the proof of the 3-input preset.
        Add(A2, 0, IN, -2, -1, -2, -0.794, -0.878, -0.795, 9, 1, "game", "p08", 8, "ROW-3IN (P08) west L1", "Sorter Mk.II; inputOffset 4", " (p08)");
        Add(A2, 0, IN, -3, 0, -3, 0.002, -0.877, 0, 10, 2, "game", "p08", 6, "ROW-3IN (P08) west L2", "Sorter Mk.II; inputOffset 0", " (p08)");
        Add(A2, 0, OUT, -4, 1, -0.877, 0.795, -4, 0.799, 11, 3, "game", "p08", 5, "ROW-3IN (P08) west L3 output",
            "output beyond two input lanes; outputOffset -5 (0 at the first machine, where the lane starts)");
        Add(A2, 0, IN, 2, -1, 2, -0.794, 0.878, -0.795, 5, 1, "game", "p08", 7, "ROW-3IN (P08) east L1", "Sorter Mk.II; inputOffset 4");
        // ---- P09 refs\patterns\p09-asm-row-4in.txt: same frame, Missile Set, plus east x7 L2 in (slot 4).
        Add(A2, 0, IN, 3, 0, 3, 0.002, 0.877, 0, 4, 2, "game", "p09", 6, "ROW-4IN (P09) east L2", "Sorter Mk.II; inputOffset 0", " (p09)");
        // ======== END PATTERN ROWS ========

        // ---- derived outputs: every L2/L3 input reversed, unless a proven output with the same key exists ----
        var proven = new List<SorterGeo>(ordered);
        foreach (var g in proven)
        {
            if (!g.BeltToMachine || g.Length < 2) continue;
            SorterGeo r = g.Reversed();
            if (table.ContainsKey(r.Name)) continue;
            table[r.Name] = r; ordered.Add(r);
        }
    }
}

/// Belt pieces (no game DLL). Straight: yaw = flow. Corner: yaw = bisector of in and out flow.
/// Proof: every template; corners in refs\coil-360-v3 magnet chain ((6,27) N->W 315, (4,27) W->S 225).
public static class BeltGeo
{
    /// Side-join (rule 6): joining chain's last tile Output = target, OutputToSlot 2 (3 when 2 is taken).
    /// Proof: refs\splitters.txt #33 (2,9) flow N -> #30 (2,10) flow W, slot 2; #31 (3,10) -> #30 keeps slot 1.
    public const int SideMergeSlot = 2, SideMergeSlotAlt = 3, StraightSlot = 1;
    public const string SideMergeSource = @"refs\splitters.txt #33 -> #30";

    public static double FlowYaw(Dir flow) { return Dirs.Yaw(flow); }

    /// Corner yaw for a tile entered with inFlow and left with outFlow (bisector, e.g. N->E 45, N->W 315).
    public static double CornerYaw(Dir inFlow, Dir outFlow)
    {
        if (inFlow == outFlow) return Dirs.Yaw(inFlow);
        if (outFlow == Dirs.Opposite(inFlow)) throw new ArgumentException("U-turn on one belt tile: " + inFlow + " -> " + outFlow);
        double yIn = Dirs.Yaw(inFlow), yOut = Dirs.Yaw(outFlow);
        double diff = ((yOut - yIn + 540.0) % 360.0) - 180.0;
        return Dirs.Norm(yIn + diff / 2.0);
    }
}

/// Splitter cell from refs\splitters.txt (splitter yaw 0 at its tile; 3 instances, #2 is the priority variant).
/// Belt k (1 = touching the splitter) on side s sits at distance Dist(k) from the splitter center along s:
/// 0.199, 1.073 (z 0.003), then 2, 3, ... Incoming belt k=1: Output = splitter, OutputToSlot = SideSlot(s).
/// Outgoing belt k=1: Input = splitter, InputFromSlot = SideSlot(s), Output = belt k=2.
public static class SplitterGeo
{
    public const double NearDist = 0.199, SecondDist = 1.073, SecondZ = 0.003;
    public const string BeltCell = "splitter-belt";
    public const string Source = @"refs\splitters.txt (#0 (7,0): in E #3..#6, out N #10..#7, out W #18..#15; #1 (7,10): in S #22..#19; #2 priority)";

    /// Splitter side slot: N 0, E 1, S 2, W 3 (proof: in E -> slot 1, in S -> slot 2, out N from 0, out W from 3).
    public static int SideSlot(Dir side) { return (int)side; }

    public static double Dist(int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException("k");
        return k == 1 ? NearDist : k == 2 ? SecondDist : k - 1;
    }

    /// Belt tile k on side s of the splitter. incoming: flows toward the splitter (yaw of the opposite side).
    /// Fractional tiles (k 1, 2) get Cell = "splitter-belt" so the Emitter keeps their offsets (no 0.5 snap).
    /// Links between consecutive belts are left to the caller (Layout.Link); only the splitter link is set here.
    public static Bld Belt(Bld splitter, Dir side, int k, bool incoming, int beltItem, int carries, string chain)
    {
        double d = Dist(k);
        double x = splitter.X + Dirs.Dx(side) * d, y = splitter.Y + Dirs.Dy(side) * d;
        Bld b = Bld.Belt(beltItem, x, y, Dirs.Yaw(incoming ? Dirs.Opposite(side) : side), carries, chain);
        if (k == 2) { b.Z = SecondZ; b.Z2 = SecondZ; }
        if (k <= 2) b.Cell = BeltCell;
        b.Role = "splitter " + (incoming ? "in " : "out ") + side + " #" + k;
        if (k == 1)
        {
            if (incoming) { b.Output = splitter; b.OutputToSlot = SideSlot(side); }
            else { b.Input = splitter; b.InputFromSlot = SideSlot(side); }
        }
        return b;
    }
}
}
