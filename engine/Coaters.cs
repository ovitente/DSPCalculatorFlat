// Coaters.cs - spray coaters (item 2313) on the input lanes of proliferated steps (PLAN.md decision 6) and the one
// external proliferator lane that feeds them. Pure model code (no game DLL, no I/O). Hooks into the shared files are
// marked "COATERS HOOK" (Model.cs, GameData.cs, Modules.cs, Composer.cs, Validator.cs, PlanMapper.cs).
//
// Geometry (no guesses; every number from game data or a decoded blueprint):
// - Footprint / colliders: analysis\buildings\colliders.json item 2313 model 120: build boxes 0.7 x 3.5 m (z -1.6..1.9,
//   0..0.9 m high) and 0.7 x 2 m (z -1.6..0.4, 0..2.5 m high), z = the coater's forward axis; landPoints at z +-2 m;
//   addonType 1 (belt addon). analysis\buildings\slots.md: the coater has no sorter slots.
// - The coater sits on a straight belt tile, its yaw along the belt. Blueprint records carry no links for a coater
//   (outputObj / inputObj -1, slots 14/15/15/14): the game attaches it to the belt under it and to the proliferator belt
//   by position. Scan of all 662 coaters in the user's blueprint folder (92 blueprints, blueprints-db.json): no record
//   ever links to a coater and no sorter feeds one; 568 of them have a belt node at z +1 (one belt level) exactly one
//   tile BEHIND the coater (coater frame f = -1, r = 0), perpendicular to the coater axis: 81 as the chain end, 487 as a
//   belt passing through (one raised feed belt serving several coaters on parallel lanes).
// - Reference used for the exact numbers (user's own blueprint, game 0.10.35.29151, the installed version):
//   Blueprint\7-logi\L1.txt: coater #36 at (3,7) yaw 180 on belt #32 (3,7) yaw 180; its feed chain
//   #72 (2.799,3,0) -> #68 (1.913,3,0) foot -> #67 (1,3,0.503) ramp -> #70 (0,3,1) raised corner yaw 315 ->
//   #81..#84 (0,4..7,1) -> #76 (0,8,1) corner -> #77 (1,8,1) -> #79 (2,8,1) -> #80 (3,7.987,1.003) chain end = one tile
//   behind the coater, 1.0 level up. Second coater #65 (11,2) yaw 0, feed end #86 (11,1.013,1.006) after the ramp
//   #88 (7,1,0) -> #87 (8,1,0.5) -> #85 (9,1,1). The 0.013 offset is game noise (other files: f = -1.000 / -0.999,
//   e.g. x-imp\Anti Matter\Annihilation Constraint Sphere 3.75 #136); the Emitter's 0.5 snap makes it 1.0.
// - Pass-through feeds over coaters on neighbouring lanes 1 tile apart: the advanced-mining-machine blueprint in
//   Blueprint\1-mine (Cyrillic file name, game 0.9.27.15466) coaters #45/#19/#34/#11/#38 at (6,2..6), yaw 90 on belts
//   flowing east, with the raised feed belt #25/#18/#26/#49/#22 (5.013,2..6,1.002) running north across their backs.
//   Passing through is only seen in 0.9.x files (no 0.10.x file has it; unverified for 0.10.35).
//
// Layout cell COATER (built by MachineRow in the row's Flow-N frame, origin = first machine):
//   c = (lowest first-pickup tile of the coated lanes) - 2  (coater body reaches +1.52 tiles forward: the pickup tile
//       c + 2 is clear of it, 2.51 m > 1.9 + 0.23 m);
//   coater (x, c) yaw = flow on every coated input lane x;
//   feed row y = c - 1 at z 1 (one tile behind every coater) from the ramp column xr (one tile outside side A) across
//   all coated lanes to the outermost coated lane of side B (chain end there);
//   ramp column xr: approach/port (xr, c-4) -> ground foot (xr, c-3) -> (xr, c-2) z 0.5 -> raised corner (xr, c-1) z 1
//   (L1.txt chain #68 -> #67 -> #70);
//   coated lanes start at c - 2 (a straight belt before the tile under the feed), external lanes at c - 4 so their
//   icon tail stays the first belt and sits on the input edge next to the proliferator port.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

