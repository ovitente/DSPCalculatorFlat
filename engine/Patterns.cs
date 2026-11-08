// Patterns.cs - recognition of the user's pattern drawings (docs\patterns-design.md, Architecture 2-4; catalog in
// docs\pattern-catalog.md). Pure model code: no game DLL, no I/O. Input: a Layout from BpImport.ToLayout; output: a
// list of PatternInfo (machine rows, double columns, input combs, bridge bundles, splitter turns, coater rows, unknown
// groups). Heuristics are deliberately simple and explicit; a group that matches nothing becomes Unknown, never an
// exception. RowCells turns a Row / DoubleColumn into SorterGeo cells (status "game", source = file + record index) for
// Geo.Register; Catalog / Status map the P01..P17 catalog onto the drawn files.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DspFlat.Engine
{

public enum PatternKind { Row, DoubleColumn, Comb, Bridge, SplitterTurn, CoaterRow, Unknown }

/// One belt lane of a machine row: the sorter geometry of its first machine plus the item it carries.
public class LaneInfo
{
    public Dir Side;                  // world side of the machine the belt is on
    public int Length;                // sorter grid length (parameters[0])
    public int Lateral;               // belt tile offset along the side (dy for E/W sides, dx for N/S)
    public int Slot;                  // machine slot (outputToSlot for an input, inputFromSlot for an output)
    public int BeltDx, BeltDy;        // belt tile relative to the machine centre
    public double Sx, Sy, Ex, Ey;     // sorter start/end relative to the machine centre
    public double SorterYaw;
    public int Item;                  // 0 = unknown
    public bool ItemFromIcon;         // item read from the lane's icon tail (else from the recipe)
    public bool Input;                // belt -> machine
    public int Column;                // DoubleColumn: 0 (first column) / 1 (second)
    public int Machines;              // machines of the row this lane touches
    public Bld Sorter, Machine, Belt, Head;
    public string Note;

    public string Describe(GameData gd)
    {
        string item = Item != 0 ? (gd != null ? gd.ItemName(Item) : Item.ToString()) + (ItemFromIcon ? "" : " (recipe)") : "item ?";
        return Side + " L" + Length + " " + (Input ? "in" : "out") + " slot " + Slot + " belt(" + BeltDx + "," + BeltDy + ") " + item
            + (Note != null ? " [" + Note + "]" : "");
    }
}

public class PatternInfo
{
    public PatternKind Kind;
    public Rect Bounds = Rect.Empty;
    public string Note;
    // Row / DoubleColumn
    public int MachineItem, RecipeId, MachineCount, MachineCount2;
    public double MachineYaw;
    public int Pitch;
    public Dir Flow;
    public List<LaneInfo> Lanes = new List<LaneInfo>();
    public List<Bld> Machines = new List<Bld>();
    public int Coord, Coord2;                 // line coordinate of the column(s): x for vertical columns, y for horizontal rows
    public bool Vertical = true;              // machines stacked along y (lanes flow N/S)
    public int SharedLanes;                   // DoubleColumn: lanes with sorters from both columns
    public List<int> LaneOffsets = new List<int>();   // DoubleColumn: lane coordinate - midpoint of the two columns (per lane)
    // Comb
    public int LaneCount, LanePitch;
    public Dir Edge;
    public List<int> Items = new List<int>();
    // Bridge
    public int Runs;
    public List<int> Crossed = new List<int>();
    // SplitterTurn
    public Bld Splitter;
    public List<int> CornerDistances = new List<int>();   // per output; -1 = no turn seen
    // CoaterRow
    public int Coaters, FeedCount, FeedOffset;
    public string FeedZProfile;
    public bool FeedDeadEnds;
    // Unknown
    public int Belts, Sorters, Splitters, Others;

    public int InputLanes { get { return Lanes.Count(x => x.Input); } }
    public int OutputLanes { get { return Lanes.Count(x => !x.Input); } }

    public string Describe(GameData gd)
    {
        var c = CultureInfo.InvariantCulture;
        Func<int, string> n = id => gd != null ? gd.ItemName(id) : id.ToString();
        string tail = Note != null ? " [" + Note + "]" : "";
        switch (Kind)
        {
            case PatternKind.Row:
            {
                RecipeDef r = gd != null ? gd.Recipe(RecipeId) : null;
                return "Row: " + MachineCount + " x " + n(MachineItem) + " recipe " + RecipeId + (r != null ? " (" + r.Name + ")" : "") + " yaw " + MachineYaw.ToString("0", c)
                    + " at " + (Vertical ? "x " : "y ") + Coord + " pitch " + Pitch + " flow " + Flow + "; lanes: "
                    + (Lanes.Count == 0 ? "none" : string.Join(" | ", Lanes.Select(x => x.Describe(gd)).ToArray())) + tail;
            }
            case PatternKind.DoubleColumn:
            {
                RecipeDef r = gd != null ? gd.Recipe(RecipeId) : null;
                return "DoubleColumn: " + n(MachineItem) + " " + MachineCount + "+" + MachineCount2 + " recipe " + RecipeId + (r != null ? " (" + r.Name + ")" : "")
                    + " at " + (Vertical ? "x " : "y ") + Coord + "/" + Coord2 + " pitch " + Pitch + " flow " + Flow + ", shared lanes " + SharedLanes + "; lanes: "
                    + string.Join(" | ", Lanes.Select((x, i) => (x.Column == 0 ? "A " : "B ") + x.Describe(gd) + " off " + (i < LaneOffsets.Count ? LaneOffsets[i].ToString() : "?")).ToArray()) + tail;
            }
            case PatternKind.Comb:
                return "Comb: " + LaneCount + " icon tails on the " + Edge + " edge (" + Bounds + "), pitch " + LanePitch + ": " + string.Join(", ", Items.Select(n).ToArray()) + tail;
            case PatternKind.Bridge:
                return "Bridge: " + Runs + " adjacent raised run(s) crossing " + string.Join("/", Crossed.Select(x => x.ToString()).ToArray()) + " lane(s) at " + Bounds + tail;
            case PatternKind.SplitterTurn:
                return "SplitterTurn: splitter at (" + (Splitter != null ? Splitter.TileX + "," + Splitter.TileY : "?") + "), " + CornerDistances.Count + " output(s), first corner at "
                    + string.Join("/", CornerDistances.Select(x => x < 0 ? "-" : x.ToString()).ToArray()) + " tile(s)" + tail;
            case PatternKind.CoaterRow:
                return "CoaterRow: " + Coaters + " coater(s) at " + Bounds + ", " + FeedCount + " feed lane(s)" + (FeedCount > 0 ? " z " + FeedZProfile + " at offset " + FeedOffset + " behind, dead-ends: " + (FeedDeadEnds ? "yes" : "no") : "") + tail;
            default:
                return "Unknown: " + Bounds + ": " + Belts + " belts, " + Sorters + " sorters, " + Splitters + " splitters, " + Others + " other" + tail;
        }
    }
}

/// Catalog entry (docs\pattern-catalog.md).
public class CatalogEntry
{
    public string Id, FilePattern, Purpose;
    public PatternKind ProofKind;
    public Func<PatternInfo, bool> Proves;   // an info of a drawn file that shows the pattern (null = file name only)
}

/// A drawn file handed to Catalog.Status: file name, header desc, analysis.
public class PatternFile
{
    public string Name, Desc;
    public List<PatternInfo> Infos = new List<PatternInfo>();
}

public class CatalogStatus
{
    public CatalogEntry Entry;
    public string State;    // missing | drawn | proven
    public string File;     // file that draws it (null when missing)
    public string Why;
}

public static class Patterns
{
    // ------------------------------------------------------------------ analysis
    public static List<PatternInfo> Analyze(Layout l, GameData gd)
    {
        var res = new List<PatternInfo>();
        if (l == null) return res;
        var ctx = new Ctx(l, gd);
        try
        {
            Combs(ctx, res);
            Bridges(ctx, res);
            SplitterTurns(ctx, res);
            Components(ctx, res);
        }
        catch (Exception e)
        {
            res.Add(new PatternInfo { Kind = PatternKind.Unknown, Bounds = l.Bounds(), Note = "analysis error: " + e.GetType().Name + ": " + e.Message });
        }
        return res;
    }

    class Ctx
    {
        public readonly Layout L; public readonly GameData Gd;
        public readonly Dictionary<Bld, List<Bld>> Preds = new Dictionary<Bld, List<Bld>>();
        public readonly Dictionary<long, List<Bld>> BeltsAt = new Dictionary<long, List<Bld>>();
        public readonly HashSet<Bld> Covered = new HashSet<Bld>();
        readonly Dictionary<Bld, Bld> heads = new Dictionary<Bld, Bld>();

        public Ctx(Layout l, GameData gd)
        {
            L = l; Gd = gd;
            foreach (Bld b in l.All)
            {
                if (b.Kind != Kind.Belt) continue;
                long k = K(b.TileX, b.TileY);
                List<Bld> at;
                if (!BeltsAt.TryGetValue(k, out at)) BeltsAt[k] = at = new List<Bld>();
                at.Add(b);
                if (b.Output != null && b.Output.Kind == Kind.Belt)
                {
                    List<Bld> p;
                    if (!Preds.TryGetValue(b.Output, out p)) Preds[b.Output] = p = new List<Bld>();
                    p.Add(b);
                }
            }
        }

        public static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }

        public List<Bld> PredsOf(Bld b) { List<Bld> p; return Preds.TryGetValue(b, out p) ? p : new List<Bld>(); }

        /// First belt of the straight chain that leads to b (stops at a merge: 2+ predecessors).
        public Bld Head(Bld b)
        {
            Bld h;
            if (heads.TryGetValue(b, out h)) return h;
            var seen = new HashSet<Bld>();
            h = b;
            while (seen.Add(h))
            {
                List<Bld> p = PredsOf(h);
                if (p.Count != 1) break;
                h = p[0];
            }
            heads[b] = h;
            return h;
        }

        /// Belts of the chain from head onwards (Output links, belts only, loop-safe).
        public List<Bld> Chain(Bld head)
        {
            var r = new List<Bld>();
            var seen = new HashSet<Bld>();
            for (Bld n = head; n != null && n.Kind == Kind.Belt && seen.Add(n); n = n.Output) r.Add(n);
            return r;
        }

        public List<Bld> At(int x, int y) { List<Bld> at; return BeltsAt.TryGetValue(K(x, y), out at) ? at : new List<Bld>(); }

        public bool HasIcon(Bld b) { return b.Parameters != null && b.Parameters.Length > 0 && b.Parameters[0] != 0; }
    }

    static Rect Tile(Bld b) { return new Rect(b.TileX, b.TileY, b.TileX, b.TileY); }

    // ---- combs: icon tails (chain heads with an icon) on one edge line, clustered by adjacency (gap <= 2)
    static void Combs(Ctx c, List<PatternInfo> res)
    {
        var groups = new Dictionary<string, List<Bld>>();
        foreach (Bld b in c.L.All)
        {
            if (b.Kind != Kind.Belt || !c.HasIcon(b) || c.PredsOf(b).Count > 0) continue;
            Dir f = b.FlowDir;
            bool ns = f == Dir.N || f == Dir.S;
            string key = (ns ? "y" + b.TileY : "x" + b.TileX) + ":" + f;
            List<Bld> g;
            if (!groups.TryGetValue(key, out g)) groups[key] = g = new List<Bld>();
            g.Add(b);
        }
        foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Dir f = kv.Value[0].FlowDir;
            bool ns = f == Dir.N || f == Dir.S;
            Func<Bld, int> along = b => ns ? b.TileX : b.TileY;
            var sorted = kv.Value.OrderBy(along).ThenBy(b => b.Z).ToList();
            int start = 0;
            for (int i = 1; i <= sorted.Count; i++)
            {
                if (i < sorted.Count && along(sorted[i]) - along(sorted[i - 1]) <= 2) continue;
                var cluster = sorted.GetRange(start, i - start);
                start = i;
                var positions = cluster.Select(along).Distinct().OrderBy(v => v).ToList();
                if (positions.Count < 2) continue;
                int pitch = int.MaxValue;
                for (int k = 1; k < positions.Count; k++) pitch = Math.Min(pitch, positions[k] - positions[k - 1]);
                var info = new PatternInfo { Kind = PatternKind.Comb, LaneCount = positions.Count, LanePitch = pitch, Edge = Dirs.Opposite(f), Flow = f };
                foreach (Bld b in cluster) { info.Items.Add(b.Parameters[0]); info.Bounds = info.Bounds.Union(Tile(b)); c.Covered.Add(b); }
                if (cluster.Count != positions.Count) info.Note = "two tails on one tile (different z)";
                res.Add(info);
            }
        }
    }

    // ---- bridges: maximal runs of raised belts, bundled by adjacency; coater feeds (a node behind a coater) excluded
    class Run { public List<Bld> Belts = new List<Bld>(); public int Crossed; public HashSet<long> Tiles = new HashSet<long>(); }

    static void Bridges(Ctx c, List<PatternInfo> res)
    {
        var feedTiles = new HashSet<long>();
        foreach (Bld b in c.L.All) if (Coaters.IsCoater(b)) { int[] t = Coaters.FeedTile(b); feedTiles.Add(Ctx.K(t[0], t[1])); }
        var visited = new HashSet<Bld>();
        var runs = new List<Run>();
        foreach (Bld b in c.L.All)
        {
            if (!b.IsRaised || visited.Contains(b) || c.PredsOf(b).Any(p => p.IsRaised)) continue;
            var run = new Run();
            for (Bld n = b; n != null && n.IsRaised && visited.Add(n); n = n.Output) { run.Belts.Add(n); run.Tiles.Add(Ctx.K(n.TileX, n.TileY)); }
            if (run.Belts.Any(n => feedTiles.Contains(Ctx.K(n.TileX, n.TileY)))) continue;   // spray coater feed, not a bridge
            var lanes = new HashSet<Bld>();
            foreach (Bld n in run.Belts) { Bld g = c.L.BeltAt(n.TileX, n.TileY); if (g != null) lanes.Add(c.Head(g)); }
            run.Crossed = lanes.Count;
            runs.Add(run);
        }
        // bundle adjacent runs (Chebyshev distance 1 between any two tiles)
        var parent = Enumerable.Range(0, runs.Count).ToArray();
        Func<int, int> find = null;
        find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
        for (int i = 0; i < runs.Count; i++)
            for (int j = i + 1; j < runs.Count; j++)
                if (Adjacent(runs[i], runs[j])) parent[find(i)] = find(j);
        foreach (var bundle in Enumerable.Range(0, runs.Count).GroupBy(i => find(i)).OrderBy(g => g.Min()))
        {
            var members = bundle.Select(i => runs[i]).OrderBy(r => r.Belts[0].TileX).ThenBy(r => r.Belts[0].TileY).ToList();
            if (members.Sum(r => r.Crossed) == 0) continue;   // raised run over nothing: a technique, reported with its group
            var info = new PatternInfo { Kind = PatternKind.Bridge, Runs = members.Count };
            foreach (Run r in members) { info.Crossed.Add(r.Crossed); foreach (Bld b in r.Belts) { info.Bounds = info.Bounds.Union(Tile(b)); c.Covered.Add(b); } }
            res.Add(info);
        }
    }

    static bool Adjacent(Run a, Run b)
    {
        foreach (Bld x in a.Belts) foreach (Bld y in b.Belts)
            if (Math.Max(Math.Abs(x.TileX - y.TileX), Math.Abs(x.TileY - y.TileY)) == 1) return true;
        return false;
    }

    // ---- splitter turns: distance from the splitter centre to the first off-axis tile of every output lane
    static void SplitterTurns(Ctx c, List<PatternInfo> res)
    {
        foreach (Bld s in c.L.All.Where(b => b.Kind == Kind.Splitter).OrderBy(b => b.TileX).ThenBy(b => b.TileY))
        {
            var outs = c.L.All.Where(b => b.Kind == Kind.Belt && b.Input == s).OrderBy(b => Math.Atan2(b.Y - s.Y, b.X - s.X)).ToList();
            if (outs.Count == 0) continue;
            var info = new PatternInfo { Kind = PatternKind.SplitterTurn, Splitter = s, Bounds = Tile(s) };
            c.Covered.Add(s);
            foreach (Bld o in outs)
            {
                double dx = o.X - s.X, dy = o.Y - s.Y;
                bool ew = Math.Abs(dx) >= Math.Abs(dy);
                int dist = -1;
                var seen = new HashSet<Bld>();
                for (Bld n = o; n != null && n.Kind == Kind.Belt && seen.Add(n) && seen.Count <= 60; n = n.Output)
                {
                    info.Bounds = info.Bounds.Union(Tile(n));
                    bool off = ew ? n.TileY != s.TileY : n.TileX != s.TileX;
                    if (off) { dist = ew ? Math.Abs(n.TileX - s.TileX) : Math.Abs(n.TileY - s.TileY); break; }
                }
                info.CornerDistances.Add(dist);
            }
            res.Add(info);
        }
    }

    // ---- connectivity groups -> rows / double columns / coater rows / unknown
    static void Components(Ctx c, List<PatternInfo> res)
    {
        var nodes = c.L.All.Where(b => b.Kind != Kind.Tesla).ToList();
        var idx = new Dictionary<Bld, int>();
        for (int i = 0; i < nodes.Count; i++) idx[nodes[i]] = i;
        var parent = Enumerable.Range(0, nodes.Count).ToArray();
        Func<int, int> find = null;
        find = i => parent[i] == i ? i : (parent[i] = find(parent[i]));
        Action<Bld, Bld> join = (a, b) => { int ia, ib; if (a != null && b != null && idx.TryGetValue(a, out ia) && idx.TryGetValue(b, out ib)) parent[find(ia)] = find(ib); };
        var machines = nodes.Where(b => b.Kind == Kind.Machine).ToList();
        foreach (Bld b in nodes)
        {
            join(b, b.Output); join(b, b.Input);
            if (Coaters.IsCoater(b))
            {
                foreach (Bld u in c.At(b.TileX, b.TileY)) join(b, u);
                int[] t = Coaters.FeedTile(b);
                foreach (Bld u in c.At(t[0], t[1])) if (u.Z > 0.75) join(b, u);
            }
        }
        for (int i = 0; i < machines.Count; i++)
        {
            Rect a = c.L.Footprints.TileRect(machines[i]);
            for (int j = i + 1; j < machines.Count; j++)
            {
                Rect b = c.L.Footprints.TileRect(machines[j]);
                if (a.X0 - 1 <= b.X1 && b.X0 - 1 <= a.X1 && a.Y0 - 1 <= b.Y1 && b.Y0 - 1 <= a.Y1) join(machines[i], machines[j]);
            }
        }
        var groups = Enumerable.Range(0, nodes.Count).GroupBy(i => find(i)).Select(g => g.Select(i => nodes[i]).ToList())
            .OrderBy(g => g.Min(b => b.TileX)).ThenBy(g => g.Min(b => b.TileY)).ToList();
        foreach (var g in groups)
        {
            var ms = g.Where(b => b.Kind == Kind.Machine).ToList();
            var coaters = g.Where(Coaters.IsCoater).ToList();
            int before = res.Count;
            if (coaters.Count > 0) CoaterRows(c, coaters, res);
            if (ms.Count > 0) Rows(c, ms, res);
            if (res.Count > before || g.Any(c.Covered.Contains)) continue;
            var u = new PatternInfo { Kind = PatternKind.Unknown };
            foreach (Bld b in g)
            {
                if (b.Kind == Kind.Belt) u.Belts++; else if (b.Kind == Kind.Sorter) u.Sorters++; else if (b.Kind == Kind.Splitter) u.Splitters++; else u.Others++;
                if (b.Kind != Kind.Sorter) u.Bounds = u.Bounds.Union(Tile(b));
            }
            res.Add(u);
        }
    }

    static void CoaterRows(Ctx c, List<Bld> coaters, List<PatternInfo> res)
    {
        foreach (var line in coaters.GroupBy(b => { Dir f = Dirs.FromYaw(b.Yaw); return (f == Dir.N || f == Dir.S) ? "y" + b.TileY : "x" + b.TileX; }).OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var info = new PatternInfo { Kind = PatternKind.CoaterRow, Coaters = line.Count() };
            var feeds = new List<Bld>();
            int offset = 0;
            foreach (Bld b in line)
            {
                info.Bounds = info.Bounds.Union(Tile(b));
                int[] t = Coaters.FeedTile(b);
                foreach (Bld u in c.At(t[0], t[1]))
                {
                    if (u.Z <= 0.75) continue;
                    Bld h = c.Head(u);
                    if (!feeds.Contains(h)) feeds.Add(h);
                    Dir f = Dirs.FromYaw(b.Yaw);
                    offset = (t[0] - b.TileX) * Dirs.Dx(f) + (t[1] - b.TileY) * Dirs.Dy(f);   // along the coater axis: -1 = behind
                }
            }
            info.FeedCount = feeds.Count;
            info.FeedOffset = offset;
            if (feeds.Count > 0)
            {
                List<Bld> chain = c.Chain(feeds[0]);
                var prof = new List<string>();
                foreach (Bld b in chain)
                {
                    string z = (Math.Round(b.Z * 2) / 2).ToString("0.#", CultureInfo.InvariantCulture);
                    if (prof.Count == 0 || prof[prof.Count - 1] != z) prof.Add(z);
                }
                info.FeedZProfile = string.Join("/", prof.ToArray());
                info.FeedDeadEnds = chain.Count > 0 && chain[chain.Count - 1].Output == null;
                if (feeds.Count > 1) info.Note = feeds.Count + " feed lanes";
            }
            else info.Note = "no raised feed node behind the coaters";
            res.Add(info);
        }
    }

    class Line
    {
        public int Item, Recipe; public double Yaw; public bool Vertical; public int Coord;
        public List<Bld> Machines;
        public Dictionary<Bld, List<Bld>> Lanes;   // lane head -> sorters (in machine order)
    }

    static void Rows(Ctx c, List<Bld> ms, List<PatternInfo> res)
    {
        var lines = new List<Line>();
        foreach (var grp in ms.GroupBy(m => m.ItemId + ":" + m.RecipeId + ":" + ((int)Math.Round(Dirs.Norm(m.Yaw) / 90.0) % 4)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = grp.ToList();
            int cols = list.Select(m => m.TileX).Distinct().Count(), rows = list.Select(m => m.TileY).Distinct().Count();
            bool vertical = cols <= rows;
            foreach (var sub in list.GroupBy(m => vertical ? m.TileX : m.TileY).OrderBy(s => s.Key))
            {
                var ln = new Line { Item = list[0].ItemId, Recipe = list[0].RecipeId, Yaw = list[0].Yaw, Vertical = vertical, Coord = sub.Key,
                    Machines = sub.OrderBy(m => vertical ? m.TileY : m.TileX).ToList() };
                ln.Lanes = LanesOf(c, ln.Machines);
                lines.Add(ln);
            }
        }
        var used = new HashSet<Line>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (used.Contains(lines[i])) continue;
            for (int j = i + 1; j < lines.Count; j++)
            {
                Line a = lines[i], b = lines[j];
                if (used.Contains(b) || a.Item != b.Item || a.Vertical != b.Vertical) continue;
                int gap = Math.Abs(a.Coord - b.Coord);
                if (gap < 3 || gap > 6) continue;
                var shared = a.Lanes.Keys.Where(b.Lanes.ContainsKey).ToList();
                if (shared.Count == 0) continue;
                used.Add(a); used.Add(b);
                res.Add(Describe(c, a, b, shared.Count));
                break;
            }
            if (!used.Contains(lines[i])) { used.Add(lines[i]); res.Add(Describe(c, lines[i], null, 0)); }
        }
    }

    /// Lane head -> sorters of these machines on that lane (machine order). Sorters to another machine are skipped.
    static Dictionary<Bld, List<Bld>> LanesOf(Ctx c, List<Bld> machines)
    {
        var lanes = new Dictionary<Bld, List<Bld>>();
        var set = new HashSet<Bld>(machines);
        foreach (Bld s in c.L.All)
        {
            if (s.Kind != Kind.Sorter) continue;
            Bld m = set.Contains(s.Output) ? s.Output : set.Contains(s.Input) ? s.Input : null;
            if (m == null) continue;
            Bld other = s.Output == m ? s.Input : s.Output;
            if (other == null || other.Kind != Kind.Belt) continue;
            Bld h = c.Head(other);
            List<Bld> l;
            if (!lanes.TryGetValue(h, out l)) lanes[h] = l = new List<Bld>();
            l.Add(s);
        }
        foreach (var l in lanes.Values) l.Sort((p, q) => machines.IndexOf(MachineOf(p, set)).CompareTo(machines.IndexOf(MachineOf(q, set))));
        return lanes;
    }

    static Bld MachineOf(Bld s, HashSet<Bld> set) { return set.Contains(s.Output) ? s.Output : s.Input; }

    static PatternInfo Describe(Ctx c, Line a, Line b, int shared)
    {
        var info = new PatternInfo { Kind = b == null ? PatternKind.Row : PatternKind.DoubleColumn, MachineItem = a.Item, RecipeId = a.Recipe, MachineYaw = Math.Round(a.Yaw, 1),
            MachineCount = a.Machines.Count, MachineCount2 = b != null ? b.Machines.Count : 0, Vertical = a.Vertical, Coord = a.Coord, Coord2 = b != null ? b.Coord : 0, SharedLanes = shared };
        info.Machines.AddRange(a.Machines);
        if (b != null) info.Machines.AddRange(b.Machines);
        foreach (Bld m in info.Machines) info.Bounds = info.Bounds.Union(c.L.Footprints.TileRect(m));
        // pitch
        var coords = a.Machines.Select(m => a.Vertical ? m.TileY : m.TileX).ToList();
        var diffs = new List<int>();
        for (int i = 1; i < coords.Count; i++) diffs.Add(coords[i] - coords[i - 1]);
        if (diffs.Count > 0) { info.Pitch = diffs.Min(); if (diffs.Distinct().Count() > 1) info.Note = "irregular pitch " + string.Join("/", diffs.Select(d => d.ToString()).ToArray()); }
        // lanes
        var flows = new List<Dir>();
        var notes = new List<string>();
        int col = 0;
        foreach (Line ln in new[] { a, b })
        {
            if (ln == null) continue;
            var set = new HashSet<Bld>(ln.Machines);
            foreach (var kv in ln.Lanes)
            {
                LaneInfo li = Lane(kv.Key, kv.Value, set);
                li.Column = col;
                li.Machines = kv.Value.Select(s => MachineOf(s, set)).Distinct().Count();
                flows.Add(li.Belt.FlowDir);
                info.Bounds = info.Bounds.Union(Tile(li.Belt));
                info.Lanes.Add(li);
            }
            int direct = c.L.All.Count(s => s.Kind == Kind.Sorter && (set.Contains(s.Output) || set.Contains(s.Input)) && (s.Output == null || s.Input == null || (s.Output.Kind != Kind.Belt && s.Input.Kind != Kind.Belt)));
            if (direct > 0) notes.Add(direct + " machine-to-machine sorter(s)");
            col++;
        }
        if (flows.Count > 0) info.Flow = flows.GroupBy(f => f).OrderByDescending(g => g.Count()).First().Key;
        else info.Flow = a.Vertical ? Dir.N : Dir.E;
        info.Lanes = info.Lanes.OrderBy(x => x.Column).ThenBy(x => (int)x.Side).ThenBy(x => x.Length).ThenByDescending(x => x.Input).ToList();
        ItemsFromRecipe(c.Gd, info);
        if (b != null)
        {
            double mid = (a.Coord + b.Coord) / 2.0;
            foreach (LaneInfo li in info.Lanes) info.LaneOffsets.Add((int)Math.Round((a.Vertical ? li.Belt.TileX : li.Belt.TileY) - mid));
        }
        if (notes.Count > 0) info.Note = (info.Note != null ? info.Note + "; " : "") + string.Join("; ", notes.ToArray());
        return info;
    }

    static LaneInfo Lane(Bld head, List<Bld> sorters, HashSet<Bld> machines)
    {
        Bld s = sorters[0], m = MachineOf(s, machines);
        Bld belt = s.Output == m ? s.Input : s.Output;
        double mx = Math.Round(m.X), my = Math.Round(m.Y);
        var li = new LaneInfo { Sorter = s, Machine = m, Belt = belt, Head = head, Input = s.Output == m, Length = s.SorterLength, SorterYaw = s.Yaw,
            BeltDx = belt.TileX - (int)mx, BeltDy = belt.TileY - (int)my, Sx = s.X - mx, Sy = s.Y - my, Ex = s.X2 - mx, Ey = s.Y2 - my };
        li.Slot = li.Input ? s.OutputToSlot : s.InputFromSlot;
        li.Side = Math.Abs(li.BeltDx) >= Math.Abs(li.BeltDy) ? (li.BeltDx > 0 ? Dir.E : Dir.W) : (li.BeltDy > 0 ? Dir.N : Dir.S);
        li.Lateral = li.Side == Dir.E || li.Side == Dir.W ? li.BeltDy : li.BeltDx;
        if (head.Parameters != null && head.Parameters.Length > 0 && head.Parameters[0] != 0) { li.Item = head.Parameters[0]; li.ItemFromIcon = true; }
        var odd = new List<string>();
        foreach (Bld o in sorters)
        {
            Bld om = MachineOf(o, machines);
            bool oin = o.Output == om;
            int oslot = oin ? o.OutputToSlot : o.InputFromSlot;
            if (oin != li.Input || oslot != li.Slot || o.SorterLength != li.Length) odd.Add("#" + o.Index + " " + (oin ? "in" : "out") + " slot " + oslot + " L" + o.SorterLength);
        }
        if (odd.Count > 0) li.Note = "mixed: " + string.Join(", ", odd.Take(3).ToArray());
        return li;
    }

    /// Items of lanes without an icon tail from the recipe: output lanes carry the product, an input lane the one
    /// recipe input that no icon lane carries (ambiguous -> item stays 0).
    static void ItemsFromRecipe(GameData gd, PatternInfo info)
    {
        RecipeDef r = gd != null ? gd.Recipe(info.RecipeId) : null;
        if (r == null) return;
        var known = new HashSet<int>(info.Lanes.Where(x => x.Item != 0).Select(x => x.Item));
        var open = r.Inputs.Select(i => i.Item).Where(i => !known.Contains(i)).ToList();
        var openLanes = info.Lanes.Where(x => x.Item == 0 && x.Input).ToList();
        foreach (LaneInfo li in info.Lanes)
        {
            if (li.Item != 0) continue;
            if (!li.Input) { if (r.Outputs.Count > 0) li.Item = r.Outputs[0].Item; }
            else if (open.Count == 1 && openLanes.Count == 1) li.Item = open[0];
            else if (openLanes.Count == open.Count && open.Count > 0)
            {
                li.Item = open[openLanes.IndexOf(li)];   // positional guess
                li.Note = (li.Note != null ? li.Note + "; " : "") + "item by position";
            }
        }
    }

    // ------------------------------------------------------------------ registration
    /// SorterGeo cells of a Row / DoubleColumn (one per lane, the first machine's sorter): status "game", Source =
    /// source + " #" + record index, SourceKey null (not re-checked by Cells.Load), Name suffixed with the source so it
    /// never collides with a table cell. Empty for other kinds.
    public static List<SorterGeo> RowCells(PatternInfo row, string source)
    {
        var cells = new List<SorterGeo>();
        if (row == null || (row.Kind != PatternKind.Row && row.Kind != PatternKind.DoubleColumn)) return cells;
        foreach (LaneInfo li in row.Lanes)
        {
            if (li.Sorter == null || li.Machine == null) continue;
            double myaw = Dirs.Norm(Math.Round(row.MachineYaw / 90.0) * 90.0);
            var g = new SorterGeo
            {
                MachineItem = row.MachineItem, MachineYaw = myaw, BeltToMachine = li.Input, BeltDx = li.BeltDx, BeltDy = li.BeltDy,
                Sx = li.Sx, Sy = li.Sy, Ex = li.Ex, Ey = li.Ey, Slot = li.Slot, Length = li.Length, Status = "game", SourceKey = null,
                SourceIndex = li.Sorter.Index, Pattern = row.Kind + " " + source,
                Note = "pattern drawing, " + (li.Item != 0 ? "lane item " + li.Item : "lane item unknown")
            };
            g.Yaw = Dirs.Yaw(Dirs.FromYaw(Math.Atan2(li.Ex - li.Sx, li.Ey - li.Sy) * 180.0 / Math.PI));
            g.Source = source + " #" + li.Sorter.Index;
            g.Name = Geo.MakeName(g) + " [" + source + "]";
            cells.Add(g);
        }
        return cells;
    }

    // ------------------------------------------------------------------ catalog (docs\pattern-catalog.md)
    static bool Asm(int item) { return item == Ids.AsmMk1 || item == Ids.AsmMk2 || item == Ids.AsmMk3; }
    static bool Smelter(int item) { return item == Ids.ArcSmelter || item == Ids.PlaneSmelter; }

    public static readonly List<CatalogEntry> Catalog = new List<CatalogEntry>
    {
        E("P01", "spray coater on one input lane, proliferator lane + sorter into the coater", PatternKind.CoaterRow, i => i.Kind == PatternKind.CoaterRow && i.Coaters >= 1 && i.FeedCount >= 1),
        E("P02", "coaters on both lanes of a 2-input row, one proliferator lane", PatternKind.CoaterRow, i => i.Kind == PatternKind.CoaterRow && i.Coaters >= 2 && i.FeedCount == 1),
        E("P03", "coaters on 3 lanes of a 3-input row", PatternKind.CoaterRow, null),
        E("P04", "Matrix Lab tower row, one sorter per input at the base", PatternKind.Row, i => i.Kind == PatternKind.Row && i.MachineItem == Ids.MatrixLab),
        E("P05", "two tower rows with a shared output lane", PatternKind.DoubleColumn, i => i.Kind == PatternKind.DoubleColumn && i.MachineItem == Ids.MatrixLab),
        E("P06", "Miniature Particle Collider row, 1 input", PatternKind.Row, i => i.Kind == PatternKind.Row && i.MachineItem == Ids.Collider && i.InputLanes == 1),
        E("P07", "Miniature Particle Collider row, 3 inputs", PatternKind.Row, i => i.Kind == PatternKind.Row && i.MachineItem == Ids.Collider && i.InputLanes == 3),
        E("P08", "3-input Assembler Mk.II row on Mk.II sorters (L1 + L1 + L2 behind), output lane", PatternKind.Row, i => i.Kind == PatternKind.Row && Asm(i.MachineItem) && i.MachineCount >= 3 && i.InputLanes == 3 && i.OutputLanes >= 1),
        E("P09", "4-input Assembler Mk.II row (2 lanes per side), output lane", PatternKind.Row, i => i.Kind == PatternKind.Row && Asm(i.MachineItem) && i.MachineCount >= 3 && i.InputLanes == 4 && i.OutputLanes >= 1),
        E("P10", "maximum machines on one Mk.II lane (double smelter column, shared lane)", PatternKind.DoubleColumn, i => i.Kind == PatternKind.DoubleColumn && Smelter(i.MachineItem) && i.MachineCount + i.MachineCount2 >= 12 && i.SharedLanes >= 1),
        E("P11", "flow above Mk.II: two parallel lanes of one item", PatternKind.Comb, null),
        E("P12", "comb of external input tails on one edge", PatternKind.Comb, i => i.Kind == PatternKind.Comb && i.LaneCount >= 5),
        E("P13", "bundle of lanes bridging over a bundle of lanes", PatternKind.Bridge, i => i.Kind == PatternKind.Bridge && i.Runs >= 2),
        E("P14", "splitter 1->2 with a turn right after the output", PatternKind.SplitterTurn, i => i.Kind == PatternKind.SplitterTurn && i.CornerDistances.Any(d => d >= 0 && d <= 2)),
        E("P15", "splitter cascade 1->4", PatternKind.SplitterTurn, null),
        E("P16", "smelters flush against their consumer", PatternKind.Row, null),
        E("P17", "product into a buffer depot, other directions", PatternKind.Row, null),
    };

    static CatalogEntry E(string id, string purpose, PatternKind kind, Func<PatternInfo, bool> proves)
    {
        return new CatalogEntry { Id = id, FilePattern = id.ToLowerInvariant() + "-*.txt", Purpose = purpose, ProofKind = kind, Proves = proves };
    }

    static readonly Regex OkWord = new Regex(@"(^|[^a-z])ok([^a-z]|$)", RegexOptions.IgnoreCase);

    /// True when a drawing's header desc marks it as tested in game (the word "ok").
    public static bool IsOk(string desc) { return desc != null && OkWord.IsMatch(desc); }

    /// Per catalog entry: missing / drawn / proven. A file named pNN-*.txt draws PNN; otherwise any file whose analysis
    /// contains an info satisfying the entry's proof predicate draws it (a sheet with several patterns). Proven = that
    /// file's desc contains the word "ok".
    public static List<CatalogStatus> Status(IEnumerable<PatternFile> files)
    {
        var list = files != null ? files.ToList() : new List<PatternFile>();
        var res = new List<CatalogStatus>();
        foreach (CatalogEntry e in Catalog)
        {
            var st = new CatalogStatus { Entry = e, State = "missing" };
            string prefix = e.Id.ToLowerInvariant() + "-";
            PatternFile named = list.FirstOrDefault(f => f.Name != null && f.Name.ToLowerInvariant().StartsWith(prefix));
            if (named != null)
            {
                st.File = named.Name;
                PatternInfo p = e.Proves != null && named.Infos != null ? named.Infos.FirstOrDefault(e.Proves) : null;
                st.Why = p != null ? "file + " + p.Kind : (e.Proves != null ? "file (analysis shows no " + e.ProofKind + " for it)" : "file");
            }
            else if (e.Proves != null)
            {
                foreach (PatternFile f in list)
                {
                    PatternInfo p = f.Infos != null ? f.Infos.FirstOrDefault(e.Proves) : null;
                    if (p == null) continue;
                    st.File = f.Name; st.Why = p.Kind + " in " + f.Name;
                    break;
                }
            }
            if (st.File != null) { PatternFile f = list.First(x => x.Name == st.File); st.State = IsOk(f.Desc) ? "proven" : "drawn"; }
            res.Add(st);
        }
        return res;
    }
}
}
