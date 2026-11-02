// Modules.cs - parametric building blocks that return Fragments (lib\CONTRACT.md, "lib/Modules.cs").
// Pure model code: no game DLL. Sorter geometry comes only from Geo / SorterGeo (Cells.cs); splitter belt offsets
// are the measured values of refs\splitters.txt (CONTRACT.md "Splitter geometry").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

/// Parameters of a machine row (a column of machines with belt lanes alongside, lanes flowing along the row).
public class RowSpec
{
    public string Name;                       // fragment name, e.g. "magnet", "coil"
    public int MachineItem;                   // 2302, 2303, ...
    public int RecipeId;
    public int Count;                         // machines in the row
    public int Pitch;                         // 0 = preset default (3 when all sorters are L1, else 4)
    public int[] InputItems;                  // 1..4 items, in lane-priority order (first = highest rate)
    public double[] InputRates;               // items/min per input (whole row)
    public int OutputItem;
    public double OutputRate;                 // items/min (whole row)
    public Dir Flow = Dir.N;                  // lane flow direction; machines stacked along it
    public bool Mirror;                       // swap east/west sides
    public int BeltItem = Ids.BeltMk2, SorterItem = Ids.SorterMk2;
    public int OutputLength;                  // 0 = preset default; user rule: output sorter L2 (max L3) allowed

    // ---- extensions (Modules.cs only) ----
    public GameData Data;                     // optional: enables the per-sorter throughput check
    public bool LaneIcons = true;             // first tile of every INPUT lane gets Parameters = [item, 0] (rule 5)
    /// Input items whose lane starts with a TAIL: one belt before the first pickup tile (at least down to the row's
    /// bottom line). The tail is the block's connection point and carries the icon (user review 2026-10-31).
    public int[] TailItems;
    /// Final orientation of the row in the blueprint (the Composer rotates fragments by whole quarter turns): true when
    /// the machines end up stacked along E-W (blueprint x). With Safe60 the E-W collider pitch that stays clean up to
    /// 60 deg latitude is used (colliders.json minIntegerPitchEW_upTo60deg).
    public bool AlongEW, Safe60;
    public int FinalQuarters;                 // quarter turns applied after MachineRow (machine yaw in the blueprint)
    public readonly List<string> Problems = new List<string>();  // filled by MachineRow: copy into Layout.Errors
    public readonly List<string> Notes = new List<string>();     // filled by MachineRow: informational
}

public static class Modules
{
    // ================================================================ MachineRow

    /// One sorter requirement of the row: an input or the output, its side, length and the chosen geometries.
    class Req
    {
        public string Role; public int Item; public bool In; public Dir Side; public int L; public double PerMachine;
        public List<SorterGeo> Geos = new List<SorterGeo>();
    }

    static readonly int[] InLateralPref = { -1, 1, 0 };
    static readonly int[] OutLateralPref = { 0, -1, 1 };

