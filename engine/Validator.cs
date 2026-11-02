// Validator.cs - layout-agnostic checks of an abstract Layout against game data, the production plan and the
// user's layout rules (lib\CONTRACT.md, analysis\generator-design.md section d). Generalized from the
// layout-specific validators of MakeCoilFactoryV3.cs and MakeAssemblers8x4.cs: no coordinates, chain names or
// building counts are hard-coded. Pure model code (no game DLL); the export check (check 8) lives with the host's
// serializer (CLI: DspFlat.Host.GameEmitter.CheckExport).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

public class Validator
{
    readonly GameData gd;
    readonly BlockSpec spec;

    public Validator(GameData gd, BlockSpec spec) { this.gd = gd; this.spec = spec; }

    /// Layout-agnostic checks 1-7 of generator-design.md plus the user's layout rules (CONTRACT.md). Pure model, no DLL.
    public ValidationReport Validate(Layout layout, ProductionPlan plan)
    {
        var report = new ValidationReport();
        if (layout == null) { report.Fail("no layout"); return report; }
        var run = new Run(gd, spec, layout, plan, report);
        run.All();
        BeltLoads = run.Loads; SorterNeeds = run.Needs;   // lanes hook (Lanes.AssignTiers)
        return report;
    }

    // ---- lanes hook (engine\Lanes.cs): flow numbers of the last Validate
    /// Total items/min on every belt reached by the flow walk (check 6).
    public Dictionary<Bld, double> BeltLoads;
    /// Items/min every machine sorter must move: max(planned rate, full-speed machine rate) per sorter.
    public Dictionary<Bld, double> SorterNeeds;

    /// Check 7 only (power coverage, tower links, power-node distance) - for blueprints read back from the game,
    /// e.g. the user's game-proven refs\engine-electric-motor-120-v3-user.txt (selftest).
    public ValidationReport ValidatePower(Layout layout)
    {
        var report = new ValidationReport();
        if (layout == null) { report.Fail("no layout"); return report; }
        new Run(gd, spec, layout, null, report).PowerOnly();
        return report;
    }

    /// Conveyor checks only (overlap, belt links incl. game distance/slope limits, yaw, cycles, splitters, bridges) -
    /// for belt-only blueprints such as the game-proven refs\bridges-splitters-user.txt (selftest).
    public ValidationReport ValidateBelts(Layout layout)
    {
        var report = new ValidationReport();
        if (layout == null) { report.Fail("no layout"); return report; }
        new Run(gd, spec, layout, null, report).BeltsOnly();
        return report;
    }

    static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    // ======================================================================== Validate implementation
    sealed class Run
    {
        const double Eps = 1e-6;
        readonly GameData gd;
        readonly BlockSpec spec;
        readonly Layout L;
        readonly ProductionPlan plan;
        readonly ValidationReport R;
        readonly Footprints fp;

        List<Bld> belts, sorters, machines, towers, splitters;
        readonly HashSet<Bld> inLayout = new HashSet<Bld>();
        readonly HashSet<Bld> splitterTouch = new HashSet<Bld>();     // belts linked straight to a splitter
        readonly Dictionary<long, Bld> beltTile = new Dictionary<long, Bld>();
        readonly Dictionary<Bld, List<Bld>> preds = new Dictionary<Bld, List<Bld>>();      // belt/splitter -> belts linking into it
        readonly Dictionary<Bld, List<Bld>> splitOuts = new Dictionary<Bld, List<Bld>>();  // splitter -> outgoing belts
        readonly Dictionary<Bld, List<Bld>> picksAt = new Dictionary<Bld, List<Bld>>();    // belt -> input sorters taking from it
        readonly Dictionary<Bld, List<Bld>> dropsAt = new Dictionary<Bld, List<Bld>>();    // belt -> output sorters dropping on it
        readonly Dictionary<Bld, List<Bld>> inSorters = new Dictionary<Bld, List<Bld>>();  // machine -> input sorters
        readonly Dictionary<Bld, List<Bld>> outSorters = new Dictionary<Bld, List<Bld>>(); // machine -> output sorters
        readonly Dictionary<Bld, Step> stepOf = new Dictionary<Bld, Step>();
        readonly Dictionary<Step, int> placed = new Dictionary<Step, int>();
        readonly Dictionary<Bld, HashSet<int>> items = new Dictionary<Bld, HashSet<int>>(); // belt -> items on it (after drops)
        readonly Dictionary<Bld, int> pickItem = new Dictionary<Bld, int>();               // input sorter -> item it takes
        readonly Dictionary<Bld, string> laneOf = new Dictionary<Bld, string>();
        List<Bld> topo;
        bool beltGraphOk = true;
        public readonly Dictionary<Bld, double> Loads = new Dictionary<Bld, double>();   // lanes hook
        public readonly Dictionary<Bld, double> Needs = new Dictionary<Bld, double>();   // lanes hook
        // feat-labs: Matrix Lab towers (LabTowers.cs) - upper levels take no sorters and no tile; a base lab's sorters
        // carry the whole tower
        HashSet<Bld> upperLabs = new HashSet<Bld>();
        Dictionary<Bld, int> towerSize = new Dictionary<Bld, int>();
        int TowerSize(Bld m) { int n; return towerSize.TryGetValue(m, out n) ? n : 1; }

        public Run(GameData gd, BlockSpec spec, Layout layout, ProductionPlan plan, ValidationReport report)
        {
            this.gd = gd; this.spec = spec; L = layout; this.plan = plan; R = report; fp = layout.Footprints ?? new Footprints(gd);
        }

        public void PowerOnly() { Classify(); Power(); }
        public void BeltsOnly() { Classify(); Overlap(); BeltLinks(); BeltYaw(); Topo(); Sorters(); Splitters(); Bridges(); }

        public void All()
        {
            foreach (string e in L.Errors)
            {
                // Layout.Add records grid collisions; check 1 redoes them splitter-aware, so they are not copied twice.
                if (e.StartsWith("overlap: ", StringComparison.Ordinal)) continue;
                R.Fail("layout: " + e);
            }
            Classify();
            Tiers();
            Overlap();
            BeltLinks();
            BeltYaw();
            Topo();
            Sorters();
            Splitters();
            Bridges();
            Recipes();
            LabTowers.Validate(L.All, spec, R.Fail, R.Note);   // feat-labs
            Items();
            Flow();
            Ports();
            Icons();
            Power();
            Coaters.Validate(L, plan, gd, R);   // COATERS HOOK: coater cells + proliferator lanes
            Summary();
        }