public static class Coaters
{
    public const int SprayCoater = 2313;
    public const int ProliferatorMk1 = 1141, ProliferatorMk2 = 1142, ProliferatorMk3 = 1143;
    /// Port name prefix of the proliferator feed port of a row ("coat:<item>").
    public const string PortPrefix = "coat:";
    /// Chain name prefix of every belt of a proliferator feed chain.
    public const string ChainPrefix = "coat-feed:";
    /// Feed belt level over the coater (blueprint z units = belt levels, 1.333 m each).
    public const double FeedZ = 1.0;
    /// Ramp node between ground and the feed level.
    public const double RampZ = 0.5;
    /// Coater record slots as the game writes them (L1.txt #36 / #65: 14/15/15/14, no links).
    public const int SlotOut = 14, SlotIn = 15;
    /// Lateral tolerance of a belt passing under a coater (game: belt within 0.3 m of the addon axis, conditions.md 4.1).
    public const double AxisTolM = 0.3;
    /// Grid tolerance of a feed node as the game writes it: the user's drawn coater block (refs\patterns\p-sheet-001.txt
    /// #87..#91) has the feed row at y 4.013 / 4.014 and x 60.995 / 62.995, L1.txt #80 at y 7.987 (header above). The old
    /// 0.01 rejected the drawing.
    public const double FeedGridTol = 0.02;

    // ---------------------------------------------------------------- game numbers
    /// Sprays per proliferator item: ItemProto.HpMax of the game's ItemProtoSet (analysis\raw-protos.json, extracted
    /// from resources.assets): Mk.I 12, Mk.II 24, Mk.III 60. Used when the host did not fill GameData.SprayCounts.
    static readonly Dictionary<int, int> SpraysFromProtos = new Dictionary<int, int> { { ProliferatorMk1, 12 }, { ProliferatorMk2, 24 }, { ProliferatorMk3, 60 } };
    /// Proliferator item by IncLevel (= ItemProto.Ability: Mk.I 1, Mk.II 2, Mk.III 4, raw-protos.json).
    static readonly Dictionary<int, int> ItemByLevel = new Dictionary<int, int> { { 1, ProliferatorMk1 }, { 2, ProliferatorMk2 }, { 4, ProliferatorMk3 } };

    public static bool IsProliferator(int item) { return item == ProliferatorMk1 || item == ProliferatorMk2 || item == ProliferatorMk3; }

    /// Proliferator item for an inc level (0 if none matches).
    public static int ItemForLevel(int incLevel) { int it; return ItemByLevel.TryGetValue(incLevel, out it) ? it : 0; }

    /// Sprays one proliferator item gives (0 = unknown item).
    public static int Sprays(GameData gd, int prolif)
    {
        int n;
        if (gd != null && gd.SprayCounts.TryGetValue(prolif, out n) && n > 0) return n;
        return SpraysFromProtos.TryGetValue(prolif, out n) ? n : 0;
    }

    /// Proliferator items per minute for the coated lanes' flows (items/min passing the coaters; one spray per item).
    public static double ProliferatorPerMin(GameData gd, int prolif, IEnumerable<double> coatedLaneRates)
    {
        int s = Sprays(gd, prolif);
        if (s <= 0 || coatedLaneRates == null) return 0;
        return coatedLaneRates.Sum() / s;
    }

    // ---------------------------------------------------------------- identity
    public static bool IsCoater(Bld b) { return b != null && b.ItemId == SprayCoater; }
    /// Belt of a COATER cell's feed chain (port, foot, ramp, z 1 feed nodes). Router connectors leading to the cell's
    /// port are ordinary belts (their bridges are checked like every other route).
    public static bool IsFeed(Bld b) { return b != null && b.Kind == Kind.Belt && b.Cell == FeedCell; }
    public const string FeedCell = "coat-feed";