    /// Row of machines with the preset lane arrangement for 1/2/3/4 inputs (CONTRACT.md "MachineRow presets").
    /// Fragment: machines, all lane belts (full length past every machine), sorters; ports "in:<item>" (first tile of
    /// each input lane) and "out:<item>" (last tile of the output lane). Origin: first machine center at (0,0) for
    /// Flow N; other flows are the rigid rotation around (0,0). Input-lane icons follow spec.LaneIcons.
    /// Throws ArgumentException for a bad spec and InvalidOperationException when Geo has no proven cell for a preset.
    public static Fragment MachineRow(RowSpec spec)
    {
        if (spec == null) throw new ArgumentNullException("spec");
        if (spec.Count < 1) throw new ArgumentException("row " + spec.Name + ": Count must be >= 1");
        if (spec.InputItems == null || spec.InputItems.Length < 1 || spec.InputItems.Length > 4)
            throw new ArgumentException("row " + spec.Name + ": 1..4 input items required");
        if (spec.InputRates != null && spec.InputRates.Length != spec.InputItems.Length)
            throw new ArgumentException("row " + spec.Name + ": InputRates length differs from InputItems");
        if (spec.InputItems.Distinct().Count() != spec.InputItems.Length || spec.InputItems.Contains(spec.OutputItem))
            throw new ArgumentException("row " + spec.Name + ": duplicate item among inputs/output");
        // lanes hook (PLAN.md decision 8): Mk.III belts/sorters are allowed, the tiers are caps (engine\Lanes.cs)
        spec.Problems.Clear(); spec.Notes.Clear();

        int n = spec.InputItems.Length;
        Dir sideA = spec.Mirror ? Dir.W : Dir.E, sideB = Dirs.Opposite(sideA);

        // ---- requirements: rule 1 (L1 one per side), rule 2 (3rd/4th at L2 behind A/B), rule 3 (output L <= 3)
        var reqs = new List<Req>();
        for (int i = 0; i < n; i++)
            reqs.Add(new Req { Role = "in" + (i + 1), Item = spec.InputItems[i], In = true, Side = i % 2 == 0 ? sideA : sideB,
                L = i < 2 ? 1 : 2, PerMachine = spec.InputRates != null ? spec.InputRates[i] / spec.Count : 0 });
        int lanesA = reqs.Count(r => r.Side == sideA), lanesB = reqs.Count(r => r.Side == sideB);
        var outSides = new List<Dir[]>();   // (side) candidates in preference order: B first, then A
        outSides.Add(new[] { sideB }); outSides.Add(new[] { sideA });
        var outReq = new Req { Role = "out", Item = spec.OutputItem, In = false, PerMachine = spec.OutputRate / spec.Count };
        // output sorters must carry the machine's FULL-speed output (user rule: a machine at 100% is never blocked by
        // its output sorter; e.g. Mk.II assembler circuit 120/min > L2 Mk.II 90/min -> second output sorter)
        RecipeDef rdef = spec.Data != null ? spec.Data.Recipe(spec.RecipeId) : null;
        if (rdef != null && rdef.TimeSec > 0)
            outReq.PerMachine = Math.Max(outReq.PerMachine, rdef.OutCount(spec.OutputItem) * 60.0 * spec.Data.Speed(spec.MachineItem) / rdef.TimeSec);

        bool placed = false; string why = "";
        foreach (var os in outSides)
        {
            Dir side = os[0];
            int minL = (side == sideB ? lanesB : lanesA) + 1;
            int L = spec.OutputLength > 0 ? spec.OutputLength : minL;
            if (L < minL) { why += " side " + side + ": output L" + L + " would collide with an input lane (needs >= L" + minL + ");"; continue; }
            if (L > 3) { why += " side " + side + ": output would need L" + L + " (rule 3 max L3);"; continue; }
            outReq.Side = side; outReq.L = L;
            var all = new List<Req>(reqs); all.Add(outReq);
            foreach (var r in all) r.Geos.Clear();
            int yaw;
            if (Assign(spec.MachineItem, all, 0, new HashSet<int>(new[] { 0, 90, 180, 270 }), out yaw))
            {
                reqs = all; placed = true;
                if (side != sideB) spec.Notes.Add("output placed on side A (side B has no proven cell)");
                break;
            }
            why += " side " + side + " L" + L + ": no proven sorter cell combination in Geo;";
        }
        // fallback: unproven cells ("derived" reversed inputs, "inferred" re-yawed) at one fixed machine yaw.
        // Used only when no proven combination exists; every such cell is reported in Notes (needs an in-game test).
        if (!placed)
        {
            foreach (var os in outSides)
            {
                if (placed) break;
                Dir side = os[0];
                int minL = (side == sideB ? lanesB : lanesA) + 1;
                int L = spec.OutputLength > 0 ? spec.OutputLength : minL;
                if (L < minL || L > 3) continue;
                outReq.Side = side; outReq.L = L;
                var all = new List<Req>(reqs); all.Add(outReq);
                foreach (int fy in new[] { 0, 90, 180, 270 })
                {
                    foreach (var r in all) r.Geos.Clear();
                    int yaw;
                    anyYaw = fy;
                    bool ok;
                    try { ok = Assign(spec.MachineItem, all, 0, new HashSet<int>(new[] { fy }), out yaw); }
                    finally { anyYaw = null; }
                    if (!ok) continue;
                    reqs = all; placed = true;
                    foreach (var r in all) foreach (var g in r.Geos)
                        if (!g.Proven) spec.Notes.Add(r.Role + " uses unproven cell " + g.Name + " [" + g.Status + "] - needs an in-game test");
                    break;
                }
                if (!placed) why += " side " + side + " L" + L + ": no unproven (derived/inferred) combination either;";
            }
        }
        if (!placed)
            throw new InvalidOperationException("row " + spec.Name + " (" + n + " inputs, machine " + spec.MachineItem + "):" + why);

        int machineYaw = MachineYaw(spec.MachineItem, reqs);

        // ---- throughput: extra sorters on free laterals of the same lane, else report
        if (spec.Data == null) spec.Notes.Add("sorter throughput not checked (RowSpec.Data is null)");
        else
        {
            foreach (var r in reqs)
            {
                double cap = spec.Data.SorterRate(spec.SorterItem, r.L);
                if (cap <= 0) { spec.Problems.Add("row " + spec.Name + ": no sorter rate for item " + spec.SorterItem + " L" + r.L); continue; }
                int need = (int)Math.Ceiling(r.PerMachine / cap - 1e-9);
                while (r.Geos.Count < need)
                {
                    SorterGeo extra = ExtraGeo(spec.MachineItem, reqs, r, machineYaw, false) ?? ExtraGeo(spec.MachineItem, reqs, r, machineYaw, true);
                    if (extra == null) break;
                    if (!extra.Proven) spec.Notes.Add(r.Role + " extra sorter uses unproven cell " + extra.Name + " [" + extra.Status + "] - needs an in-game test");
                    r.Geos.Add(extra);
                }
                if (r.Geos.Count < need)
                    spec.Problems.Add(string.Format(CultureInfo.InvariantCulture,
                        "row {0}: {1} item {2} needs {3:0.##}/min per machine, {4} sorter(s) L{5} carry {6:0.##}/min",
                        spec.Name, r.Role, r.Item, r.PerMachine, r.Geos.Count, r.L, cap * r.Geos.Count));
                else if (r.Geos.Count > 1) spec.Notes.Add(r.Role + ": " + r.Geos.Count + " sorters per machine for throughput");
            }
        }
        if (n >= 3) spec.Notes.Add(n + "-input preset (rule 2" + (n == 4 ? "+3" : "") + ") is not proven in game yet - needs an in-game test");

        bool onlyL1 = reqs.All(r => r.L == 1);
        int pitch = ChoosePitch(spec, onlyL1 ? 3 : 4, machineYaw);

        // ---- build in the Flow-N frame
        var f = new Fragment(spec.Name);
        int y0 = -1, y1 = (spec.Count - 1) * pitch + 1;          // output lane covers every machine's footprint rows
        var lanes = new Dictionary<int, List<Bld>>();             // lane x -> tiles (index = y - laneStart[x])
        var laneStart = new Dictionary<int, int>();
        foreach (var r in reqs)
        {
            int lx = r.Geos[0].BeltDx;
            if (lanes.ContainsKey(lx)) throw new InvalidOperationException("row " + spec.Name + ": two requirements on lane x=" + lx);
            string chain = spec.Name + ":" + (r.In ? "in:" : "out:") + r.Item;
            var tiles = new List<Bld>();
            // input lanes end at the last machine's pickup tile: no dead-end stub beyond it (user rule, proven by the
            // user's in-game edit of refs\engine-coil-360.txt, which removed exactly those tail tiles)
            int yEnd = r.In ? (spec.Count - 1) * pitch + r.Geos.Max(g => g.BeltDy) : y1;
            // block input lanes start one belt before the first pickup (the tail, user's edit of
            // refs\engine-electric-motor-120-v3-user.txt), never above the row's bottom line
            int yStart = y0;
            if (r.In && spec.TailItems != null && spec.TailItems.Contains(r.Item)) yStart = Math.Min(y0, r.Geos.Min(g => g.BeltDy) - 1);
            laneStart[lx] = yStart;
            for (int y = yStart; y <= yEnd; y++)
            {
                Bld b = f.Add(Bld.Belt(spec.BeltItem, lx, y, 0, r.Item, chain));
                b.Role = spec.Name + " " + r.Role + " lane";
                if (tiles.Count > 0) Layout.Link(tiles[tiles.Count - 1], b, 1);
                tiles.Add(b);
            }
            Layout.Link(tiles[tiles.Count - 1], null, 0);
            if (r.In && spec.LaneIcons) tiles[0].Parameters = new[] { r.Item, 0 };
            lanes[lx] = tiles;
            Bld end = r.In ? tiles[0] : tiles[tiles.Count - 1];
            f.AddPort(new Port { Name = (r.In ? "in:" : "out:") + r.Item, Kind = r.In ? PortKind.In : PortKind.Out,
                X = lx, Y = r.In ? yStart : y1, Dir = Dir.N, Item = r.Item,
                Rate = r.In ? (spec.InputRates != null ? spec.InputRates[reqs.IndexOf(r)] : 0) : spec.OutputRate,
                Belt = end, Chain = chain });
        }
        for (int k = 0; k < spec.Count; k++)
        {
            int my = k * pitch;
            Bld m = f.Add(Bld.Machine(spec.MachineItem, 0, my, machineYaw, spec.RecipeId));
            m.Carries = spec.OutputItem; m.Role = spec.Name + " machine " + (k + 1);
            foreach (var r in reqs)
                foreach (var g in r.Geos)
                {
                    Bld belt = lanes[g.BeltDx][my + g.BeltDy - laneStart[g.BeltDx]];
                    Bld s = g.Make(m, belt, spec.SorterItem, r.Item);
                    s.Role = spec.Name + " " + r.Role + " L" + r.L + " (" + g.Name + ")";
                    f.Add(s);
                }
        }

        int q = (int)spec.Flow;
        if (q != 0) f.RotateCW(q, 0, 0);
        return f;
    }

    /// Machine pitches (center to center along a row, tiles) seen in blueprints that work in game (analysis\patterns.md):
    /// arc smelter 3 (SMELT-COL-1 / coil-360-v3, ROW-1 x-imp Iron 48) and 4 (smelter-arc-3); plane smelter 3
    /// (2-smelt/Smelters); assemblers Mk.I-III only 4 (assemblers-8, -8-2L, coil-360-v2/v3, 2-/3-input templates).
    /// An assembler collider is 3.04 tiles wide (game-data), so assemblers at pitch 3 collide in game.
    public static int[] ProvenPitches(int machineItem)
    {
        switch (machineItem)
        {
            case Ids.ArcSmelter: return new[] { 3, 4 };
            case Ids.PlaneSmelter: return new[] { 3 };
            case Ids.AsmMk1: case Ids.AsmMk2: case Ids.AsmMk3: return new[] { 4 };
        }
        return new int[0];
    }

    /// Collider pitch from the game data (analysis\buildings\colliders.json derived_pitch, exact OBB test): the smallest
    /// integer centre distance at which two machines of the row never overlap, along the row's FINAL axis (rows end up
    /// along x when the Composer turns them by an odd number of quarters) and the machine's final yaw. safe60 = the E-W
    /// value that stays clean up to 60 deg latitude. 0 = unknown.
    public static int ColliderPitch(GameData gd, int machineItem, double finalYaw, bool alongEW, bool safe60)
    {
        return gd == null ? 0 : gd.MinPitch(machineItem, finalYaw, alongEW, safe60);
    }

