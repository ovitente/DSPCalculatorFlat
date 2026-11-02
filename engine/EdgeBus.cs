// EdgeBus.cs - the user's layout rules 2 and 3 (approved 2026-11-02, blueprint-tools\analysis\user-edit-turret75.md):
//  R2: every long lane to the final machine runs on ONE shared edge bus, stacked z 0 / 0.5 / 1 over the same tiles,
//      instead of a ring per lane;
//  R3: tier order is nested: the lane that joins the bus further upstream rides one level higher; its up-ramp node sits
//      on the tile where the lane below turns into the bus, its down-ramp right after the tile where the lane below
//      leaves (last in, first out).
// Game-proven geometry: refs\engine-missile-turret-7.5-v1-useredit3.txt (top bus, row y=25, flow W):
//   motors h z 0:   corner #404 (21,25,0) yaw 315 ... #520 (5,25,0) corner yaw 225 (leaves S)
//   copper p z 0.5: ground #479 (22,25,0), up-ramp #478 (21,25,0.5) ON h's corner, run to #67 (5,25,0.5) over h's
//                   exit corner, down-ramp #37 (4,25,0)
//   engine s z 1:   ground #472 (29,25,0), #488 (28,25,0.5) ON p's corner #114 (28,25,0) yaw 315, #487 (27,25,1)
//                   ... #557 (5,25,1), #558 (4,25,0.5) over p's landing #37, z 0.5 on
//   one tile per half level (0 -> 0.5 -> 1), three records on one tile (e.g. x 5..20: z 0, 0.5, 1).
// Two parts:
//  - Composer side (partial Composer.Attempt, hook "EDGEBUS HOOK" in Composer.Route): builds the bus for the long
//    lanes into the final row, routes source -> bus entry and bus exit -> target with the normal grid router; falls
//    back to the normal per-lane routes when no bus fits;
//  - Validator side (static EdgeBus, hook in Validator.Bridges): recognises stacked bus runs and checks them against
//    the proven geometry (replaces the single-level RAISED_RUN checks for those runs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{
    public static class EdgeBus
    {
        /// Switch for the generative bus (rules 2/3). Off = the per-lane routes of the pre-bus engine.
        /// OFF by default (2026-11-02): on the turret 7.5 every bus attempt broke the steel / circuit-copper routes and the
        /// slower attempts used up the time budget before the valid split-rows attempt (52x27) -> no valid layout. The
        /// Validator check of stacked bus runs is always active.
        public static bool Enabled = false;
        /// Minimum Manhattan length (tiles) of a lane into the final row to be taken onto the bus.
        public static int MinLength = 12;
        /// Test knob: build the bus even where the separate routes are shorter / stay below the bus row.
        public static bool Force;
        /// Belt role prefix of bus belts (Composer output; decoded blueprints have no roles).
        public const string Role = "edge bus";

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        static string At(Bld b) { return "(" + F(b.X) + "," + F(b.Y) + "," + F(b.Z) + ")"; }
        static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }
        static bool OnGrid(Bld b) { return Math.Abs(b.X - Math.Round(b.X)) < 0.01 && Math.Abs(b.Y - Math.Round(b.Y)) < 0.01; }
        static bool IsCorner(Bld b) { double y = Dirs.Norm(b.Yaw) % 90.0; return y > 1 && y < 89; }

        static Dir? Step(Bld a, Bld b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (Math.Abs(Math.Abs(dx) + Math.Abs(dy) - 1) > 0.05) return null;
            return Dirs.FromDelta((int)Math.Round(dx), (int)Math.Round(dy));
        }

        /// Main (in-line) predecessor of every belt: the belt whose Output it is, one tile behind it.
        static Dictionary<Bld, Bld> Preds(IList<Bld> belts)
        {
            var p = new Dictionary<Bld, Bld>();
            foreach (Bld b in belts)
            {
                Bld o = b.Output;
                if (o == null || o.Kind != Kind.Belt || Step(b, o) == null) continue;
                Bld old;
                // a side-join is not the main predecessor: prefer the one flowing the same way as o's own successor
                if (p.TryGetValue(o, out old) && old != null)
                {
                    Dir? on = o.Output != null ? Step(o, o.Output) : null;
                    if (on != null && Step(old, o) == on) continue;
                }
                p[o] = b;
            }
            return p;
        }

        /// Raised runs (maximal Output-chains of raised integer-tile nodes) that are stacked-bus runs: one of their nodes
        /// is at z 1 above a ground foot, shares its tile with another raised node, or sits over a ground corner.
        /// Splitter high-port runs (fractional foot) are not bus runs. Returns every node of every bus run.
        public static HashSet<Bld> BusNodes(IList<Bld> belts)
        {
            var res = new HashSet<Bld>();
            var preds = Preds(belts);
            var raisedOn = new Dictionary<long, int>();
            var ground = new Dictionary<long, Bld>();
            foreach (Bld b in belts)
            {
                if (!OnGrid(b)) continue;
                long k = K(b.TileX, b.TileY);
                if (b.IsRaised) { int n; raisedOn.TryGetValue(k, out n); raisedOn[k] = n + 1; }
                else ground[k] = b;
            }
            foreach (Bld b in belts)
            {
                if (!b.IsRaised || !OnGrid(b)) continue;
                Bld foot; preds.TryGetValue(b, out foot);
                if (foot == null || foot.IsRaised) continue;   // not a run start
                if (!OnGrid(foot)) continue;                    // splitter high port / cell belt
                var run = new List<Bld>();
                for (Bld n = b; n != null && n.Kind == Kind.Belt && n.IsRaised && OnGrid(n) && !run.Contains(n); n = n.Output) run.Add(n);
                if (run.Any(Coaters.IsFeed)) continue;   // COATERS feed cell (z 1 node, checked in Coaters.Validate)
                bool bus = false;
                foreach (Bld n in run)
                {
                    long k = K(n.TileX, n.TileY);
                    Bld g;
                    if (n.Z > 0.75 || raisedOn[k] > 1 || (ground.TryGetValue(k, out g) && IsCorner(g)) || (n.Role != null && n.Role.StartsWith(Role))) { bus = true; break; }
                }
                if (bus) foreach (Bld n in run) res.Add(n);
            }
            return res;
        }

        /// Checks the stacked bus runs (nodes from BusNodes) against the proven geometry (useredit3 top bus):
        /// integer tiles, z 0.5 or 1, at most one belt per tile and level, z changes by at most 0.5 per tile (ramp
        /// 0 -> 0.5 -> 1, 1 -> 0.5 -> 0), a node where z changes is straight (no turn on a ramp), the run starts after a
        /// straight ground approach + foot and ends on a ground foot with a straight approach after it, never over a
        /// building, at least 0.5 tile from a sorter. Returns failures; warn receives the soft findings.
        public static List<string> Check(IList<Bld> belts, HashSet<Bld> bus, Func<int, int, Bld> buildingAt, IList<Bld> sorters, Action<string> warn)
        {
            var fails = new List<string>();
            if (bus == null || bus.Count == 0) return fails;
            var preds = Preds(belts);
            var level = new Dictionary<string, Bld>();
            int near = 0;
            foreach (Bld b in belts)
            {
                if (!OnGrid(b)) continue;
                string key = b.TileX + "," + b.TileY + "," + F(Math.Round(b.Z * 2) / 2);
                Bld o;
                if (level.TryGetValue(key, out o) && (bus.Contains(b) || bus.Contains(o))) fails.Add("edge bus: two belts on one tile and level " + At(b));
                level[key] = b;
            }
            foreach (Bld n in bus)
            {
                string at = " at " + At(n);
                if (Math.Abs(n.Z - 0.5) > 0.01 && Math.Abs(n.Z - 1.0) > 0.01) fails.Add("edge bus: node z " + F(n.Z) + " (proven levels 0.5 and 1)" + at);
                Bld bd = buildingAt != null ? buildingAt(n.TileX, n.TileY) : null;
                if (bd != null && bd.Kind != Kind.Belt) fails.Add("edge bus: node over a building " + bd + at);
                Bld p; preds.TryGetValue(n, out p);
                Bld s = n.Output != null && n.Output.Kind == Kind.Belt ? n.Output : null;
                if (p == null) { fails.Add("edge bus: node without a predecessor" + at); continue; }
                if (s == null) { fails.Add("edge bus: run ends in the air" + at); continue; }
                if (Math.Abs(p.Z - n.Z) > 0.51 || Math.Abs(s.Z - n.Z) > 0.51) fails.Add("edge bus: z step above 0.5 per tile" + at);
                Dir? din = Step(p, n), dout = Step(n, s);
                if (din == null || dout == null) { fails.Add("edge bus: link longer than one tile" + at); continue; }
                bool ramp = Math.Abs(p.Z - n.Z) > 0.01 || Math.Abs(s.Z - n.Z) > 0.01;
                if (ramp && din != dout) fails.Add("edge bus: turn on a ramp node" + at);
                if (!p.IsRaised)
                {
                    // up-ramp: foot straight from its approach node
                    Bld a; preds.TryGetValue(p, out a);
                    if (a == null || Step(a, p) != din) fails.Add("edge bus: up-ramp foot without a straight approach" + at);
                }
                if (!s.IsRaised)
                {
                    Bld a = s.Output != null && s.Output.Kind == Kind.Belt ? s.Output : null;
                    if (a == null || Step(s, a) != dout) fails.Add("edge bus: down-ramp foot without a straight approach after it" + at);
                }
                if (sorters != null && sorters.Count > 0)
                {
                    double ds = sorters.Min(o => GameData.SegmentDist(n.X, n.Y, o.X, o.Y, o.X2, o.Y2));
                    if (ds < 0.5) fails.Add("edge bus: node " + F(ds) + " tile from a sorter" + at);
                    else if (ds < GameData.RaisedSorterMin - 1e-6) near++;
                }
            }
            if (near > 0 && warn != null) warn("UNPROVEN: " + near + " edge bus node(s) 0.5..1.0 tile from a sorter");
            return fails;
        }

        /// Rule 3 measurement (metric): stacked bus runs, nodes per level, and whether every z 0.5 bus node over a
        /// ground corner / z 1 node is nested (the lower lane's belt lies below it on the same tile).
        public static RuleResult Measure(Layout l)
        {
            var belts = l.All.Where(b => b.Kind == Kind.Belt).ToList();
            var bus = BusNodes(belts);
            int z05 = bus.Count(b => b.Z < 0.75), z1 = bus.Count - z05, nested = 0;
            var lower = new HashSet<string>();
            foreach (Bld b in belts) if (OnGrid(b)) lower.Add(b.TileX + "," + b.TileY + "," + F(Math.Round(b.Z * 2) / 2));
            foreach (Bld b in bus) if (lower.Contains(b.TileX + "," + b.TileY + "," + F(Math.Round(b.Z * 2) / 2 - 0.5))) nested++;
            return new RuleResult { Rule = 3, Detail = "edge bus: " + bus.Count + " stacked node(s) (" + z05 + " at z 0.5, " + z1 + " at z 1), " + nested + " stacked over the lane below" };
        }
    }

    public partial class Composer
    {
        partial class Attempt
        {
            /// Rules 2/3 (EDGEBUS HOOK in Route, before the primary routes): the long lanes into the final row (2 or 3,
            /// Manhattan length >= EdgeBus.MinLength) go onto one stacked bus row just beyond the top of the layout.
            /// Tier by entry position: the lane entering furthest downstream is z 0, the next z 0.5, the next z 1;
            /// exits nested (z 0 leaves first). Edges routed here are marked Done. Nothing is changed when no geometry fits.
            void EdgeBusRoute(List<Edge> primaries, Dictionary<Port, int> tgtConn)
            {
                if (!EdgeBus.Enabled || NoBus || plan == null) return;
                var lanes = primaries.Where(e => !e.Done && e.Source != null && e.Target != null && e.To != null && e.To.Step != null
                    && e.To.Step.Item == plan.TargetItem && Dist(e.Source, e.Target) >= EdgeBus.MinLength
                    && PortBelt(e.Source) != null && PortBelt(e.Target) != null).OrderByDescending(e => Dist(e.Source, e.Target)).Take(3).ToList();
                if (lanes.Count < 2) return;
                double ms = lanes.Average(e => (double)e.Source.NextX), mt = lanes.Average(e => (double)e.Target.PrevX);
                int sg = mt < ms ? -1 : 1;            // flow along the bus: -1 = W, +1 = E
                Dir F = sg < 0 ? Dir.W : Dir.E;
                // tiers: most downstream source -> z 0 (rule 3: who joins further upstream rides higher)
                var byTier = lanes.OrderByDescending(e => sg * e.Source.NextX).ToList();
                int n = byTier.Count;
                Rect bx = work.Bounds();
                // worth it only where the separate routes ring around the layout anyway: dry-run each lane alone; the bus
                // row may not lie above the highest separate route, and the bus may not use more tiles than they do
                int sepTop = int.MinValue, sepLen = 0; bool sepFail = false;
                foreach (Edge e in byTier)
                {
                    var p = DryRoute(e, tgtConn[e.Target]);
                    if (p == null) { sepFail = true; continue; }
                    sepLen += p.Count;
                    foreach (int[] q in p) sepTop = Math.Max(sepTop, q[1]);
                }
                if (EdgeBus.Force) sepFail = true;
                busTileLimit = sepFail ? int.MaxValue : sepLen;
                foreach (int up in new[] { 1, 2, 3 })
                foreach (int din in new[] { 0, 1, -1, 2, -2 })
                foreach (int dout in new[] { 0, 2, 4 })
                {
                    int Y = bx.Y1 + up;
                    if (!sepFail && Y > sepTop) continue;
                    var ain = new int[n]; var aout = new int[n];
                    for (int t = 0; t < n; t++)
                    {
                        int a = sg * byTier[t].Source.NextX + sg * din;
                        ain[t] = t == 0 ? a : Math.Min(a, ain[t - 1] - 2);
                    }
                    for (int t = 0; t < n; t++)
                    {
                        int a = sg * byTier[t].Target.PrevX + dout;
                        aout[t] = t == 0 ? Math.Max(a, ain[0] + 2) : Math.Max(a, aout[t - 1] + 2);
                    }
                    if (TryBus(byTier, tgtConn, Y, sg, F, ain, aout)) return;
                }
                RowNotes.Add("edge bus: no stacked bus fits for " + n + " long lanes into the final row, separate routes");
            }

            int busTileLimit = int.MaxValue;
            /// Set by Composer.Try: build no bus in this attempt (an earlier run of the same try failed with it).
            public bool NoBus;
            /// The attempt laid a stacked bus.
            public bool BusUsed;

            /// The lane's separate route (as the primary routes would lay it now), not laid; null when none exists.
            List<int[]> DryRoute(Edge e, int id)
            {
                Port s = e.Source, t = e.Target;
                Bld sb = PortBelt(s), tb = PortBelt(t);
                long sk = K(s.NextX, s.NextY);
                int old; bool had = reserved.TryGetValue(sk, out old);
                reserved[sk] = id;
                try
                {
                    var goals = new Dictionary<long, Goal>();
                    goals[K(t.PrevX, t.PrevY)] = new Goal { NextDir = t.Dir, Target = tb, NoBridgeLanding = IsFractional(tb) };
                    return WithZones(() => { Goal hh; return Route2(new[] { new StartState(s.NextX, s.NextY, s.Dir, null) { HasPred = !IsFractional(sb) } }, goals, id, Area(0, 2), out hh); }, s, t);
                }
                finally { if (had) reserved[sk] = old; else reserved.Remove(sk); }
            }

            /// z of tier t at along-position a (sg * x) on the bus row (rule 3 nesting, useredit3 top bus).
            static double BusZ(int t, int a, int[] ain, int[] aout)
            {
                if (t == 0) return 0;
                if (t == 1) return a >= ain[0] && a <= aout[0] ? 0.5 : 0;
                if (a == ain[1]) return 0.5;
                if (a > ain[1] && a <= aout[0]) return 1;
                if (a > aout[0] && a <= aout[1]) return 0.5;
                return 0;
            }

            bool TryBus(List<Edge> lanes, Dictionary<Port, int> tgtConn, int Y, int sg, Dir F, int[] ain, int[] aout)
            {
                int n = lanes.Count;
                var ids = lanes.Select(e => tgtConn[e.Target]).ToArray();
                Rect area = Area(0, 2);
                var xs = ain.Concat(aout).Select(a => sg * a).ToList();
                area = new Rect(Math.Min(area.X0, xs.Min() - 3), area.Y0, Math.Max(area.X1, xs.Max() + 3), Math.Max(area.Y1, Y + 1));
                // bus tiles: one ground belt per tile; every tile and its feeder / exit tile below must be free
                var tiles = new List<long>();
                for (int t = 0; t < n; t++)
                {
                    for (int a = ain[t]; a <= aout[t]; a++)
                    {
                        if (BusZ(t, a, ain, aout) > 0.25) continue;
                        int x = sg * a;
                        long k = K(x, Y);
                        if (tiles.Contains(k) || !Passable(x, Y, ids[t], area) || raisedAt.ContainsKey(k) || onSorter.Contains(k)) return false;
                        tiles.Add(k);
                    }
                    if (!Passable(sg * ain[t], Y - 1, ids[t], area) || !Passable(sg * aout[t], Y - 1, ids[t], area)) return false;
                    if (raisedAt.ContainsKey(K(sg * ain[t], Y - 1)) || raisedAt.ContainsKey(K(sg * aout[t], Y - 1))) return false;
                }
                // plan the feeder and exit routes with the bus tiles taken (nothing is laid until all routes exist)
                var tempBlocked = new List<long>();
                var tempRaised = new List<long>();
                Action<long> block = k => { if (blocked.Add(k)) tempBlocked.Add(k); };
                foreach (long k in tiles) block(k);
                for (int t = 0; t < n; t++) { block(K(sg * ain[t], Y - 1)); block(K(sg * aout[t], Y - 1)); }
                var feeds = new List<int[]>[n]; var exits = new List<int[]>[n];
                var feedHit = new Goal[n]; var exitHit = new Goal[n];
                var savedRes = new Dictionary<long, int>(reserved);
                bool ok = true;
                try
                {
                    for (int t = 0; t < n && ok; t++)
                    {
                        Edge e = lanes[t];
                        Port s = e.Source, tg = e.Target;
                        Bld sb = PortBelt(s);
                        reserved[K(s.NextX, s.NextY)] = ids[t];
                        long fk = K(sg * ain[t], Y - 1), xk = K(sg * aout[t], Y - 1);
                        blocked.Remove(fk);
                        var goals = new Dictionary<long, Goal>();
                        goals[fk] = new Goal { NextDir = Dir.N };
                        List<int[]> fp;
                        if (s.NextX == sg * ain[t] && s.NextY == Y - 1 && s.Dir == Dir.N) fp = new List<int[]> { new[] { s.NextX, s.NextY, 0 } };
                        else fp = WithZones(() => { Goal hh; var r = Route2(new[] { new StartState(s.NextX, s.NextY, s.Dir, null) { HasPred = !IsFractional(sb) } }, goals, ids[t], area, out hh); return r; }, s);
                        if (fp == null) { ok = false; break; }
                        Take(fp, block, tempRaised);
                        feeds[t] = fp;
                        blocked.Remove(xk);
                        var g2 = new Dictionary<long, Goal>();
                        g2[K(tg.PrevX, tg.PrevY)] = new Goal { NextDir = tg.Dir, Target = PortBelt(tg), NoBridgeLanding = IsFractional(PortBelt(tg)) };
                        var xp = WithZones(() => { Goal hh; var r = Route2(new[] { new StartState(sg * aout[t], Y - 1, Dir.S, null) { HasPred = true } }, g2, ids[t], area, out hh); return r; }, tg);
                        if (xp == null) { ok = false; break; }
                        Take(xp, block, tempRaised);
                        exits[t] = xp;
                    }
                }
                finally
                {
                    foreach (long k in tempBlocked) blocked.Remove(k);
                    foreach (long k in tempRaised) raisedAt.Remove(k);
                    if (ok && tiles.Count + feeds.Sum(p => p.Count) + exits.Sum(p => p.Count) > busTileLimit) ok = false;   // longer than the separate routes
                    if (!ok) { reserved.Clear(); foreach (var kv in savedRes) reserved[kv.Key] = kv.Value; }
                }
                if (!ok) return false;
                // lay: bus belts first (so the router's raised nodes of the feeds see them), then feeds and exits
                for (int t = 0; t < n; t++)
                {
                    Edge e = lanes[t];
                    string chain = "bus:" + e.Item + "#" + (chainNo++);
                    var bus = new List<Bld>();
                    for (int a = ain[t]; a <= aout[t]; a++)
                    {
                        Dir di = a == ain[t] ? Dir.N : F, dd = a == aout[t] ? Dir.S : F;
                        var b = Bld.Belt(spec.Tiers.Belt, sg * a, Y, Yaw(di, dd), e.Item, chain);
                        double z = BusZ(t, a, ain, aout);
                        b.Z = b.Z2 = z;
                        b.Role = EdgeBus.Role + " z " + F2(z) + " " + chain;
                        long k = K(sg * a, Y);
                        bridged.Add(k);
                        if (z > 0.25) raisedAt[k] = new[] { di, dd };
                        work.Add(b);
                        bus.Add(b);
                    }
                    for (int i = 0; i < bus.Count - 1; i++) Layout.Link(bus[i], bus[i + 1], 1);
                    var fr = Lay(feeds[t], e.Source.Dir, Dir.N, e.Item, chain);
                    Layout.Link(PortBelt(e.Source), fr[0], 1);
                    Layout.Link(fr[fr.Count - 1], bus[0], 1);
                    var xr = Lay(exits[t], Dir.S, e.Target.Dir, e.Item, chain);
                    Layout.Link(bus[bus.Count - 1], xr[0], 1);
                    Layout.Link(xr[xr.Count - 1], PortBelt(e.Target), 1);
                    e.Run = fr.Concat(bus).Concat(xr).ToList();
                    e.Done = true;
                }
                BusUsed = true;
                RowNotes.Add("edge bus (rules 2/3): " + n + " long lanes to the final machine stacked z 0" + (n > 1 ? " / 0.5" : "") + (n > 2 ? " / 1" : "")
                    + " on one row (useredit3 top bus, records #404 #478 #488 #487)");
                return true;
            }

            static string F2(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

            /// Marks a planned path as taken (ground tiles blocked, raised tiles as raised nodes) until the real Lay.
            void Take(List<int[]> path, Action<long> block, List<long> tempRaised)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    long k = K(path[i][0], path[i][1]);
                    if (Modules.IsRaisedTile(path[i])) { if (!raisedAt.ContainsKey(k)) { raisedAt[k] = new[] { Dir.N, Dir.N }; tempRaised.Add(k); } }
                    else block(k);
                }
            }
        }
    }
}