        // ---------------------------------------------------------------- helpers
        static long K(int x, int y) { return ((long)x << 32) ^ (uint)y; }
        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        static string At(double x, double y) { return "(" + F(x) + "," + F(y) + ")"; }
        string N(int item) { return gd != null ? gd.ItemName(item) + " (" + item + ")" : item.ToString(CultureInfo.InvariantCulture); }
        static List<Bld> Get(Dictionary<Bld, List<Bld>> d, Bld k) { List<Bld> l; if (!d.TryGetValue(k, out l)) { l = new List<Bld>(); d[k] = l; } return l; }
        static List<Bld> Peek(Dictionary<Bld, List<Bld>> d, Bld k) { List<Bld> l; return d.TryGetValue(k, out l) ? l : new List<Bld>(); }
        static bool IsInt(double v) { return Math.Abs(v - Math.Round(v)) < 0.01; }
        bool Fractional(Bld b) { return !IsInt(b.X) || !IsInt(b.Y); }
        static double Dist(double ax, double ay, double bx, double by) { return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)); }
        static double YawDiff(double a, double b) { return Math.Abs(((a - b) % 360.0 + 540.0) % 360.0 - 180.0); }
        static bool AxisAligned(double dx, double dy) { return Math.Min(Math.Abs(dx), Math.Abs(dy)) < 0.05; }
        static Dir DeltaDir(double dx, double dy) { return Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? Dir.E : Dir.W) : (dy >= 0 ? Dir.N : Dir.S); }
        static Dir DirTo(Bld a, Bld b) { return DeltaDir(b.X - a.X, b.Y - a.Y); }

        /// Half extents (x, y) of the building footprint in world axes (rotation-aware, game-data sizes).
        double[] Half(Bld b)
        {
            int[] s = fp.Size(b.ItemId);
            int q = (int)Math.Round(Dirs.Norm(b.Yaw) / 90.0) % 4;
            return q % 2 == 0 ? new[] { s[0] / 2.0, s[1] / 2.0 } : new[] { s[1] / 2.0, s[0] / 2.0 };
        }
        bool Inside(Bld b, double x, double y, double tol)
        {
            double[] h = Half(b);
            return Math.Abs(x - b.X) <= h[0] + tol && Math.Abs(y - b.Y) <= h[1] + tol;
        }
        Bld BeltAtTile(int x, int y) { Bld b; return beltTile.TryGetValue(K(x, y), out b) ? b : null; }
        string Lane(Bld belt) { string s; return belt != null && laneOf.TryGetValue(belt, out s) ? s : "?"; }

        /// Machine slot implied by a sorter (3x3 table, clockwise from the NW corner, machine frame): the face is chosen by
        /// the sorter DIRECTION first (input: the face opposite its travel, output: the face along it), then the lateral
        /// offset picks the slot on that face. The old closest-face rule picked the wrong slot at smelter corners
        /// (slots 3/5/9/11 share their point with 2/6/8/0; analysis\buildings\slots.md section 5).
        static int SlotAt(Bld m, Bld s, bool toMachine)
        {
            double vx = s.X2 - s.X, vy = s.Y2 - s.Y;
            Dir travel = DeltaDir(vx, vy);
            Dir face = toMachine ? Dirs.Opposite(travel) : travel;
            double wx = toMachine ? s.X2 : s.X, wy = toMachine ? s.Y2 : s.Y;
            return Geo.SlotAt(m.Yaw, wx - m.X, wy - m.Y, face);
        }

        /// Flow direction leaving a belt (to its successor), else entering it, else its yaw.
        Dir FlowOf(Bld b)
        {
            if (b.Output != null && b.Output.Kind == Kind.Belt) return DirTo(b, b.Output);
            if (b.Output != null && b.Output.Kind == Kind.Splitter) return DirTo(b, b.Output);
            Bld p = MainPred(b);
            if (p != null) return DirTo(p, b);
            if (b.Input != null && b.Input.Kind == Kind.Splitter) return DirTo(b.Input, b);
            return Dirs.FromYaw(b.Yaw);
        }
        /// The straight/corner predecessor (OutputToSlot 1) of a belt, or null.
        Bld MainPred(Bld b)
        {
            foreach (Bld p in Peek(preds, b)) if (p.OutputToSlot <= 1) return p;
            return null;
        }
        IEnumerable<Bld> Next(Bld node)
        {
            if (node.Kind == Kind.Splitter) { foreach (Bld o in Peek(splitOuts, node)) yield return o; yield break; }
            if (node.Output != null && (node.Output.Kind == Kind.Belt || node.Output.Kind == Kind.Splitter) && inLayout.Contains(node.Output)) yield return node.Output;
        }

        // ---------------------------------------------------------------- classify
        void Classify()
        {
            foreach (Bld b in L.All) inLayout.Add(b);
            belts = L.All.Where(b => b.Kind == Kind.Belt).ToList();
            sorters = L.All.Where(b => b.Kind == Kind.Sorter).ToList();
            machines = L.All.Where(b => b.Kind == Kind.Machine).ToList();
            towers = L.All.Where(b => b.Kind == Kind.Tesla).ToList();
            splitters = L.All.Where(b => b.Kind == Kind.Splitter).ToList();
            LabTowers.Index(L.All, out upperLabs, out towerSize);   // feat-labs
            foreach (Bld b in L.All)
            {
                if (b.Kind == Kind.Belt && !Ids.IsBelt(b.ItemId)) R.Fail("belt record with non-belt item " + b);
                if (b.Kind == Kind.Sorter && !Ids.IsSorter(b.ItemId)) R.Fail("sorter record with non-sorter item " + b);
                if (b.Kind != Kind.Belt && Ids.IsBelt(b.ItemId)) R.Fail("belt item recorded as " + b.Kind + ": " + b);
                if (b.Kind != Kind.Sorter && Ids.IsSorter(b.ItemId)) R.Fail("sorter item recorded as " + b.Kind + ": " + b);
                if (b.Output != null && !inLayout.Contains(b.Output)) R.Fail("link to a building outside the layout from " + b);
                if (b.Input != null && !inLayout.Contains(b.Input)) R.Fail("link from a building outside the layout into " + b);
            }
            foreach (Bld b in belts)
            {
                if ((b.Output != null && b.Output.Kind == Kind.Splitter) || (b.Input != null && b.Input.Kind == Kind.Splitter)) splitterTouch.Add(b);
                if (b.Output != null && inLayout.Contains(b.Output)) Get(preds, b.Output).Add(b);
                if (b.Input != null && b.Input.Kind == Kind.Splitter) Get(splitOuts, b.Input).Add(b);
            }
            // lanes: chain name, else connected run of belts (union over all belt links)
            var parent = new Dictionary<Bld, Bld>();
            Func<Bld, Bld> find = null;
            find = x => { Bld p = parent[x]; if (p == x) return x; Bld r = find(p); parent[x] = r; return r; };
            foreach (Bld b in belts) parent[b] = b;
            foreach (Bld b in belts) if (b.Output != null && b.Output.Kind == Kind.Belt && parent.ContainsKey(b.Output) && b.OutputToSlot <= 1) parent[find(b)] = find(b.Output);
            var runId = new Dictionary<Bld, int>();
            foreach (Bld b in belts) { Bld r = find(b); if (!runId.ContainsKey(r)) runId[r] = runId.Count; laneOf[b] = b.Chain ?? ("run#" + runId[r]); }
        }

        // ---------------------------------------------------------------- user rule 4 (tiers)
        void Tiers()
        {
            // lanes hook (PLAN.md decision 8, replaces the old "no Mk.III" rule): no belt / sorter above the spec caps
            if (spec == null) return;
            int bad = 0;
            foreach (Bld b in L.All)
            {
                if (b.Kind == Kind.Belt && !Lanes.WithinCap(b.ItemId, spec.Tiers.Belt) && bad++ < 10)
                    R.Fail("rule 4: belt " + b.ItemId + " above the belt cap " + spec.Tiers.Belt + " at " + At(b.X, b.Y));
                if (b.Kind == Kind.Sorter && !Lanes.WithinCap(b.ItemId, spec.Tiers.Sorter) && bad++ < 10)
                    R.Fail("rule 4: sorter " + b.ItemId + " above the sorter cap " + spec.Tiers.Sorter + " at " + At(b.X, b.Y));
            }
            if (bad > 10) R.Fail(bad + " belts/sorters above the tier caps in total");
            if (belts.Any(b => b.ItemId != spec.Tiers.Belt) || sorters.Any(s => s.ItemId != spec.Tiers.Sorter)) R.Note("tiers: " + Lanes.TierSummary(gd, L));
        }

        // ---------------------------------------------------------------- 1. overlap
        readonly Dictionary<long, Bld> occ = new Dictionary<long, Bld>();   // tile -> building (sorters, splitter cell belts, raised nodes excluded)

        void Overlap()
        {
            var seen = new HashSet<string>();
            foreach (Bld b in L.All)
            {
                if (b.Kind == Kind.Sorter || splitterTouch.Contains(b)) continue;   // splitter end belts sit inside the splitter tile
                Rect r = fp.TileRect(b);
                if (r.IsEmpty) continue;
                for (int x = r.X0; x <= r.X1; x++)
                    for (int y = r.Y0; y <= r.Y1; y++)
                    {
                        Bld o;
                        if (occ.TryGetValue(K(x, y), out o))
                        {
                            string key = Math.Min(o.GetHashCode(), b.GetHashCode()) + ":" + Math.Max(o.GetHashCode(), b.GetHashCode());
                            if (seen.Add(key)) R.Fail("overlap at tile (" + x + "," + y + "): " + b + " with " + o);
                        }
                        else occ[K(x, y)] = b;
                    }
            }
            foreach (Bld b in belts) if (!splitterTouch.Contains(b) && !b.IsRaised) beltTile[K(b.TileX, b.TileY)] = b;
            Colliders();
        }

        /// Exact game build colliders (analysis\buildings\colliders.json: every trigger box the game tests, e.g. the
        /// splitter's two crossed arms) when available, else the game-data footprint box.
        double PairGap(Bld a, double[] ha, Bld c, double[] hc, double ew)
        {
            if (gd.HasCollider(a.ItemId) && gd.HasCollider(c.ItemId)) return gd.ColliderGap(a, c, ew);
            double gx = Math.Abs(a.X - c.X) * ew - ha[0] - hc[0];
            double gy = Math.Abs(a.Y - c.Y) - ha[1] - hc[1];
            return Math.Max(gx, gy);
        }

        /// Real game build colliders (analysis\game-data.json footprintTiles = collider metres / 1.2566, rotated by yaw)
        /// must not intersect. The tile check above cannot see this: Footprints rounds an assembler (3.04 tiles) to a
        /// 3x3 tile rect, so assemblers edge to edge at pitch 3 passed it but collide in game. Clearance rule from the
        /// proven blueprints: gap >= 0 (smallest proven gaps: arc smelters at pitch 3 = 0.69 tile, assemblers at pitch
        /// 4 = 0.96 tile; assemblers at pitch 3 = -0.04 = the in-game "collides with another object").
        /// Belt-belt and belt-splitter pairs are linked parts of one conveyor line and are not checked.
        void Colliders()
        {
            if (gd == null) { R.Warn("collider check skipped: no game data"); return; }
            var list = new List<KeyValuePair<Bld, double[]>>();
            foreach (Bld b in L.All)
            {
                if (b.Kind == Kind.Sorter || splitterTouch.Contains(b) || b.IsRaised || upperLabs.Contains(b)) continue;   // feat-labs: upper levels = base footprint
                MachineDef md = gd.Machine(b.ItemId);
                if (md == null || md.FootprintTiles == null || md.FootprintTiles.Length != 2) { R.Fail("collider check: no game collider size for item " + b.ItemId + " at " + At(b.X, b.Y)); continue; }
                int q = (int)Math.Round(Dirs.Norm(b.Yaw) / 90.0) % 4;
                double w = md.FootprintTiles[0], h = md.FootprintTiles[1];
                list.Add(new KeyValuePair<Bld, double[]>(b, q % 2 == 0 ? new[] { w / 2, h / 2 } : new[] { h / 2, w / 2 }));
            }
            int bad = 0, bad60 = 0; double minGap = double.MaxValue; string minAt = null;
            bool safe60 = spec != null && spec.Safe60;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    Bld a = list[i].Key, c = list[j].Key;
                    bool aBelt = a.Kind == Kind.Belt, cBelt = c.Kind == Kind.Belt;
                    if (aBelt && cBelt) continue;
                    if ((aBelt && c.Kind == Kind.Splitter) || (cBelt && a.Kind == Kind.Splitter)) continue;
                    if (Coaters.PassesUnder(a, c) || Coaters.PassesUnder(c, a)) continue;   // COATERS HOOK: lane belt under its coater
                    if (Math.Abs(a.X - c.X) > 12 || Math.Abs(a.Y - c.Y) > 12) continue;   // no collider is that large here
                    double gap = PairGap(a, list[i].Value, c, list[j].Value, 1.0);   // > 0: separated along at least one axis
                    if (a.Kind == Kind.Machine && c.Kind == Kind.Machine && gap < minGap) { minGap = gap; minAt = a + " / " + c; }
                    if (gap < -0.001 && bad++ < 10)
                        R.Fail("collider overlap " + F(-gap) + " tile: " + a + " with " + c + " (game collider sizes " + F(2 * list[i].Value[0]) + "x" + F(2 * list[i].Value[1])
                            + " / " + F(2 * list[j].Value[0]) + "x" + F(2 * list[j].Value[1]) + " tiles)");
                    // Safe60: machines and splitters must also clear each other with E-W tiles 0.8557 of the equator
                    if (safe60 && !aBelt && !cBelt && a.Kind != Kind.Tesla && c.Kind != Kind.Tesla)
                    {
                        double g60 = PairGap(a, list[i].Value, c, list[j].Value, GameData.EwFactorMin);
                        if (g60 < -0.001 && bad60++ < 10) R.Fail("collider overlap " + F(-g60) + " tile up to 60 deg latitude (E-W tile x" + F(GameData.EwFactorMin) + "): " + a + " with " + c);
                    }
                }
            if (bad > 10) R.Fail(bad + " collider overlaps in total");
            if (bad60 > 10) R.Fail(bad60 + " collider overlaps up to 60 deg in total");
            if (minAt != null) R.Note("min machine collider gap " + F(minGap) + " tile (" + minAt + ")" + (gd.CollidersPath != null ? " [colliders.json]" : " [game-data footprint]"));
        }

        // ---------------------------------------------------------------- 2. belt links
        void BeltLinks()
        {
            var tiles = new Dictionary<long, Bld>();
            foreach (Bld b in belts)
            {
                if (splitterTouch.Contains(b) || b.IsRaised) continue;
                long k = K(b.TileX, b.TileY);
                if (tiles.ContainsKey(k)) { R.Fail("belt tile used twice at (" + b.TileX + "," + b.TileY + ")"); beltGraphOk = false; }
                else tiles[k] = b;
                if (Fractional(b) && !IsSplitterNeighbour(b)) R.Fail("belt at fractional position not next to a splitter: " + b);
            }
            foreach (Bld b in belts)
            {
                if (YawDiff(b.Yaw, b.Yaw2) > 1) R.Warn("belt yaw2 differs from yaw: " + b);
                Bld o = b.Output;
                if (o == null) { if (b.OutputToSlot != 0) R.Warn("chain end with outputToSlot " + b.OutputToSlot + ": " + b); continue; }
                if (o.Kind == Kind.Splitter) continue;      // checked in Splitters()
                if (o.Kind != Kind.Belt) { R.Fail("belt outputs into a " + o.Kind + ": " + b); beltGraphOk = false; continue; }
                double dx = o.X - b.X, dy = o.Y - b.Y, d = Math.Abs(dx) + Math.Abs(dy);
                bool frac = Fractional(b) || Fractional(o);
                bool ok = AxisAligned(dx, dy) && (frac ? d > 0.6 && d < 1.15 : Math.Abs(d - 1) < 0.01);
                if (!ok) { R.Fail("belt link not adjacent: " + b + " -> " + At(o.X, o.Y)); beltGraphOk = false; continue; }
                if (b.OutputToSlot < 1 || b.OutputToSlot > 3) R.Fail("belt link with outputToSlot " + b.OutputToSlot + ": " + b);
                if (o.Output == b) R.Fail("two belts link into each other at " + At(b.X, b.Y));
                CheckLinkGeometry(b, o);
            }
            foreach (Bld b in belts)
                if (b.Output != null && b.Output.Kind == Kind.Splitter) CheckLinkGeometry(b, b.Output);
                else if (b.Input != null && b.Input.Kind == Kind.Splitter) CheckLinkGeometry(b.Input, b);
            if (linkErrors > 10) R.Fail(linkErrors + " belt link geometry errors in total");
            // per target: one main predecessor, side-joins perpendicular on distinct slots (rule 6)
            foreach (Bld t in belts)
            {
                List<Bld> ps = Peek(preds, t);
                if (ps.Count == 0) continue;
                var main = ps.Where(p => p.OutputToSlot <= 1).ToList();
                if (main.Count > 1) R.Fail("belt has " + main.Count + " straight predecessors (use a side-join, slot 2/3): " + t);
                Dir flow = FlowOf(t);
                var slots = new HashSet<int>();
                foreach (Bld p in ps)
                {
                    Dir pd = DirTo(p, t);
                    if (p.OutputToSlot <= 1)
                    {
                        if (t.Output != null && t.Output.Kind == Kind.Belt && DirTo(t, t.Output) == Dirs.Opposite(pd)) R.Fail("belt reverses direction at " + At(t.X, t.Y));
                        continue;
                    }
                    if (!slots.Add(p.OutputToSlot)) R.Fail("rule 6: two side-joins on slot " + p.OutputToSlot + " of " + t);
                    if (pd == flow || pd == Dirs.Opposite(flow)) R.Fail("rule 6: side-join is not perpendicular to the target flow " + flow + ": " + p + " -> " + t);
                    if (p.OutputToSlot == 3 && !ps.Any(q => q.OutputToSlot == 2)) R.Warn("side-join uses slot 3 although slot 2 is free: " + p);
                }
            }
            // a chain end must not point straight into a foreign belt or splitter it is not linked to
            foreach (Bld b in belts)
            {
                if (b.Output != null || splitterTouch.Contains(b)) continue;
                Dir d = FlowOf(b);
                int nx = b.TileX + Dirs.Dx(d), ny = b.TileY + Dirs.Dy(d);
                Bld o = BeltAtTile(nx, ny);
                if (o != null && Math.Abs(o.Z - b.Z) < 0.9) R.Fail("chain end " + At(b.X, b.Y) + " points into belt (" + nx + "," + ny + ") without a side-join link");
                foreach (Bld s in splitters) if (s.TileX == nx && s.TileY == ny) R.Fail("chain end " + At(b.X, b.Y) + " points into a splitter without a link");
            }
        }

        /// Game paste rules for a belt link (conditions.md F4/F5/F6), metres, worst case over the E-W tile factor up to
        /// 60 deg latitude (0.8557 .. 1.1409): belt-belt 3D distance <= 2.302 m, slope |sin| <= 0.6 (36.87 deg);
        /// belt-splitter: node within 4.0 m of the splitter origin.
        int linkErrors;
        void CheckLinkGeometry(Bld a, Bld b)
        {
            double dxT = Math.Abs(b.X - a.X), dyT = Math.Abs(b.Y - a.Y), dzM = Math.Abs(b.Z - a.Z) * GameData.BeltLevelM;
            Func<double, double> horiz = f => Math.Sqrt(Math.Pow(dxT * GameData.MetersPerTile * f, 2) + Math.Pow(dyT * GameData.MetersPerTile, 2));
            double runMin = horiz(GameData.EwFactorMin), runMax = horiz(GameData.EwFactorMax);
            string at = " " + a + " -> " + b;
            if (a.Kind == Kind.Splitter || b.Kind == Kind.Splitter)
            {
                if (Math.Sqrt(runMax * runMax + dzM * dzM) > GameData.BeltToBuildingMaxM + 1e-6 && linkErrors++ < 10)
                    R.Fail("belt node farther than " + F(GameData.BeltToBuildingMaxM) + " m from its splitter (game TooFar, F6):" + at);
                return;
            }
            double len = Math.Sqrt(runMax * runMax + dzM * dzM);
            if (len > GameData.BeltMaxLinkM + 1e-6 && linkErrors++ < 10)
                R.Fail("belt link " + F(len) + " m > " + F(GameData.BeltMaxLinkM) + " m (game TooFar, F4):" + at);
            if (dzM > 1e-6)
            {
                double slope = Math.Atan2(dzM, runMin) * 180.0 / Math.PI;
                if (slope > GameData.BeltMaxSlopeDeg + 1e-6 && linkErrors++ < 10)
                    R.Fail("belt slope " + F(slope) + " deg > " + F(GameData.BeltMaxSlopeDeg) + " (game TooSteep, F5, E-W tile x" + F(GameData.EwFactorMin) + "):" + at);
            }
        }

        /// Belt of a splitter cell (0.2 / 1.07 from the splitter centre, ground or high port).
        bool IsPortBelt(Bld b) { return splitterTouch.Contains(b) || b.Cell == SplitterGeo.BeltCell || (Fractional(b) && IsSplitterNeighbour(b)); }

        /// High run: integer nodes at z 1 continuing straight from a splitter high port (m39/m40, user's file: open stubs
        /// (18,37..39,1), (21,37..40,1), (21,33..32,1)).
        bool InHighRun(Bld b)
        {
            Bld n = b;
            for (int guard = 0; n != null && guard < 64; guard++)
            {
                if (Math.Abs(n.Z - 1.0) > 0.02 && !(IsPortBelt(n) && n.Z > 0.9)) return false;
                if (IsPortBelt(n)) return n.Input != null && n.Input.Kind == Kind.Splitter || MainPred(n) != null && IsPortBelt(MainPred(n));
                n = MainPred(n);
            }
            return false;
        }

        /// Cell RAISED_RUN (generalises BRIDGE; game-proven by refs\bridges-splitters-user.txt and the user's in-game edit
        /// refs\engine-turbine-60-v2-userupgrade.txt, analysis\user-upgrade-turbine.md section 1). Raised node: z 0.5 on
        /// the grid, at most one per tile, never over a building; below it free ground or a straight ground belt
        /// (predecessor and successor in line, no side-join) flowing across the node or along it (double-deck), along a
        /// leg under a raised corner; at least 0.5 tile from any sorter (>= 1.0 proven, 0.5..1.0 warned). Run: approach -
        /// ground foot - R1 straight (one-tile ramp), raised corners only between R1 and Rn, Rn - ground foot - approach
        /// straight. A run may start at a splitter high port (m39/m40, z 1, no up-ramp; derived). z 1 nodes are accepted
        /// only as a straight high run from a splitter high port (proven as open stubs in the user's file).
        void Bridges()
        {
            var raisedAt = new Dictionary<long, Bld>();
            int bad = 0, nearSorter = 0, runs = 0, rCorners = 0, high = 0, maxRun = 0;
            Action<string> fail = m => { if (bad++ < 10) R.Fail(m); };
            Func<Bld, Dir?> din = b => { Bld p = MainPred(b); if (p != null) return DirTo(p, b); if (b.Input != null && b.Input.Kind == Kind.Splitter) return DirTo(b.Input, b); return null; };
            Func<Bld, Dir?> dout = b => b.Output != null && (b.Output.Kind == Kind.Belt || b.Output.Kind == Kind.Splitter) ? (Dir?)DirTo(b, b.Output) : null;
            foreach (Bld b in belts)
            {
                if (!b.IsRaised || IsPortBelt(b) || Coaters.IsFeed(b)) continue;   // COATERS HOOK: feed cell checked in Coaters.Validate
                string at = " at " + At(b.X, b.Y);
                bool hr = Math.Abs(b.Z - 1.0) < 0.02 && InHighRun(b);
                if (hr) high++;
                else if (Math.Abs(b.Z - GameData.BridgeZ) > 0.01) fail("raised node z " + F(b.Z) + ", the proven raised level is z " + F(GameData.BridgeZ) + " (z 1 only straight from a splitter high port)" + at);
                if (Fractional(b)) fail("raised node off the tile grid" + at);
                long k = K(b.TileX, b.TileY);
                if (raisedAt.ContainsKey(k)) fail("two raised nodes on one tile (two-level crossing, not proven)" + at);
                raisedAt[k] = b;
                Bld o;
                if (occ.TryGetValue(k, out o) && o.Kind != Kind.Belt) fail("raised node over a building " + o + at);
                Dir? i0 = din(b), o0 = dout(b);
                if (o0 == null && !hr) fail("raised run ends in the air (no ground foot after it)" + at);
                Dir a = i0 ?? o0 ?? Dirs.FromYaw(b.Yaw), c = o0 ?? a;
                if (a != c) rCorners++;
                Bld g = BeltAtTile(b.TileX, b.TileY);
                // z 1 high-port runs pass over anything the game accepts (user's m39 stubs end under each other);
                // the ground belt may be a ramp foot of its own run (user's (9,22): C1 up-ramp under C3's raised corner)
                if (g != null && !hr)
                {
                    Bld gp = MainPred(g);
                    bool straight = g.Output != null && g.Output.Kind == Kind.Belt && gp != null
                        && DirTo(gp, g) == DirTo(g, g.Output) && Math.Abs(Math.Abs(g.Output.X - g.X) + Math.Abs(g.Output.Y - g.Y) - 1) < 0.01;
                    if (!straight) fail("ground belt under a raised node is not straight through (turns, starts or ends)" + at);
                    else
                    {
                        Dir f = DirTo(g, g.Output);
                        if (a == c ? f == Dirs.Opposite(a) : (f != a && f != c)) fail("ground belt under a raised node flows " + f + " against the raised node (" + a + "->" + c + ")" + at);
                    }
                    if (Peek(preds, g).Any(p => p.OutputToSlot > 1)) fail("ground belt under a raised node takes a side-join (not proven)" + at);
                }
                double ds = sorters.Count == 0 ? double.MaxValue : sorters.Min(s => GameData.SegmentDist(b.X, b.Y, s.X, s.Y, s.X2, s.Y2));
                if (ds < 0.5) fail("raised node " + F(ds) + " tile from a sorter (on its tile)" + at);
                else if (ds < GameData.RaisedSorterMin - 1e-6) nearSorter++;
            }
            // runs: first raised node of every run
            foreach (Bld b in belts)
            {
                if (!b.IsRaised || IsPortBelt(b) || Coaters.IsFeed(b) || Math.Abs(b.Z - 1.0) < 0.02 && InHighRun(b)) continue;   // COATERS HOOK
                Bld foot = MainPred(b);
                if (foot != null && foot.IsRaised && !IsPortBelt(foot)) continue;
                string at = " at " + At(b.X, b.Y);
                runs++;
                var run = new List<Bld>();
                for (Bld n = b; n != null && n.Kind == Kind.Belt && n.IsRaised && !IsPortBelt(n); n = n.Output) run.Add(n);
                maxRun = Math.Max(maxRun, run.Count);
                Bld last = run[run.Count - 1], far = last.Output;
                if (foot == null) { fail("raised run without a ground foot (chain starts in the air)" + at); continue; }
                Dir d1 = DirTo(foot, b);
                bool fromHigh = IsPortBelt(foot) && foot.Z > 0.9;
                if (!fromHigh)
                {
                    if (foot.IsRaised) { fail("raised run starts on a raised splitter belt that is not a high port" + at); continue; }
                    Bld before = MainPred(foot);
                    if (before == null && !(foot.Input != null && foot.Input.Kind == Kind.Splitter)) fail("raised run foot without an approach node (chain start on a ramp foot)" + at);
                    if (before != null && DirTo(before, foot) != d1) fail("raised run is not straight from the approach node over the up-ramp (turn on a ramp, S4)" + at);
                    if (YawDiff(foot.Yaw, Dirs.Yaw(d1)) > 1) fail("raised run foot yaw " + F(foot.Yaw) + " is not the flow direction" + at);
                    if (Peek(preds, foot).Any(p => p.OutputToSlot > 1)) fail("raised run foot takes a side-join" + at);
                }
                if (run.Count > 1 && DirTo(b, run[1]) != d1) fail("raised run turns on its first raised node (turn on a ramp, S4)" + at);
                if (far == null || far.Kind != Kind.Belt || far.IsRaised) { if (far != null) fail("raised run without a ground foot after it" + at); continue; }
                Dir d2 = DirTo(last, far);
                Bld lp = run.Count > 1 ? run[run.Count - 2] : foot;
                if (DirTo(lp, last) != d2) fail("raised run turns on its last raised node (turn on a ramp, S4)" + at);
                if (far.Output == null) fail("raised run far foot is a chain end (no approach node after it)" + at);
                else if (far.Output.Kind == Kind.Belt && DirTo(far, far.Output) != d2) fail("raised run is not straight over the down-ramp to the far approach node (S4)" + at);
                if (YawDiff(far.Yaw, Dirs.Yaw(d2)) > 1) fail("raised run foot yaw " + F(far.Yaw) + " is not the flow direction" + at);
                if (Peek(preds, far).Any(p => p.OutputToSlot > 1)) fail("raised run foot takes a side-join" + at);
                if (fromHigh) R.Note("raised run from a splitter high port at " + At(foot.X, foot.Y) + " (derived: z 1 -> 0.5 step, no up-ramp)");
            }
            if (bad > 10) R.Fail(bad + " raised-run errors in total");
            if (nearSorter > 0) R.Warn("UNPROVEN: " + nearSorter + " raised node(s) 0.5..1.0 tile from a sorter (proven >= 1.0 in refs\\engine-turbine-60-v2-userupgrade.txt)");
            if (raisedAt.Count > 0) R.Note("raised runs: " + runs + " run(s), " + (raisedAt.Count - high) + " node(s) at z 0.5, " + rCorners + " raised corner(s), longest " + maxRun
                + (high > 0 ? ", " + high + " high-port node(s) at z 1" : "") + " (cell RAISED_RUN, refs\\engine-turbine-60-v2-userupgrade.txt)");
        }

        bool IsSplitterNeighbour(Bld b)
        {
            if (splitterTouch.Contains(b)) return true;
            if (b.Output != null && splitterTouch.Contains(b.Output)) return true;
            foreach (Bld p in Peek(preds, b)) if (splitterTouch.Contains(p)) return true;
            return false;
        }

        // ---------------------------------------------------------------- rules 6/7: splitters
        void Splitters()
        {
            foreach (Bld s in splitters)
            {
                List<Bld> ins = Peek(preds, s), outs = Peek(splitOuts, s);
                // user rule (in-game edit 2026-10-31: an accidental second splitter stacked at z 2 on (28,22)): no stacked
                // splitters, every splitter carries at least 2 connected belts
                if (Math.Abs(s.Z) > 0.1 || (s.Input != null && s.Input.Kind == Kind.Splitter) || splitters.Any(o => o != s && Math.Abs(o.X - s.X) < 0.1 && Math.Abs(o.Y - s.Y) < 0.1))
                    R.Fail("stacked splitter at " + At(s.X, s.Y) + " z " + F(s.Z) + " (splitters are multi-level; a splitter on a splitter is not a construct)");
                if (ins.Count + outs.Count < 2) R.Fail("unused splitter at " + At(s.X, s.Y) + ": " + (ins.Count + outs.Count) + " connected belt(s), at least 2 needed");
                if (ins.Count == 0) R.Fail("splitter without an incoming belt at " + At(s.X, s.Y));
                if (ins.Count > 1) R.Fail("rule 6: splitter used as a merge (" + ins.Count + " incoming belts) at " + At(s.X, s.Y));
                if (outs.Count == 0) R.Fail("rule 7: splitter without outgoing belts at " + At(s.X, s.Y));
                else if (outs.Count == 1) R.Warn("rule 7: splitter with a single outgoing belt at " + At(s.X, s.Y));
                if (s.Parameters == null || s.Parameters.Length != 6 || (s.Parameters[0] != 0 && s.Parameters[0] != 1)) R.Fail("splitter parameters not [priority,0,0,0,0,0] at " + At(s.X, s.Y));
                if (Math.Abs(Dirs.Norm(s.Yaw + 0.5) - 0.5) > 1.0) R.Warn("splitter at " + At(s.X, s.Y) + " has yaw " + F(s.Yaw) + " (only yaw 0 is proven in game)");
                var sides = new HashSet<int>();
                int model = s.ModelIndex == 39 || s.ModelIndex == 40 ? s.ModelIndex : 38;
                var inSide = new KeyValuePair<Dir, bool>(Dir.S, false);
                var outSides = new List<KeyValuePair<Dir, bool>>();
                foreach (Bld b in ins.Concat(outs))
                {
                    double dx = b.X - s.X, dy = b.Y - s.Y, d = Math.Abs(dx) + Math.Abs(dy);
                    if (!AxisAligned(dx, dy) || Math.Abs(d - 0.2) > 0.05) { R.Fail("rule 7: belt " + At(b.X, b.Y) + " is not 0.2 from the splitter center " + At(s.X, s.Y)); beltGraphOk = false; continue; }
                    // side in the splitter's own frame (slots are prefab-local, analysis\buildings\slots.md); the engine
                    // keeps splitters at yaw 0, so this is the world side
                    int side = (((int)DeltaDir(dx, dy) - (int)Math.Round(Dirs.Norm(s.Yaw) / 90.0)) % 4 + 4) % 4;
                    bool hi = b.Z - s.Z > 0.5;
                    if (!sides.Add(side * 2 + (hi ? 1 : 0))) R.Fail("rule 7: two belts on splitter side " + (Dir)side + (hi ? " high" : "") + " at " + At(s.X, s.Y));
                    bool incoming = b.Output == s;
                    int slot = incoming ? b.OutputToSlot : b.InputFromSlot;
                    int want = Modules.SplitterSlot(model, (Dir)side, hi);
                    if (want < 0) R.Fail("rule 7: splitter model " + model + " has no " + (hi ? "high" : "ground") + " port on side " + (Dir)side + " at " + At(s.X, s.Y));
                    else if (slot != want) R.Fail("rule 7: splitter m" + model + " " + (incoming ? "input" : "output") + " belt slot " + slot + " but it sits on side " + (Dir)side + (hi ? " high" : "") + " (" + want + ")");
                    if (!incoming && b.Output == null && !hi) R.Fail("splitter output belt leads nowhere at " + At(b.X, b.Y));
                    if (incoming) inSide = new KeyValuePair<Dir, bool>((Dir)side, hi); else outSides.Add(new KeyValuePair<Dir, bool>((Dir)side, hi));
                }
                if (model != 38)
                {
                    bool proven = Modules.IsProvenSplitterConfig(model, inSide.Key, inSide.Value, outSides);
                    if (proven) R.Note("splitter m" + model + " at " + At(s.X, s.Y) + ": configuration built in game (refs\\engine-turbine-60-v2-userupgrade.txt)");
                    else R.Warn("UNPROVEN splitter m" + model + " configuration at " + At(s.X, s.Y) + " (derived from the port table, needs an in-game test)");
                }
                if (ins.Count == 1 && outs.Count == 3 && !(ins[0].OutputToSlot == 2 && outs.All(o => o.InputFromSlot != 2)))
                    R.Note("splitter at " + At(s.X, s.Y) + ": 1 -> 3 with the input not from S (proven SPLIT cell: in S, out N/E/W; same geometry turned)");
            }
            SplitterClearance();
            // cascades (one splitter's output running into another splitter) are not proven in game; spacing
            for (int i = 0; i < splitters.Count; i++)
            {
                foreach (Bld o in Peek(splitOuts, splitters[i]))
                {
                    Bld n = o;
                    for (int guard = 0; n != null && n.Kind == Kind.Belt && guard < 200; guard++)
                    {
                        if (Peek(picksAt, n).Count > 0 || Peek(dropsAt, n).Count > 0) { n = null; break; }
                        n = n.Output;
                    }
                    if (n != null && n.Kind == Kind.Splitter)
                    {
                        double d = Dist(splitters[i].X, splitters[i].Y, n.X, n.Y);
                        R.Warn("UNPROVEN splitter cascade: splitter at " + At(splitters[i].X, splitters[i].Y) + " feeds splitter at " + At(n.X, n.Y) + " (needs an in-game test)");
                        if (d < GameData.ProvenSplitterSpacing - 1e-6) R.Fail("splitter cascade " + F(d) + " tiles apart, below the proven splitter spacing " + F(GameData.ProvenSplitterSpacing) + " (refs\\splitters.txt)");
                    }
                }
                for (int j = i + 1; j < splitters.Count; j++)
                {
                    double d = Dist(splitters[i].X, splitters[i].Y, splitters[j].X, splitters[j].Y);
                    if (d < GameData.ProvenSplitterSpacing - 1e-6)
                        R.Warn("splitters " + F(d) + " tiles apart at " + At(splitters[i].X, splitters[i].Y) + " / " + At(splitters[j].X, splitters[j].Y) + " (smallest proven spacing " + F(GameData.ProvenSplitterSpacing) + "; independent splitters, colliders checked)");
                }
            }
        }

        /// Pasted belts near a pasted splitter (in-game test 2026-10-31: engine-turbine-60-v1 and engine-missile-90-v1 were
        /// refused, each with a foreign belt on the tile straight next to a splitter). Two checks:
        /// a) game Collide (GameData.BeltProbeGapM): a belt more than 3 belt hops from the splitter whose 0.23 m probe
        ///    sphere touches a splitter arm box; b) engine clearance from the game-proven files: a belt that is not on the
        ///    splitter's own chains (feed upstream, outputs downstream up to a side-join) must be >= 3.0 tiles from the
        ///    splitter centre (proven 3.0 in refs\splitters.txt and refs\engine-coil-1080-v1.txt).
        void SplitterClearance()
        {
            int bad = 0;
            Action<string> fail = m => { if (bad++ < 10) R.Fail(m); };
            double minForeign = double.MaxValue;
            foreach (Bld s in splitters)
            {
                // hops along belt links in both directions (game pass 2 clears a belt whose hits are within 3 hops)
                var hops = new Dictionary<Bld, int> { { s, 0 } };
                var q = new Queue<Bld>(); q.Enqueue(s);
                while (q.Count > 0)
                {
                    Bld n = q.Dequeue(); int h = hops[n];
                    if (h >= GameData.PasteOwnBeltHops) continue;
                    var nb = new List<Bld>(Peek(preds, n));
                    if (n.Kind == Kind.Splitter) nb.AddRange(Peek(splitOuts, n));
                    else if (n.Output != null && n.Output.Kind == Kind.Belt) nb.Add(n.Output);
                    foreach (Bld m in nb) if (!hops.ContainsKey(m)) { hops[m] = h + 1; q.Enqueue(m); }
                }
                // own chains: straight predecessors upstream, outputs downstream until a side-join
                var own = new HashSet<Bld>();
                foreach (Bld i in Peek(preds, s)) for (Bld n = i; n != null && own.Add(n); n = MainPred(n)) { }
                foreach (Bld o in Peek(splitOuts, s))
                    for (Bld n = o; n != null && n.Kind == Kind.Belt && own.Add(n); n = n.OutputToSlot <= 1 ? n.Output : null) { }
                foreach (Bld b in belts)
                {
                    if (Math.Abs(b.X - s.X) > 4 || Math.Abs(b.Y - s.Y) > 4) continue;
                    double d = Dist(b.X, b.Y, s.X, s.Y);
                    if (!hops.ContainsKey(b) && gd != null && gd.HasCollider(s.ItemId))
                    {
                        double g = gd.BeltProbeGapM(b, s);
                        if (g < 0) fail("splitter clearance: belt " + b + " probe hits the splitter at " + At(s.X, s.Y) + " (" + F(-g) + " m into its arm; game Collide: pasted belt vs pasted splitter, belt not within " + GameData.PasteOwnBeltHops + " hops)");
                    }
                    if (own.Contains(b)) continue;
                    minForeign = Math.Min(minForeign, d);
                    if (d < GameData.SplitterForeignBeltMin - 0.01)
                        fail("splitter clearance: foreign belt " + At(b.X, b.Y) + " " + F(d) + " tiles from the splitter at " + At(s.X, s.Y) + " (minimum " + F(GameData.SplitterForeignBeltMin) + ", game-proven 2.0 in refs\\engine-turbine-60-v2-userupgrade.txt; 1.0 refused in game)");
                }
            }
            if (bad > 10) R.Fail(bad + " splitter clearance errors in total");
            if (splitters.Count > 0 && minForeign < double.MaxValue) R.Note("splitter clearance: nearest foreign belt " + F(minForeign) + " tiles (min " + F(GameData.SplitterForeignBeltMin) + ")");
        }

        // ---------------------------------------------------------------- belt yaw (straight = flow, corner = bisector)
        void BeltYaw()
        {
            int bad = 0;
            foreach (Bld b in belts)
            {
                Bld p = MainPred(b);
                Dir? din = null, dout = null;
                if (p != null) din = DirTo(p, b);
                else if (b.Input != null && b.Input.Kind == Kind.Splitter) din = DirTo(b.Input, b);
                if (b.Output != null) dout = DirTo(b, b.Output);
                double want;
                if (din.HasValue && dout.HasValue && din.Value != dout.Value)
                {
                    if (din.Value == Dirs.Opposite(dout.Value)) continue;          // reported in BeltLinks
                    double a = Dirs.Yaw(din.Value), c = Dirs.Yaw(dout.Value);
                    want = Math.Abs(a - c) > 180 ? Dirs.Norm((a + c + 360) / 2.0) : (a + c) / 2.0;
                }
                else if (dout.HasValue) want = Dirs.Yaw(dout.Value);
                else if (din.HasValue) want = Dirs.Yaw(din.Value);
                else continue;
                if (YawDiff(b.Yaw, want) > 5 && bad++ < 10) R.Fail("belt yaw " + F(b.Yaw) + " but flow says " + F(want) + ": " + b);
            }
            if (bad > 10) R.Fail(bad + " belt yaw errors in total");
        }

        // ---------------------------------------------------------------- 2. cycles + topological order of belts/splitters
        void Topo()
        {
            var nodes = belts.Concat(splitters).ToList();
            var indeg = new Dictionary<Bld, int>();
            foreach (Bld n in nodes) indeg[n] = 0;
            foreach (Bld n in nodes) foreach (Bld m in Next(n)) if (indeg.ContainsKey(m)) indeg[m]++;
            var q = new Queue<Bld>(nodes.Where(n => indeg[n] == 0));
            topo = new List<Bld>();
            while (q.Count > 0)
            {
                Bld n = q.Dequeue(); topo.Add(n);
                foreach (Bld m in Next(n)) if (indeg.ContainsKey(m) && --indeg[m] == 0) q.Enqueue(m);
            }
            if (topo.Count != nodes.Count)
            {
                Bld c = nodes.First(n => indeg[n] > 0);
                R.Fail("belt cycle through " + c);
                beltGraphOk = false;
            }
        }

        // ---------------------------------------------------------------- 3. sorters + user rules 1-3
        void Sorters()
        {
            var usedSlots = new HashSet<string>();
            var obstacles = L.All.Where(b => (b.Kind == Kind.Machine && !upperLabs.Contains(b)) || b.Kind == Kind.Tesla || b.Kind == Kind.Splitter || b.Kind == Kind.Storage).ToList();
            var roles = new SortedDictionary<string, int>();
            foreach (Bld s in sorters)
            {
                string at = " at " + At(s.X, s.Y) + "->" + At(s.X2, s.Y2);
                Bld i = s.Input, o = s.Output;
                if (i == null || o == null) { R.Fail("sorter without link" + at); continue; }
                bool toMachine = i.Kind == Kind.Belt && o.Kind == Kind.Machine;
                bool fromMachine = i.Kind == Kind.Machine && o.Kind == Kind.Belt;
                if ((i.Kind == Kind.Belt && o.Kind == Kind.Storage) || (i.Kind == Kind.Storage && o.Kind == Kind.Belt))
                {
                    StoreSorter(s, i.Kind == Kind.Belt, usedSlots, roles, at);
                    continue;
                }
                if (!toMachine && !fromMachine) { R.Fail("sorter must link a belt and a machine or depot (" + i.Kind + " -> " + o.Kind + ")" + at); continue; }
                Bld mach = toMachine ? o : i, belt = toMachine ? i : o;
                Get(toMachine ? inSorters : outSorters, mach).Add(s);
                Get(toMachine ? picksAt : dropsAt, belt).Add(s);

                if (!Inside(i, s.X, s.Y, 0.05)) R.Fail("sorter start not on its input " + i + at);
                if (!Inside(o, s.X2, s.Y2, 0.05)) R.Fail("sorter end not on its output " + o + at);

                // length: game rule L = clamp(round(segment - 0.3), 1, 3), unreachable above 3.499
                int lenP = s.SorterLength;
                double seg = Dist(s.X, s.Y, s.X2, s.Y2);
                if (lenP != (int)Math.Round(seg - 0.3)) R.Fail("sorter length param " + lenP + " vs segment " + F(seg) + at);
                int maxL = 3;
                MachineDef sd = gd != null ? gd.Machine(s.ItemId) : null;
                if (sd != null && sd.SorterMaxLength > 0) maxL = Math.Min(maxL, sd.SorterMaxLength);
                if (lenP < 1 || lenP > maxL || seg > 3.499) R.Fail("sorter length " + lenP + " (segment " + F(seg) + ") outside 1.." + maxL + at);
                // game paste limits for a sorter with exactly one belt end (conditions.md F7-F10), metres over the E-W
                // tile factor range up to 60 deg latitude
                {
                    double ax = Math.Abs(s.X2 - s.X) * GameData.MetersPerTile, ay = Math.Abs(s.Y2 - s.Y) * GameData.MetersPerTile, dh = Math.Abs(s.Z2 - s.Z) * GameData.BeltLevelM;
                    double lmin = Math.Sqrt(Math.Pow(ax * GameData.EwFactorMin, 2) + ay * ay + dh * dh), lmax = Math.Sqrt(Math.Pow(ax * GameData.EwFactorMax, 2) + ay * ay + dh * dh);
                    if (lmin < GameData.SorterOneBeltMinM) R.Fail("sorter " + F(lmin) + " m < " + F(GameData.SorterOneBeltMinM) + " m (game TooClose, F7)" + at);
                    if (lmax > GameData.SorterOneBeltMaxM) R.Fail("sorter " + F(lmax) + " m > " + F(GameData.SorterOneBeltMaxM) + " m (game TooFar, F7)" + at);
                    if (seg > GameData.SorterOneBeltMaxCells) R.Fail("sorter spans " + F(seg) + " cells > " + F(GameData.SorterOneBeltMaxCells) + " (game TooFar, F8)" + at);
                    double span = Math.Sqrt(seg * seg + Math.Pow(dh / 0.2, 2));
                    if (span < GameData.SorterOneBeltMinSpan) R.Fail("sorter span " + F(span) + " < " + F(GameData.SorterOneBeltMinSpan) + " (game TooClose, F9)" + at);
                    if (YawDiff(s.Yaw, s.Yaw2) > GameData.SorterMaxTwistDeg) R.Fail("sorter end rotations differ by " + F(YawDiff(s.Yaw, s.Yaw2)) + " deg > 30 (game TooSkew, F10)" + at);
                }

                // machine slot from geometry, belt side -1, one sorter per machine slot
                int slot = toMachine ? s.OutputToSlot : s.InputFromSlot;
                int[] msz = fp.Size(mach.ItemId);
                int wantS;
                if (msz[0] == 3 && msz[1] == 3)
                {
                    int want = SlotAt(mach, s, toMachine);
                    if (slot != want) R.Fail("sorter slot " + slot + " but geometry says " + want + at);
                }
                else if (SlotRows.SlotFor(mach, s, toMachine, out wantS))   // feat-labs: lab / collider slot tables
                {
                    if (slot != wantS) R.Fail("sorter slot " + slot + " but the " + mach.ItemId + " slot table says " + wantS + at);
                }
                else R.Warn("slot table unknown for machine " + mach.ItemId + " (not 3x3), slot " + slot + " not checked" + at);
                if (slot < 0 || slot > 11) R.Fail("sorter machine slot " + slot + " outside 0..11" + at);
                if ((toMachine ? s.InputFromSlot : s.OutputToSlot) != -1) R.Fail("sorter belt-side slot not -1" + at);
                if (!usedSlots.Add(mach.GetHashCode() + ":" + slot + ":" + At(mach.X, mach.Y))) R.Fail("two sorters on slot " + slot + " of machine at " + At(mach.X, mach.Y));

                // belt offset floor(20*d), d = belt-side end displacement along the belt flow. Clamped at 0: in coil-360-v3
                // the assembler cells have d = -0.158 with offset 0 and the smelter cells d = +0.203 with offset 4 (inferred rule).
                double bx = toMachine ? s.X : s.X2, by = toMachine ? s.Y : s.Y2;
                Dir bf = FlowOf(belt);
                double dd = (bx - belt.X) * Dirs.Dx(bf) + (by - belt.Y) * Dirs.Dy(bf);
                int off = toMachine ? s.InputOffset : s.OutputOffset, wantOff = Math.Max(0, (int)Math.Floor(20 * dd + 1e-3));
                if (off != wantOff) R.Warn("sorter belt offset " + off + " but geometry gives max(0, floor(20*" + F(dd) + ")) = " + wantOff + at);

                // yaw points from start to end
                double vx = s.X2 - s.X, vy = s.Y2 - s.Y;
                double dirYaw = Dirs.Norm(Math.Atan2(vx, vy) * 180.0 / Math.PI);
                if (YawDiff(s.Yaw, dirYaw) > 5) R.Fail("sorter yaw " + F(s.Yaw) + " does not match its direction " + F(dirYaw) + at);

                // does not pass through a third building
                foreach (Bld m in obstacles)
                {
                    if (m == i || m == o) continue;
                    double[] h = Half(m);
                    for (int k = 1; k < 10; k++)
                    {
                        double px = s.X + vx * k / 10.0, py = s.Y + vy * k / 10.0;
                        if (Math.Abs(px - m.X) < h[0] - 0.05 && Math.Abs(py - m.Y) < h[1] - 0.05) { R.Fail("sorter passes through " + m + at); break; }
                    }
                }

                // user rules 1-3: input lanes adjacent (L1) or directly behind an L1 lane of the same machine (L2)
                if (toMachine)
                {
                    int dist; Dir side;
                    bool straight = SideOf(mach, belt, out side, out dist);
                    if (!straight) R.Fail("rule 1: input belt " + At(belt.X, belt.Y) + " is diagonal to its machine" + at);
                    else if (lenP == 1 && dist != 1) R.Fail("rule 1: L1 input belt not adjacent to the machine (distance " + dist + ")" + at);
                    else if (lenP == 2)
                    {
                        Bld front = dist == 2 ? BeltAtTile(belt.TileX - Dirs.Dx(side), belt.TileY - Dirs.Dy(side)) : null;
                        // the L1 lane may end one pickup earlier than the L2 lane behind it (lanes end at their last
                        // pickup, no stub): then the L1 lane of the same machine must still run in that column
                        if (front == null && dist == 2)
                        {
                            int fx = belt.TileX - Dirs.Dx(side), fy = belt.TileY - Dirs.Dy(side);
                            foreach (Bld x in sorters)
                                if (x.Output == mach && x.Input != null && x.Input.Kind == Kind.Belt && x.SorterLength == 1
                                    && ((side == Dir.E || side == Dir.W) ? x.Input.TileX == fx : x.Input.TileY == fy)) { front = x.Input; break; }
                        }
                        if (front == null) R.Fail("rule 2: L2 input lane is not directly behind a belt lane" + at);
                        else
                        {
                            string fl = Lane(front);
                            bool l1 = sorters.Any(x => x.Output == mach && x.Input != null && x.Input.Kind == Kind.Belt && x.SorterLength == 1 && Lane(x.Input) == fl);
                            if (!l1) R.Fail("rule 2: L2 input lane is not behind an L1 input lane of the same machine" + at);
                        }
                    }
                    else if (lenP >= 3) R.Fail("rules 1-2: input sorter length " + lenP + " (inputs are L1, extra lanes L2)" + at);
                }
                string rk = (toMachine ? "IN " : "OUT ") + mach.ItemId + " L" + lenP;
                int c; roles.TryGetValue(rk, out c); roles[rk] = c + 1;
            }
            foreach (var kv in roles) R.Note("sorters " + kv.Key + ": " + kv.Value);

            // rule 1: at most one adjacent input lane per machine side; 2 L1 lanes -> one on each side
            foreach (Bld m in machines)
            {
                var sideLanes = new Dictionary<Dir, HashSet<string>>();
                foreach (Bld s in Peek(inSorters, m))
                {
                    int dist; Dir side;
                    if (s.SorterLength != 1 || !SideOf(m, s.Input, out side, out dist)) continue;
                    if (!sideLanes.ContainsKey(side)) sideLanes[side] = new HashSet<string>();
                    sideLanes[side].Add(Lane(s.Input));
                }
                foreach (var kv in sideLanes)
                    if (kv.Value.Count > 1) R.Fail("rule 1: " + kv.Value.Count + " L1 input lanes on side " + kv.Key + " of machine at " + At(m.X, m.Y));
            }
        }

        /// Sorter between a belt and a depot (cell DEPOT_BUFFER, refs\engine-turbine-60-v2-userupgrade.txt #491-#493):
        /// both ends on their buildings, length rule, game paste limits, depot slot by geometry (3x3 slot table, yaw 0),
        /// one sorter per slot, yaw = direction, belt offset floor(20 d - 0.001) as the game wrote it (4 / -1 / -5).
        void StoreSorter(Bld s, bool toStore, HashSet<string> usedSlots, SortedDictionary<string, int> roles, string at)
        {
            Bld i = s.Input, o = s.Output, store = toStore ? o : i, belt = toStore ? i : o;
            if (!Inside(i, s.X, s.Y, 0.05)) R.Fail("sorter start not on its input " + i + at);
            if (!Inside(o, s.X2, s.Y2, 0.05)) R.Fail("sorter end not on its output " + o + at);
            double seg = Dist(s.X, s.Y, s.X2, s.Y2);
            if (s.SorterLength != (int)Math.Round(seg - 0.3) || s.SorterLength < 1 || s.SorterLength > 3) R.Fail("depot sorter length param " + s.SorterLength + " vs segment " + F(seg) + at);
            double ax = Math.Abs(s.X2 - s.X) * GameData.MetersPerTile, ay = Math.Abs(s.Y2 - s.Y) * GameData.MetersPerTile;
            double lmin = Math.Sqrt(Math.Pow(ax * GameData.EwFactorMin, 2) + ay * ay), lmax = Math.Sqrt(Math.Pow(ax * GameData.EwFactorMax, 2) + ay * ay);
            if (lmin < GameData.SorterOneBeltMinM || lmax > GameData.SorterOneBeltMaxM || seg > GameData.SorterOneBeltMaxCells) R.Fail("depot sorter outside the game paste limits (F7/F8)" + at);
            if (Math.Abs(Dirs.Norm(store.Yaw + 0.5) - 0.5) > 1.0) R.Fail("depot at " + At(store.X, store.Y) + " has yaw " + F(store.Yaw) + " (Depot Mk.I does not rotate, yaw 0)");
            int slot = toStore ? s.OutputToSlot : s.InputFromSlot;
            int want = SlotAt(store, s, toStore);
            if (slot != want) R.Fail("depot sorter slot " + slot + " but geometry says " + want + at);
            if ((toStore ? s.InputFromSlot : s.OutputToSlot) != -1) R.Fail("sorter belt-side slot not -1" + at);
            if (!usedSlots.Add(store.GetHashCode() + ":" + slot + ":" + At(store.X, store.Y))) R.Fail("two sorters on slot " + slot + " of depot at " + At(store.X, store.Y));
            double vx = s.X2 - s.X, vy = s.Y2 - s.Y;
            if (YawDiff(s.Yaw, Dirs.Norm(Math.Atan2(vx, vy) * 180.0 / Math.PI)) > 5) R.Fail("sorter yaw " + F(s.Yaw) + " does not match its direction" + at);
            double bx = toStore ? s.X : s.X2, by = toStore ? s.Y : s.Y2;
            Dir bf = FlowOf(belt);
            double dd = (bx - belt.X) * Dirs.Dx(bf) + (by - belt.Y) * Dirs.Dy(bf);
            int off = toStore ? s.InputOffset : s.OutputOffset, wantOff = (int)Math.Floor(20 * dd - 1e-3);
            if (off != wantOff) R.Warn("depot sorter belt offset " + off + " but the game cell gives floor(20*" + F(dd) + " - 0.001) = " + wantOff + at);
            string rk = (toStore ? "IN " : "OUT ") + store.ItemId + " L" + s.SorterLength;
            int c; roles.TryGetValue(rk, out c); roles[rk] = c + 1;
        }

        /// Side of the machine footprint a belt tile lies on and its distance from the footprint edge (1 = adjacent).
        bool SideOf(Bld m, Bld belt, out Dir side, out int dist)
        {
            Rect r = fp.TileRect(m);
            int x = belt.TileX, y = belt.TileY;
            side = Dir.N; dist = 0;
            if (x >= r.X0 && x <= r.X1)
            {
                if (y > r.Y1) { side = Dir.N; dist = y - r.Y1; return true; }
                if (y < r.Y0) { side = Dir.S; dist = r.Y0 - y; return true; }
                return false;
            }
            if (y >= r.Y0 && y <= r.Y1)
            {
                if (x > r.X1) { side = Dir.E; dist = x - r.X1; return true; }
                side = Dir.W; dist = r.X0 - x; return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 5. recipes vs game data and plan
        void Recipes()
        {
            foreach (Bld m in machines)
            {
                string at = " at " + At(m.X, m.Y);
                if (m.RecipeId == 0) { R.Fail("machine " + m.ItemId + " without recipe" + at); continue; }
                RecipeDef rd = gd != null ? gd.Recipe(m.RecipeId) : null;
                if (rd == null) { R.Fail("unknown recipe " + m.RecipeId + at); continue; }
                if (!rd.Buildings.Contains(m.ItemId)) R.Fail("machine " + m.ItemId + " cannot run recipe " + rd.Name + " (" + rd.Id + ")" + at);
                if (plan == null) continue;
                Step st = plan.Steps.FirstOrDefault(x => x.Recipe != null && x.Recipe.Id == m.RecipeId && x.MachineItem == m.ItemId)
                          ?? plan.Steps.FirstOrDefault(x => x.Recipe != null && x.Recipe.Id == m.RecipeId);
                if (st == null) { R.Fail("machine recipe " + rd.Name + " (" + rd.Id + ") is not in the plan" + at); continue; }
                if (st.MachineItem != m.ItemId) R.Fail("plan wants machine " + st.MachineItem + " for " + rd.Name + ", layout has " + m.ItemId + at);
                stepOf[m] = st;
                int c; placed.TryGetValue(st, out c); placed[st] = c + 1;
            }
            if (plan == null) { R.Warn("no plan: flow/rate checks use full machine speed"); return; }
            foreach (Step st in plan.Steps)
            {
                int c; placed.TryGetValue(st, out c);
                string nm = st.Recipe != null ? st.Recipe.Name : N(st.Item);
                if (c < st.Machines) R.Fail("plan needs " + st.Machines + " machines for " + nm + ", layout has " + c);
                else if (c > st.Machines) R.Warn("layout has " + c + " machines for " + nm + ", plan needs " + st.Machines);
                if (c == 0 || st.Recipe == null) continue;
                // 6. capacity of the placed machines vs what the plan needs from the step
                double speed = st.Speed > 0 ? st.Speed : (gd != null ? gd.Speed(st.MachineItem) : 1);
                foreach (ItemRate o in st.Outputs)
                {
                    double cap = c * st.Recipe.OutCount(o.Item) * 60.0 * speed / st.Recipe.TimeSec;
                    if (cap + Eps < o.Rate) R.Fail("supply below demand: " + c + " machines make " + F(cap) + "/min of " + N(o.Item) + ", plan needs " + F(o.Rate));
                }
            }
        }

        double MachineSpeed(Bld m)
        {
            Step st; if (stepOf.TryGetValue(m, out st) && st.Speed > 0) return st.Speed;
            return gd != null ? gd.Speed(m.ItemId) : 1;
        }
        /// Items/min one machine (a lab tower base: its whole tower, feat-labs) must move for the item.
        double MachineRate(Bld m, int item, bool output) { return MachineRate1(m, item, output) * TowerSize(m); }
        double FullRate(Bld m, int item, bool output) { return FullRate1(m, item, output) * TowerSize(m); }

        /// Items/min one machine must move for the item: plan rate shared over the placed machines, else full speed.
        double MachineRate1(Bld m, int item, bool output)
        {
            RecipeDef rd = gd != null ? gd.Recipe(m.RecipeId) : null;
            if (rd == null) return 0;
            // per-row load set by the Composer/Kit: the same formula as the row's port rates (row rate = sum of its
            // machines' rates), so a small extra row (e.g. 1 smelter for gear 45/min) is not judged at the step average
            if (m.Load > 0) return FullRate1(m, item, output) * m.Load;
            Step st; int c;
            if (stepOf.TryGetValue(m, out st) && placed.TryGetValue(st, out c) && c > 0)
            {
                ItemRate ir = (output ? st.Outputs : st.Inputs).FirstOrDefault(x => x.Item == item);
                if (ir != null) return ir.Rate / c;
            }
            return FullRate1(m, item, output);
        }
        double FullRate1(Bld m, int item, bool output)
        {
            RecipeDef rd = gd != null ? gd.Recipe(m.RecipeId) : null;
            if (rd == null || rd.TimeSec <= 0) return 0;
            return (output ? rd.OutCount(item) : rd.InCount(item)) * 60.0 * MachineSpeed(m) / rd.TimeSec;
        }
        IEnumerable<int> DropItems(Bld s)
        {
            RecipeDef rd = gd != null && s.Input != null ? gd.Recipe(s.Input.RecipeId) : null;
            if (rd == null) yield break;
            foreach (ItemCount o in rd.Outputs) if (s.FilterId == 0 || s.FilterId == o.Item) yield return o.Item;
        }
        Port InPortAt(Bld belt) { return L.Ports.FirstOrDefault(p => p.Kind == PortKind.In && p.Belt == belt); }
        double PortRate(Port p)
        {
            if (p.Rate > 0) return p.Rate;
            if (plan != null) { ItemRate r = plan.RawInputs.FirstOrDefault(x => x.Item == p.Item); if (r != null) return r.Rate; }
            return 0;
        }

        // ---------------------------------------------------------------- 4. flow semantics (items per lane)
        void Items()
        {
            if (!beltGraphOk) { R.Fail("flow checks skipped: belt graph is broken"); return; }
            foreach (Bld n in topo)
            {
                var set = new HashSet<int>();
                if (n.Kind == Kind.Splitter) { foreach (Bld p in Peek(preds, n)) set.UnionWith(items[p]); items[n] = set; continue; }
                foreach (Bld p in Peek(preds, n)) if (items.ContainsKey(p)) set.UnionWith(items[p]);
                if (n.Input != null && n.Input.Kind == Kind.Splitter && items.ContainsKey(n.Input)) set.UnionWith(items[n.Input]);
                Port ip = InPortAt(n); if (ip != null) set.Add(ip.Item);
                foreach (Bld s in Peek(dropsAt, n)) set.UnionWith(DropItems(s));
                items[n] = set;
            }
            // machine inputs: every recipe input on some input sorter's lane; every sorter on a lane with a recipe input
            foreach (Bld m in machines)
            {
                RecipeDef rd = gd != null ? gd.Recipe(m.RecipeId) : null;
                if (rd == null || upperLabs.Contains(m)) continue;   // feat-labs: upper levels are fed by the stack
                string at = " at " + At(m.X, m.Y);
                var ins = Peek(inSorters, m);
                foreach (ItemCount need in rd.Inputs)
                    if (!ins.Any(s => ItemsOn(s.Input).Contains(need.Item) && (s.FilterId == 0 || s.FilterId == need.Item)))
                        R.Fail("machine " + rd.Name + at + " gets no " + N(need.Item));
                foreach (Bld s in ins)
                {
                    var can = rd.Inputs.Select(x => x.Item).Where(x => ItemsOn(s.Input).Contains(x) && (s.FilterId == 0 || s.FilterId == x)).ToList();
                    if (can.Count == 0) { R.Fail("input sorter of " + rd.Name + at + " sits on lane " + Lane(s.Input) + " carrying " + Items(ItemsOn(s.Input)) + ", recipe needs " + Items(rd.Inputs.Select(x => x.Item))); continue; }
                    if (s.Carries != 0 && !can.Contains(s.Carries)) R.Fail("input sorter of " + rd.Name + at + " is meant for " + N(s.Carries) + " but its lane carries " + Items(ItemsOn(s.Input)));
                    pickItem[s] = s.Carries != 0 && can.Contains(s.Carries) ? s.Carries : can[0];
                }
                if (rd.Outputs.Count > 0 && Peek(outSorters, m).Count == 0) R.Fail("machine " + rd.Name + at + " has no output sorter");
            }
            // every produced item reaches a consumer or a block output port; every raw input reaches a consumer
            foreach (Bld s in sorters.Where(x => x.Input != null && x.Input.Kind == Kind.Machine && x.Output != null && x.Output.Kind == Kind.Belt))
                foreach (int it in DropItems(s))
                    if (!Reaches(s.Output, it)) R.Fail("output " + N(it) + " of machine at " + At(s.Input.X, s.Input.Y) + " reaches neither a consumer nor a block output port");
            foreach (Port p in L.Ports.Where(x => x.Kind == PortKind.In && x.Belt != null && items.ContainsKey(x.Belt) && !Coaters.IsFeedPort(x)))   // COATERS HOOK: feeds coaters, not machines
                if (!Reaches(p.Belt, p.Item)) R.Fail("block input " + N(p.Item) + " at (" + p.X + "," + p.Y + ") reaches no consumer");
            // belt metadata agrees with the flow
            int bad = 0;
            foreach (Bld b in belts)
            {
                HashSet<int> set;
                if (b.Carries == 0 || !items.TryGetValue(b, out set)) continue;
                if (set.Count > 0 && !set.Contains(b.Carries) && bad++ < 10) R.Fail("belt marked " + N(b.Carries) + " but flow brings " + Items(set) + ": " + b);
                else if (set.Count > 1 && bad++ < 10) R.Warn("mixed lane " + Lane(b) + " carries " + Items(set) + " at " + At(b.X, b.Y));
            }
        }

        HashSet<int> ItemsOn(Bld b) { HashSet<int> s; return b != null && items.TryGetValue(b, out s) ? s : new HashSet<int>(); }
        string Items(IEnumerable<int> set) { var l = set.Select(x => N(x)).ToList(); return l.Count == 0 ? "nothing" : string.Join(" + ", l.ToArray()); }

        bool Reaches(Bld start, int item)
        {
            var seen = new HashSet<Bld>();
            var st = new Stack<Bld>(); st.Push(start);
            while (st.Count > 0)
            {
                Bld n = st.Pop();
                if (!seen.Add(n)) continue;
                if (n.Kind == Kind.Belt)
                {
                    foreach (Bld s in Peek(picksAt, n))
                    {
                        RecipeDef rd = gd != null ? gd.Recipe(s.Output.RecipeId) : null;
                        if (rd != null && rd.InCount(item) > 0 && (s.FilterId == 0 || s.FilterId == item)) return true;
                    }
                    foreach (Port p in L.Ports) if (p.Kind == PortKind.Out && p.Belt == n && p.Item == item) return true;
                }
                foreach (Bld x in Next(n)) st.Push(x);
            }
            return false;
        }

        // ---------------------------------------------------------------- 6. rates along the lanes
        void Flow()
        {
            if (!beltGraphOk || topo == null) return;
            var flow = new Dictionary<Bld, Dictionary<int, double>>();
            var sorterRate = new Dictionary<Bld, double>();
            // input sorters: machine demand per item shared over the sorters that take it
            foreach (Bld m in machines)
            {
                RecipeDef rd = gd != null ? gd.Recipe(m.RecipeId) : null;
                if (rd == null) continue;
                foreach (ItemCount need in rd.Inputs)
                {
                    var ss = Peek(inSorters, m).Where(s => pickItem.ContainsKey(s) && pickItem[s] == need.Item).ToList();
                    foreach (Bld s in ss) { sorterRate[s] = MachineRate(m, need.Item, false) / ss.Count; CheckSorterRate(s, FullRate(m, need.Item, false) / ss.Count); }
                }
                var outs = Peek(outSorters, m);
                foreach (ItemCount o in rd.Outputs)
                {
                    var ss = outs.Where(s => s.FilterId == 0 || s.FilterId == o.Item).ToList();
                    foreach (Bld s in ss)
                    {
                        double r; sorterRate.TryGetValue(s, out r);
                        sorterRate[s] = r + MachineRate(m, o.Item, true) / ss.Count;
                    }
                }
                foreach (Bld s in outs)
                {
                    double full = rd.Outputs.Where(o => s.FilterId == 0 || s.FilterId == o.Item).Sum(o => FullRate(m, o.Item, true) / Math.Max(1, outs.Count(x => x.FilterId == 0 || x.FilterId == o.Item)));
                    CheckSorterRate(s, full);
                }
            }
            // downstream demand per node and item (reverse topological order): what the pickups after a node take plus
            // the block output ports. A splitter hands each output what lies behind it: when one output backs up, the
            // game's splitter sends the items to the others, so the steady state follows the demand (supply split in
            // proportion to the downstream demand; evenly when nothing downstream asks for the item).
            var demand = new Dictionary<Bld, Dictionary<int, double>>();
            for (int ti = topo.Count - 1; ti >= 0; ti--)
            {
                Bld n = topo[ti];
                var d = new Dictionary<int, double>();
                Action<int, double> addD = (it, v) => { double o; d.TryGetValue(it, out o); d[it] = o + v; };
                foreach (Bld m in Next(n)) { Dictionary<int, double> dm; if (demand.TryGetValue(m, out dm)) foreach (var kv in dm) addD(kv.Key, kv.Value); }
                if (n.Kind == Kind.Belt)
                {
                    foreach (Bld s in Peek(picksAt, n)) { int it; double r; if (pickItem.TryGetValue(s, out it) && sorterRate.TryGetValue(s, out r)) addD(it, r); }
                    foreach (Port p in L.Ports) if (p.Kind == PortKind.Out && p.Belt == n) addD(p.Item, p.Rate > 0 ? p.Rate : 0);
                }
                demand[n] = d;
            }
            double maxBelt = 0; Bld maxAt = null;
            var shortage = new HashSet<int>();
            foreach (Bld n in topo)
            {
                var f = new Dictionary<int, double>();
                Action<Dictionary<int, double>, double> add = (src, k) => { foreach (var kv in src) { double v; f.TryGetValue(kv.Key, out v); f[kv.Key] = v + kv.Value * k; } };
                foreach (Bld p in Peek(preds, n)) if (flow.ContainsKey(p)) add(flow[p], 1);
                if (n.Kind == Kind.Splitter) { flow[n] = f; continue; }
                if (n.Input != null && n.Input.Kind == Kind.Splitter && flow.ContainsKey(n.Input))
                {
                    var outsS = Peek(splitOuts, n.Input);
                    foreach (var kv in flow[n.Input])
                    {
                        double tot = 0, mine = 0, v;
                        foreach (Bld o in outsS) { Dictionary<int, double> dm; if (demand.TryGetValue(o, out dm) && dm.TryGetValue(kv.Key, out v)) { tot += v; if (o == n) mine = v; } }
                        double share = tot > 1e-9 ? mine / tot : 1.0 / Math.Max(1, outsS.Count);
                        double cur; f.TryGetValue(kv.Key, out cur); f[kv.Key] = cur + kv.Value * share;
                    }
                }
                Port ip = InPortAt(n);
                if (ip != null) add(new Dictionary<int, double> { { ip.Item, PortRate(ip) } }, 1);
                foreach (Bld s in Peek(dropsAt, n))
                {
                    RecipeDef rd = gd.Recipe(s.Input.RecipeId);
                    if (rd == null) continue;
                    var outs = Peek(outSorters, s.Input);
                    foreach (ItemCount o in rd.Outputs)
                    {
                        if (s.FilterId != 0 && s.FilterId != o.Item) continue;
                        int share = Math.Max(1, outs.Count(x => x.FilterId == 0 || x.FilterId == o.Item));
                        add(new Dictionary<int, double> { { o.Item, MachineRate(s.Input, o.Item, true) / share } }, 1);
                    }
                }
                double total = f.Values.Sum();
                if (n.Kind == Kind.Belt) Loads[n] = total;   // lanes hook
                double cap = gd != null ? gd.BeltRate(n.ItemId) : 0;
                if (total > maxBelt) { maxBelt = total; maxAt = n; }
                if (cap > 0 && total > cap + Eps) R.Fail("lane " + Lane(n) + " carries " + F(total) + "/min at " + At(n.X, n.Y) + ", belt " + n.ItemId + " moves " + F(cap) + "/min");
                foreach (Bld s in Peek(picksAt, n))
                {
                    int it; double r;
                    if (!pickItem.TryGetValue(s, out it) || !sorterRate.TryGetValue(s, out r)) continue;
                    double v; f.TryGetValue(it, out v);
                    v -= r;
                    if (v < -1e-4 && shortage.Add(it)) R.Fail("lane " + Lane(n) + " runs short of " + N(it) + " by " + F(-v) + "/min at " + At(n.X, n.Y) + " (supply upstream below demand)");
                    f[it] = Math.Max(0, v);
                }
                flow[n] = f;
                if (n.Output == null && InPortAt(n) == null && !L.Ports.Any(p => p.Kind == PortKind.Out && p.Belt == n))
                {
                    var left = f.Where(kv => kv.Value > 1e-4).ToList();
                    if (left.Count > 0 && ItemsOn(n).Count > 1)
                        R.Warn("dead end of lane " + Lane(n) + " at " + At(n.X, n.Y) + " collects surplus of a mixed belt (" + string.Join(", ", left.Select(kv => N(kv.Key) + " " + F(kv.Value)).ToArray()) + "/min): it may clog");
                }
            }
            foreach (Port p in L.Ports.Where(x => x.Kind == PortKind.Out))
            {
                Dictionary<int, double> f;
                if (p.Belt == null || !flow.TryGetValue(p.Belt, out f)) continue;
                double v; f.TryGetValue(p.Item, out v);
                double want = p.Rate > 0 ? p.Rate : (plan != null && plan.TargetItem == p.Item ? plan.TargetRate : 0);
                if (v + 1e-4 < want) R.Fail("block output " + N(p.Item) + " delivers " + F(v) + "/min, needs " + F(want));
                R.Note("block output " + N(p.Item) + ": " + F(v) + "/min");
            }
            if (maxAt != null) R.Note("max lane load " + F(maxBelt) + "/min at " + At(maxAt.X, maxAt.Y) + " (" + Lane(maxAt) + ")");
            sorterRates = sorterRate;
        }
        Dictionary<Bld, double> sorterRates;

        void CheckSorterRate(Bld s, double fullSpeed)
        {
            // called before sorterRates is final for output sorters, so the planned rate is re-read below
            pendingSorterChecks.Add(new KeyValuePair<Bld, double>(s, fullSpeed));
        }
        readonly List<KeyValuePair<Bld, double>> pendingSorterChecks = new List<KeyValuePair<Bld, double>>();

        void SorterRates()
        {
            if (sorterRates == null || gd == null) return;
            int bad = 0;
            foreach (var kv in pendingSorterChecks)
            {
                Bld s = kv.Key;
                double r, cap = gd.SorterRate(s.ItemId, s.SorterLength);
                if (!sorterRates.TryGetValue(s, out r) || cap <= 0) continue;
                double had; Needs[s] = Math.Max(Needs.TryGetValue(s, out had) ? had : 0, Math.Max(r, kv.Value));   // lanes hook
                string at = " at " + At(s.X, s.Y) + "->" + At(s.X2, s.Y2);
                if (r > cap + Eps) { if (bad++ < 10) R.Fail("sorter " + s.ItemId + " L" + s.SorterLength + " must move " + F(r) + "/min, max " + F(cap) + at); }
                // output sorters vs the machine's full-speed output (bug fix 2026-10-31, user rule: a machine at 100%
                // must never be blocked by its output sorter; e.g. Mk.II assembler circuit 120/min vs L2 Mk.II 90/min):
                // the row needs an L1 output or a second output sorter
                else if (s.Input != null && s.Input.Kind == Kind.Machine && kv.Value > cap + Eps)
                { if (bad++ < 10) R.Fail("output sorter " + s.ItemId + " L" + s.SorterLength + " moves " + F(cap) + "/min, its machine makes " + F(kv.Value) + "/min at full speed (needs an L1 output or a second output sorter)" + at); }
                else if (kv.Value > cap + Eps) R.Warn("sorter " + s.ItemId + " L" + s.SorterLength + " limits its machine at full speed (" + F(kv.Value) + " > " + F(cap) + "/min)" + at);
            }
            if (bad > 10) R.Fail(bad + " sorter throughput errors in total");
        }

        // ---------------------------------------------------------------- 2./ports: chain starts and ends vs block ports
        void Ports()
        {
            SorterRates();
            var starts = belts.Where(b => Peek(preds, b).Count == 0 && !(b.Input != null && b.Input.Kind == Kind.Splitter)).ToList();
            var ends = belts.Where(b => b.Output == null).ToList();
            int nIn = 0, nOut = 0;
            foreach (Port p in L.Ports)
            {
                string w = p.Kind + " port " + N(p.Item) + " at (" + p.X + "," + p.Y + ")";
                if (p.Belt == null || !inLayout.Contains(p.Belt)) { R.Fail(w + " has no belt in the layout"); continue; }
                if (p.Belt.Kind != Kind.Belt) { R.Fail(w + " is not on a belt"); continue; }
                if (p.Belt.TileX != p.X || p.Belt.TileY != p.Y) R.Fail(w + " but its belt is at " + At(p.Belt.X, p.Belt.Y));
                if (FlowOf(p.Belt) != p.Dir) R.Warn(w + ": port dir " + p.Dir + ", belt flows " + FlowOf(p.Belt));
                if (p.Item <= 0) R.Fail(w + " has no item");
                if (p.Kind == PortKind.In) { nIn++; if (!starts.Contains(p.Belt)) R.Fail(w + " is not the first tile of a chain"); }
                else { nOut++; if (p.Belt.Output != null) R.Fail(w + " is not the last tile of a chain"); }
            }
            foreach (Bld b in starts)
            {
                if (InPortAt(b) != null) continue;
                bool fed = false;
                for (Bld n = b; n != null && n.Kind == Kind.Belt; n = n.Output)
                {
                    if (Peek(dropsAt, n).Count > 0) { fed = true; break; }
                    if (n.Output != null && n.OutputToSlot > 1) break;
                    if (n.Output != null && !inLayout.Contains(n.Output)) break;
                }
                if (!fed) R.Fail("chain start " + At(b.X, b.Y) + " (" + Lane(b) + ") has no block input port and no output sorter feeding it");
            }
            // input lanes end at the last consumer's pickup tile (user rule, proven by the user's re-saved
            // refs\engine-coil-360.txt): a dead end (not a block output) must be a pickup tile, no stub beyond it
            foreach (Bld b in ends)
            {
                if (splitterTouch.Contains(b) || L.Ports.Any(p => p.Kind == PortKind.Out && p.Belt == b)) continue;
                if (Peek(picksAt, b).Count > 0) continue;
                if (Coaters.IsFeed(b)) continue;   // COATERS HOOK: feed chain ends over its last coater
                int tail = 0; Bld n = b;
                while (n != null && Peek(picksAt, n).Count == 0 && Peek(dropsAt, n).Count == 0) { tail++; n = MainPred(n); }
                if (tail == 0) continue;   // ends on a drop tile: output flow checks report it
                R.Fail("input lane " + Lane(b) + " runs " + tail + " belt(s) beyond its last pickup, ending at " + At(b.X, b.Y)
                    + (n == null ? " (no sorter on the lane at all)" : ""));
            }
            if (plan != null)
            {
                foreach (ItemRate raw in plan.RawInputs)
                {
                    double have = L.Ports.Where(p => p.Kind == PortKind.In && p.Item == raw.Item).Sum(p => PortRate(p));
                    if (!L.Ports.Any(p => p.Kind == PortKind.In && p.Item == raw.Item)) R.Fail("raw input " + N(raw.Item) + " has no block input port");
                    else if (have + 1e-4 < raw.Rate) R.Fail("block input ports bring " + F(have) + "/min of " + N(raw.Item) + ", plan needs " + F(raw.Rate));
                }
                if (plan.TargetItem != 0 && !L.Ports.Any(p => p.Kind == PortKind.Out && p.Item == plan.TargetItem)) R.Fail("target " + N(plan.TargetItem) + " has no block output port");
            }
            R.Note("belts: " + belts.Count + " tiles, " + starts.Count + " chain starts, " + ends.Count + " chain ends; block ports " + nIn + " in, " + nOut + " out");
        }

        // ---------------------------------------------------------------- user rule 5: input-belt icons
        void Icons()
        {
            var iconBelts = new HashSet<Bld>();
            foreach (Port p in L.Ports)
            {
                if (p.Kind != PortKind.In || p.Belt == null) continue;
                if (p.Seam) continue;     // bus overlap tail: icon-less on purpose (only raw inputs carry icons)
                iconBelts.Add(p.Belt);
                int[] par = p.Belt.Parameters;
                if (par == null || par.Length != 2 || par[0] != p.Item || par[1] != 0)
                    R.Fail("rule 5: first tile (" + p.X + "," + p.Y + ") of input chain " + N(p.Item) + " has no icon [" + p.Item + ",0]");
            }
            foreach (Bld b in belts)
                if (!iconBelts.Contains(b) && b.Parameters != null && b.Parameters.Length > 0 && b.Parameters[0] != 0)
                    R.Warn("belt icon outside a block input start at " + At(b.X, b.Y));
            InputTails();
            InputGroups();
            foreach (string m in Lanes.InputEdgeErrors(L, spec)) R.Fail(m);   // lanes hook (PLAN.md decision 7)
        }

        /// User rule (2026-10-31 review): the icon belt of a block input is a TAIL - the connection point for joining
        /// blueprints - one belt before the first input sorter's pickup tile, never the pickup tile itself. The old
        /// coil-360-v3 has the same tail (iron ore (10,0) before pickup (10,1)).
        void InputTails()
        {
            foreach (Port p in L.Ports)
            {
                if (p.Kind != PortKind.In || p.Belt == null || !inLayout.Contains(p.Belt)) continue;
                if (Peek(picksAt, p.Belt).Count > 0)
                    R.Fail("input tail: icon belt " + At(p.Belt.X, p.Belt.Y) + " of " + N(p.Item) + " is a pickup tile; it must be a tail belt before the first pickup");
                else if (p.Belt.Output == null)
                    R.Fail("input tail: icon belt " + At(p.Belt.X, p.Belt.Y) + " of " + N(p.Item) + " leads nowhere");
            }
        }

        /// User rule (2026-10-31 review): block input lanes along the input edge are grouped by material - all lanes of
        /// one raw item adjacent, never interleaved (iron, copper, iron), so one belt + splitters can feed a group.
        void InputGroups()
        {
            var ins = L.Ports.Where(p => p.Kind == PortKind.In).ToList();
            if (ins.Count < 3) return;
            Dir edge = spec != null ? spec.InEdge : Dir.S;
            bool alongX = edge == Dir.S || edge == Dir.N;
            var seq = ins.OrderBy(p => alongX ? p.X : p.Y).ThenBy(p => alongX ? p.Y : p.X).Select(p => p.Item).ToList();
            var closed = new HashSet<int>();
            for (int i = 1; i < seq.Count; i++)
            {
                if (seq[i] == seq[i - 1]) continue;
                closed.Add(seq[i - 1]);
                if (closed.Contains(seq[i]))
                {
                    R.Fail("input groups: block input lanes interleave materials along the " + edge + " edge (" +
                        string.Join(", ", seq.Select(it => gd != null ? gd.ItemName(it) : it.ToString(CultureInfo.InvariantCulture)).ToArray()) + "); lanes of one raw item must be adjacent");
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- 7. power
        void Power()
        {
            // game limits (PowerSystem cover / connect distance, see GameData.GameTeslaCoverTiles); the game-proven
            // refs\engine-electric-motor-120-v3-user.txt reaches 8.1 tiles
            double cover = GameData.GameTeslaCoverTiles, link = GameData.GameTeslaLinkTiles;
            var powered = L.All.Where(b => b.Kind == Kind.Machine || b.Kind == Kind.Sorter || Coaters.IsCoater(b)).ToList();   // COATERS HOOK
            if (powered.Count == 0) return;
            if (towers.Count == 0) { R.Fail("no tesla tower for " + powered.Count + " powered buildings"); return; }
            double maxD = 0; int bad = 0;
            foreach (Bld b in powered)
                foreach (double[] pt in GameData.PowerPoints(b))
                {
                    double x = pt[0], y = pt[1];
                    double best = towers.Min(t => Dist(x, y, t.X, t.Y));
                    maxD = Math.Max(maxD, best);
                    if (best > cover + 1e-6 && bad++ < 10) R.Fail("no tesla tower within " + F(cover) + " tiles of " + b.Kind + " " + b.ItemId + " at " + At(x, y) + " (nearest " + F(best) + ")");
                }
            if (bad > 10) R.Fail(bad + " power coverage errors in total");
            // tower graph connected with links <= link
            var seen = new HashSet<Bld> { towers[0] };
            var q = new Queue<Bld>(); q.Enqueue(towers[0]);
            double maxLink = 0;
            while (q.Count > 0)
            {
                Bld t = q.Dequeue();
                foreach (Bld u in towers)
                    if (!seen.Contains(u) && Dist(t.X, t.Y, u.X, u.Y) <= link + 1e-6) { seen.Add(u); q.Enqueue(u); }
            }
            foreach (Bld t in towers) if (towers.Count > 1) maxLink = Math.Max(maxLink, towers.Where(u => u != t).Min(u => Dist(t.X, t.Y, u.X, u.Y)));
            // game PowerTooClose: power nodes (towers, generators) closer than the minimum (accumulator pairs exempt)
            var nodes = L.All.Where(b => b.Kind == Kind.Tesla || (gd != null && gd.IsPowerNode(b.ItemId))).Distinct().ToList();
            double minNode = double.MaxValue; int close = 0;
            for (int i = 0; i < nodes.Count; i++)
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    if (GameData.IsAccumulator(nodes[i].ItemId) && GameData.IsAccumulator(nodes[j].ItemId)) continue;
                    double d = Dist(nodes[i].X, nodes[i].Y, nodes[j].X, nodes[j].Y);
                    minNode = Math.Min(minNode, d);
                    // the game rule itself: 3.5 m, with E-W tiles as narrow as 0.8557 of the equator tile (60 deg)
                    double dm = Math.Sqrt(Math.Pow((nodes[i].X - nodes[j].X) * GameData.MetersPerTile * GameData.EwFactorMin, 2) + Math.Pow((nodes[i].Y - nodes[j].Y) * GameData.MetersPerTile, 2));
                    if (dm < GameData.GameMinPowerNodeM - 1e-6)
                        R.Fail("power nodes " + F(dm) + " m apart up to 60 deg latitude (game PowerTooClose 3.5 m, accumulators included): " + nodes[i] + " / " + nodes[j]);
                    if (d < GameData.MinPowerNodeTiles - 1e-6 && close++ < 10)
                        R.Fail("power nodes too close (game PowerTooClose, min " + F(GameData.MinPowerNodeTiles) + " tiles): " + nodes[i].ItemId + " at " +
                            At(nodes[i].X, nodes[i].Y) + " and " + nodes[j].ItemId + " at " + At(nodes[j].X, nodes[j].Y) + " = " + F(d));
                }
            if (close > 10) R.Fail(close + " power-node distance errors in total");
            if (nodes.Count > 1) R.Note("power: min power-node distance " + F(minNode));
            if (seen.Count != towers.Count) R.Fail("tesla network split: " + (towers.Count - seen.Count) + " of " + towers.Count + " towers farther than " + F(link) + " tiles from the rest");
            R.Note("power: " + towers.Count + " towers, max building distance " + F(maxD) + ", max nearest-tower link " + F(maxLink));
        }

        void Summary()
        {
            var byKind = L.All.GroupBy(b => b.Kind + " " + b.ItemId).OrderBy(g => g.Key).Select(g => g.Key + " x" + g.Count());
            R.Note("checked " + L.All.Count + " buildings: " + string.Join(", ", byKind.ToArray()));
        }
    }
}
}