    /// Smallest pitch >= what the sorters need (3 with only L1 sorters, 4 with L2/L3) and >= the collider pitch
    /// (colliders.json; arc smelter 3, assemblers 4, E-W up to 60 deg the same). Without collider data: the pitches
    /// seen in working blueprints (ProvenPitches). An explicit spec.Pitch must satisfy both.
    static int ChoosePitch(RowSpec spec, int need, int machineYaw)
    {
        int col = ColliderPitch(spec.Data, spec.MachineItem, machineYaw + 90.0 * spec.FinalQuarters, spec.AlongEW, spec.Safe60);
        if (col > 0)
        {
            int min = Math.Max(need, col);
            if (spec.Pitch > 0)
            {
                if (spec.Pitch < min) throw new ArgumentException("row " + spec.Name + ": pitch " + spec.Pitch + " < " + min + " (sorters need " + need + ", collider pitch " + col + ")");
                return spec.Pitch;
            }
            return min;
        }
        int[] ok = ProvenPitches(spec.MachineItem);
        if (spec.Pitch > 0)
        {
            if (spec.Pitch < need || !ok.Contains(spec.Pitch))
                throw new ArgumentException("row " + spec.Name + ": pitch " + spec.Pitch + " is not a proven pitch >= " + need + " for machine " + spec.MachineItem
                    + " (proven: " + string.Join(",", ok.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")");
            return spec.Pitch;
        }
        foreach (int p in ok.OrderBy(p => p)) if (p >= need) return p;
        throw new ArgumentException("row " + spec.Name + ": no proven machine pitch >= " + need + " for machine " + spec.MachineItem);
    }

    /// Backtracking over the proven cells: distinct lateral rows per side, one machine yaw for all cells.
    static bool Assign(int machineItem, List<Req> reqs, int i, HashSet<int> yaws, out int yaw)
    {
        yaw = yaws.Count > 0 ? yaws.Min() : 0;
        if (i == reqs.Count) return yaws.Count > 0;
        Req r = reqs[i];
        // candidates in lateral preference order; proven cells before unproven ones (only the fallback returns those)
        var cands = (r.In ? InLateralPref : OutLateralPref).Select(lat => Lookup(machineItem, r.In, r.Side, r.L, lat))
            .Where(c => c != null).Select((c, k) => new { c, k }).OrderBy(a => a.c.Proven ? 0 : 1).ThenBy(a => a.k).Select(a => a.c).ToList();
        foreach (SorterGeo g in cands)
        {
            int actual = Lateral(g, r.Side);
            if (reqs.Take(i).Any(o => o.Side == r.Side && o.Geos.Any(og => Lateral(og, o.Side) == actual))) continue;
            if (r.Geos.Any(og => Lateral(og, r.Side) == actual)) continue;
            var ys = new HashSet<int>(yaws); ys.IntersectWith(YawsFor(g));
            if (ys.Count == 0) continue;
            r.Geos.Clear(); r.Geos.Add(g);
            if (Assign(machineItem, reqs, i + 1, ys, out yaw)) return true;
            r.Geos.Clear();
        }
        return false;
    }

    /// Geo.Find plus a sanity check of what came back (direction, side, length); null when unusable.
    [ThreadStatic] static int? anyYaw;   // set by the unproven fallback of MachineRow: Geo.FindAny at this machine yaw

    static SorterGeo Lookup(int machineItem, bool beltToMachine, Dir side, int length, int lateral)
    {
        SorterGeo g = anyYaw.HasValue
            ? Geo.FindAny(machineItem, beltToMachine, side, length, lateral, anyYaw.Value)
            : Geo.Find(machineItem, beltToMachine, side, length, lateral);
        if (g == null || g.BeltToMachine != beltToMachine || g.Length != length) return null;
        int along = side == Dir.E || side == Dir.W ? g.BeltDx : g.BeltDy;
        int sign = side == Dir.E || side == Dir.N ? 1 : -1;
        if (along != sign * (length + 1)) return null;
        if (Math.Abs(Lateral(g, side)) > 1) return null;
        return g;
    }

    static int Lateral(SorterGeo g, Dir side) { return side == Dir.E || side == Dir.W ? g.BeltDy : g.BeltDx; }

    /// Machine yaws (0/90/180/270) for which the geometry's machine-side end lands in g.Slot.
    static HashSet<int> YawsFor(SorterGeo g)
    {
        double px = g.BeltToMachine ? g.Ex : g.Sx, py = g.BeltToMachine ? g.Ey : g.Sy;
        // face of the machine the sorter crosses: opposite of travel for an input, along travel for an output
        double vx = g.Ex - g.Sx, vy = g.Ey - g.Sy;
        Dir travel = Math.Abs(vx) >= Math.Abs(vy) ? (vx > 0 ? Dir.E : Dir.W) : (vy > 0 ? Dir.N : Dir.S);
        Dir face = g.BeltToMachine ? Dirs.Opposite(travel) : travel;
        var s = new HashSet<int>();
        foreach (int yaw in new[] { 0, 90, 180, 270 }) if (SlotAt(yaw, px, py, face) == g.Slot) s.Add(yaw);
        return s;
    }

    static int MachineYaw(int machineItem, List<Req> reqs)
    {
        var ys = new HashSet<int>(new[] { 0, 90, 180, 270 });
        foreach (var r in reqs) foreach (var g in r.Geos) ys.IntersectWith(YawsFor(g));
        if (ys.Count == 0) throw new InvalidOperationException("machine " + machineItem + ": proven cells disagree on machine yaw");
        return ys.Min();
    }

    /// 3x3 slot table (patterns.md section 0): slot of a sorter touching the machine at (dx,dy) (relative to its
    /// center, world frame) through world face worldFace, for a machine with the given yaw. The face settles the
    /// corner cases ((-0.81,+0.79) is slot 11 from the west, slot 0 from the north).
    public static int SlotAt(double machineYaw, double dx, double dy, Dir worldFace) { return Geo.SlotAt(machineYaw, dx, dy, worldFace); }

    /// Another proven cell on the same lane (side, L) at a lateral row nobody on that side uses yet.
    static SorterGeo ExtraGeo(int machineItem, List<Req> reqs, Req r, int machineYaw, bool unproven)
    {
        foreach (int lat in new[] { -1, 0, 1 })
        {
            if (reqs.Any(o => o.Side == r.Side && o.Geos.Any(og => Lateral(og, o.Side) == lat))) continue;
            SorterGeo g;
            if (unproven) { anyYaw = machineYaw; try { g = Lookup(machineItem, r.In, r.Side, r.L, lat); } finally { anyYaw = null; } }
            else g = Lookup(machineItem, r.In, r.Side, r.L, lat);
            if (g == null || Lateral(g, r.Side) != lat || g.BeltDx != r.Geos[0].BeltDx && (r.Side == Dir.E || r.Side == Dir.W)) continue;
            if (!YawsFor(g).Contains(machineYaw)) continue;
            return g;
        }
        return null;
    }

    // ================================================================ belts

    /// Belt chain through tiles (straight + 90-degree corners; corner yaw = bisector). Links consecutive tiles with
    /// outputToSlot 1; last tile outputToSlot 0, or side-joins into mergeInto (rule 6) when given.
    /// Every tile: Carries = carries, Chain = chain. Ports "start" (In, first tile) and "end" (Out, last tile).
    public static Fragment BeltRun(string chain, int beltItem, int carries, IList<int[]> tiles, Bld mergeInto)
    {
        if (tiles == null || tiles.Count == 0) throw new ArgumentException("BeltRun " + chain + ": no tiles");
        if (tiles.Count == 1 && mergeInto == null) throw new ArgumentException("BeltRun " + chain + ": a single tile needs mergeInto to define its direction");
        var seen = new HashSet<long>();
        for (int i = 0; i < tiles.Count; i++)
        {
            if (!seen.Add(((long)tiles[i][0] << 32) ^ (uint)tiles[i][1]))
                throw new ArgumentException("BeltRun " + chain + ": tile " + tiles[i][0] + "," + tiles[i][1] + " used twice");
            if (i > 0 && Math.Abs(tiles[i][0] - tiles[i - 1][0]) + Math.Abs(tiles[i][1] - tiles[i - 1][1]) != 1)
                throw new ArgumentException("BeltRun " + chain + ": tiles " + i + " and " + (i - 1) + " are not adjacent");
        }
        string bad = BridgeShapeError(tiles);
        if (bad != null) throw new ArgumentException("BeltRun " + chain + ": " + bad);
        var f = new Fragment(chain);
        var made = new List<Bld>();
        Dir firstDir = Dir.N, lastDir = Dir.N;
        for (int i = 0; i < tiles.Count; i++)
        {
            int x = tiles[i][0], y = tiles[i][1];
            Dir? dIn = null, dOut = null;
            if (i > 0) dIn = Dirs.FromDelta(x - tiles[i - 1][0], y - tiles[i - 1][1]);
            if (i < tiles.Count - 1) dOut = Dirs.FromDelta(tiles[i + 1][0] - x, tiles[i + 1][1] - y);
            else if (mergeInto != null) dOut = Dirs.FromDelta(mergeInto.TileX - x, mergeInto.TileY - y);
            if (dIn.HasValue && dOut.HasValue && dOut.Value == Dirs.Opposite(dIn.Value))
                throw new ArgumentException("BeltRun " + chain + ": U-turn at " + x + "," + y);
            double yaw = CornerYaw(dIn.HasValue ? dIn.Value : dOut.Value, dOut.HasValue ? dOut.Value : dIn.Value);
            Bld b = f.Add(Bld.Belt(beltItem, x, y, yaw, carries, chain));
            if (IsRaisedTile(tiles[i])) { b.Z = b.Z2 = GameData.BridgeZ; b.Role = chain + " bridge"; }
            if (made.Count > 0) Layout.Link(made[made.Count - 1], b, 1);
            made.Add(b);
            if (i == 0) firstDir = dOut.HasValue ? dOut.Value : dIn.Value;
            lastDir = dOut.HasValue ? dOut.Value : dIn.Value;
        }
        Bld last = made[made.Count - 1];
        if (mergeInto != null) SideMerge(last, mergeInto);
        else Layout.Link(last, null, 0);
        f.AddPort(new Port { Name = "start", Kind = PortKind.In, X = tiles[0][0], Y = tiles[0][1], Dir = firstDir, Item = carries, Belt = made[0], Chain = chain });
        f.AddPort(new Port { Name = "end", Kind = PortKind.Out, X = last.TileX, Y = last.TileY, Dir = lastDir, Item = carries, Belt = last, Chain = chain });
        return f;
    }

    // ================================================================ bridge (cell BRIDGE, analysis\bridges.md)

    /// A tile entry {x, y, 1} is a raised bridge node (z = GameData.BridgeZ) on the tile of a crossed ground belt.
    public static bool IsRaisedTile(int[] t) { return t != null && t.Length > 2 && t[2] != 0; }

    /// Cell RAISED_RUN (generalises BRIDGE; game-proven by refs\bridges-splitters-user.txt and the user's in-game edit
    /// refs\engine-turbine-60-v2-userupgrade.txt, analysis\user-upgrade-turbine.md section 1): approach A, ground foot
    /// G0, raised nodes R1..Rn at z 0.5 (any n), ground foot G1, approach B. A-G0-R1 and Rn-G1-B are straight lines
    /// (one-tile ramps, no turn on a ramp, conditions.md S4); R2..Rn-1 may be 90-degree corners (raised corner records
    /// with the bisector yaw, as N->W 315 / W->S 225 in the user's file). Returns null when every raised run fits.
    public static string BridgeShapeError(IList<int[]> tiles)
    {
        Func<int, int, bool> same = (a, b) => tiles[a][0] - tiles[a - 1][0] == tiles[b][0] - tiles[b - 1][0] && tiles[a][1] - tiles[a - 1][1] == tiles[b][1] - tiles[b - 1][1];
        for (int i = 0; i < tiles.Count; i++)
        {
            if (!IsRaisedTile(tiles[i]) || (i > 0 && IsRaisedTile(tiles[i - 1]))) continue;
            int j = i; while (j + 1 < tiles.Count && IsRaisedTile(tiles[j + 1])) j++;
            if (i < 2 || j + 2 >= tiles.Count) return "raised run at " + tiles[i][0] + "," + tiles[i][1] + " needs a ground foot and an approach node on both sides";
            // A -> G0 -> R1 -> R1's successor straight; Rn's predecessor -> Rn -> G1 -> B straight
            if (!same(i - 1, i) || !same(i, i + 1)) return "raised run at " + tiles[i][0] + "," + tiles[i][1] + " is not straight from the approach node over the up-ramp";
            if (!same(j, j + 1) || !same(j + 1, j + 2)) return "raised run at " + tiles[i][0] + "," + tiles[i][1] + " is not straight over the down-ramp to the far approach node";
        }
        return null;
    }

    // ================================================================ depot buffer (cell DEPOT_BUFFER)

    /// Depot Mk.I slot positions (analysis\buildings\slots.json storage-1: +-1 m = 0.796 tiles, face 0.795): 3 slots per
    /// face, clockwise from the NW corner (face N 0,1,2 W->E; E 3,4,5 N->S; S 6,7,8 E->W; W 9,10,11 S->N).
    public static double[] DepotSlotPos(int slot)
    {
        Dir face = (Dir)(slot / 3); int k = slot % 3;
        Dir t = Dirs.Right(face);
        return new[] { 0.795 * Dirs.Dx(face) + (k - 1) * 0.796 * Dirs.Dx(t), 0.795 * Dirs.Dy(face) + (k - 1) * 0.796 * Dirs.Dy(t) };
    }

    /// Cell DEPOT_BUFFER of the user's in-game edit (refs\engine-turbine-60-v2-userupgrade.txt #482 depot, #491 in,
    /// #492/#493 out; analysis\user-upgrade-turbine.md section 2): three straight product belts t0 (upstream), t1, t2
    /// (downstream) along flow; Depot Mk.I (yaw 0) centred 2 tiles beside t1; Sorter L1 t0 -> depot (slot under t0),
    /// two Sorters L1 depot -> t1 and depot -> t2. Every sorter is straight across (belt end = slot projected on the belt
    /// line), belt offset floor(20 d - 0.001) (reproduces the game's 4 / -1 / -5). The belt stays continuous.
    /// Exact game cell: flow E, depot on the right (south) = IsProvenDepot; other orientations are the same cell turned
    /// or mirrored (derived). Returns depot + 3 sorters (sorters carry Cell names of the user's records).
    public static Fragment DepotBuffer(Bld t0, Bld t1, Bld t2, Dir flow, bool depotRight, int sorterItem, int carries)
    {
        Dir side = depotRight ? Dirs.Right(flow) : Dirs.Left(flow);
        Dir face = Dirs.Opposite(side);
        var f = new Fragment("depot-buffer");
        Bld dep = f.Add(Bld.Depot(t1.X + 2 * Dirs.Dx(side), t1.Y + 2 * Dirs.Dy(side)));
        dep.Carries = carries; dep.Role = "depot buffer";
        Bld[] ts = { t0, t1, t2 };
        string[] cells = { "depot-in", "depot-out1", "depot-out2" };
        for (int i = 0; i < 3; i++)
        {
            Bld t = ts[i];
            // slot on the face under this belt tile
            double lat = (t.X - dep.X) * Dirs.Dx(Dirs.Right(face)) + (t.Y - dep.Y) * Dirs.Dy(Dirs.Right(face));
            int k = lat < -0.5 ? 0 : lat > 0.5 ? 2 : 1;
            int slot = 3 * (int)face + k;
            double[] sp = DepotSlotPos(slot);
            double sx = dep.X + sp[0], sy = dep.Y + sp[1];
            // belt end: the slot projected onto the belt line (perpendicular sorter)
            double bx = Dirs.Dx(flow) != 0 ? sx : t.X, by = Dirs.Dy(flow) != 0 ? sy : t.Y;
            double d = (bx - t.X) * Dirs.Dx(flow) + (by - t.Y) * Dirs.Dy(flow);
            int off = (int)Math.Floor(20 * d - 1e-3);
            Bld s;
            if (i == 0) s = Bld.Sorter(sorterItem, bx, by, sx, sy, Dirs.Yaw(side), t, dep, slot, 1, off, carries);
            else s = Bld.Sorter(sorterItem, sx, sy, bx, by, Dirs.Yaw(face), dep, t, slot, 1, off, carries);
            s.Cell = cells[i]; s.Role = "depot buffer " + (i == 0 ? "in" : "out");
            f.Add(s);
        }
        return f;
    }

    public static bool IsProvenDepot(Dir flow, bool depotRight) { return flow == Dir.E && depotRight; }

    // ================================================================ splitter variants (Tab = modelIndex 38 / 39 / 40)

    /// Splitter port slot of a model variant (analysis\buildings\slots.json, user-upgrade-turbine.md section 3); -1 =
    /// no port there. m38: N0 E1 S2 W3, all ground. m39: N low 0, N high 1, S low 2, S high 3, no E/W.
    /// m40: N high 0, E low 1, S high 2, W low 3. High = one belt level up (z 1 = 1.333 m).
    public static int SplitterSlot(int model, Dir side, bool high)
    {
        if (model == 39) return side == Dir.N ? (high ? 1 : 0) : side == Dir.S ? (high ? 3 : 2) : -1;
        if (model == 40) return (side == Dir.N || side == Dir.S) ? (high ? (side == Dir.N ? 0 : 2) : -1) : (high ? -1 : (int)side);
        return high ? -1 : (int)side;
    }

    /// Configurations of the variants built in game (refs\engine-turbine-60-v2-userupgrade.txt #279 m39, #280 m40);
    /// everything else is derived from the port table.
    public static bool IsProvenSplitterConfig(int model, Dir inSide, bool inHigh, IList<KeyValuePair<Dir, bool>> outs)
    {
        if (model == 38 || model == 0) return !inHigh && outs.All(o => !o.Value);
        Func<Dir, bool, bool> has = (d, h) => outs.Any(o => o.Key == d && o.Value == h);
        if (model == 39) return inSide == Dir.S && !inHigh && outs.Count == 2 && has(Dir.N, false) && has(Dir.N, true);
        if (model == 40) return inSide == Dir.E && !inHigh && outs.Count == 2 && has(Dir.N, true) && has(Dir.S, true);
        return false;
    }

    /// High port belts (user's file): touching belt 0.197 from the centre at z 1, next 1.066 at z 1.003.
    const double SplitTouchHigh = 0.197, SplitNextHigh = 1.066;

    /// Splitter variant at tile (x,y), yaw 0: input on side inFrom (ground), outputs on (side, high) ports of the model.
    /// Ports "in" and "out:<side>" (ground) / "out:<side>:hi" (high: the run continues raised, see Composer).
    public static Fragment SplitterV(int x, int y, int model, Dir inFrom, IList<KeyValuePair<Dir, bool>> outs, int beltItem, int carries, bool priority)
    {
        if (model == 38 && outs.All(o => !o.Value)) return Splitter(x, y, inFrom, outs.Select(o => o.Key).ToArray(), beltItem, carries, priority);
        if (SplitterSlot(model, inFrom, false) < 0) throw new ArgumentException("SplitterV: model " + model + " has no ground port on side " + inFrom);
        var f = new Fragment("splitter@" + x + "," + y);
        Bld sp = f.Add(Bld.Splitter(x, y, priority));
        sp.Carries = carries; sp.Role = "splitter m" + model; sp.ModelIndex = model;
        string inChain = f.Name + ":in";
        Bld inNext = f.Add(SideBelt(beltItem, x, y, inFrom, SplitNext, SplitNextZ, Dirs.Opposite(inFrom), carries, inChain));
        Bld inTouch = f.Add(SideBelt(beltItem, x, y, inFrom, SplitTouch, 0, Dirs.Opposite(inFrom), carries, inChain));
        Layout.Link(inNext, inTouch, 1);
        Layout.Link(inTouch, sp, SplitterSlot(model, inFrom, false));
        f.AddPort(new Port { Name = "in", Kind = PortKind.In, X = x + Dirs.Dx(inFrom), Y = y + Dirs.Dy(inFrom), Dir = Dirs.Opposite(inFrom), Item = carries, Belt = inNext, Chain = inChain });
        foreach (var o in outs)
        {
            int slot = SplitterSlot(model, o.Key, o.Value);
            if (slot < 0) throw new ArgumentException("SplitterV: model " + model + " has no " + (o.Value ? "high" : "ground") + " port on side " + o.Key);
            string ch = f.Name + ":out:" + o.Key + (o.Value ? ":hi" : "");
            Bld touch = f.Add(SideBelt(beltItem, x, y, o.Key, o.Value ? SplitTouchHigh : SplitTouch, o.Value ? 1.0 : 0, o.Key, carries, ch));
            Bld next = f.Add(SideBelt(beltItem, x, y, o.Key, o.Value ? SplitNextHigh : SplitNext, (o.Value ? 1.0 : 0) + SplitNextZ, o.Key, carries, ch));
            touch.Input = sp; touch.InputFromSlot = slot;
            Layout.Link(touch, next, 1);
            Layout.Link(next, null, 0);
            f.AddPort(new Port { Name = "out:" + o.Key + (o.Value ? ":hi" : ""), Kind = PortKind.Out, X = x + Dirs.Dx(o.Key), Y = y + Dirs.Dy(o.Key), Dir = o.Key, Item = carries, Belt = next, Chain = ch });
        }
        return f;
    }

    /// Yaw of a belt tile entering with dIn and leaving with dOut: the flow direction, or the bisector on a corner
    /// (N->E 45, E->S 135, W->S 225, N->W 315 ...), as Chain() in MakeCoilFactoryV3.
    public static double CornerYaw(Dir dIn, Dir dOut)
    {
        double yIn = Dirs.Yaw(dIn), yOut = Dirs.Yaw(dOut);
        if (dIn == dOut) return yIn;
        double diff = ((yOut - yIn + 540.0) % 360.0) - 180.0;
        return Dirs.Norm(yIn + diff / 2.0);
    }

    /// Tile path through waypoints x0,y0, x1,y1, ... (axis-aligned segments only, no duplicate tiles).
    public static int[][] Path(params int[] xy)
    {
        if (xy == null || xy.Length < 2 || xy.Length % 2 != 0) throw new ArgumentException("Path: need x,y pairs");
        var l = new List<int[]>();
        var seen = new HashSet<long>();
        l.Add(new[] { xy[0], xy[1] }); seen.Add(((long)xy[0] << 32) ^ (uint)xy[1]);
        for (int k = 2; k < xy.Length; k += 2)
        {
            int x0 = xy[k - 2], y0 = xy[k - 1], x1 = xy[k], y1 = xy[k + 1];
            if (x0 != x1 && y0 != y1) throw new ArgumentException("Path: diagonal segment " + x0 + "," + y0 + " -> " + x1 + "," + y1);
            int dx = Math.Sign(x1 - x0), dy = Math.Sign(y1 - y0);
            for (int x = x0 + dx, y = y0 + dy; dx != 0 || dy != 0; x += dx, y += dy)
            {
                if (!seen.Add(((long)x << 32) ^ (uint)y)) throw new ArgumentException("Path: tile " + x + "," + y + " visited twice");
                l.Add(new[] { x, y });
                if (x == x1 && y == y1) break;
            }
        }
        return l.ToArray();
    }

    // side-join slots already used per target belt (2, then 3)
    static readonly Dictionary<Bld, List<int>> joinSlots = new Dictionary<Bld, List<int>>();

    /// Side-join (rule 6): last tile of a joining chain outputs into target (adjacent, perpendicular to the target's
    /// incoming flow, never from the target's successor tile), outputToSlot 2 (3 when slot 2 is taken). No splitter.
    /// A straight joining tile that does not point at the target becomes a corner toward it.
    public static void SideMerge(Bld joiningLast, Bld target)
    {
        if (joiningLast == null || target == null) throw new ArgumentNullException(joiningLast == null ? "joiningLast" : "target");
        if (joiningLast.Kind != Kind.Belt || target.Kind != Kind.Belt) throw new ArgumentException("SideMerge: both ends must be belts");
        if (joiningLast == target) throw new ArgumentException("SideMerge: a tile cannot join itself");
        int dx = target.TileX - joiningLast.TileX, dy = target.TileY - joiningLast.TileY;
        if (Math.Abs(dx) + Math.Abs(dy) != 1)
            throw new ArgumentException("SideMerge: " + joiningLast + " is not adjacent to " + target);
        Dir d = Dirs.FromDelta(dx, dy);
        Dir tIn = InDir(target);
        if (d == tIn || d == Dirs.Opposite(tIn))
            throw new ArgumentException("SideMerge: join direction " + d + " is not perpendicular to the target flow " + tIn + " at " + target);
        if (target.Output != null)
        {
            Dir tOut = Dirs.FromDelta(Math.Sign(Round(target.Output.X - target.X)), Math.Sign(Round(target.Output.Y - target.Y)));
            if (d == Dirs.Opposite(tOut)) throw new ArgumentException("SideMerge: joining from the target's successor tile at " + target);
        }
        // joining tile must point at the target: a straight end tile turns into a corner toward it
        if (IsCardinal(joiningLast.Yaw))
        {
            Dir own = joiningLast.FlowDir;
            if (own == Dirs.Opposite(d)) throw new ArgumentException("SideMerge: joining tile flows away from the target");
            if (own != d) { joiningLast.Yaw = joiningLast.Yaw2 = CornerYaw(own, d); }
        }
        List<int> used;
        if (!joinSlots.TryGetValue(target, out used)) { used = new List<int>(); joinSlots[target] = used; }
        if (joiningLast.Output == target && (joiningLast.OutputToSlot == 2 || joiningLast.OutputToSlot == 3)) return;
        int slot = !used.Contains(2) ? 2 : !used.Contains(3) ? 3 : -1;
        if (slot < 0) throw new InvalidOperationException("SideMerge: target " + target + " already has two side-joins");
        used.Add(slot);
        Layout.Link(joiningLast, target, slot);
    }

    static int Round(double v) { return (int)Math.Round(v); }
    static bool IsCardinal(double yaw) { double r = Dirs.Norm(yaw) % 90.0; return r < 1.0 || r > 89.0; }

    /// Incoming flow direction of a belt tile: its yaw when straight; on a corner the cardinal at yaw +-45 that is
    /// not the direction toward its Output.
    static Dir InDir(Bld b)
    {
        if (IsCardinal(b.Yaw)) return b.FlowDir;
        Dir a = Dirs.FromYaw(b.Yaw - 45.0), c = Dirs.FromYaw(b.Yaw + 45.0);
        if (b.Output == null) throw new ArgumentException("SideMerge: corner target without Output " + b);
        Dir tOut = Dirs.FromDelta(Math.Sign(Round(b.Output.X - b.X)), Math.Sign(Round(b.Output.Y - b.Y)));
        return tOut == a ? c : a;
    }

    // ================================================================ splitter

    // Measured in refs\splitters.txt (splitter (7,0) yaw 0): touching belt 0.199 from the splitter
    // center on the side axis, next belt 1.073 from the center at z 0.003, then integer tiles.
    const double SplitTouch = 0.199, SplitNext = 1.073, SplitNextZ = 0.003;

    /// Splitter at tile (x,y), yaw 0, one input (from side inFrom) and 1..3 outputs (sides), geometry from
    /// refs\splitters.txt. Ports "in" (In: tile one step out on the inFrom side, flowing toward the splitter;
    /// the upstream run ends two tiles out) and "out:N"/"out:E"/"out:S"/"out:W" (Out: tile one step out, flowing away).
    /// Each side gets two fractional belts (0.199 and 1.073 from the center). Only for splitting (rule 7).
    public static Fragment Splitter(int x, int y, Dir inFrom, Dir[] outs, int beltItem, int carries, bool priority)
    {
        if (outs == null || outs.Length < 1 || outs.Length > 3) throw new ArgumentException("Splitter: 1..3 outputs required");
        if (outs.Distinct().Count() != outs.Length || outs.Contains(inFrom)) throw new ArgumentException("Splitter: output sides must be distinct and differ from the input side");
        var f = new Fragment("splitter@" + x + "," + y);
        Bld sp = f.Add(Bld.Splitter(x, y, priority));
        sp.Carries = carries; sp.Role = "splitter";

        // input side: next (1.073) -> touch (0.199) -> splitter
        string inChain = f.Name + ":in";
        Bld inNext = f.Add(SideBelt(beltItem, x, y, inFrom, SplitNext, SplitNextZ, Dirs.Opposite(inFrom), carries, inChain));
        Bld inTouch = f.Add(SideBelt(beltItem, x, y, inFrom, SplitTouch, 0, Dirs.Opposite(inFrom), carries, inChain));
        Layout.Link(inNext, inTouch, 1);
        Layout.Link(inTouch, sp, (int)inFrom);           // outputToSlot = side 0 N, 1 E, 2 S, 3 W
        f.AddPort(new Port { Name = "in", Kind = PortKind.In, X = x + Dirs.Dx(inFrom), Y = y + Dirs.Dy(inFrom),
            Dir = Dirs.Opposite(inFrom), Item = carries, Belt = inNext, Chain = inChain });

        foreach (Dir o in outs)
        {
            string ch = f.Name + ":out:" + o;
            Bld touch = f.Add(SideBelt(beltItem, x, y, o, SplitTouch, 0, o, carries, ch));
            Bld next = f.Add(SideBelt(beltItem, x, y, o, SplitNext, SplitNextZ, o, carries, ch));
            touch.Input = sp; touch.InputFromSlot = (int)o;
            Layout.Link(touch, next, 1);
            Layout.Link(next, null, 0);
            f.AddPort(new Port { Name = "out:" + o, Kind = PortKind.Out, X = x + Dirs.Dx(o), Y = y + Dirs.Dy(o),
                Dir = o, Item = carries, Belt = next, Chain = ch });
        }
        return f;
    }

    static Bld SideBelt(int beltItem, int x, int y, Dir side, double dist, double z, Dir flow, int carries, string chain)
    {
        Bld b = Bld.Belt(beltItem, x + Dirs.Dx(side) * dist, y + Dirs.Dy(side) * dist, Dirs.Yaw(flow), carries, chain);
        b.Z = b.Z2 = z;
        b.Cell = SplitterGeo.BeltCell;   // fractional: no grid tile for the touching belt, no 0.5 snap (Footprints.IsSplitterTouch)
        b.Role = "splitter belt " + side;
        return b;
    }

    // ================================================================ power

    /// Tesla towers on free tiles (not under a sorter) so every power point (GameData.PowerPoints: machine center,
    /// sorter start and midpoint) is within GameData.PlaceTeslaCover (7.9; game 8.36) of a tower, the towers link
    /// within GameData.PlaceTeslaLink (16; game 17.9) and no two power nodes are closer than GameData.MinPowerNodeTiles.
    /// Systematic placement (user review 2026-10-31: far fewer towers, regular grid): square/rectangular lattices of
    /// spacing 8..11 at every offset are snapped to the nearest free tile (gap rows / between lanes preferred), holes
    /// are filled greedily, redundant towers pruned; the lattice with the fewest towers (then the best alignment) wins.
    /// The plain greedy cover is the fallback. Returns the NEW towers (caller adds them). Problems go to layout.Errors.
    public static List<Bld> TeslaGrid(Layout layout, GameData gd) { return TeslaGrid(layout, gd, true); }

    /// Optional tile filter for tower candidates (kit modules: power keep-out of 2 tiles from the W/E frame edges and
    /// no tower in the port strips). null = every free tile.
    [ThreadStatic] public static Func<int, int, bool> TeslaAllowed;

    /// thorough = false: greedy cover only (fast; the Composer uses it while comparing attempts).
    public static List<Bld> TeslaGrid(Layout layout, GameData gd, bool thorough)
    {
        if (layout == null) throw new ArgumentNullException("layout");
        double cover = GameData.PlaceTeslaCover, link = GameData.PlaceTeslaLink;
        var greedyErr = new List<string>();
        List<Bld> best = TeslaGreedy(layout, gd, cover, link, greedyErr);
        if (!thorough || layout.OfKind(Kind.Tesla).Any()) { layout.Errors.AddRange(greedyErr); return best; }   // existing towers: keep greedy
        int bestScore = greedyErr.Count > 0 ? int.MaxValue : Score(best);

        var pts = new List<double[]>();
        var blocked = new HashSet<long>();
        Collect(layout, pts, blocked);
        BlockColliders(layout, gd, blocked);
        Rect bounds = layout.Bounds();
        if (pts.Count == 0 || bounds.IsEmpty) { layout.Errors.AddRange(greedyErr); return best; }
        var cands = Candidates(layout, bounds, blocked, new List<double[]>());   // inside the box: towers never grow the area
        var candSet = new Dictionary<long, double[]>();
        foreach (var c in cands) candSet[K((int)c[0], (int)c[1])] = c;
        // coverage lists per candidate
        var reach = new Dictionary<long, int[]>();
        foreach (var c in cands)
        {
            var l = new List<int>();
            for (int i = 0; i < pts.Count; i++) if (Dist(pts[i], c) <= cover + 1e-9) l.Add(i);
            reach[K((int)c[0], (int)c[1])] = l.ToArray();
        }
        int[][] spacings = { new[] { 11, 11 }, new[] { 10, 10 }, new[] { 9, 9 }, new[] { 11, 9 }, new[] { 9, 11 }, new[] { 10, 8 }, new[] { 8, 10 }, new[] { 8, 8 } };
        foreach (var sp in spacings)
            for (int ox = 0; ox < sp[0]; ox++)
                for (int oy = 0; oy < sp[1]; oy++)
                {
                    var towers = new List<double[]>();
                    for (int gx = bounds.X0 + ox - sp[0]; gx <= bounds.X1 + sp[0]; gx += sp[0])
                        for (int gy = bounds.Y0 + oy - sp[1]; gy <= bounds.Y1 + sp[1]; gy += sp[1])
                        {
                            if (gx < bounds.X0 - 1 || gx > bounds.X1 + 1 || gy < bounds.Y0 - 1 || gy > bounds.Y1 + 1) continue;
                            double[] s = Snap(layout, candSet, gx, gy, towers);
                            if (s != null) towers.Add(s);
                        }
                    var sol = Complete(layout, towers, pts, reach, cands, cover, link);
                    if (sol == null) continue;
                    var blds = sol.Select(NewTower).ToList();
                    int sc = Score(blds);
                    if (sc < bestScore) { bestScore = sc; best = blds; }
                }
        if (bestScore == int.MaxValue) layout.Errors.AddRange(greedyErr);
        return best;
    }

    /// Fewer towers first, then fewer distinct rows/columns (aligned grid).
    static int Score(List<Bld> towers)
    {
        return towers.Count * 1000 + towers.Select(t => (int)Math.Round(t.X)).Distinct().Count() + towers.Select(t => (int)Math.Round(t.Y)).Distinct().Count();
    }

    /// Tiles where a tower's collider would hit another building's collider (e.g. the free tile next to a splitter on
    /// an unused side: splitter arms 1.25 x 2.38 m, colliders.json). Uses GameData.ColliderGap when available.
    static void BlockColliders(Layout layout, GameData gd, HashSet<long> blocked)
    {
        foreach (var b in layout.All)
        {
            if (b.Kind == Kind.Belt || b.Kind == Kind.Sorter || b.Kind == Kind.Tesla) continue;
            Rect r = layout.Footprints.TileRect(b);
            if (r.IsEmpty) continue;
            for (int x = r.X0 - 2; x <= r.X1 + 2; x++)
                for (int y = r.Y0 - 2; y <= r.Y1 + 2; y++)
                {
                    if (r.Contains(x, y)) continue;
                    bool hit;
                    if (gd != null && gd.HasCollider(b.ItemId) && gd.HasCollider(Ids.Tesla))
                        hit = gd.ColliderGap(Bld.Tesla(x, y), b, 1.0) < 0.001;
                    else hit = b.Kind == Kind.Splitter && Math.Abs(x - b.TileX) + Math.Abs(y - b.TileY) == 1;
                    if (hit) blocked.Add(K(x, y));
                }
        }
    }

    static void Collect(Layout layout, List<double[]> pts, HashSet<long> blocked)
    {
        foreach (var b in layout.All)
        {
            pts.AddRange(GameData.PowerPoints(b));
            if (b.Kind == Kind.Sorter)
            {
                double len = Math.Sqrt((b.X2 - b.X) * (b.X2 - b.X) + (b.Y2 - b.Y) * (b.Y2 - b.Y));
                int steps = Math.Max(1, (int)Math.Ceiling(len / 0.1));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    blocked.Add(K(Round(b.X + (b.X2 - b.X) * t), Round(b.Y + (b.Y2 - b.Y) * t)));
                }
            }
        }
    }

