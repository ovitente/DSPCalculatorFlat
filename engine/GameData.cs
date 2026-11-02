// GameData.cs - game numbers the engine needs (recipes, items, building numbers, colliders, icon tags) as plain values.
// No file I/O and no game DLL: hosts fill it (CLI: DspFlat.Host.GameDataFiles from analysis\game-data.json etc.).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

public class ItemCount
{
    public int Item; public int Count;
    public ItemCount(int item, int count) { Item = item; Count = count; }
}

public class ItemDef
{
    public int Id; public string Name; public string Type; public bool IsFluid; public int StackSize; public bool CanBuild;
    /// Ores, water, crude oil and other "Resource" items: the solver treats them as raw by default.
    public bool IsResource { get { return Type == "Resource"; } }
}

public class RecipeDef
{
    public int Id; public string Name; public string Type;      // Type: Smelt, Assemble, Chemical, Refine, Particle, Research, Fractionate
    public double TimeSec;
    public List<ItemCount> Inputs = new List<ItemCount>();
    public List<ItemCount> Outputs = new List<ItemCount>();
    public int[] Buildings = new int[0];                        // machine item ids that can run it
    public bool Explicit;                                       // true for alternate/advanced recipes
    public int OutCount(int item) { foreach (var o in Outputs) if (o.Item == item) return o.Count; return 0; }
    public int InCount(int item) { foreach (var i in Inputs) if (i.Item == item) return i.Count; return 0; }
}

public class MachineDef
{
    public int ItemId; public string Name; public int ModelIndex;
    public string RecipeType;                 // production buildings
    public double SpeedMultiplier;            // arc smelter 1, plane 2, asm Mk.I 0.75, Mk.II 1, Mk.III 1.5
    public double BeltItemsPerSec;            // belts: Mk.I 6, Mk.II 12, Mk.III 30
    public Dictionary<int, double> SorterTripsPerSec = new Dictionary<int, double>(); // sorters: length -> round trips/s (1 item per trip)
    public int SorterMaxLength;
    public double PowerConnectDistanceM, PowerCoverRadiusM;   // tesla: 22.5 / 10.5 metres
    public double[] FootprintTiles;           // derived (metres / 1.2566), not the grid size
    public bool IsSplitter;
}

public class GameData
{
    /// Metres per grid tile on the equator of a standard planet (game-data notes).
    public const double MetersPerTile = 1.2566;

    public readonly Dictionary<int, ItemDef> Items = new Dictionary<int, ItemDef>();
    public readonly Dictionary<int, RecipeDef> Recipes = new Dictionary<int, RecipeDef>();
    public readonly Dictionary<int, MachineDef> Machines = new Dictionary<int, MachineDef>();
    readonly Dictionary<string, int> nameToItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public string SourcePath;

    /// Item icon tags of the game's ItemProto.IconTag ("zzt1" for Assembling Machine Mk.I), used by BpText for the
    /// thumbnail text. Filled by the host (CLI: analysis\raw-protos.json; addon: LDB). Empty = no icon tags.
    public readonly Dictionary<int, string> IconTags = new Dictionary<int, string>();

    /// Plain-value construction (no file I/O in the engine): the host adds items, recipes, machines and colliders
    /// through the public dictionaries and these helpers. CLI loader: DspFlat.Host.GameDataFiles.Load.
    public void AddItem(ItemDef it)
    {
        Items[it.Id] = it;
        if (it.Name != null && !nameToItem.ContainsKey(it.Name)) nameToItem[it.Name] = it.Id;
    }

    /// Extra name alias (e.g. wiki names from item-ids.json); existing names win on conflict.
    public void AddName(string name, int id)
    {
        if (name != null && !nameToItem.ContainsKey(name)) nameToItem[name] = id;
    }

