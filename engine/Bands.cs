// Bands.cs - belts of the pattern-driven composer (docs\floorplan-design.md, agent "bands").
// Bands.Lay takes an FpPlan whose row fragments are already in the Layout and lays every connection:
//   Straight: the producer's out port is under the consumer's in port - link them, or lay the 1..2 joining tiles.
//   Comb:     block input tail on y = CombY at x = Slot (icon [item,0]), straight north into the target in port; a lane
//             whose target column differs turns along the edge (first free row >= CombY + 2) and the straight lanes it
//             crosses bridge over it (P12 drawing refs\patterns\p-sheet-001.txt x3 y40..42: raised z 0.5 on the crossed
//             tiles, straight ground feet).
//   Band:     from the source out port north to the band row y = BandY0 + Slot, along the row, south into the target in
//             port (a port flowing S is entered from the north; a port flowing N is reached by a descent in the free
//             column beside the lane and a one-tile hook). Every vertical ground lane crossed on the band row gets a
//             one-tile bridge: the band belt is the raised node (P13 bundle x13..15 y40..42, adjacent bridges allowed).
//             Forks (one source, 2 targets) = one 2-way Modules.Splitter on the band row (P14: the outputs may turn 2
//             tiles after the splitter); a third target is fed from the second target's lane end. Merges (two sources
//             into one target) = Modules.SideMerge into the first run.
// Laying order: Straight, Comb, then Band verticals (rises / descents, by slot ascending), Band horizontals by slot
// descending (they cross the verticals by bridges; see LayBands), merges last. Every chain is checked completely (occupancy, bridge shape, sorter distance,
// link ends) before a single belt is added, so a failed connection leaves nothing behind. Pure model code: no game DLL,
// no I/O. C# 5.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

public static class Bands
{
    /// Lays the belts of every connection of fp into work (which already holds every FpRow.Frag). Returns the errors
    /// (empty = every connection laid); notes per connection go to fp.Notes.
    public static List<string> Lay(GameData gd, BlockSpec spec, FpPlan fp, Layout work)
    {
        var errors = new List<string>();
        if (fp == null || work == null) { errors.Add("bands: no plan or no layout"); return errors; }
        var ctx = new Ctx(gd, spec, fp, work);
        try
        {
            foreach (FpConnection c in fp.Connections.Where(c => c.Route == FpRoute.Straight)) ctx.LayStraight(c, errors);
            ctx.LayCombs(fp.Connections.Where(c => c.Route == FpRoute.Comb).ToList(), errors);
            ctx.LayBands(fp.Connections.Where(c => c.Route == FpRoute.Band).ToList(), errors);
        }
        catch (Exception ex)
        {
            errors.Add("bands: internal error " + ex.GetType().Name + ": " + ex.Message);
        }
        return errors;
    }

    // ================================================================ context
    sealed class Ctx
    {
        readonly GameData gd;
        readonly FpPlan fp;
        readonly Layout work;
        readonly int beltItem;
        readonly Dictionary<Bld, Bld> predOf = new Dictionary<Bld, Bld>();   // belt -> straight predecessor (slot <= 1)
        readonly HashSet<long> raisedTiles = new HashSet<long>();             // tiles carrying a raised node
        readonly HashSet<Bld> noJoin = new HashSet<Bld>();                    // feet, crossed ground belts, corners: no side-join
        readonly HashSet<Bld> removed = new HashSet<Bld>();                   // belts taken out again (Layout.BeltAt keeps them)
        readonly List<Bld> sorters;
        readonly Dictionary<Bld, int[]> clearedIcon = new Dictionary<Bld, int[]>();   // lane icons removed by a link, restored on rollback
        readonly Dictionary<string, int> chainCount = new Dictionary<string, int>();

        public Ctx(GameData gd, BlockSpec spec, FpPlan fp, Layout work)
        {
            this.gd = gd; this.fp = fp; this.work = work;
            beltItem = spec != null && spec.Tiers != null ? spec.Tiers.Belt : Ids.BeltMk2;
            foreach (Bld b in work.All)
            {
                if (b.Kind != Kind.Belt) continue;
                if (b.Output != null && b.Output.Kind == Kind.Belt && b.OutputToSlot <= 1 && !predOf.ContainsKey(b.Output)) predOf[b.Output] = b;
                if (b.IsRaised) raisedTiles.Add(K(b.TileX, b.TileY));
            }
            sorters = work.All.Where(b => b.Kind == Kind.Sorter).ToList();
        }

        static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }
        static string At(int x, int y) { return "(" + x + "," + y + ")"; }
        static bool Fractional(Bld b) { return Math.Abs(b.X - Math.Round(b.X)) > 0.01 || Math.Abs(b.Y - Math.Round(b.Y)) > 0.01; }

        string ChainName(string prefix, int item)
        {
            string key = prefix + ":" + item;
            int n; chainCount.TryGetValue(key, out n); chainCount[key] = n + 1;
            return key + "#" + n.ToString(CultureInfo.InvariantCulture);
        }

        Bld GroundAt(int x, int y)
        {
            Bld b = work.BeltAt(x, y);
            return b != null && !removed.Contains(b) ? b : null;
        }

        Bld Pred(Bld b) { Bld p; return predOf.TryGetValue(b, out p) ? p : null; }

        static Dir Delta(int x0, int y0, int x1, int y1) { return Dirs.FromDelta(Math.Sign(x1 - x0), Math.Sign(y1 - y0)); }

        /// Waypoints -> tile path; duplicates between consecutive waypoints are skipped. Null on a bad shape.
        static List<int[]> Path(params int[] xy)
        {
            var pts = new List<int>();
            for (int i = 0; i + 1 < xy.Length; i += 2)
                if (pts.Count < 2 || pts[pts.Count - 2] != xy[i] || pts[pts.Count - 1] != xy[i + 1]) { pts.Add(xy[i]); pts.Add(xy[i + 1]); }
            try { return new List<int[]>(Modules.Path(pts.ToArray())); }
            catch (ArgumentException) { return null; }
        }

        // ================================================================ chain plan: check completely, then commit
        sealed class Plan
        {
            public List<int[]> Tiles;
            public bool[] Raised;
            public Bld LinkFrom;     // belt whose chain end feeds the first tile (null = chain start)
            public Bld LinkTo;       // belt the last tile links into (slot 1)
            public Bld MergeInto;    // belt the last tile side-joins (rule 6)
            public int Bridges { get { return Raised.Count(r => r); } }
        }

        /// Tile of the extended path at index idx (-2 .. n+1): the path itself, the linked belts before / after it.
        int[] Ext(Plan p, int idx)
        {
            int n = p.Tiles.Count;
            if (idx >= 0 && idx < n) return p.Tiles[idx];
            if (idx == -1) return p.LinkFrom != null && !Fractional(p.LinkFrom) ? new[] { p.LinkFrom.TileX, p.LinkFrom.TileY } : null;
            if (idx == -2)
            {
                if (p.LinkFrom == null) return null;
                Bld q = Pred(p.LinkFrom);
                if (q == null && p.LinkFrom.Input != null && p.LinkFrom.Input.Kind == Kind.Splitter) q = p.LinkFrom.Input;   // splitter port belt: the splitter is the approach
                return q != null && (q.Kind == Kind.Splitter || !Fractional(q)) ? new[] { q.TileX, q.TileY } : null;
            }
            if (idx == n) return p.LinkTo != null && !Fractional(p.LinkTo) ? new[] { p.LinkTo.TileX, p.LinkTo.TileY } : null;
            if (idx == n + 1)
            {
                if (p.LinkTo == null || p.LinkTo.Output == null) return null;
                Bld q = p.LinkTo.Output;
                return q.Kind == Kind.Splitter || (q.Kind == Kind.Belt && !Fractional(q)) ? new[] { q.TileX, q.TileY } : null;
            }
            return null;
        }