    /// Nearest free candidate tile to a lattice point (radius 3), gap rows / between-lane tiles preferred at equal
    /// distance, at least MinPowerNodeTiles from the towers so far.
    static double[] Snap(Layout layout, Dictionary<long, double[]> cand, int gx, int gy, List<double[]> towers)
    {
        double[] best = null; double bestV = double.MaxValue;
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
            {
                double[] c;
                if (!cand.TryGetValue(K(gx + dx, gy + dy), out c)) continue;
                if (towers.Any(t => Dist(c, t) < GameData.MinPowerNodeTiles - 1e-9)) continue;
                double v = Math.Abs(dx) + Math.Abs(dy) - 0.3 * Preference(layout, gx + dx, gy + dy);
                if (v < bestV) { bestV = v; best = c; }
            }
        return best;
    }

    /// Lattice towers -> fill uncovered points greedily, prune redundant towers, check links. null = not possible.
    static List<double[]> Complete(Layout layout, List<double[]> lattice, List<double[]> pts, Dictionary<long, int[]> reach,
        List<double[]> cands, double cover, double link)
    {
        var towers = new List<double[]>(lattice);
        var cnt = new int[pts.Count];
        Func<double[], int[]> R = c => reach[K((int)c[0], (int)c[1])];
        foreach (var t in towers) foreach (int i in R(t)) cnt[i]++;
        // prune lattice towers that cover nothing on their own (most redundant first)
        for (bool changed = true; changed; )
        {
            changed = false;
            double[] drop = null;
            foreach (var t in towers) if (R(t).All(i => cnt[i] > 1)) { drop = t; break; }
            if (drop != null) { towers.Remove(drop); foreach (int i in R(drop)) cnt[i]--; changed = true; }
        }
        // fill holes
        while (true)
        {
            if (cnt.All(c => c > 0)) break;
            double[] best = null; int bestN = 0;
            foreach (var c in cands)
            {
                if (towers.Any(t => Dist(c, t) < GameData.MinPowerNodeTiles - 1e-9)) continue;
                int n = 0; foreach (int i in R(c)) if (cnt[i] == 0) n++;
                if (n > bestN) { bestN = n; best = c; }
            }
            if (best == null) return null;
            towers.Add(best); foreach (int i in R(best)) cnt[i]++;
        }
        for (int k = towers.Count - 1; k >= 0; k--)
        {
            var t = towers[k];
            if (R(t).All(i => cnt[i] > 1)) { towers.RemoveAt(k); foreach (int i in R(t)) cnt[i]--; }
        }
        if (towers.Count == 0) return null;
        if (Component(towers, link).Count != towers.Count) return null;
        return towers;
    }