    /// Registers an exact build collider; its derived footprint replaces MachineDef.FootprintTiles (the game-data
    /// footprint is the LAST trigger box only, wrong for buildings with 3+ trigger boxes).
    public void AddCollider(ColliderDef def)
    {
        if (def == null || def.Boxes.Count == 0) return;
        Colliders[def.ItemId] = def;
        MachineDef md = Machine(def.ItemId);
        if (md != null && def.FootW > 0) md.FootprintTiles = new[] { def.FootW / MetersPerTile, def.FootH / MetersPerTile };
    }
    // ---- lookups ----
    public ItemDef Item(int id) { ItemDef d; return Items.TryGetValue(id, out d) ? d : null; }
    public RecipeDef Recipe(int id) { RecipeDef d; return Recipes.TryGetValue(id, out d) ? d : null; }
    public MachineDef Machine(int itemId) { MachineDef d; return Machines.TryGetValue(itemId, out d) ? d : null; }
    public string ItemName(int id) { ItemDef d = Item(id); return d != null ? d.Name : "item" + id; }

    /// Item id from a numeric string or a name (game name or item-ids.json name, case-insensitive). -1 if unknown.
    public int ResolveItem(string nameOrId)
    {
        int id;
        if (int.TryParse(nameOrId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return Items.ContainsKey(id) ? id : -1;
        return nameToItem.TryGetValue(nameOrId.Trim(), out id) ? id : -1;
    }

    /// All recipes producing the item.
    public List<RecipeDef> RecipesFor(int item) { return Recipes.Values.Where(r => r.OutCount(item) > 0).OrderBy(r => r.Id).ToList(); }

    /// Default recipe: lowest-id non-explicit recipe whose FIRST output is the item; else lowest-id non-explicit
    /// producer; else lowest-id producer. null if nothing makes it (raw resource).
    public RecipeDef DefaultRecipe(int item)
    {
        var all = RecipesFor(item);
        return all.FirstOrDefault(r => !r.Explicit && r.Outputs[0].Item == item)
            ?? all.FirstOrDefault(r => !r.Explicit)
            ?? all.FirstOrDefault();
    }

    /// Machine speed multiplier (1 if unknown).
    public double Speed(int machineItem) { MachineDef m = Machine(machineItem); return m != null && m.SpeedMultiplier > 0 ? m.SpeedMultiplier : 1.0; }

    /// Belt throughput, items/min (Mk.II = 720).
    public double BeltRate(int beltItem) { MachineDef m = Machine(beltItem); return m != null ? m.BeltItemsPerSec * 60.0 : 0; }

    /// Sorter throughput, items/min at the given grid length (Mk.II: L1 180, L2 90, L3 60). Unstacked, full power.
    public double SorterRate(int sorterItem, int length)
    {
        MachineDef m = Machine(sorterItem); double t;
        return m != null && m.SorterTripsPerSec.TryGetValue(length, out t) ? t * 60.0 : 0;
    }

    /// Tesla tower numbers in tiles. Theoretical cover 10.5 m = 8.36 tiles, link 22.5 m = 17.9 tiles.
    /// The validator should keep the proven envelope instead (ProvenTeslaCover / ProvenTeslaLink).
    public double TeslaCoverTiles { get { MachineDef m = Machine(Ids.Tesla); return m != null ? m.PowerCoverRadiusM / MetersPerTile : 0; } }
    public double TeslaLinkTiles { get { MachineDef m = Machine(Ids.Tesla); return m != null ? m.PowerConnectDistanceM / MetersPerTile : 0; } }
    public const double ProvenTeslaCover = 7.0;   // old conservative envelope (MakeCoilFactory*); no longer used by the engine
    public const double ProvenTeslaLink = 12.0;   // old conservative envelope; no longer used by the engine

    /// Game power coverage (Assembly-CSharp PowerSystem.OnNodeAdded / OnConsumerAdded): a consumer is powered when the
    /// distance between the node's position and the consumer's plugPos (= its EntityData.pos) is <= coverRadius
    /// (Tesla 10.5 m = 8.36 tiles at the equator; tiles are never wider off the equator). Towers link at <= 22.5 m.
    /// Power points used by the engine: machine center; sorter start AND midpoint (which one the game uses as an
    /// inserter's entity position is not verified; the user's game-proven refs\engine-electric-motor-120-v3-user.txt
    /// has sorter far ends at 8.6 tiles, so the far end is not it).
    public const double GameTeslaCoverTiles = 10.5 / MetersPerTile;   // 8.356, validator limit
    public const double GameTeslaLinkTiles = 22.5 / MetersPerTile;    // 17.9, validator limit
    /// Placement targets (TeslaGrid): a margin below the game limits.
    public const double PlaceTeslaCover = 7.9;
    public const double PlaceTeslaLink = 16.0;
    /// Points of a building that must be within the cover radius of a tower.
    public static List<double[]> PowerPoints(Bld b)
    {
        var l = new List<double[]>();
        if (b.Kind == Kind.Machine) l.Add(new[] { b.X, b.Y });
        else if (b.Kind == Kind.Sorter) { l.Add(new[] { b.X, b.Y }); l.Add(new[] { (b.X + b.X2) / 2, (b.Y + b.Y2) / 2 }); }
        return l;
    }

    /// Minimum distance between two power nodes. Game rule (BuildTool_BlueprintPaste/BuildTool_Click.CheckBuildConditions):
    /// a power node (PrefabDesc.isPowerNode, not an accumulator) closer than sqrt(12.25) = 3.5 m (world distance) to any
    /// other power node (item ids 2199..2299 with isPowerNode, pasted or existing) gets EBuildCondition.PowerTooClose
    /// (wind turbine - wind turbine 10.5 m, geothermal - geothermal 12 m). 3.5 m = 2.79 tiles at the equator; the engine
    /// keeps the proven floor of 4.0 tiles (smallest tower-tower distance in the user's working blueprints, e.g.
    /// t-tpl\Assemblers mk III.txt), which also leaves margin for the narrower longitude tiles away from the equator.
    public const double GameMinPowerNodeM = 3.5;
    public const double MinPowerNodeTiles = 4.0;

    /// True for buildings that are power nodes in the game (PowerDesc.connectDistance > 0: towers, generators, ...).
    /// Accumulators count as the OTHER node of a pair (conditions.md F15: exempt only as the building being checked,
    /// so accumulator-accumulator pairs are allowed, tower-accumulator pairs are not).
    public bool IsPowerNode(int item)
    {
        if (item == Ids.Tesla || IsAccumulator(item)) return true;
        MachineDef m = Machine(item);
        return m != null && m.PowerConnectDistanceM > 0 && item >= 2199 && item <= 2299;
    }
    public static bool IsAccumulator(int item) { return item == 2206 || item == 2207; }

    // ---- placement limits (analysis\buildings\conditions.md, read from the game IL) ----
    /// E-W tile size factor between 0 and 60 deg latitude (colliders.json ewTileFactorRange): blueprint x units are
    /// longitude cells, 0.8557 .. 1.1409 of the equator tile; N-S (y) is latitude independent.
    public const double EwFactorMin = 0.8557, EwFactorMax = 1.1409;
    /// z unit of a blueprint record (one belt level) in metres.
    public const double BeltLevelM = 1.333333;
    /// F4: consecutive belt nodes at most 2.302 m apart (sqrt 5.3, TooFar).
    public const double BeltMaxLinkM = 2.302;
    /// F5: belt slope |sin| <= 0.6 (36.87 deg, TooSteep).
    public const double BeltMaxSlopeDeg = 36.87;
    /// F6: belt node linked to a non-belt building (splitter) within 4.0 m of its origin.
    public const double BeltToBuildingMaxM = 4.0;
    /// F7/F8/F9 sorter with exactly one belt end: 0.6 .. 5.5 m, <= 3.499 cells, sqrt(cells^2 + (dh/0.2)^2) >= 0.88.
    public const double SorterOneBeltMinM = 0.6, SorterOneBeltMaxM = 5.5, SorterOneBeltMaxCells = 3.499, SorterOneBeltMinSpan = 0.88;
    /// F10 end rotation <= 30 deg, F11 line vs each end's forward axis <= 24 deg.
    public const double SorterMaxTwistDeg = 30, SorterMaxSkewDeg = 24;
    /// Proven bridge (analysis\bridges.md, refs\bridges-splitters-user.txt): raised nodes at z 0.5 on the crossed tiles,
    /// 1..3 crossed belts, one-tile ramps.
    public const double BridgeZ = 0.5;
    public const int BridgeMaxSpan = 3;   // old BRIDGE cell limit; RAISED_RUN (user's in-game edit) has no span limit
    /// Raised node vs sorter (refs\engine-turbine-60-v2-userupgrade.txt, accepted by the game): raised (9,20) 1.0 tile and
    /// raised (7,22) 1.21 from a sorter end on a neighbouring lane. Engine: >= 1.0 proven, 0.5..1.0 warned, < 0.5 fails.
    public const double RaisedSorterMin = 1.0;

    /// Distance from point (px,py) to the segment (ax,ay)-(bx,by), tiles.
    public static double SegmentDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy;
        double t = l2 > 1e-12 ? Math.Max(0, Math.Min(1, ((px - ax) * vx + (py - ay) * vy) / l2)) : 0;
        double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
        return Math.Sqrt(dx * dx + dy * dy);
    }
    /// Smallest distance between two splitters in a game-proven file (refs\splitters.txt: 10 tiles); cascades
    /// (splitter output straight into another splitter) are not proven at all.
    public const double ProvenSplitterSpacing = 10.0;

    // ---- pasted belt vs pasted splitter (in-game test 2026-10-31: engine-turbine-60-v1 / engine-missile-90-v1 refused) ----
    /// Game (BuildTool_BlueprintPaste.CheckBuildConditions, conditions.md 4.1): a pasted belt has no box, it probes
    /// OverlapSphere(lpos + up*0.2, r 0.23 m) against build colliders incl. other paste previews (layer 18), so it hits a
    /// pasted splitter's arm boxes (2 x 1.25 x 2.38 m, crossed, 0..2.3 m high). Pass 2 clears a collided belt only when
    /// every building it hit is within 3 belt hops of it (the splitter's own chain). A foreign belt on the tile straight
    /// next to a splitter: 1.2566 - 1.19 = 0.067 m < 0.23 -> Collide (both refused files had one); diagonal tile 0.63 m clear.
    public const double BeltProbeRadiusM = 0.23, BeltProbeLiftM = 0.2;
    public const int PasteOwnBeltHops = 3;
    /// Clearance used by the engine (proven, not only computed): smallest distance between a splitter centre and a belt that
    /// is not on the splitter's own chains in the game-proven files = 3.0 tiles (refs\splitters.txt, refs\engine-coil-1080-v1.txt;
    /// own-chain belts are proven down to 2.24 tiles, 4 hops, in engine-coil-1080-v1). Lowered to 2.0: the user's in-game edit
    /// refs\engine-turbine-60-v2-userupgrade.txt has foreign belts (15,22) 2.0 tiles from the splitter (15,20), accepted by the game.
    public const double SplitterForeignBeltMin = 2.0;

    /// Smallest clearance (metres) between a belt node's paste probe sphere and a building's build-collider boxes, worst
    /// case over the E-W tile factor range up to 60 deg; negative = the game reports Collide for the belt. Heights are not
    /// compared: every belt level used here (z 0 / 0.5 = 0 / 0.67 m, probe up to 1.1 m) is inside a 2.3 m splitter box.
    public double BeltProbeGapM(Bld belt, Bld bld)
    {
        var boxes = BoxesAt(bld.ItemId, bld.Yaw);
        if (boxes.Count == 0) return double.MaxValue;
        double best = double.MaxValue;
        foreach (double f in new[] { EwFactorMin, 1.0, EwFactorMax })
        {
            double px = (belt.X - bld.X) * MetersPerTile * f, pz = (belt.Y - bld.Y) * MetersPerTile;
            foreach (var b in boxes)
            {
                double ox = Math.Max(0, Math.Abs(px - b.Cx) - b.Hx), oz = Math.Max(0, Math.Abs(pz - b.Cz) - b.Hz);
                best = Math.Min(best, Math.Sqrt(ox * ox + oz * oz) - BeltProbeRadiusM);
            }
        }
        return best;
    }

    // ---- exact build colliders (analysis\buildings\colliders.json) ----
    /// One trigger box at yaw 0, metres: centre (x east, z north) and half extents.
    public class ColliderBox { public double Cx, Cz, Hx, Hz; }
    public class ColliderDef
    {
        public int ItemId, ModelId;
        public List<ColliderBox> Boxes = new List<ColliderBox>();
        public double FootW, FootH;              // footprint metres (x, z) at yaw 0
        /// minIntegerPitch per key: "yaw0_0_x", "yaw0_0_z", "yaw90_90_x", "yaw90_90_z"; EW60 = minIntegerPitchEW_upTo60deg
        public Dictionary<string, int> Pitch = new Dictionary<string, int>();
        public Dictionary<string, int> PitchEW60 = new Dictionary<string, int>();
    }
    public readonly Dictionary<int, ColliderDef> Colliders = new Dictionary<int, ColliderDef>();
    public string CollidersPath;

    /// Collider boxes of a building in world axes, metres, centre offsets relative to the building origin
    /// (quarter-turn yaw; clockwise: world = (x cos a + z sin a, -x sin a + z cos a)). Empty when unknown.
    public List<ColliderBox> BoxesAt(int item, double yaw)
    {
        var l = new List<ColliderBox>();
        ColliderDef d;
        if (!Colliders.TryGetValue(item, out d)) return l;
        int q = (int)Math.Round(Dirs.Norm(yaw) / 90.0) % 4;
        foreach (var b in d.Boxes)
        {
            double cx = b.Cx, cz = b.Cz, hx = b.Hx, hz = b.Hz;
            for (int i = 0; i < q; i++) { double t = cx; cx = cz; cz = -t; t = hx; hx = hz; hz = t; }
            l.Add(new ColliderBox { Cx = cx, Cz = cz, Hx = hx, Hz = hz });
        }
        return l;
    }

    /// Separation of the two buildings' build colliders in equator tiles: min over box pairs of max(gap x, gap y);
    /// negative = overlap (game: Collide, no tolerance). ewFactor scales blueprint x distances (1 = equator,
    /// EwFactorMin = worst case up to 60 deg latitude). double.MaxValue when a collider is unknown.
    public double ColliderGap(Bld a, Bld b, double ewFactor)
    {
        var ba = BoxesAt(a.ItemId, a.Yaw); var bb = BoxesAt(b.ItemId, b.Yaw);
        if (ba.Count == 0 || bb.Count == 0) return double.MaxValue;
        double best = double.MaxValue;
        double dxm = (a.X - b.X) * MetersPerTile * ewFactor, dym = (a.Y - b.Y) * MetersPerTile;
        foreach (var p in ba)
            foreach (var q in bb)
            {
                double gx = Math.Abs(dxm + p.Cx - q.Cx) - (p.Hx + q.Hx);
                double gy = Math.Abs(dym + p.Cz - q.Cz) - (p.Hz + q.Hz);
                best = Math.Min(best, Math.Max(gx, gy) / MetersPerTile);
            }
        return best;
    }

    public bool HasCollider(int item) { return Colliders.ContainsKey(item); }

    /// Smallest integer pitch (tiles) of two equal buildings at the given yaw stacked along E-W (blueprint x) or N-S
    /// (blueprint y), from colliders.json derived_pitch. safe60: the E-W pitch that stays clean up to 60 deg latitude.
    /// 0 when unknown.
    public int MinPitch(int item, double yaw, bool alongEW, bool safe60)
    {
        ColliderDef d;
        if (!Colliders.TryGetValue(item, out d)) return 0;
        int q = (int)Math.Round(Dirs.Norm(yaw) / 90.0) % 2;
        string key = (q == 0 ? "yaw0_0_" : "yaw90_90_") + (alongEW ? "x" : "z");
        int v;
        if (alongEW && safe60 && d.PitchEW60.TryGetValue(key, out v) && v > 0) return v;
        return d.Pitch.TryGetValue(key, out v) ? v : 0;
    }
}
}
