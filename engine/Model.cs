// Model.cs - shared contract of the block generator (no game DLL needed).
// Abstract layout objects (Bld, Port, Fragment, Grid, Layout), plan objects (Step, ProductionPlan),
// the block spec (BlockSpec, Tiers) and the validation report. Every other lib file builds on these types.
// Frame: blueprint local offsets in tile units, x = east, y = north; yaw clockwise from north (N=0, E=90, S=180, W=270).
// Rates are items per MINUTE everywhere unless a name says otherwise (e.g. PerSec).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DspFlat.Engine
{

// ------------------------------------------------------------------ ids
public static class Ids
{
    // logistics
    public const int BeltMk1 = 2001, BeltMk2 = 2002, BeltMk3 = 2003;
    public const int SorterMk1 = 2011, SorterMk2 = 2012, SorterMk3 = 2013;
    public const int Splitter = 2020;
    public const int DepotMk1 = 2101, DepotMk2 = 2102;
    public const int Tesla = 2201;
    // production
    public const int ArcSmelter = 2302, PlaneSmelter = 2315;
    public const int AsmMk1 = 2303, AsmMk2 = 2304, AsmMk3 = 2305;
    public const int ChemicalPlant = 2309, Refinery = 2308, Collider = 2310, MatrixLab = 2901;
    // items used by the regression block
    public const int IronOre = 1001, CopperOre = 1002;
    public const int IronIngot = 1101, Magnet = 1102, CopperIngot = 1104, MagneticCoil = 1202;
    // recipes used by the regression block
    public const int RecipeIronIngot = 1, RecipeMagnet = 2, RecipeCopperIngot = 3, RecipeMagneticCoil = 6;

    public static bool IsBelt(int item) { return item == BeltMk1 || item == BeltMk2 || item == BeltMk3; }
    public static bool IsSorter(int item) { return item == SorterMk1 || item == SorterMk2 || item == SorterMk3; }
}

// ------------------------------------------------------------------ directions
public enum Dir { N = 0, E = 1, S = 2, W = 3 }

public static class Dirs
{
    public static int Dx(Dir d) { return d == Dir.E ? 1 : d == Dir.W ? -1 : 0; }
    public static int Dy(Dir d) { return d == Dir.N ? 1 : d == Dir.S ? -1 : 0; }
    public static double Yaw(Dir d) { return 90.0 * (int)d; }
    public static Dir Opposite(Dir d) { return (Dir)(((int)d + 2) % 4); }
    public static Dir Right(Dir d) { return (Dir)(((int)d + 1) % 4); }   // clockwise
    public static Dir Left(Dir d) { return (Dir)(((int)d + 3) % 4); }    // counter-clockwise
    public static Dir RotateCW(Dir d, int quarters) { return (Dir)((((int)d + quarters) % 4 + 4) % 4); }
    public static Dir FromDelta(int dx, int dy)
    {
        if (dx != 0 && dy != 0) throw new ArgumentException("diagonal step " + dx + "," + dy);
        if (dx == 0 && dy == 0) throw new ArgumentException("zero step");
        return dx > 0 ? Dir.E : dx < 0 ? Dir.W : dy > 0 ? Dir.N : Dir.S;
    }
    /// Nearest cardinal direction of a yaw in degrees (tolerates game noise like 89.93 or 270.011).
    public static Dir FromYaw(double yaw) { return (Dir)(((int)Math.Round(Norm(yaw) / 90.0)) % 4); }
    public static double Norm(double yaw) { double y = yaw % 360.0; return y < 0 ? y + 360.0 : y; }
}

// ------------------------------------------------------------------ abstract building
public enum Kind { Machine, Belt, Sorter, Splitter, Tesla, Storage, Other }

/// One blueprint building in the abstract model. Fields mirror the game's BlueprintBuilding record
/// (localOffset_x/y/z, *_2, yaw/yaw2, inputObj/outputObj, the four slot fields, the two offsets,
/// recipeId, filterId, parameters) so the Emitter is a plain field copy onto a cloned prototype.
/// Metadata fields below the line are never emitted.
public class Bld
{
    public Kind Kind;
    public int ItemId;                         // building item id (2302 arc smelter, 2002 belt Mk.II, ...)
    public double X, Y, Z;                     // start point (sorter) or center (everything else)
    public double X2, Y2, Z2;                  // end point (sorter); equal to X/Y/Z for other kinds
    public double Yaw, Yaw2;                   // degrees, clockwise from north
    public int RecipeId;                       // machines
    public int FilterId;                       // sorters/distributors (0 = none)
    public Bld Output, Input;                  // outputObj / inputObj
    public int OutputToSlot, InputFromSlot, OutputFromSlot, InputToSlot;
    public int OutputOffset, InputOffset;
    public int[] Parameters;                   // belt icon [itemId,0]; sorter [length]; splitter [priority,...]

    // ---- metadata (not emitted) ----
    public int Index = -1;                     // assigned by Layout.Reindex, equals the emitted index
    public int Carries;                        // belt/sorter: item moved; machine: main output item
    public string Chain;                       // belt: chain name (one per belt run, e.g. "in:1001#0")
    public string Role;                        // free text for reports, e.g. "ROW-1 in", "SMELT-COL-1 out"
    public string Cell;                        // unit-cell / prototype name the Emitter clones (null = by ItemId)
    public object Game;                        // Emitter-owned game object
    /// Machine: planned load = fraction of full speed this machine runs at (its row's rate / (machines * full rate)).
    /// Set by the Composer/Kit per row so the Validator uses the same per-machine rate as the row's port rates.
    /// 0 = unknown (refs loaded from files): the Validator falls back to the step rate shared over the placed machines.
    public double Load;
    /// Emitted modelIndex; 0 = the prototype's (splitter variants: 38 crossed, 39 N/S low + high, 40 E/W low + N/S high).
    public int ModelIndex;

    // ---- factories (fill the record conventions from analysis/patterns.md section 0) ----
    public static Bld Machine(int itemId, double x, double y, double yaw, int recipeId)
    {
        return new Bld { Kind = Kind.Machine, ItemId = itemId, X = x, Y = y, X2 = x, Y2 = y, Yaw = yaw, Yaw2 = yaw, RecipeId = recipeId,
            OutputToSlot = 0, InputFromSlot = 0, OutputFromSlot = 0, InputToSlot = 0 };
    }

    /// Belt tile. Link fields follow the proven belt record: outputToSlot 1 (0 for a chain end, 2 for a
    /// side-join), inputFromSlot 0, outputFromSlot 0, inputToSlot 1, offsets 0. Output is set by Layout.Link.
    public static Bld Belt(int itemId, double x, double y, double yaw, int carries, string chain)
    {
        return new Bld { Kind = Kind.Belt, ItemId = itemId, X = x, Y = y, X2 = x, Y2 = y, Yaw = yaw, Yaw2 = yaw, Carries = carries, Chain = chain,
            OutputToSlot = 0, InputFromSlot = 0, OutputFromSlot = 0, InputToSlot = 1 };
    }

    /// Sorter between a belt and a machine. (sx,sy) start, (ex,ey) end, absolute coordinates.
    /// beltToMachine: inputObj = belt, outputToSlot = machine slot, inputFromSlot = -1, inputOffset = beltOffset.
    /// otherwise:     inputObj = machine, inputFromSlot = machine slot, outputToSlot = -1, outputOffset = beltOffset.
    public static Bld Sorter(int itemId, double sx, double sy, double ex, double ey, double yaw,
                             Bld from, Bld to, int machineSlot, int length, int beltOffset, int carries)
    {
        bool beltToMachine = from != null && from.Kind == Kind.Belt;
        return new Bld { Kind = Kind.Sorter, ItemId = itemId, X = sx, Y = sy, X2 = ex, Y2 = ey, Yaw = yaw, Yaw2 = yaw,
            Input = from, Output = to, Carries = carries, Parameters = new[] { length },
            OutputToSlot = beltToMachine ? machineSlot : -1, InputFromSlot = beltToMachine ? -1 : machineSlot,
            OutputFromSlot = 0, InputToSlot = 1,
            InputOffset = beltToMachine ? beltOffset : 0, OutputOffset = beltToMachine ? 0 : beltOffset };
    }

    public static Bld Tesla(double x, double y)
    {
        return new Bld { Kind = Kind.Tesla, ItemId = Ids.Tesla, X = x, Y = y, X2 = x, Y2 = y };
    }

    /// Splitter (2020). Belt links to it are made on the BELT records (see CONTRACT.md, splitter geometry).
    public static Bld Splitter(double x, double y, bool priority)
    {
        return new Bld { Kind = Kind.Splitter, ItemId = Ids.Splitter, X = x, Y = y, X2 = x, Y2 = y,
            OutputToSlot = 14, InputFromSlot = 15, OutputFromSlot = 15, InputToSlot = 14,
            Parameters = new[] { priority ? 1 : 0, 0, 0, 0, 0, 0 } };
    }

    /// Depot Mk.I (2101, model 51) at yaw 0 (square, multiLevelAllowRotate false); record defaults as the game wrote
    /// them in refs\engine-turbine-60-v2-userupgrade.txt #482 (slots 14/15/15/14, no links).
    public static Bld Depot(double x, double y)
    {
        return new Bld { Kind = Kind.Storage, ItemId = Ids.DepotMk1, X = x, Y = y, X2 = x, Y2 = y,
            OutputToSlot = 14, InputFromSlot = 15, OutputFromSlot = 15, InputToSlot = 14 };
    }

    public int SorterLength { get { return Parameters != null && Parameters.Length > 0 ? Parameters[0] : 0; } }
    /// Raised belt node of a bridge (z 0.5 on the tile of a crossed ground belt, analysis\bridges.md): occupies no tile.
    public bool IsRaised { get { return Kind == Kind.Belt && Z > 0.25; } }
    public int TileX { get { return (int)Math.Round(X); } }
    public int TileY { get { return (int)Math.Round(Y); } }
    public Dir FlowDir { get { return Dirs.FromYaw(Yaw); } }   // belts: flow direction (corners: see Layout.Link)

    /// Rigid move.
    public void Translate(double dx, double dy) { X += dx; Y += dy; X2 += dx; Y2 += dy; }

    /// Rigid rotation by quarter turns clockwise around (px,py). Machine-local slots do not change, because
    /// sorters and their machine turn together (the game does the same when rotating a pasted blueprint).
    public void RotateCW(int quarters, double px, double py)
    {
        int q = ((quarters % 4) + 4) % 4;
        for (int i = 0; i < q; i++)
        {
            double rx = X - px, ry = Y - py; X = px + ry; Y = py - rx;
            rx = X2 - px; ry = Y2 - py; X2 = px + ry; Y2 = py - rx;
        }
        Yaw = Dirs.Norm(Yaw + 90.0 * q); Yaw2 = Dirs.Norm(Yaw2 + 90.0 * q);
    }

    public override string ToString()
    {
        var c = CultureInfo.InvariantCulture;
        return string.Format(c, "{0} {1} ({2:0.###},{3:0.###}){4} yaw {5:0.#}{6}{7}", Kind, ItemId, X, Y,
            Kind == Kind.Sorter ? string.Format(c, "->({0:0.###},{1:0.###})", X2, Y2) : "", Yaw,
            RecipeId != 0 ? " r" + RecipeId : "", Chain != null ? " [" + Chain + "]" : "");
    }
}

// ------------------------------------------------------------------ ports and fragments
public enum PortKind { In, Out }

/// A named belt end of a fragment. For an In port, (X,Y) is the FIRST tile of the lane and Dir its flow
/// direction: an upstream run must end on the tile (X - dx, Y - dy) and link into this tile.
/// For an Out port, (X,Y) is the LAST tile of the lane and Dir its flow direction: the downstream run
/// starts on (X + dx, Y + dy).
public class Port
{
    public string Name;           // unique within the fragment, e.g. "in:1001", "in:1104", "out:1202"
    public PortKind Kind;
    public int X, Y;              // tile
    public Dir Dir;               // flow direction at that tile
    public int Item;
    public double Rate;           // items/min the plan puts through this port
    public Bld Belt;              // the belt tile at (X,Y)
    public bool Seam;             // In port that is a bus overlap tail (joins the previous module's belt end): no icon (rule 5 exempt)
    public bool FedFromBand;      // FLOORPLAN HOOK: In port fed inside the block (lane descending from the north band, RowSpec.InputFlows): no tail to the input edge, no input-edge check (Lanes.cs)
    public string Chain;          // chain name of that lane

    public int NextX { get { return X + Dirs.Dx(Dir); } }
    public int NextY { get { return Y + Dirs.Dy(Dir); } }
    public int PrevX { get { return X - Dirs.Dx(Dir); } }
    public int PrevY { get { return Y - Dirs.Dy(Dir); } }

    public override string ToString() { return Kind + " " + Name + " item " + Item + " @(" + X + "," + Y + ") " + Dir + " " + Rate.ToString("0.##", CultureInfo.InvariantCulture) + "/min"; }
}

/// Output of a module: buildings + named ports. Coordinates are absolute in the fragment's own frame;
/// the Composer moves the whole fragment with Translate / RotateCW before adding it to a Layout.
public class Fragment
{
    public string Name;
    public List<Bld> Blds = new List<Bld>();
    public List<Port> Ports = new List<Port>();

    public Fragment(string name) { Name = name; }

    public Bld Add(Bld b) { Blds.Add(b); return b; }
    public Port AddPort(Port p) { Ports.Add(p); return p; }
    public Port Port(string name)
    {
        foreach (var p in Ports) if (p.Name == name) return p;
        throw new KeyNotFoundException("fragment " + Name + " has no port " + name);
    }
    public IEnumerable<Port> InPorts { get { return Ports.Where(p => p.Kind == PortKind.In); } }
    public IEnumerable<Port> OutPorts { get { return Ports.Where(p => p.Kind == PortKind.Out); } }

    public void Translate(int dx, int dy)
    {
        foreach (var b in Blds) b.Translate(dx, dy);
        foreach (var p in Ports) { p.X += dx; p.Y += dy; }
    }

    /// Rigid rotation, quarter turns clockwise around tile (px,py).
    public void RotateCW(int quarters, int px, int py)
    {
        foreach (var b in Blds) b.RotateCW(quarters, px, py);
        int q = ((quarters % 4) + 4) % 4;
        foreach (var p in Ports)
        {
            for (int i = 0; i < q; i++) { int rx = p.X - px, ry = p.Y - py; p.X = px + ry; p.Y = py - rx; }
            p.Dir = Dirs.RotateCW(p.Dir, q);
        }
    }

    /// Tile bounding box using Footprint.Of (sorters ignored).
    public Rect Bounds(Footprints fp)
    {
        var r = Rect.Empty;
        foreach (var b in Blds) if (b.Kind != Kind.Sorter) r = r.Union(fp.TileRect(b));
        return r;
    }
}

/// Inclusive integer tile rectangle.
public struct Rect
{
    public int X0, Y0, X1, Y1;
    public Rect(int x0, int y0, int x1, int y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
    public static readonly Rect Empty = new Rect(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
    public bool IsEmpty { get { return X0 > X1 || Y0 > Y1; } }
    public int Width { get { return IsEmpty ? 0 : X1 - X0 + 1; } }
    public int Height { get { return IsEmpty ? 0 : Y1 - Y0 + 1; } }
    public Rect Union(Rect o)
    {
        if (o.IsEmpty) return this; if (IsEmpty) return o;
        return new Rect(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
    }
    public bool Contains(int x, int y) { return x >= X0 && x <= X1 && y >= Y0 && y <= Y1; }
    public override string ToString() { return "x " + X0 + ".." + X1 + ", y " + Y0 + ".." + Y1 + " (" + Width + "x" + Height + ")"; }
}

/// Grid footprints in whole tiles. Proven sizes are hard-coded; anything else falls back to the collider footprint
/// (analysis\buildings\colliders.json via GameData; game-data footprintTiles only when colliders.json is missing).
public class Footprints
{
    readonly GameData gd;
    public Footprints(GameData gd) { this.gd = gd; }

    /// (w,h) in tiles at yaw 0. Sorters are (0,0): they occupy no tile.
    public int[] Size(int itemId)
    {
        if (Ids.IsSorter(itemId)) return new[] { 0, 0 };
        if (itemId == Coaters.SprayCoater) return new[] { 0, 0 };   // COATERS HOOK: belt addon, sits over a lane tile
        if (Ids.IsBelt(itemId) || itemId == Ids.Tesla || itemId == Ids.Splitter) return new[] { 1, 1 };
        switch (itemId)
        {
            case Ids.ArcSmelter: case Ids.PlaneSmelter: case Ids.AsmMk1: case Ids.AsmMk2: case Ids.AsmMk3: case Ids.DepotMk1:
                return new[] { 3, 3 };
        }
        MachineDef m = gd != null ? gd.Machine(itemId) : null;
        if (m != null && m.FootprintTiles != null && m.FootprintTiles.Length == 2)
            return new[] { (int)Math.Ceiling(m.FootprintTiles[0] - 0.2), (int)Math.Ceiling(m.FootprintTiles[1] - 0.2) };
        return new[] { 1, 1 };
    }

    /// The belt touching a splitter (Cell "splitter-belt", 0.199 from the splitter center, refs\splitters.txt) sits on the
    /// splitter's own tile: it occupies no tile of its own. The second splitter belt (1.073) rounds to its own tile.
    /// Single rule used by Grid/Layout.Add, Composer and Emitter (no 0.5 snap for any "splitter-belt").
    public static bool IsSplitterTouch(Bld b)
    {
        if (b.Kind != Kind.Belt || b.Cell != "splitter-belt") return false;
        return Math.Abs(b.X - Math.Round(b.X)) > 0.1 || Math.Abs(b.Y - Math.Round(b.Y)) > 0.1;
    }

    /// Occupied tile rectangle of a building (rotation-aware; centered on its tile for odd sizes).
    public Rect TileRect(Bld b)
    {
        // hook (feat-labs, SlotRows.cs): stacked Matrix Labs occupy no tile of their own; the Miniature Particle Collider
        // footprint is off its centre (exact collider boxes)
        Rect special;
        if (SlotRows.TileRect(gd, b, out special)) return special;
        int[] s = Size(b.ItemId);
        if (s[0] == 0 || IsSplitterTouch(b) || b.IsRaised) return Rect.Empty;
        int q = (int)Math.Round(Dirs.Norm(b.Yaw) / 90.0) % 4;
        int w = q % 2 == 0 ? s[0] : s[1], h = q % 2 == 0 ? s[1] : s[0];
        int x0 = (int)Math.Round(b.X - (w - 1) / 2.0), y0 = (int)Math.Round(b.Y - (h - 1) / 2.0);
        return new Rect(x0, y0, x0 + w - 1, y0 + h - 1);
    }
}

/// Tile occupancy (machines, belts, towers, splitters). Sorters are never registered.
public class Grid
{
    readonly Dictionary<long, Bld> cells = new Dictionary<long, Bld>();
    readonly Footprints fp;
    public Grid(Footprints fp) { this.fp = fp; }

    static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }

    public Bld At(int x, int y) { Bld b; return cells.TryGetValue(K(x, y), out b) ? b : null; }
    public bool IsFree(int x, int y) { return !cells.ContainsKey(K(x, y)); }
    public bool IsFree(Rect r)
    {
        for (int x = r.X0; x <= r.X1; x++) for (int y = r.Y0; y <= r.Y1; y++) if (!IsFree(x, y)) return false;
        return true;
    }

    /// Registers a building; returns the buildings it collides with (empty list = ok, nothing overwritten).
    public List<Bld> Occupy(Bld b)
    {
        var hits = new List<Bld>();
        Rect r = fp.TileRect(b);
        if (r.IsEmpty) return hits;
        for (int x = r.X0; x <= r.X1; x++) for (int y = r.Y0; y <= r.Y1; y++)
        {
            Bld o = At(x, y);
            if (o != null) { if (!hits.Contains(o)) hits.Add(o); }
            else cells[K(x, y)] = b;
        }
        return hits;
    }

    public void Release(Bld b)
    {
        Rect r = fp.TileRect(b);
        for (int x = r.X0; x <= r.X1; x++) for (int y = r.Y0; y <= r.Y1; y++) if (At(x, y) == b) cells.Remove(K(x, y));
    }
}

/// The whole abstract block: every building, the exposed ports and the occupancy grid.
public class Layout
{
    public readonly List<Bld> All = new List<Bld>();
    public readonly List<Port> Ports = new List<Port>();     // block-level ports (raw inputs, products)
    public readonly List<string> Errors = new List<string>(); // collisions found while adding
    public readonly Footprints Footprints;
    public readonly Grid Grid;
    readonly Dictionary<long, Bld> beltAt = new Dictionary<long, Bld>();

    public Layout(GameData gd) { Footprints = new Footprints(gd); Grid = new Grid(Footprints); }

    static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }

    public Bld Add(Bld b)
    {
        foreach (var o in Grid.Occupy(b)) Errors.Add("overlap: " + b + " with " + o);
        if (b.Kind == Kind.Belt && !Footprints.IsSplitterTouch(b) && !b.IsRaised) beltAt[K(b.TileX, b.TileY)] = b;
        All.Add(b);
        return b;
    }

    /// Adds every building of a fragment (in its current position). Fragment ports stay on the fragment.
    public void Add(Fragment f) { foreach (var b in f.Blds) Add(b); }

    public Bld BeltAt(int x, int y) { Bld b; return beltAt.TryGetValue(K(x, y), out b) ? b : null; }

    public IEnumerable<Bld> OfKind(Kind k) { return All.Where(b => b.Kind == k); }

    /// Belt link: from.Output = to with the given outputToSlot (1 straight/corner, 2 side-join).
    public static void Link(Bld from, Bld to, int outputToSlot)
    {
        from.Output = to; from.OutputToSlot = to == null ? 0 : outputToSlot;
    }

    /// Assigns Index = position in All (the emitted building order).
    public void Reindex() { for (int i = 0; i < All.Count; i++) All[i].Index = i; }

    public Rect Bounds()
    {
        var r = Rect.Empty;
        foreach (var b in All) if (b.Kind != Kind.Sorter) r = r.Union(Footprints.TileRect(b));
        return r;
    }

    public int Count(Kind k, int itemId) { return All.Count(b => b.Kind == k && (itemId == 0 || b.ItemId == itemId)); }
}

// ------------------------------------------------------------------ spec and tiers
/// Building tiers. Belt and sorter are the caps (Mk.I-III, PLAN.md decision 8): the composer builds at these tiers,
/// BlockSpec.CheapestTiers then lowers every lane / sorter to the cheapest tier that carries its flow.
public class Tiers
{
    public int Smelter = Ids.ArcSmelter;
    public int Assembler = Ids.AsmMk1;
    public int Belt = Ids.BeltMk2;
    public int Sorter = Ids.SorterMk2;
    public int Tesla = Ids.Tesla;

    /// Machine item for a recipe type ("Smelt", "Assemble", "Chemical", ...); 0 if no tier is configured.
    public int MachineFor(string recipeType)
    {
        switch (recipeType)
        {
            case "Smelt": return Smelter;
            case "Assemble": return Assembler;
            case "Chemical": return Ids.ChemicalPlant;
            case "Refine": return Ids.Refinery;
            case "Particle": return Ids.Collider;
            case "Research": return Ids.MatrixLab;
        }
        return 0;
    }

    /// Belt/sorter must be a tier the engine can emit (Mk.I-III; PLAN.md decision 8 lifted the old Mk.II-max rule:
    /// the tiers are caps now, Lanes.AssignTiers picks the cheapest tier inside them).
    public void CheckUserRules()
    {
        if (!Ids.IsBelt(Belt)) throw new ArgumentException("belt tier " + Belt + " is not Mk.I/II/III");
        if (!Ids.IsSorter(Sorter)) throw new ArgumentException("sorter tier " + Sorter + " is not Mk.I/II/III");
    }
}

public class BlockSpec
{
    public int Item;                                  // target item id
    public double Rate;                               // target items/min
    public Tiers Tiers = new Tiers();
    public HashSet<int> TreatAsRaw = new HashSet<int>();                    // supplied from outside the block
    public Dictionary<int, int> RecipeOverrides = new Dictionary<int, int>(); // item -> recipe id
    public bool CleanRatio;                           // let the solver suggest a nearby clean rate
    public Dir InEdge = Dir.S, OutEdge = Dir.N;       // block edges for raw inputs / products (coil-360-v3: S in, N out)
    public string OutPath;                            // .txt to write; null = do not write
    public string ShortDesc, Desc;                    // header text; null = generated from the plan
    /// Machine pitch along E-W (blueprint x) that stays collision-free up to 60 deg latitude (colliders.json
    /// minIntegerPitchEW_upTo60deg); the Validator also checks colliders with the worst E-W tile factor 0.8557.
    public bool Safe60;
    /// Per-step raw inputs (--raw "Iron Ingot@Steel"): consumer item -> input items it takes from outside the block
    /// (block input lane), while other consumers of the same item still get it from the block's own step.
    public Dictionary<int, HashSet<int>> RawFor = new Dictionary<int, HashSet<int>>();
    /// Own supply rows (--own "Steel"): consumer items whose inputs come from producer rows that feed nothing else
    /// (grouped row mode: such a consumer row is never packed into a splitter group with other consumers).
    public HashSet<int> OwnSupply = new HashSet<int>();
    /// Kit mode: machines per module row at most (12 assemblers at pitch 4 = 48 tiles).
    public int MaxRow = 12;
    /// Buffer depot at the product end of the block (user rule 2026-10-31, cell DEPOT_BUFFER); --no-buffer disables it.
    /// Kit mode: only on the final product module.
    public bool Buffer = true;
    // ---- lanes (engine\Lanes.cs, PLAN.md decision 8)
    /// Cheapest belt/sorter tier per lane / sorter inside the caps (Tiers.Belt / Tiers.Sorter). Off = every belt and
    /// sorter at the cap tier (CLI default, keeps the coil-360-v3 regression); the addon path (LayoutOptions) sets it.
    public bool CheapestTiers;
    /// Belt/sorter tiers without a prototype record (FlatEngine fills it from PrototypeCells): never chosen.
    public HashSet<int> MissingTiers = new HashSet<int>();
    /// Matrix Lab tower height cap (vertical construction tech; LayoutOptions.LabStackMax). feat-labs.
    public int LabStackMax = 15;
    /// COATERS HOOK: proliferator item on the external proliferator lane (1141/1142/1143; 0 = none, engine\Coaters.cs).
    public int ProliferatorItem;
    /// Rule 14 margin (Rules.ApplyMargin) inside Compose: true for the engine's own solver plans (CLI); PlanMapper copies
    /// LayoutOptions.AddMargin (default false: a DSPCalculator plan's machine counts are authoritative). engine\Packing.cs.
    public bool AddMargin = true;
}

// ------------------------------------------------------------------ plan (Solver output)
public class ItemRate
{
    public int Item; public double Rate;              // items/min
    public ItemRate(int item, double rate) { Item = item; Rate = rate; }
    public override string ToString() { return Item + "@" + Rate.ToString("0.##", CultureInfo.InvariantCulture); }
}

/// One production step: N machines of one kind on one recipe.
public class Step
{
    public int Item;                                  // main product of this step
    public RecipeDef Recipe;
    public int MachineItem;                           // e.g. 2302 arc smelter, 2303 assembler Mk.I
    public double Speed;                              // machine speed multiplier (game-data speedMultiplier)
    public double MachinesExact;                      // rate * time / (outCount * 60 * speed)
    public int Machines;                              // ceil(MachinesExact)
    public List<ItemRate> Inputs = new List<ItemRate>();   // required at the exact (unrounded) rate
    public List<ItemRate> Outputs = new List<ItemRate>();  // exact rate the plan needs from this step
    public double MaxOutputRate;                      // what Machines can make at full power
    public List<int> ConsumerSteps = new List<int>(); // indices into ProductionPlan.Steps (empty = block output)
    /// Inputs this step takes from outside the block although the plan has a step for them (BlockSpec.RawFor).
    public HashSet<int> External = new HashSet<int>();
    /// COATERS HOOK: calculator incLevel (> 0 = spray coaters on every input lane of this step, engine\Coaters.cs).
    public int IncLevel;
    /// True when the item reaches this step through a block input lane (no step makes it, or BlockSpec.RawFor).
    public bool IsExternal(ProductionPlan plan, int item) { return External.Contains(item) || plan == null || plan.StepFor(item) == null; }
    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture, "{0} x{1} ({2:0.###}) machine {3} recipe {4} {5}: in {6} -> out {7}",
            Recipe != null ? Recipe.Name : "?", Machines, MachinesExact, MachineItem, Recipe != null ? Recipe.Id : 0, Item,
            string.Join(" + ", Inputs.Select(i => i.ToString()).ToArray()), string.Join(" + ", Outputs.Select(o => o.ToString()).ToArray()));
    }
}