    /// Greedy cover + bridging + pruning (the previous TeslaGrid), also the fallback.
    static List<Bld> TeslaGreedy(Layout layout, GameData gd, double cover, double link, List<string> errors)
    {
        var result = new List<Bld>();

        var pts = new List<double[]>();
        var blocked = new HashSet<long>();
        BlockColliders(layout, gd, blocked);
        foreach (var b in layout.All)
        {
            if (b.Kind == Kind.Machine || b.Kind == Kind.Sorter) pts.AddRange(GameData.PowerPoints(b));
            if (b.Kind == Kind.Sorter)
            {
                double len = Math.Sqrt((b.X2 - b.X) * (b.X2 - b.X) + (b.Y2 - b.Y) * (b.Y2 - b.Y));
                int steps = Math.Max(1, (int)Math.Ceiling(len / 0.1));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    blocked.Add(K(Round(b.X + (b.X2 - b.X) * t), Round(b.Y + (b.Y2 - b.Y) * t)));
                }
            }
        }
        var towers = new List<double[]>();
        foreach (var t in layout.OfKind(Kind.Tesla)) towers.Add(new[] { t.X, t.Y });
        if (pts.Count == 0 && towers.Count == 0) return result;

        Rect bounds = layout.Bounds();
        if (bounds.IsEmpty) return result;