    /// Tags the feed chains of a layout that carries no cell tags (a blueprint drawn in the game and imported): every
    /// raised node at FeedZ one tile behind a coater, and from it the raised nodes of its chain upstream (down to the
    /// z 0.5 ramp) and downstream (to the chain end) get Cell = FeedCell. Proven by refs\patterns\p-sheet-001.txt
    /// (#92..#96 ramp (60,1,0) -> (60,2,0.5) -> (60,3,1) -> corner (60,4,1), #87..#91 feed (61..65,4,1.002..1.005)
    /// behind the coaters #0..#3 at (61/62/64/65,5)). Tagged belts are left alone; the Validator calls this first.
    public static void MarkFeeds(Layout l)
    {
        if (l == null) return;
        var coaters = l.All.Where(IsCoater).ToList();
        if (coaters.Count == 0) return;
        var belts = l.All.Where(b => b.Kind == Kind.Belt).ToList();
        var mainPred = new Dictionary<Bld, Bld>();
        foreach (Bld b in belts) if (b.Output != null && b.OutputToSlot <= 1 && !mainPred.ContainsKey(b.Output)) mainPred[b.Output] = b;
        foreach (Bld c in coaters)
        {
            int[] ft = FeedTile(c);
            foreach (Bld f in belts.Where(b => b.IsRaised && b.TileX == ft[0] && b.TileY == ft[1] && Math.Abs(b.Z - FeedZ) < 0.05))
            {
                var seen = new HashSet<Bld>();
                for (Bld n = f; n != null && n.IsRaised && seen.Add(n); ) { if (n.Cell == null) n.Cell = FeedCell; Bld p; n = mainPred.TryGetValue(n, out p) ? p : null; }
                for (Bld n = f.Output; n != null && n.Kind == Kind.Belt && n.IsRaised && seen.Add(n); n = n.Output) if (n.Cell == null) n.Cell = FeedCell;
            }
        }
    }
    /// Block input port of a proliferator lane: proliferator item and its lane leads into a COATER feed cell.
    public static bool IsFeedPort(Port p)
    {
        if (p == null || p.Kind != PortKind.In || !IsProliferator(p.Item)) return false;
        if (p.Name != null && p.Name.StartsWith(PortPrefix, StringComparison.Ordinal)) return true;
        var seen = new HashSet<Bld>();
        for (Bld n = p.Belt; n != null && n.Kind == Kind.Belt && seen.Add(n); n = n.Output) if (IsFeed(n)) return true;
        return false;
    }
    /// True when the step gets coaters (calculator incLevel > 0).
    public static bool Coated(Step s) { return s != null && s.IncLevel > 0; }

    /// Coater record (Kind.Other: no grid tile, no recipe; powered, collider-checked).
    public static Bld Coater(double x, double y, double yaw)
    {
        return new Bld { Kind = Kind.Other, ItemId = SprayCoater, X = x, Y = y, X2 = x, Y2 = y, Yaw = yaw, Yaw2 = yaw,
            OutputToSlot = SlotOut, InputFromSlot = SlotIn, OutputFromSlot = SlotIn, InputToSlot = SlotOut };
    }

    /// Tile one step behind the coater along its axis (where its feed belt node sits, one level up).
    public static int[] FeedTile(Bld c)
    {
        Dir f = Dirs.FromYaw(c.Yaw), back = Dirs.Opposite(f);
        return new[] { c.TileX + Dirs.Dx(back), c.TileY + Dirs.Dy(back) };
    }

    // ---------------------------------------------------------------- MachineRow cell (Flow-N frame)
    /// Coater row line c for a row: lowest first-pickup tile of the coated input lanes - 2; int.MinValue when the row
    /// coats nothing. firstPickupDy = per coated input lane, the lowest BeltDy of its sorter cells.
    public static int RowCoaterY(RowSpec spec, IEnumerable<int> firstPickupDy)
    {
        if (spec == null || spec.CoatItems == null || spec.CoatItems.Length == 0 || spec.ProliferatorItem == 0) return int.MinValue;
        var l = firstPickupDy.ToList();
        return l.Count == 0 ? int.MinValue : l.Min() - 2;
    }