public class ProductionPlan
{
    public int TargetItem;
    public double TargetRate;                         // requested items/min
    public double SuggestedRate;                      // clean-ratio suggestion (0 = none)
    public List<Step> Steps = new List<Step>();       // topological: raw-consuming steps first, target last
    public List<ItemRate> RawInputs = new List<ItemRate>();
    public List<string> Warnings = new List<string>();

    public Step StepFor(int item) { return Steps.FirstOrDefault(s => s.Item == item); }

    public string Describe(GameData gd)
    {
        var sb = new StringBuilder();
        Func<int, string> n = id => gd != null ? gd.ItemName(id) : id.ToString();
        sb.AppendLine("target: " + n(TargetItem) + " " + TargetRate.ToString("0.##", CultureInfo.InvariantCulture) + "/min"
            + (SuggestedRate > 0 ? " (clean ratio: " + SuggestedRate.ToString("0.##", CultureInfo.InvariantCulture) + "/min)" : ""));
        foreach (var r in RawInputs) sb.AppendLine("  raw  " + n(r.Item) + " " + r.Rate.ToString("0.##", CultureInfo.InvariantCulture) + "/min");
        foreach (var s in Steps) sb.AppendLine("  step " + s);
        foreach (var w in Warnings) sb.AppendLine("  WARN " + w);
        return sb.ToString();
    }
}

// ------------------------------------------------------------------ validation report
public class ValidationReport
{
    public readonly List<string> Errors = new List<string>();
    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> Info = new List<string>();
    public bool Ok { get { return Errors.Count == 0; } }
    public void Fail(string m) { Errors.Add(m); }
    public void Warn(string m) { Warnings.Add(m); }
    public void Note(string m) { Info.Add(m); }
    /// Writes the report line by line through the host's callback (the engine never writes to a console itself).
    public void Print(Action<string> line)
    {
        if (line == null) return;
        foreach (var m in Info) line("  " + m);
        foreach (var m in Warnings) line("WARN: " + m);
        foreach (var m in Errors) line("FAIL: " + m);
        line(Ok ? "validation ok (" + Warnings.Count + " warnings)" : Errors.Count + " validation errors");
    }
}
}