        var covered = new bool[pts.Count];
        for (int i = 0; i < pts.Count; i++) foreach (var t in towers) if (Dist(pts[i], t) <= cover + 1e-9) { covered[i] = true; break; }

        for (int grow = 0; grow <= 3; grow++)
        {
            if (covered.All(c => c)) break;
            Rect r = new Rect(bounds.X0 - grow, bounds.Y0 - grow, bounds.X1 + grow, bounds.Y1 + grow);
            var cands = Candidates(layout, r, blocked, towers);
            var reach = new List<int[]>();
            foreach (var c in cands)
            {
                var l = new List<int>();
                for (int i = 0; i < pts.Count; i++) if (Dist(pts[i], c) <= cover + 1e-9) l.Add(i);
                reach.Add(l.ToArray());
            }
            while (true)
            {
                int best = -1, bestN = 0, bestPref = -1;
                for (int ci = 0; ci < cands.Count; ci++)
                {
                    int cnt = 0; foreach (int i in reach[ci]) if (!covered[i]) cnt++;
                    if (cnt == 0) continue;
                    int pref = Preference(layout, (int)cands[ci][0], (int)cands[ci][1]);
                    if (cnt > bestN || (cnt == bestN && pref > bestPref)) { best = ci; bestN = cnt; bestPref = pref; }
                }
                if (best < 0) break;
                foreach (int i in reach[best]) covered[i] = true;
                double[] placed = cands[best];
                towers.Add(placed);
                result.Add(NewTower(placed));
                // drop every candidate closer than the game's power-node minimum to the new tower (incl. itself)
                for (int ci = cands.Count - 1; ci >= 0; ci--)
                    if (Dist(cands[ci], placed) < GameData.MinPowerNodeTiles - 1e-9) { cands.RemoveAt(ci); reach.RemoveAt(ci); }
            }
        }
        for (int i = 0; i < pts.Count; i++)
            if (!covered[i]) errors.Add(string.Format(CultureInfo.InvariantCulture,
                "tesla: no free tile within {0} tiles of powered point ({1:0.###},{2:0.###})", cover, pts[i][0], pts[i][1]));