    public static bool Coats(RowSpec spec, int item) { return spec != null && spec.CoatItems != null && spec.CoatItems.Contains(item); }

    /// Lane start of an input lane in a coated row (see the cell above).
    public static int LaneStart(RowSpec spec, int item, bool tail, int yStart, int coatY)
    {
        if (coatY == int.MinValue || !Coats(spec, item)) return yStart;
        return Math.Min(yStart, tail ? coatY - 4 : coatY - 2);
    }

    /// Adds the coaters, the feed chain and the feed port "coat:<item>" to a MachineRow fragment (Flow-N frame, before the
    /// fragment is rotated). lanes: lane x -> belts (index = y - laneStart[x]); laneItem: input lane x -> item;
    /// sideSign: +1 when side A is east (x > 0), -1 when side A is west.
    public static void AddToRow(RowSpec spec, Fragment f, int coatY, Dictionary<int, List<Bld>> lanes, Dictionary<int, int> laneStart,
        Dictionary<int, int> laneItem, int sideSign)
    {
        if (coatY == int.MinValue) return;
        var coated = laneItem.Where(kv => Coats(spec, kv.Value)).Select(kv => kv.Key).ToList();
        if (coated.Count == 0) return;
        int c = coatY, feedY = c - 1;
        // coaters on every coated lane (yaw = lane flow, N in this frame)
        foreach (int x in coated)
        {
            List<Bld> lane = lanes[x];
            int idx = c - laneStart[x];
            if (idx < 1 || idx >= lane.Count) { spec.Problems.Add("row " + spec.Name + ": no lane belt under the coater at (" + x + "," + c + ")"); return; }
            Bld cb = Coater(x, c, 0);
            cb.Carries = laneItem[x];
            cb.Role = spec.Name + " coater on " + laneItem[x];
            cb.Chain = lane[idx].Chain;
            f.Add(cb);
        }
        // ramp column one tile outside the outermost lane of side A; feed row from there across to the far coated lane
        int xr = sideSign > 0 ? lanes.Keys.Max() + 1 : lanes.Keys.Min() - 1;
        int xEnd = sideSign > 0 ? coated.Min() : coated.Max();
        Dir across = sideSign > 0 ? Dir.W : Dir.E;
        string chain = ChainPrefix + spec.Name;
        int prolif = spec.ProliferatorItem;
        var tiles = new List<double[]>();   // x, y, z
        tiles.Add(new double[] { xr, c - 4, 0 });           // approach (port)
        tiles.Add(new double[] { xr, c - 3, 0 });           // ground foot
        tiles.Add(new double[] { xr, c - 2, RampZ });       // ramp
        tiles.Add(new double[] { xr, feedY, FeedZ });       // raised corner N -> across
        for (int x = xr - sideSign; sideSign > 0 ? x >= xEnd : x <= xEnd; x -= sideSign) tiles.Add(new double[] { x, feedY, FeedZ });
        var made = new List<Bld>();
        for (int i = 0; i < tiles.Count; i++)
        {
            Dir din = i == 0 ? Dir.N : Dirs.FromDelta((int)(tiles[i][0] - tiles[i - 1][0]), (int)(tiles[i][1] - tiles[i - 1][1]));
            Dir dout = i + 1 < tiles.Count ? Dirs.FromDelta((int)(tiles[i + 1][0] - tiles[i][0]), (int)(tiles[i + 1][1] - tiles[i][1])) : din;
            Bld b = Bld.Belt(spec.BeltItem, tiles[i][0], tiles[i][1], BeltGeo.CornerYaw(din, dout), prolif, chain);
            b.Z = b.Z2 = tiles[i][2];
            b.Cell = FeedCell;
            b.Role = chain + (i == 0 ? " port" : i == 1 ? " foot" : i == 2 ? " ramp" : " feed");
            f.Add(b);
            if (made.Count > 0) Layout.Link(made[made.Count - 1], b, 1);
            made.Add(b);
        }
        Layout.Link(made[made.Count - 1], null, 0);
        double rate = ProliferatorPerMin(spec.Data, prolif, coated.Select(x => LaneRate(spec, laneItem[x])));
        f.AddPort(new Port { Name = PortPrefix + prolif, Kind = PortKind.In, X = xr, Y = c - 4, Dir = Dir.N, Item = prolif, Rate = rate,
            Belt = made[0], Chain = chain });
        spec.Notes.Add(coated.Count + " spray coater(s), proliferator " + prolif + " " + rate.ToString("0.##", CultureInfo.InvariantCulture)
            + "/min (" + Sprays(spec.Data, prolif) + " sprays per item)");
        if (coated.Count > 1) spec.Notes.Add("coater feed passes through " + (coated.Count - 1) + " coater(s) (pass-through feed proven in game 0.9.x files only)");
    }