        /// Validates a plan against the layout: tiles free or straight ground belts to bridge over (one-tile bridges),
        /// raised groups straight for two tiles on both sides (approach + foot), link ends open. Fills p.Raised.
        /// Returns null when the plan can be laid, else the reason.
        string Check(Plan p, int item)
        {
            var t = p.Tiles;
            int n = t.Count;
            if (n == 0) return "empty path";
            p.Raised = new bool[n];
            var seen = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                if (!seen.Add(K(t[i][0], t[i][1]))) return "tile " + At(t[i][0], t[i][1]) + " used twice";
                if (i > 0 && Math.Abs(t[i][0] - t[i - 1][0]) + Math.Abs(t[i][1] - t[i - 1][1]) != 1) return "tiles not adjacent at " + At(t[i][0], t[i][1]);
            }
            // link ends
            if (p.LinkFrom != null)
            {
                if (p.LinkFrom.Output != null) return "source belt " + p.LinkFrom + " is already linked";
                if (Math.Abs(p.LinkFrom.TileX - t[0][0]) + Math.Abs(p.LinkFrom.TileY - t[0][1]) != 1) return "source belt " + p.LinkFrom + " is not adjacent to the first tile " + At(t[0][0], t[0][1]);
                if (n > 1 && Delta(p.LinkFrom.TileX, p.LinkFrom.TileY, t[0][0], t[0][1]) == Dirs.Opposite(Delta(t[0][0], t[0][1], t[1][0], t[1][1]))) return "U-turn at the first tile " + At(t[0][0], t[0][1]);
            }
            if (p.LinkTo != null && p.MergeInto != null) return "both LinkTo and MergeInto";
            Bld end = p.LinkTo ?? p.MergeInto;
            if (end != null && Math.Abs(end.TileX - t[n - 1][0]) + Math.Abs(end.TileY - t[n - 1][1]) != 1) return "target belt " + end + " is not adjacent to the last tile " + At(t[n - 1][0], t[n - 1][1]);
            if (p.LinkTo != null)
            {
                if (Pred(p.LinkTo) != null) return "target belt " + p.LinkTo + " already has a feeding belt";
                if (n > 1 && Delta(t[n - 1][0], t[n - 1][1], p.LinkTo.TileX, p.LinkTo.TileY) == Dirs.Opposite(Delta(t[n - 2][0], t[n - 2][1], t[n - 1][0], t[n - 1][1]))) return "U-turn into the target at " + At(t[n - 1][0], t[n - 1][1]);
                if (p.LinkTo.Kind == Kind.Belt && p.LinkTo.Output != null && p.LinkTo.Output.Kind == Kind.Belt && !Fractional(p.LinkTo.Output)
                    && Delta(p.LinkTo.TileX, p.LinkTo.TileY, p.LinkTo.Output.TileX, p.LinkTo.Output.TileY) == Dirs.Opposite(Delta(t[n - 1][0], t[n - 1][1], p.LinkTo.TileX, p.LinkTo.TileY)))
                    return "target belt " + p.LinkTo + " flows back toward the last tile";
            }
            if (p.MergeInto != null)
            {
                string m = MergeOk(p.MergeInto, t[n - 1][0], t[n - 1][1]);
                if (m != null) return m;
            }
            // U-turns inside
            for (int i = 1; i + 1 < n; i++)
                if (Delta(t[i - 1][0], t[i - 1][1], t[i][0], t[i][1]) == Dirs.Opposite(Delta(t[i][0], t[i][1], t[i + 1][0], t[i + 1][1]))) return "U-turn at " + At(t[i][0], t[i][1]);
            // occupancy per tile
            for (int i = 0; i < n; i++)
            {
                int x = t[i][0], y = t[i][1];
                long k = K(x, y);
                Bld occ = work.Grid.At(x, y);
                if (occ != null && removed.Contains(occ)) occ = null;
                if (occ == null)
                {
                    if (raisedTiles.Contains(k)) return "tile " + At(x, y) + " lies under a raised node of another lane";
                    continue;
                }
                if (occ.Kind != Kind.Belt) return "tile " + At(x, y) + " is occupied by " + occ;
                Bld g = GroundAt(x, y);
                if (g == null || g != occ) return "tile " + At(x, y) + " is occupied by a belt that cannot be crossed (" + occ + ")";
                string why = CrossOk(g, i, p);
                if (why != null) return "cannot bridge over " + g + " at " + At(x, y) + ": " + why;
                p.Raised[i] = true;
            }
            // raised groups: straight from two tiles before to two tiles after (approach - foot - R.. - foot - approach)
            for (int i = 0; i < n; i++)
            {
                if (!p.Raised[i] || (i > 0 && p.Raised[i - 1])) continue;
                int j = i; while (j + 1 < n && p.Raised[j + 1]) j++;
                if (p.MergeInto != null && j + 2 > n - 1) return "bridge at " + At(t[i][0], t[i][1]) + " too close to the side-join end";
                int[] a = Ext(p, i - 2), f0 = Ext(p, i - 1), f1 = Ext(p, j + 1), b = Ext(p, j + 2);
                if (a == null || f0 == null) return "bridge at " + At(t[i][0], t[i][1]) + " has no ground foot and approach before it";
                if (f1 == null || b == null) return "bridge at " + At(t[j][0], t[j][1]) + " has no ground foot and approach after it";
                int dx = t[i][0] - f0[0], dy = t[i][1] - f0[1];
                if (Math.Abs(dx) + Math.Abs(dy) != 1) return "bridge at " + At(t[i][0], t[i][1]) + ": foot before it is not adjacent";
                for (int q = i - 2; q <= j + 1; q++)
                {
                    int[] u = Ext(p, q), v = Ext(p, q + 1);
                    if (u == null || v == null || v[0] - u[0] != dx || v[1] - u[1] != dy) return "bridge at " + At(t[i][0], t[i][1]) + " is not straight from the approach node to the far approach node";
                }
                if (i - 1 < 0 && p.LinkFrom != null && (p.LinkFrom.IsRaised || Fractional(p.LinkFrom))) return "bridge at " + At(t[i][0], t[i][1]) + ": the source belt cannot be a ramp foot";
                if (j + 1 > n - 1 && p.LinkTo != null && (p.LinkTo.IsRaised || Fractional(p.LinkTo) || p.LinkTo.Output == null)) return "bridge at " + At(t[j][0], t[j][1]) + ": the target belt cannot be a ramp foot";
                if (i - 1 >= 0 && p.Raised[i - 1]) return "internal: foot raised";
            }
            return null;
        }

        /// A ground belt g may be crossed by a raised node of plan p at index i: straight through (predecessor and
        /// successor in line, adjacent, no side-join), not flowing against the crossing belt, no raised node yet on the
        /// tile, no fractional neighbours, at least GameData.RaisedSorterMin from every sorter (no UNPROVEN warning).
        string CrossOk(Bld g, int i, Plan p)
        {
            int x = g.TileX, y = g.TileY;
            if (raisedTiles.Contains(K(x, y))) return "a raised node is already there";
            if (g.IsRaised || Fractional(g)) return "not a ground grid belt";
            Bld pr = Pred(g), su = g.Output;
            if (pr == null || su == null || su.Kind != Kind.Belt) return "the belt starts or ends there";
            if (g.OutputToSlot != 1 || Fractional(pr) || Fractional(su) || pr.IsRaised || su.IsRaised) return "the belt is not a plain ground chain there";
            if (Math.Abs(pr.TileX - x) + Math.Abs(pr.TileY - y) != 1 || Math.Abs(su.TileX - x) + Math.Abs(su.TileY - y) != 1) return "the belt's links are not one tile long";
            Dir f = Delta(pr.TileX, pr.TileY, x, y);
            if (Delta(x, y, su.TileX, su.TileY) != f) return "the belt turns there";
            if (HasSideJoin(g)) return "the belt takes a side-join there";
            // my direction at the tile
            var t = p.Tiles;
            int[] before = i > 0 ? t[i - 1] : Ext(p, -1), after = i + 1 < t.Count ? t[i + 1] : Ext(p, t.Count);
            if (before == null || after == null) return "no neighbour tile to define the crossing direction";
            Dir d = Delta(before[0], before[1], x, y);
            if (Delta(x, y, after[0], after[1]) != d) return "my lane turns on the crossed tile";
            if (f == Dirs.Opposite(d)) return "the belt flows against my lane";
            foreach (Bld s in sorters)
            {
                double ds = GameData.SegmentDist(x, y, s.X, s.Y, s.X2, s.Y2);
                if (ds < GameData.RaisedSorterMin - 1e-6) return "raised node would be " + ds.ToString("0.##", CultureInfo.InvariantCulture) + " tile from sorter " + s + " (proven >= " + GameData.RaisedSorterMin + ")";
            }
            return null;
        }

        bool HasSideJoin(Bld g)
        {
            foreach (Bld b in work.All) if (b.Kind == Kind.Belt && b.Output == g && b.OutputToSlot > 1 && !removed.Contains(b)) return true;
            return false;
        }

        /// Side-join pre-check (Modules.SideMerge rules + the bridge rules: never into a foot, a crossed belt or a corner).
        string MergeOk(Bld target, int jx, int jy)
        {
            if (target.IsRaised || Fractional(target)) return "side-join target " + target + " is not a ground grid belt";
            if (noJoin.Contains(target) || raisedTiles.Contains(K(target.TileX, target.TileY))) return "side-join target " + target + " is a ramp foot, a corner or lies under a bridge";
            Bld pr = Pred(target), su = target.Output;
            if (pr == null || su == null || su.Kind != Kind.Belt) return "side-join target " + target + " is a chain start or end";
            Dir f = Delta(pr.TileX, pr.TileY, target.TileX, target.TileY);
            if (Delta(target.TileX, target.TileY, su.TileX, su.TileY) != f) return "side-join target " + target + " is a corner";
            Dir d = Delta(jx, jy, target.TileX, target.TileY);
            if (d == f || d == Dirs.Opposite(f)) return "side-join into " + target + " is not perpendicular to its flow";
            int joins = 0;
            foreach (Bld b in work.All) if (b.Kind == Kind.Belt && b.Output == target && b.OutputToSlot > 1 && !removed.Contains(b)) joins++;
            if (joins >= 2) return "side-join target " + target + " already has two side-joins";
            return null;
        }

        /// Adds the checked plan to the layout: belts with flow / corner yaws, raised nodes at z 0.5, links, bookkeeping.
        List<Bld> Commit(Plan p, string chain, int item, string role)
        {
            var t = p.Tiles;
            int n = t.Count;
            var made = new List<Bld>();
            for (int i = 0; i < n; i++)
            {
                int x = t[i][0], y = t[i][1];
                Dir? dIn = null, dOut = null;
                if (i > 0) dIn = Delta(t[i - 1][0], t[i - 1][1], x, y);
                else if (p.LinkFrom != null) dIn = Delta(p.LinkFrom.TileX, p.LinkFrom.TileY, x, y);
                if (i + 1 < n) dOut = Delta(x, y, t[i + 1][0], t[i + 1][1]);
                else if (p.LinkTo != null) dOut = Delta(x, y, p.LinkTo.TileX, p.LinkTo.TileY);
                else if (p.MergeInto != null) dOut = Delta(x, y, p.MergeInto.TileX, p.MergeInto.TileY);
                Dir a = dIn ?? dOut ?? Dir.N, c = dOut ?? a;
                Bld b = Bld.Belt(beltItem, x, y, Modules.CornerYaw(a, c), item, chain);
                b.Role = role;
                if (p.Raised[i]) { b.Z = b.Z2 = GameData.BridgeZ; b.Role = role + " bridge"; }
                work.Add(b);
                made.Add(b);
                if (made.Count > 1) { Layout.Link(made[made.Count - 2], b, 1); predOf[b] = made[made.Count - 2]; }
                if (a != c) noJoin.Add(b);
            }
            if (p.LinkFrom != null) { Layout.Link(p.LinkFrom, made[0], 1); predOf[made[0]] = p.LinkFrom; }
            Bld last = made[n - 1];
            if (p.LinkTo != null)
            {
                Layout.Link(last, p.LinkTo, 1); predOf[p.LinkTo] = last;
                if (p.LinkTo.Parameters != null && p.LinkTo.Parameters.Length == 2 && p.LinkTo.Parameters[0] != 0) { clearedIcon[p.LinkTo] = p.LinkTo.Parameters; p.LinkTo.Parameters = null; }   // rule 5: icon only on a block input tail
            }
            else if (p.MergeInto != null) Modules.SideMerge(last, p.MergeInto);
            else Layout.Link(last, null, 0);
            // bridge bookkeeping: raised tiles, feet and crossed belts never take a side-join
            for (int i = 0; i < n; i++)
            {
                if (!p.Raised[i]) continue;
                raisedTiles.Add(K(t[i][0], t[i][1]));
                noJoin.Add(made[i]);
                Bld g = GroundAt(t[i][0], t[i][1]);
                if (g != null) noJoin.Add(g);
                if (i > 0) noJoin.Add(made[i - 1]); else if (p.LinkFrom != null) noJoin.Add(p.LinkFrom);
                if (i + 1 < n) noJoin.Add(made[i + 1]); else if (p.LinkTo != null) noJoin.Add(p.LinkTo);
            }
            return made;
        }

        /// Takes a laid chain out again (used only when a later step of the same connection fails).
        void Unlay(List<Bld> belts, Bld linkFrom)
        {
            foreach (Bld b in belts)
            {
                work.All.Remove(b); work.Grid.Release(b); removed.Add(b);
                predOf.Remove(b); raisedTiles.Remove(K(b.TileX, b.TileY)); noJoin.Remove(b);
                if (b.Output != null && b.Output.Kind == Kind.Belt && Pred(b.Output) == b)
                {
                    predOf.Remove(b.Output);
                    int[] icon;
                    if (clearedIcon.TryGetValue(b.Output, out icon)) { b.Output.Parameters = icon; clearedIcon.Remove(b.Output); }
                }
            }
            if (linkFrom != null && belts.Count > 0 && linkFrom.Output == belts[0]) Layout.Link(linkFrom, null, 0);
        }

        /// Tries the plans in order; commits the first that checks out. Returns the belts, or null (why = last reason).
        List<Bld> TryPlans(IEnumerable<Plan> plans, string chain, int item, string role, out string why, out Plan used)
        {
            why = "no candidate path"; used = null;
            foreach (Plan p in plans)
            {
                if (p == null || p.Tiles == null) { why = "path shape invalid"; continue; }
                string e = Check(p, item);
                if (e != null) { why = e; continue; }
                used = p;
                return Commit(p, chain, item, role);
            }
            return null;
        }

        void Record(FpConnection c, string chain, List<Bld> belts, string route)
        {
            c.Chain = chain; c.Laid = true;
            c.Belts.AddRange(belts);
            c.Bridges += belts.Count(b => b.IsRaised);
            var raised = belts.Where(b => b.IsRaised).Select(b => At(b.TileX, b.TileY)).ToArray();
            fp.Notes.Add("bands: " + c + ": " + route + ", chain " + chain + ", " + belts.Count + " tile(s)"
                + (raised.Length > 0 ? ", bridges at " + string.Join(" ", raised) : "") + (belts.Count > 0 ? ", " + At(belts[0].TileX, belts[0].TileY) + " -> " + At(belts[belts.Count - 1].TileX, belts[belts.Count - 1].TileY) : ""));
        }

        static string Who(FpConnection c) { return "connection " + c; }

        // ================================================================ Straight
        public void LayStraight(FpConnection c, List<string> errors)
        {
            if (c.Source == null || c.Target == null || c.Source.Belt == null || c.Target.Belt == null) { errors.Add("bands: " + Who(c) + ": straight connection without source / target port"); return; }
            Port s = c.Source, t = c.Target;
            string chain = ChainName("straight", c.Item);
            if (s.NextX == t.X && s.NextY == t.Y)
            {
                if (s.Belt.Output != null) { errors.Add("bands: " + Who(c) + ": source belt already linked"); return; }
                if (Pred(t.Belt) != null) { errors.Add("bands: " + Who(c) + ": target belt already fed"); return; }
                if (s.Dir != t.Dir) { errors.Add("bands: " + Who(c) + ": adjacent ports flow " + s.Dir + " / " + t.Dir); return; }
                Layout.Link(s.Belt, t.Belt, 1); predOf[t.Belt] = s.Belt;
                if (t.Belt.Parameters != null && t.Belt.Parameters.Length == 2) t.Belt.Parameters = null;
                c.Chain = chain; c.Laid = true;
                fp.Notes.Add("bands: " + c + ": straight, ports linked directly at " + At(t.X, t.Y));
                return;
            }
            int ax = s.NextX, ay = s.NextY, bx = t.PrevX, by = t.PrevY;
            var plans = new List<Plan>();
            plans.Add(new Plan { Tiles = Path(ax, ay, ax, by, bx, by), LinkFrom = s.Belt, LinkTo = t.Belt });
            plans.Add(new Plan { Tiles = Path(ax, ay, bx, ay, bx, by), LinkFrom = s.Belt, LinkTo = t.Belt });
            string why; Plan used;
            var belts = TryPlans(plans, chain, c.Item, "straight " + c.Item, out why, out used);
            if (belts == null) { errors.Add("bands: " + Who(c) + ": straight link " + At(ax, ay) + " -> " + At(bx, by) + " cannot be laid: " + why); return; }
            Record(c, chain, belts, "straight");
        }

        // ================================================================ Comb
        public void LayCombs(List<FpConnection> combs, List<string> errors)
        {
            // turning lanes first (ground), then the straight lanes that bridge over them (P12: the straight iron lane is raised)
            var turning = combs.Where(c => c.Target != null && c.Slot != c.Target.X).OrderBy(c => c.Target != null ? Math.Abs(c.Slot - c.Target.X) : 0).ThenBy(c => c.Slot).ToList();
            var straight = combs.Where(c => c.Target != null && c.Slot == c.Target.X).OrderBy(c => c.Slot).ToList();
            foreach (FpConnection c in combs) if (c.Target == null) errors.Add("bands: " + Who(c) + ": comb connection without a target port");
            foreach (FpConnection c in turning.Concat(straight)) LayComb(c, errors);
        }

        void LayComb(FpConnection c, List<string> errors)
        {
            Port t = c.Target;
            if (t.Belt == null) { errors.Add("bands: " + Who(c) + ": target port has no belt"); return; }
            if (c.Slot < 0) { errors.Add("bands: " + Who(c) + ": comb slot not assigned"); return; }
            if (t.Dir == Dir.S) { errors.Add("bands: " + Who(c) + ": comb lane into a port flowing S is not supported (south comb feeds lanes flowing N, E or W)"); return; }
            int x0 = c.Slot, y0 = fp.CombY, tx = t.PrevX, ty1 = t.PrevY;   // FLOORPLAN: E / W ports (final cell lane ends) are entered from their upstream tile
            if (ty1 < y0) { errors.Add("bands: " + Who(c) + ": target port " + At(t.X, t.Y) + " lies below the comb row y" + y0); return; }
            string chain = ChainName("comb", c.Item);
            var plans = new List<Plan>();
            if (x0 == tx) plans.Add(new Plan { Tiles = Path(x0, y0, x0, ty1), LinkTo = t.Belt });
            else
                // turn along the edge: the first row >= CombY + 2 (the straight lanes need an approach + foot below the bridge)
                for (int yt = y0 + 2; yt <= ty1; yt++) plans.Add(new Plan { Tiles = Path(x0, y0, x0, yt, tx, yt, tx, ty1), LinkTo = t.Belt });
            string why; Plan used;
            var belts = TryPlans(plans, chain, c.Item, "comb " + c.Item, out why, out used);
            if (belts == null) { errors.Add("bands: " + Who(c) + ": comb lane from " + At(x0, y0) + " to " + At(tx, ty1) + " cannot be laid: " + why); return; }
            belts[0].Parameters = new[] { c.Item, 0 };   // rule 5 / 9: icon on the tail (first tile of the block input chain)
            belts[0].Role = "comb " + c.Item + " tail";
            var port = new Port { Name = "in:" + c.Item, Kind = PortKind.In, X = x0, Y = y0, Dir = Dir.N, Item = c.Item, Rate = c.Rate, Belt = belts[0], Chain = chain };
            c.Tail = port;
            work.Ports.Add(port);
            int[] corner = used.Tiles.FirstOrDefault(q => q[0] != x0);
            Record(c, chain, belts, x0 == tx || corner == null ? "comb straight" : "comb turning at y" + corner[1].ToString(CultureInfo.InvariantCulture));
        }

        // ================================================================ Band
        sealed class Group
        {
            public Port Source;
            public FpRow From;
            public int Item, Slot;
            public readonly List<FpConnection> Conns = new List<FpConnection>();   // targets, sorted by column distance
            public int Y { get { return Slot; } }
        }

        sealed class Vert   // a vertical (rise or descent) laid in phase 1
        {
            public List<Bld> Belts;
            public Plan Plan;
            public Bld LinkFrom;   // rise: the source belt
        }

        public void LayBands(List<FpConnection> bands, List<string> errors)
        {
            // lane-tail consumers (FpConnection.TailOf) are laid after the groups, from the dead end of their host's lane
            var tails = bands.Where(c => c.TailOf != null).ToList();
            bands = bands.Where(c => c.TailOf == null).ToList();
            // groups: connections sharing a source port = forks; connections sharing a target port = merges (first = primary)
            var groups = new List<Group>();
            var merges = new List<FpConnection>();
            var primaryOf = new Dictionary<Port, FpConnection>();
            foreach (FpConnection c in bands.OrderBy(c => c.Slot).ThenBy(c => c.Source != null ? c.Source.X : 0))
            {
                if (c.Source == null || c.Target == null || c.Source.Belt == null || c.Target.Belt == null) { errors.Add("bands: " + Who(c) + ": band connection without source / target port"); continue; }
                if (c.Slot < 0) { errors.Add("bands: " + Who(c) + ": band slot not assigned"); continue; }
                if (primaryOf.ContainsKey(c.Target)) { merges.Add(c); continue; }
                primaryOf[c.Target] = c;
                Group g = groups.FirstOrDefault(o => o.Source == c.Source);
                if (g == null) { g = new Group { Source = c.Source, From = c.From, Item = c.Item, Slot = c.Slot }; groups.Add(g); }
                if (g.Slot != c.Slot) { errors.Add("bands: " + Who(c) + ": slot " + c.Slot + " differs from the group's slot " + g.Slot + " (one band row per source)"); continue; }
                g.Conns.Add(c);
            }
            // geometry per group
            var geo = new Dictionary<Group, GroupGeo>();
            foreach (Group g in groups)
            {
                string e;
                GroupGeo gg = PlanGroup(g, out e);
                if (gg == null) { errors.Add("bands: " + Who(g.Conns[0]) + ": " + e); continue; }
                geo[g] = gg;
            }
            // phase 1: verticals (rises and descents) of every group, ground belts
            var done = new HashSet<Group>();
            foreach (Group g in groups.Where(geo.ContainsKey))
            {
                GroupGeo gg = geo[g];
                if (!LayVerticals(g, gg, errors)) continue;
                done.Add(g);
            }
            // merge rises (phase 1 too: a later horizontal bridges over them)
            var mergeRise = new Dictionary<FpConnection, Vert>();
            foreach (FpConnection m in merges)
            {
                FpConnection prim = primaryOf[m.Target];
                Group pg = groups.FirstOrDefault(o => o.Conns.Contains(prim));
                if (pg == null || !done.Contains(pg)) { errors.Add("bands: " + Who(m) + ": the primary run of its target was not laid"); continue; }
                int yb = fp.BandY0 + pg.Slot;
                string chain = ChainName("merge", m.Item);
                var plans = new List<Plan>();
                plans.Add(new Plan { Tiles = Path(m.Source.NextX, m.Source.NextY, m.Source.NextX, yb - 1), LinkFrom = m.Source.Belt });
                string why; Plan used;
                var belts = TryPlans(plans, chain, m.Item, "merge " + m.Item, out why, out used);
                if (belts == null) { errors.Add("bands: " + Who(m) + ": merge rise from " + At(m.Source.NextX, m.Source.NextY) + " to y" + (yb - 1) + " cannot be laid: " + why); continue; }
                mergeRise[m] = new Vert { Belts = belts, Plan = used, LinkFrom = m.Source.Belt };
                m.Chain = chain;
            }
            // phase 2: horizontals by slot DESCENDING. The top tile of a slot k+1 rise (and the first tile of its descent)
            // lies on slot k's row and only becomes a straight-through belt once slot k+1's own row is linked to it, so
            // the higher rows must exist before a lower row can bridge over those tiles. No row crosses a higher slot's row.
            foreach (Group g in groups.Where(done.Contains).OrderByDescending(o => o.Slot))
                LayHorizontals(g, geo[g], errors);
            // phase 3: merges into the primary runs
            foreach (FpConnection m in merges.Where(mergeRise.ContainsKey))
            {
                FpConnection prim = primaryOf[m.Target];
                Group pg = groups.First(o => o.Conns.Contains(prim));
                if (!prim.Laid) { Unlay(mergeRise[m].Belts, mergeRise[m].LinkFrom); errors.Add("bands: " + Who(m) + ": the primary run " + prim + " was not laid, merge dropped"); continue; }
                LayMerge(m, mergeRise[m], prim, pg, geo[pg], errors);
            }
            // phase 4: lane-tail consumers, each after its host (a chain of tails is laid front to back)
            for (bool progress = true; progress && tails.Count > 0; )
            {
                progress = false;
                foreach (FpConnection c in tails.Where(c => c.TailOf.Laid).ToList())
                {
                    LayFromTail(c, c.TailOf, c.TailOf.Chain ?? ChainName("band", c.Item), errors);
                    tails.Remove(c);
                    progress = true;
                }
            }
            foreach (FpConnection c in tails) errors.Add("bands: " + Who(c) + ": its host lane " + c.TailOf + " was not laid");
        }

        sealed class TargetGeo
        {
            public FpConnection Conn;
            public int Xd;             // descent column on the band row
            public List<int[]> Descent;   // tiles from the band row - 1 (or - 2 after a splitter S port) down into the port
            public Bld DescentFirst;   // laid in phase 1
            public List<Bld> Belts;
            public bool FromTail;      // third target: fed from the second target's lane end
        }

        sealed class GroupGeo
        {
            public int Sx, Yb;
            public List<int[]> Rise;             // (sx, sy+1) .. (sx, yb-1) or (sx, yb-2) when the splitter sits on the rise
            public Bld RiseLast;                 // last belt of the rise (or the source belt when the rise is empty)
            public List<Bld> RiseBelts;
            public List<TargetGeo> Targets = new List<TargetGeo>();
            public bool Fork;                    // 2-way splitter
            public int Fx;                       // splitter x on the band row
            public bool ThreeWay;                // 3 consumers on both sides: splitter at (sx, yb) with outputs W, E and N, the N branch runs on the row yb + 2 (useredit3 copper (28,17))
            public bool SplitOnRise;             // splitter at (sx, yb), input from S, outputs W and E
            public Dir SplitIn;                  // input side of the splitter (W / E / S)
        }

        /// Descent tiles into target port t from the band row (first tile at y = yTop in column xd). Dir S ports are
        /// entered from the north in their own column; Dir N ports (south end of the lane) through the free column beside
        /// the lane and a one-tile hook under the port.
        List<int[]> DescentTiles(Port t, int yTop, out int xd, out string why)
        {
            xd = t.X; why = null;
            if (t.Dir == Dir.S)
            {
                if (yTop < t.PrevY) { why = "band row is below the target port " + At(t.X, t.Y); return null; }
                return Path(t.X, yTop, t.X, t.PrevY);
            }
            if (t.Dir == Dir.N)
            {
                if (yTop < t.PrevY) { why = "band row is below the target port " + At(t.X, t.Y); return null; }
                foreach (int side in new[] { 1, -1 })
                {
                    int x = t.X + side;
                    bool free = true;
                    for (int y = t.PrevY; y <= yTop && free; y++) free = work.Grid.IsFree(x, y) || (work.Grid.At(x, y) != null && work.Grid.At(x, y).Kind == Kind.Belt);
                    if (!free || !work.Grid.IsFree(t.PrevX, t.PrevY)) continue;
                    xd = x;
                    return Path(x, yTop, x, t.PrevY, t.PrevX, t.PrevY);
                }
                why = "no free column beside the lane for a descent into the port " + At(t.X, t.Y) + " flowing N";
                return null;
            }
            // FLOORPLAN: a port flowing E / W (final cell N-face lane, rule-11 lane ends): descend into its upstream tile, in the first
            // free column going away from it (useredit3: N-face lane x=5 down to (5,14); copper x=0 down to (0,5) then east)
            int step = t.Dir == Dir.W ? 1 : -1;
            for (int k = 0; k <= 6; k++)
            {
                int x = t.PrevX + step * k;
                bool free = true;
                for (int y = t.PrevY; y <= yTop && free; y++) free = work.Grid.IsFree(x, y);
                for (int q = 1; q <= k && free; q++) free = work.Grid.IsFree(t.PrevX + step * (q - 1), t.PrevY);
                if (!free) continue;
                xd = x;
                return Path(x, yTop, x, t.PrevY, t.PrevX, t.PrevY);
            }
            why = "no free column to descend into the port " + At(t.X, t.Y) + " flowing " + t.Dir;
            return null;
        }

        GroupGeo PlanGroup(Group g, out string err)
        {
            err = null;
            Port s = g.Source;
            var gg = new GroupGeo { Sx = s.NextX, Yb = fp.BandY0 + g.Slot };
            if (s.Dir == Dir.S) { err = "source port " + At(s.X, s.Y) + " flows S (band rises north)"; return null; }
            if (s.NextY > gg.Yb - 1 && !(s.NextY == gg.Yb && s.Dir == Dir.N)) { err = "source port " + At(s.X, s.Y) + " lies above the band row y" + gg.Yb; return null; }
            int n = g.Conns.Count;
            if (n > 3) { err = n + " consumers on one source (rule 8: one 2-way splitter, third from the lane tail)"; return null; }
            // targets by column distance from the source
            foreach (FpConnection c in g.Conns.OrderBy(c => Math.Abs(c.Target.X - gg.Sx)))
            {
                string why; int xd;
                var tg = new TargetGeo { Conn = c };
                tg.Descent = DescentTiles(c.Target, gg.Yb - 1, out xd, out why);
                tg.Xd = xd;
                if (tg.Descent == null) { err = why; return null; }
                gg.Targets.Add(tg);
            }
            if (n == 3 && gg.Targets.Any(t => t.Xd < gg.Sx) && gg.Targets.Any(t => t.Xd > gg.Sx))
            {
                // useredit3 copper (28,17): 3 outputs on top of the rise; W and E go to the nearest consumer of each side, the N branch rises two
                // rows and takes the third (Bands reserves the rows yb + 1, yb + 2: Floorplan keeps them free)
                TargetGeo w = gg.Targets.Where(t => t.Xd < gg.Sx).OrderBy(t => gg.Sx - t.Xd).First(), e = gg.Targets.Where(t => t.Xd > gg.Sx).OrderBy(t => t.Xd - gg.Sx).First();
                TargetGeo far = gg.Targets.First(t => t != w && t != e);
                gg.Targets = new List<TargetGeo> { w, e, far };
                if (gg.Sx - w.Xd < 2 || e.Xd - gg.Sx < 2) { err = "3-way fork: a consumer column is less than 2 tiles from the splitter x" + gg.Sx; return null; }
                string why; int xd;
                far.Descent = DescentTiles(far.Conn.Target, gg.Yb + 1, out xd, out why);
                if (far.Descent == null) { err = "3-way fork: " + why; return null; }
                far.Xd = xd;
                gg.Fork = true; gg.ThreeWay = true; gg.SplitOnRise = true; gg.Fx = gg.Sx; gg.SplitIn = Dir.S;
            }
            else if (n >= 2)
            {
                gg.Fork = true;
                TargetGeo t1 = gg.Targets[0], t2 = gg.Targets[1];
                if (n == 3) gg.Targets[2].FromTail = true;
                bool sameSide = Math.Sign(t1.Xd - gg.Sx) == Math.Sign(t2.Xd - gg.Sx) && t1.Xd != gg.Sx;
                if (sameSide)
                {
                    // splitter on the band row at the nearer target's column: input from the source side, outputs S (descent) and onward
                    gg.Fx = t1.Xd;
                    gg.SplitIn = t1.Xd > gg.Sx ? Dir.W : Dir.E;
                    if (Math.Abs(t1.Xd - gg.Sx) < 2) { err = "fork: the nearer consumer column x" + t1.Xd + " is less than 2 tiles from the rise x" + gg.Sx + " (splitter clearance)"; return null; }
                    if (Math.Abs(t2.Xd - t1.Xd) < 2) { err = "fork: consumer columns x" + t1.Xd + " / x" + t2.Xd + " are less than 2 tiles apart (P14: corner 2 tiles after the splitter)"; return null; }
                    // t1's descent starts 2 tiles below the splitter (its S port belts fill yb-1)
                    string why; int xd;
                    t1.Descent = DescentTiles(t1.Conn.Target, gg.Yb - 2, out xd, out why);
                    if (t1.Descent == null) { err = "fork: " + why; return null; }
                    if (xd != t1.Xd) { err = "fork: descent column changed"; return null; }
                }
                else
                {
                    // targets on both sides (or one straight above): splitter on top of the rise, outputs W and E
                    gg.SplitOnRise = true; gg.Fx = gg.Sx; gg.SplitIn = Dir.S;
                    if (t1.Xd == gg.Sx || t2.Xd == gg.Sx) { err = "fork: a consumer column coincides with the rise column x" + gg.Sx + " (needs a side output)"; return null; }
                    if (Math.Abs(t1.Xd - gg.Sx) < 2 || Math.Abs(t2.Xd - gg.Sx) < 2) { err = "fork: a consumer column is less than 2 tiles from the splitter x" + gg.Sx + " (P14: corner 2 tiles after the splitter)"; return null; }
                }
            }
            int riseTop = gg.SplitOnRise ? gg.Yb - 2 : gg.Yb - 1;
            if (s.NextY <= riseTop) gg.Rise = Path(s.NextX, s.NextY, s.NextX, riseTop);
            else if (gg.SplitOnRise) { err = "fork: no room for the splitter input belts above the source port " + At(s.X, s.Y); return null; }
            else gg.Rise = new List<int[]>();
            return gg;
        }

        bool LayVerticals(Group g, GroupGeo gg, List<string> errors)
        {
            var laid = new List<KeyValuePair<List<Bld>, Bld>>();
            Action undo = () => { foreach (var kv in laid) Unlay(kv.Key, kv.Value); };
            string chain = ChainName("band", g.Item);
            foreach (FpConnection c in g.Conns) c.Chain = chain;
            // rise
            if (gg.Rise.Count > 0)
            {
                string why; Plan used;
                var belts = TryPlans(new[] { new Plan { Tiles = gg.Rise, LinkFrom = g.Source.Belt } }, chain, g.Item, "band " + g.Item + " rise", out why, out used);
                if (belts == null) { errors.Add("bands: " + Who(g.Conns[0]) + ": band rise at x" + gg.Sx + " cannot be laid: " + why); return false; }
                laid.Add(new KeyValuePair<List<Bld>, Bld>(belts, g.Source.Belt));
                gg.RiseBelts = belts; gg.RiseLast = belts[belts.Count - 1];
            }
            else { gg.RiseBelts = new List<Bld>(); gg.RiseLast = g.Source.Belt; }
            // descents (not for the tail-fed third target)
            foreach (TargetGeo tg in gg.Targets)
            {
                if (tg.FromTail) continue;
                string why; Plan used;
                var belts = TryPlans(new[] { new Plan { Tiles = tg.Descent, LinkTo = tg.Conn.Target.Belt } }, chain, g.Item, "band " + g.Item + " descent", out why, out used);
                if (belts == null) { undo(); errors.Add("bands: " + Who(tg.Conn) + ": band descent at x" + tg.Xd + " into " + At(tg.Conn.Target.X, tg.Conn.Target.Y) + " cannot be laid: " + why); return false; }
                laid.Add(new KeyValuePair<List<Bld>, Bld>(belts, null));
                tg.Belts = belts; tg.DescentFirst = belts[0];
            }
            return true;
        }

        void LayHorizontals(Group g, GroupGeo gg, List<string> errors)
        {
            string chain = g.Conns[0].Chain;
            int yb = gg.Yb;
            var all = new List<Bld>(gg.RiseBelts);
            foreach (TargetGeo tg in gg.Targets) if (tg.Belts != null) all.AddRange(tg.Belts);
            Action<string> fail = m =>
            {
                // take the whole group out: verticals and whatever horizontal part was laid
                foreach (TargetGeo tg in gg.Targets) if (tg.Belts != null) Unlay(tg.Belts, null);
                if (gg.RiseBelts.Count > 0) Unlay(gg.RiseBelts, g.Source.Belt);
                errors.Add(m);
            };
            if (!gg.Fork)
            {
                TargetGeo tg = gg.Targets[0];
                string why; Plan used;
                var belts = TryPlans(new[] { new Plan { Tiles = Path(gg.Sx, yb, tg.Xd, yb), LinkFrom = gg.RiseLast, LinkTo = tg.DescentFirst } }, chain, g.Item, "band " + g.Item, out why, out used);
                if (belts == null) { fail("bands: " + Who(tg.Conn) + ": band row y" + yb + " from x" + gg.Sx + " to x" + tg.Xd + " cannot be laid: " + why); return; }
                all.AddRange(belts);
                Record(tg.Conn, chain, Order(all), "band y" + yb);
                return;
            }
            // fork: splitter cell
            TargetGeo t1 = gg.Targets[0], t2 = gg.Targets[1];
            Dir out1 = gg.SplitOnRise ? (t1.Xd < gg.Sx ? Dir.W : Dir.E) : Dir.S;
            Dir out2 = gg.SplitOnRise ? (t2.Xd < gg.Sx ? Dir.W : Dir.E) : (t2.Xd > gg.Fx ? Dir.E : Dir.W);
            string clear = SplitterClear(gg.Fx, yb, all);
            if (clear != null) { fail("bands: " + Who(t1.Conn) + ": splitter at " + At(gg.Fx, yb) + ": " + clear); return; }
            Fragment sf;
            try { sf = Modules.Splitter(gg.Fx, yb, gg.SplitIn, gg.ThreeWay ? new[] { out1, out2, Dir.N } : new[] { out1, out2 }, beltItem, g.Item, false); }
            catch (Exception ex) { fail("bands: " + Who(t1.Conn) + ": Modules.Splitter: " + ex.Message); return; }
            int errs0 = work.Errors.Count;
            work.Add(sf);
            if (work.Errors.Count > errs0) { foreach (Bld b in sf.Blds) { work.All.Remove(b); work.Grid.Release(b); removed.Add(b); } fail("bands: " + Who(t1.Conn) + ": splitter cell at " + At(gg.Fx, yb) + " overlaps: " + work.Errors[errs0]); work.Errors.RemoveRange(errs0, work.Errors.Count - errs0); return; }
            Bld splitter = sf.Blds.First(b => b.Kind == Kind.Splitter);
            Port pin = sf.Port("in"), po1 = sf.Port("out:" + out1), po2 = sf.Port("out:" + out2);
            foreach (Bld b in sf.Blds) if (b.Kind == Kind.Belt) noJoin.Add(b);
            Action undoSplit = () => { foreach (Bld b in sf.Blds) { work.All.Remove(b); work.Grid.Release(b); removed.Add(b); } };
            // feed: rise (or the source belt) -> splitter in port
            List<Bld> h1;
            {
                string why; Plan used;
                if (gg.SplitOnRise)
                {
                    // the rise ends on (sx, yb-2) = the tile before the splitter's "in" port belt: link it directly
                    if (gg.RiseLast.Output != null || Pred(pin.Belt) != null || gg.RiseLast.TileX != pin.PrevX || gg.RiseLast.TileY != pin.PrevY)
                    { undoSplit(); fail("bands: " + Who(t1.Conn) + ": rise top " + gg.RiseLast + " does not end under the splitter input " + At(pin.PrevX, pin.PrevY)); return; }
                    Layout.Link(gg.RiseLast, pin.Belt, 1); predOf[pin.Belt] = gg.RiseLast;
                    h1 = new List<Bld>();
                }
                else
                {
                    var p = new Plan { Tiles = Path(gg.Sx, yb, pin.PrevX, pin.PrevY), LinkFrom = gg.RiseLast, LinkTo = pin.Belt };
                    h1 = TryPlans(new[] { p }, chain, g.Item, "band " + g.Item, out why, out used);
                    if (h1 == null) { undoSplit(); fail("bands: " + Who(t1.Conn) + ": band row y" + yb + " from x" + gg.Sx + " to the splitter at x" + gg.Fx + " cannot be laid: " + why); return; }
                }
            }
            // output 1 -> t1 descent (first tile two steps out from the splitter centre)
            {
                string why; Plan used;
                Plan p;
                if (gg.SplitOnRise) p = new Plan { Tiles = Path(po1.NextX, po1.NextY, t1.Xd, yb), LinkFrom = po1.Belt, LinkTo = t1.DescentFirst };
                else { p = null; }
                if (p != null)
                {
                    var hb = TryPlans(new[] { p }, chain, g.Item, "band " + g.Item, out why, out used);
                    if (hb == null) { undoSplit(); Unlay(h1, gg.RiseLast); fail("bands: " + Who(t1.Conn) + ": band row y" + yb + " from the splitter to x" + t1.Xd + " cannot be laid: " + why); return; }
                    h1.AddRange(hb);
                }
                else
                {
                    // S output: the descent begins right under the port belt
                    if (t1.DescentFirst.TileX != po1.NextX || t1.DescentFirst.TileY != po1.NextY || po1.Belt.Output != null || Pred(t1.DescentFirst) != null)
                    { undoSplit(); Unlay(h1, gg.RiseLast); fail("bands: " + Who(t1.Conn) + ": descent at x" + t1.Xd + " does not start under the splitter S port " + At(po1.NextX, po1.NextY)); return; }
                    Layout.Link(po1.Belt, t1.DescentFirst, 1); predOf[t1.DescentFirst] = po1.Belt;
                }
            }
            // output 2 -> t2 descent
            List<Bld> h2;
            {
                string why; Plan used;
                var p = new Plan { Tiles = Path(po2.NextX, po2.NextY, t2.Xd, yb), LinkFrom = po2.Belt, LinkTo = t2.DescentFirst };
                h2 = TryPlans(new[] { p }, chain, g.Item, "band " + g.Item, out why, out used);
                if (h2 == null) { undoSplit(); Unlay(h1, gg.RiseLast); fail("bands: " + Who(t2.Conn) + ": band row y" + yb + " from the splitter to x" + t2.Xd + " cannot be laid: " + why); return; }
            }
            var c1 = new List<Bld>(gg.RiseBelts); c1.AddRange(h1); c1.AddRange(t1.Belts);
            var c2 = new List<Bld>(h2); c2.AddRange(t2.Belts);
            t1.Conn.Splitter = splitter; t2.Conn.Splitter = splitter;
            Record(t1.Conn, chain, Order(c1), "band y" + yb + " via splitter " + At(gg.Fx, yb) + " out " + out1);
            Record(t2.Conn, chain, Order(c2), "band y" + yb + " via splitter " + At(gg.Fx, yb) + " out " + out2);
            fp.Notes.Add("bands: splitter " + At(gg.Fx, yb) + " in " + gg.SplitIn + " outs " + out1 + "/" + out2 + (gg.ThreeWay ? "/N" : "") + " for item " + g.Item);
            if (gg.ThreeWay)
            {
                // N output -> row yb + 2 -> the third consumer's descent
                TargetGeo t3 = gg.Targets[2];
                Port po3 = sf.Port("out:" + Dir.N);
                string why3; Plan used3;
                var p3 = new Plan { Tiles = Path(po3.NextX, po3.NextY, t3.Xd, yb + 2), LinkFrom = po3.Belt, LinkTo = t3.DescentFirst };
                var h3 = TryPlans(new[] { p3 }, chain, g.Item, "band " + g.Item, out why3, out used3);
                if (h3 == null) { errors.Add("bands: " + Who(t3.Conn) + ": 3-way branch row y" + (yb + 2) + " cannot be laid: " + why3); return; }
                var c3 = new List<Bld>(h3); c3.AddRange(t3.Belts);
                t3.Conn.Splitter = splitter;
                Record(t3.Conn, chain, Order(c3), "band y" + (yb + 2) + " via splitter " + At(gg.Fx, yb) + " out N");
                return;
            }
            // third target from the second target's lane end
            if (gg.Targets.Count == 3) LayFromTail(gg.Targets[2].Conn, t2.Conn, chain, errors);
        }

        /// Third consumer (rule 8 / 12): the second consumer's input lane continues from its last tile to the third
        /// consumer's in port (L-shaped, bridging what it crosses).
        void LayFromTail(FpConnection c3, FpConnection c2, string chain, List<string> errors)
        {
            Bld last = c2.Target.Belt;
            int guard = 0;
            while (last.Output != null && last.Output.Kind == Kind.Belt && guard++ < 10000) last = last.Output;
            Bld pr = Pred(last);
            if (pr == null) { errors.Add("bands: " + Who(c3) + ": lane end of " + c2.To.Name + " has no predecessor to define its flow"); return; }
            Dir d = Delta(pr.TileX, pr.TileY, last.TileX, last.TileY);
            int ax = last.TileX + Dirs.Dx(d), ay = last.TileY + Dirs.Dy(d);
            Port t = c3.Target;
            if (ax == t.X && ay == t.Y)
            {
                // the port is the tile straight ahead of the lane end (useredit3 gear under the motor row): link
                if (t.Dir != d || Pred(t.Belt) != null || last.Output != null) { errors.Add("bands: " + Who(c3) + ": lane end " + last + " does not continue into the port " + At(t.X, t.Y) + " flowing " + t.Dir); return; }
                Layout.Link(last, t.Belt, 1); predOf[t.Belt] = last;
                c3.Chain = chain; c3.Laid = true;
                fp.Notes.Add("bands: " + c3 + ": lane tail of " + c2.To.Name + " linked directly at " + At(t.X, t.Y));
                return;
            }
            int bx = t.PrevX, by = t.PrevY;
            var plans = new List<Plan>();
            plans.Add(new Plan { Tiles = Path(ax, ay, ax, by, bx, by), LinkFrom = last, LinkTo = t.Belt });
            plans.Add(new Plan { Tiles = Path(ax, ay, bx, ay, bx, by), LinkFrom = last, LinkTo = t.Belt });
            string why; Plan used;
            var belts = TryPlans(plans, chain, c3.Item, "band " + c3.Item + " tail feed", out why, out used);
            if (belts == null) { errors.Add("bands: " + Who(c3) + ": lane-tail feed from " + At(ax, ay) + " to " + At(bx, by) + " cannot be laid: " + why); return; }
            Record(c3, chain, belts, "lane tail of " + c2.To.Name);
        }

        void LayMerge(FpConnection m, Vert rise, FpConnection prim, Group pg, GroupGeo gg, List<string> errors)
        {
            int yb = gg.Yb, sx2 = m.Source.NextX;
            Bld riseLast = rise.Belts[rise.Belts.Count - 1];
            string chain = m.Chain;
            // 1) directly under the primary's horizontal: side-join north into it
            Bld over = GroundAt(sx2, yb);
            if (over != null && over.Chain == prim.Chain && MergeOk(over, riseLast.TileX, riseLast.TileY) == null)
            {
                try { Modules.SideMerge(riseLast, over); }
                catch (Exception ex) { Unlay(rise.Belts, rise.LinkFrom); errors.Add("bands: " + Who(m) + ": side-join into " + over + " failed: " + ex.Message); return; }
                Record(m, chain, rise.Belts, "merge into the band row y" + yb + " at " + At(sx2, yb));
                return;
            }
            // 2) along y = yb-1 toward the nearest vertical of the primary run (its rise or the descent), side-join into it
            var cands = new List<int>();
            cands.Add(gg.Sx);
            foreach (TargetGeo tg in gg.Targets) if (!tg.FromTail) cands.Add(tg.Xd);
            var plans = new List<Plan>();
            foreach (int vx in cands.Where(v => v != sx2).OrderBy(v => Math.Abs(v - sx2)))
            {
                Bld vb = GroundAt(vx, yb - 1);
                if (vb == null || vb.Chain != prim.Chain) continue;
                int step = Math.Sign(vx - sx2);
                if (vx - step == sx2) continue;   // adjacent: the rise top itself would be the joining tile (handled by case 1 geometry only)
                plans.Add(new Plan { Tiles = Path(sx2 + step, yb - 1, vx - step, yb - 1), LinkFrom = riseLast, MergeInto = vb });
            }
            string why; Plan used;
            var belts = TryPlans(plans, chain, m.Item, "merge " + m.Item, out why, out used);
            if (belts == null) { Unlay(rise.Belts, rise.LinkFrom); errors.Add("bands: " + Who(m) + ": merge into the run of " + prim + " cannot be laid: " + why); return; }
            var all = new List<Bld>(rise.Belts); all.AddRange(belts);
            Record(m, chain, all, "merge along y" + (yb - 1) + " into " + used.MergeInto);
        }

        /// Splitter cell at (x,y): its tile and the two "next" belt tiles free; no foreign belt within
        /// GameData.SplitterForeignBeltMin of the centre (own = belts of this group, they become the splitter's chains).
        string SplitterClear(int x, int y, List<Bld> own)
        {
            if (!work.Grid.IsFree(x, y) || raisedTiles.Contains(K(x, y))) return "tile " + At(x, y) + " is not free";
            foreach (Bld b in work.All)
            {
                if (b.Kind != Kind.Belt || removed.Contains(b) || own.Contains(b)) continue;
                double d = Math.Sqrt((b.X - x) * (b.X - x) + (b.Y - y) * (b.Y - y));
                if (d < GameData.SplitterForeignBeltMin - 0.01) return "foreign belt " + b + " " + d.ToString("0.##", CultureInfo.InvariantCulture) + " tiles from the splitter centre (minimum " + GameData.SplitterForeignBeltMin + ")";
            }
            foreach (Bld b in work.All)
            {
                if (b.Kind == Kind.Belt || b.Kind == Kind.Sorter || removed.Contains(b)) continue;
                if (Math.Abs(b.X - x) < 2.5 && Math.Abs(b.Y - y) < 2.5)
                {
                    Rect r = work.Footprints.TileRect(b);
                    if (!r.IsEmpty && r.X0 - 1 <= x && x <= r.X1 + 1 && r.Y0 - 1 <= y && y <= r.Y1 + 1) return "building " + b + " touches the splitter cell";
                }
            }
            return null;
        }

        /// Belts in flow order (follow Output from the one without a predecessor inside the set).
        static List<Bld> Order(List<Bld> belts)
        {
            var set = new HashSet<Bld>(belts);
            var preds = new HashSet<Bld>(belts.Where(b => b.Output != null && set.Contains(b.Output)).Select(b => b.Output));
            var res = new List<Bld>();
            foreach (Bld start in belts.Where(b => !preds.Contains(b)))
                for (Bld n = start; n != null && set.Contains(n) && !res.Contains(n); n = n.Output != null && n.Output.Kind == Kind.Belt ? n.Output : null) res.Add(n);
            foreach (Bld b in belts) if (!res.Contains(b)) res.Add(b);
            return res;
        }
    }
}

}