        // connectivity: bridge the component of tower 0 to the nearest other component until one remains
        Rect wide = new Rect(bounds.X0 - 3, bounds.Y0 - 3, bounds.X1 + 3, bounds.Y1 + 3);
        for (int guard = 0; guard < 1000 && towers.Count > 1; guard++)
        {
            var comp = Component(towers, link);
            if (comp.Count == towers.Count) break;
            double bestD = double.MaxValue; double[] a = null, bTo = null;
            foreach (int i in comp)
                for (int j = 0; j < towers.Count; j++)
                    if (!comp.Contains(j)) { double dd = Dist(towers[i], towers[j]); if (dd < bestD) { bestD = dd; a = towers[i]; bTo = towers[j]; } }
            double[] pick = null; double pickD = bestD - 0.5;
            foreach (var c in Candidates(layout, wide, blocked, towers))
            {
                bool linked = false; foreach (int i in comp) if (Dist(c, towers[i]) <= link + 1e-9) { linked = true; break; }
                if (!linked) continue;
                double dd = Dist(c, bTo);
                if (dd < pickD) { pickD = dd; pick = c; }
            }
            if (pick == null)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, "tesla: cannot connect tower ({0},{1}) to ({2},{3}) within {4} tiles",
                    a[0], a[1], bTo[0], bTo[1], link));
                break;
            }
            towers.Add(pick); result.Add(NewTower(pick));
        }

        // prune: drop new towers (latest first) whose removal keeps coverage and connectivity
        int existing = towers.Count - result.Count;
        for (int i = towers.Count - 1; i >= existing && towers.Count > 1; i--)
        {
            var rest = new List<double[]>(towers); rest.RemoveAt(i);
            bool ok = true;
            for (int p = 0; p < pts.Count && ok; p++)
                if (covered[p] && !rest.Any(t => Dist(pts[p], t) <= cover + 1e-9)) ok = false;
            if (ok && Component(rest, link).Count != rest.Count && Component(towers, link).Count == towers.Count) ok = false;
            if (!ok) continue;
            towers.RemoveAt(i); result.RemoveAt(i - existing);
        }
        return result;
    }

    static Bld NewTower(double[] c) { Bld t = Bld.Tesla(c[0], c[1]); t.Role = "tesla"; return t; }
    static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }
    static double Dist(double[] a, double[] b) { double dx = a[0] - b[0], dy = a[1] - b[1]; return Math.Sqrt(dx * dx + dy * dy); }

    static List<double[]> Candidates(Layout layout, Rect r, HashSet<long> blocked, List<double[]> towers)
    {
        var taken = new HashSet<long>();
        foreach (var t in towers) taken.Add(K(Round(t[0]), Round(t[1])));
        // raised belt nodes (cell RAISED_RUN) occupy no grid tile, but nothing may stand under them
        foreach (var b in layout.All) if (b.IsRaised) taken.Add(K(b.TileX, b.TileY));
        var l = new List<double[]>();
        for (int y = r.Y0; y <= r.Y1; y++)
            for (int x = r.X0; x <= r.X1; x++)
                if (layout.Grid.IsFree(x, y) && !blocked.Contains(K(x, y)) && !taken.Contains(K(x, y)) && (TeslaAllowed == null || TeslaAllowed(x, y)))
                {
                    var c = new double[] { x, y };
                    if (!towers.Any(t => Dist(c, t) < GameData.MinPowerNodeTiles - 1e-9)) l.Add(c);
                }
        return l;
    }

    /// 2 = between two belts (E+W or N+S neighbours are belts), 1 = in a gap row/column next to a machine, 0 = other.
    static int Preference(Layout layout, int x, int y)
    {
        Func<int, int, Kind?> k = (a, b) => { Bld o = layout.Grid.At(a, b); return o == null ? (Kind?)null : o.Kind; };
        if ((k(x - 1, y) == Kind.Belt && k(x + 1, y) == Kind.Belt) || (k(x, y - 1) == Kind.Belt && k(x, y + 1) == Kind.Belt)) return 2;
        if ((k(x, y - 1) == Kind.Machine && k(x, y + 1) == Kind.Machine) || (k(x - 1, y) == Kind.Machine && k(x + 1, y) == Kind.Machine)) return 1;
        return 0;
    }

    static HashSet<int> Component(List<double[]> towers, double link)
    {
        var seen = new HashSet<int> { 0 };
        var stack = new Stack<int>(); stack.Push(0);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            for (int j = 0; j < towers.Count; j++)
                if (!seen.Contains(j) && Dist(towers[i], towers[j]) <= link + 1e-9) { seen.Add(j); stack.Push(j); }
        }
        return seen;
    }
}
}