    static double LaneRate(RowSpec spec, int item)
    {
        if (spec.InputRates == null) return 0;
        int i = Array.IndexOf(spec.InputItems, item);
        return i >= 0 ? spec.InputRates[i] : 0;
    }

    // ---------------------------------------------------------------- router / towers
    /// Tiles the Composer's router must keep free of new belts and raised nodes: every feed node tile (ground tiles under
    /// raised feed nodes and the ramp included) and the lane tiles under every coater body (c-1 .. c+1).
    public static IEnumerable<int[]> RouterBlocked(Layout l)
    {
        foreach (Bld b in l.All)
        {
            if (IsFeed(b)) yield return new[] { b.TileX, b.TileY };
            else if (IsCoater(b))
            {
                Dir f = Dirs.FromYaw(b.Yaw);
                for (int k = -1; k <= 1; k++) yield return new[] { b.TileX + k * Dirs.Dx(f), b.TileY + k * Dirs.Dy(f) };
            }
        }
    }

    static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }

    /// Tower tiles to avoid: under raised feed nodes, and wherever a tower collider would touch a coater's build boxes.
    public static void BlockTowerTiles(Layout l, GameData gd, HashSet<long> blocked)
    {
        foreach (Bld b in l.All)
        {
            if (IsFeed(b) && b.IsRaised) blocked.Add(K(b.TileX, b.TileY));
            if (!IsCoater(b)) continue;
            for (int x = b.TileX - 3; x <= b.TileX + 3; x++)
                for (int y = b.TileY - 3; y <= b.TileY + 3; y++)
                {
                    bool hit = gd != null && gd.HasCollider(SprayCoater) && gd.HasCollider(Ids.Tesla)
                        ? gd.ColliderGap(Bld.Tesla(x, y), b, GameData.EwFactorMin) < 0.05
                        : Math.Abs(x - b.TileX) <= 1 && Math.Abs(y - b.TileY) <= 2;
                    if (hit) blocked.Add(K(x, y));
                }
        }
    }

    // ---------------------------------------------------------------- validator
    /// A ground belt that passes under the coater on its axis (game: belt vs addon passes when within 0.3 m of the addon
    /// axis and aligned within 18.2 deg). Such pairs are not collider-checked.
    public static bool PassesUnder(Bld coater, Bld belt)
    {
        if (!IsCoater(coater) || belt == null || belt.Kind != Kind.Belt) return false;
        Dir f = Dirs.FromYaw(coater.Yaw);
        double dx = belt.X - coater.X, dy = belt.Y - coater.Y;
        double lateral = (f == Dir.N || f == Dir.S ? Math.Abs(dx) : Math.Abs(dy)) * GameData.MetersPerTile * GameData.EwFactorMax;
        double ya = Math.Abs(((belt.Yaw - coater.Yaw) % 180.0 + 180.0) % 180.0);
        bool aligned = ya < 18.2 || ya > 180 - 18.2;
        return lateral <= AxisTolM && aligned && Math.Abs(belt.Z - coater.Z) < 0.25;
    }

    /// Coater checks: every input sorter of a proliferated step's machine sits on a lane with a coater upstream; every
    /// coater on a straight lane belt (one input, no side-join) with straight belts under its body; one feed node at z 1
    /// one tile behind every coater, reached by a proliferator feed chain from a block input port or a proliferator icon
    /// tail (approach - foot - ramp 0.5 - feed level 1, straight up to the first z 1 node; the z 1 run may go on straight
    /// before its first corner and ends dead after the last coater: p-sheet-001.txt (60,3,1) -> corner (60,4,1) -> (65,4,1)
    /// end); feed nodes on the grid (FeedGridTol), >= 0.5 tile from sorters, over free ground or straight ground belts,
    /// never two on one tile; feed chains carry a proliferator.
    public static void Validate(Layout L, ProductionPlan plan, GameData gd, ValidationReport R)
    {
        MarkFeeds(L);   // drawn blueprints carry no cell tags
        var coaters = L.All.Where(IsCoater).ToList();
        var feeds = L.All.Where(IsFeed).ToList();
        int bad = 0;
        Action<string> fail = m => { if (bad++ < 12) R.Fail("coater: " + m); };
        var belts = L.All.Where(b => b.Kind == Kind.Belt).ToList();
        var ground = new Dictionary<long, Bld>();
        foreach (Bld b in belts) if (!b.IsRaised && !Footprints.IsSplitterTouch(b)) ground[K(b.TileX, b.TileY)] = b;
        var preds = new Dictionary<Bld, List<Bld>>();
        foreach (Bld b in belts) if (b.Output != null) { List<Bld> l; if (!preds.TryGetValue(b.Output, out l)) { l = new List<Bld>(); preds[b.Output] = l; } l.Add(b); }
        Func<Bld, List<Bld>> P = b => { List<Bld> l; return preds.TryGetValue(b, out l) ? l : new List<Bld>(); };
        Func<double, double, string> at = (x, y) => "(" + x.ToString("0.###", CultureInfo.InvariantCulture) + "," + y.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        var sorters = L.All.Where(b => b.Kind == Kind.Sorter).ToList();

        // 1. coaters on proliferated steps' lanes
        if (plan != null)
            foreach (Step s in plan.Steps.Where(Coated))
                foreach (Bld m in L.All.Where(b => b.Kind == Kind.Machine && s.Recipe != null && b.RecipeId == s.Recipe.Id && b.ItemId == s.MachineItem))
                    foreach (Bld so in sorters.Where(x => x.Output == m && x.Input != null && x.Input.Kind == Kind.Belt))
                    {
                        bool found = false;
                        var seen = new HashSet<Bld>();
                        for (Bld n = so.Input; n != null && seen.Add(n); )
                        {
                            Bld nn = n;
                            if (coaters.Any(c => c.TileX == nn.TileX && c.TileY == nn.TileY && PassesUnder(c, nn))) { found = true; break; }
                            n = P(n).FirstOrDefault(p => p.OutputToSlot <= 1);
                        }
                        if (!found) fail("input sorter of " + s.Recipe.Name + " at " + at(so.X, so.Y) + " takes from a lane without a spray coater upstream (step incLevel " + s.IncLevel + ")");
                    }

        // 2. each coater: belt under it, straight, single input; body tiles straight on its axis
        var feedAt = new Dictionary<long, Bld>();
        foreach (Bld b in feeds) if (b.IsRaised && Math.Abs(b.Z - FeedZ) < 0.02)
            {
                long k = K(b.TileX, b.TileY);
                if (feedAt.ContainsKey(k)) fail("two feed nodes on tile " + at(b.X, b.Y));
                feedAt[k] = b;
            }
        foreach (Bld c in coaters)
        {
            string w = "coater at " + at(c.X, c.Y);
            if (Math.Abs(c.X - Math.Round(c.X)) > 0.01 || Math.Abs(c.Y - Math.Round(c.Y)) > 0.01) fail(w + " is off the tile grid");
            if (Math.Abs(Dirs.Norm(c.Yaw) % 90.0) > 0.5 && Math.Abs(Dirs.Norm(c.Yaw) % 90.0) < 89.5) fail(w + " yaw " + c.Yaw + " is not a quarter turn");
            Dir f = Dirs.FromYaw(c.Yaw);
            for (int k = -1; k <= 1; k++)
            {
                Bld u; ground.TryGetValue(K(c.TileX + k * Dirs.Dx(f), c.TileY + k * Dirs.Dy(f)), out u);
                if (u == null) { if (k == 0) fail(w + " has no belt under it"); continue; }
                if (!PassesUnder(c, u)) { fail(w + ": belt " + at(u.X, u.Y) + " under its body is not along its axis"); continue; }
                var ps = P(u);
                if (ps.Any(p => p.OutputToSlot > 1)) fail(w + ": belt " + at(u.X, u.Y) + " under its body takes a side-join");
                if (k == 0 && (ps.Count(p => p.OutputToSlot <= 1) != 1 || u.Output == null)) fail(w + ": the belt under it is not a straight run (needs one predecessor and a successor)");
                if (u.Output != null && u.Output.Kind == Kind.Belt && Dirs.FromYaw(u.Yaw) != Dirs.FromYaw(c.Yaw) && Dirs.FromYaw(u.Yaw) != Dirs.Opposite(Dirs.FromYaw(c.Yaw))) fail(w + ": belt " + at(u.X, u.Y) + " turns under the coater");
            }
            int[] ft = FeedTile(c);
            if (!feedAt.ContainsKey(K(ft[0], ft[1]))) fail(w + " has no proliferator feed node at z " + FeedZ + " on " + at(ft[0], ft[1]) + " (one tile behind it)");
            foreach (Bld o in coaters) if (o != c && o.TileX == c.TileX && o.TileY == c.TileY) fail(w + ": two coaters on one tile");
        }

        // 3. feed chains: a block input port with a proliferator, its lane walked to the feed cell: approach - foot -
        //    ramp 0.5 - level 1, nodes on the grid
        var reached = new HashSet<long>();
        int chains = 0;
        foreach (Bld s0 in feeds.Where(b => !P(b).Any(p => IsFeed(p))))
        {
            // first belt of the feed cell: walk back to the block input port
            Bld n0 = s0; var back = new HashSet<Bld>();
            while (back.Add(n0)) { Bld p = P(n0).FirstOrDefault(q => q.OutputToSlot <= 1); if (p == null) break; n0 = p; }
            Port port = L.Ports.FirstOrDefault(p => p.Kind == PortKind.In && p.Belt == n0);
            string w = "feed chain at " + at(s0.X, s0.Y);
            // a drawn blueprint has no block ports: its chain start is an icon tail carrying the proliferator
            // (p-sheet-001.txt #96 (60,0) params [1142,0])
            int portItem = port != null ? port.Item : n0.Parameters != null && n0.Parameters.Length == 2 && IsProliferator(n0.Parameters[0]) ? n0.Parameters[0] : 0;
            if (portItem == 0) { fail(w + " is not fed from a block input port or a proliferator icon tail"); continue; }
            chains++;
            if (!IsProliferator(portItem)) fail(w + ": port item " + portItem + " is not a proliferator");
            var chainL = new List<Bld>();
            var seen = new HashSet<Bld>();
            for (Bld n = n0; n != null && n.Kind == Kind.Belt && seen.Add(n); n = n.Output) chainL.Add(n);
            if (chainL.Any(n => n.Carries != 0 && n.Carries != portItem)) fail(w + ": belts carry another item than the port " + portItem);   // Carries 0 = imported belt, item unknown
            int firstUp = chainL.FindIndex(n => IsFeed(n) && n.IsRaised);
            if (firstUp < 2) { fail(w + ": needs an approach node and a ground foot before the ramp"); continue; }
            Bld a = chainL[firstUp - 2], foot = chainL[firstUp - 1], ramp = chainL[firstUp];
            if (firstUp + 1 >= chainL.Count) { fail(w + " ends on its ramp"); continue; }
            Bld top = chainL[firstUp + 1];
            Func<Bld, Bld, Dir> dirTo = (p, q) => Dirs.FromDelta(Math.Sign(q.TileX - p.TileX), Math.Sign(q.TileY - p.TileY));
            if (Math.Abs(ramp.Z - RampZ) > 0.02 || Math.Abs(top.Z - FeedZ) > 0.02) fail(w + ": ramp must be ground -> z " + RampZ + " -> z " + FeedZ + " (L1.txt #68 -> #67 -> #70)");
            if (a.IsRaised || foot.IsRaised || dirTo(a, foot) != dirTo(foot, ramp) || dirTo(foot, ramp) != dirTo(ramp, top)) fail(w + ": approach, foot, ramp and first feed node are not in one straight line");
            for (int i = firstUp + 1; i < chainL.Count; i++)
            {
                Bld n = chainL[i];
                if (Math.Abs(n.Z - FeedZ) > 0.02) fail(w + ": feed node " + at(n.X, n.Y) + " at z " + n.Z + " (expected " + FeedZ + "; no way down)");
                reached.Add(K(n.TileX, n.TileY));
            }
        }
        foreach (Bld b in feeds)
        {
            string w = "feed node " + at(b.X, b.Y);
            if (Math.Abs(b.X - Math.Round(b.X)) > FeedGridTol || Math.Abs(b.Y - Math.Round(b.Y)) > FeedGridTol) fail(w + " is off the tile grid");
            if (!b.IsRaised) continue;
            double ds = sorters.Count == 0 ? double.MaxValue : sorters.Min(s => GameData.SegmentDist(b.X, b.Y, s.X, s.Y, s.X2, s.Y2));
            if (ds < 0.5) fail(w + " is " + ds.ToString("0.##", CultureInfo.InvariantCulture) + " tile from a sorter");
            Bld occ = L.Grid.At(b.TileX, b.TileY);
            if (occ != null && occ.Kind != Kind.Belt) fail(w + " is over a building " + occ);
            Bld g; ground.TryGetValue(K(b.TileX, b.TileY), out g);
            if (g != null && !IsFeed(g))
            {
                var gp = P(g);
                bool straight = g.Output != null && gp.Count(p => p.OutputToSlot <= 1) == 1 && !gp.Any(p => p.OutputToSlot > 1)
                    && Dirs.FromYaw(g.Yaw) == Dirs.FromYaw(gp.First(p => p.OutputToSlot <= 1).Yaw);
                if (!straight) fail(w + ": the ground belt under it is not straight through (turns, starts, ends or takes a side-join)");
            }
            else if (g != null) fail(w + " is over its own chain");
            foreach (Bld o in L.All)
                if (o.Kind == Kind.Belt && o != b && o.IsRaised && !IsFeed(o) && o.TileX == b.TileX && o.TileY == b.TileY) fail(w + " shares its tile with another raised node");
        }
        // 4. the proliferator lane reaches every coater
        foreach (Bld c in coaters)
        {
            int[] ft = FeedTile(c);
            if (feedAt.ContainsKey(K(ft[0], ft[1])) && !reached.Contains(K(ft[0], ft[1])))
                fail("coater at " + at(c.X, c.Y) + ": its feed node is not reached by a proliferator lane from a block input port");
        }
        if (bad > 12) R.Fail("coater: " + bad + " problems in total");
        if (coaters.Count > 0)
            R.Note("spray coaters: " + coaters.Count + ", proliferator feed chains: " + chains + " (cell COATER, 7-logi\\L1.txt #36/#65)");
    }
}
}
