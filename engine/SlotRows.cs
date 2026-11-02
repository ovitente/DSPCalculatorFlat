// SlotRows.cs - machine rows for buildings that are not 3x3 smelter/assembler class: Matrix Lab towers (LabTowers.cs)
// and Miniature Particle Collider rows (Collider.cs). Same contract as Modules.MachineRow (lanes parallel to the row,
// Flow-N frame, ports "in:<item>" / "out:<item>", in1 L1 on side A, in2 L1 on side B, in3/in4 L2 behind them, output
// behind side B, else side A), but every sorter comes from the building's own cell table (sorter records of real
// blueprints, cited per cell) and the machine pitch is computed from the footprint and the collider pitch, never
// hard-coded. Slot tables: analysis\buildings\slots.md (game prefab SlotConfig). Pure model code, no game DLL.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{

/// Cell table of one non-3x3 building: sorter cells relative to the machine center at MachineYaw (the row's canonical
/// machine yaw in the Flow-N frame), in preference order. Side E = row side A, W = side B before mirroring.
public class SlotRowDef
{
    public int MachineItem;
    public string Short;                                    // "lab", "collider" (cell names)
    public int MachineYaw;                                  // canonical machine yaw in the Flow-N frame
    public readonly List<SorterGeo> Cells = new List<SorterGeo>();
    /// Raw source cells (blueprint numbers at the source machine yaw) for the host self-check.
    public readonly List<SorterGeo> Sources = new List<SorterGeo>();
    /// Local slot poses in tiles at yaw 0: face and (x, z) per slot index (analysis\buildings\slots.md section 2).
    public Dir[] SlotFace;
    public double[][] SlotPos;
    public bool Towers;                                     // Matrix Lab: machines stack into towers

    /// Adds a cell from a blueprint sorter record: numbers relative to the source machine (world frame at the source
    /// machine yaw), turned rigidly to MachineYaw (the game turns a pasted blueprint the same way; slot unchanged).
    public void Add(string sourceKey, string sourceFile, int machineIdx, int sorterIdx, double sourceYaw, bool toMachine,
                    int beltDx, int beltDy, double sx, double sy, double ex, double ey, int slot, int length, string note)
    {
        var g = new SorterGeo
        {
            MachineItem = MachineItem, MachineYaw = Dirs.Norm(sourceYaw), BeltToMachine = toMachine, BeltDx = beltDx, BeltDy = beltDy,
            Sx = sx, Sy = sy, Ex = ex, Ey = ey, Slot = slot, Length = length, Status = "game", SourceKey = sourceKey, SourceIndex = sorterIdx,
            Pattern = Short + " row", Note = note
        };
        g.Yaw = Dirs.Yaw(Dirs.FromYaw(Math.Atan2(ex - sx, ey - sy) * 180.0 / Math.PI));
        g.Source = sourceFile + " #" + sorterIdx + " (machine #" + machineIdx + ")";
        g.Name = Short + " y" + (int)Math.Round(g.MachineYaw) + " " + (toMachine ? "in" : "out") + " slot" + slot + " L" + length + " src#" + sorterIdx;
        Sources.Add(g);
        int q = (int)Math.Round(Dirs.Norm(MachineYaw - g.MachineYaw) / 90.0) % 4;
        SorterGeo c = g.Rotated(q);
        c.Name = Short + " " + (toMachine ? "in" : "out") + " " + c.Side + " L" + length + " slot" + slot + " (src#" + sorterIdx + ")";
        Cells.Add(c);
    }
}

public static class SlotRows
{
    /// True for the buildings this file lays out (Modules.MachineRow hands them over).
    public static bool Handles(int machineItem) { return Def(machineItem) != null; }

    public static SlotRowDef Def(int machineItem)
    {
        if (LabTowers.IsLab(machineItem)) return LabTowers.Def;
        if (Collider.IsCollider(machineItem)) return Collider.Def;
        return null;
    }

    /// Every cell table (host self-check of the source records).
    public static IEnumerable<SlotRowDef> Defs() { yield return LabTowers.Def; yield return Collider.Def; }

    /// Why a recipe on this building cannot be laid out (null = it can). Used by StepSupport.
    public static string WhyNot(GameData gd, RecipeDef r, int building)
    {
        SlotRowDef d = Def(building);
        if (d == null) return "no cell table";
        int n = r.Inputs.Count;
        if (n < 1 || n > 4) return n + " inputs (rows support 1-4)";
        string why;
        if (!CanAssign(d, n, out why)) return why;
        return null;
    }

    static bool CanAssign(SlotRowDef d, int n, out string why)
    {
        var used = new HashSet<int>();
        why = null;
        var roles = Roles(n);
        foreach (var r in roles)
        {
            if (r.Out) continue;
            SorterGeo g = d.Cells.FirstOrDefault(c => c.BeltToMachine && c.Side == r.Side && c.Length == r.L && !used.Contains(c.Slot));
            if (g == null) { why = "no proven " + d.Short + " input cell for " + r.Role + " (side " + r.Side + " L" + r.L + ")"; return false; }
            used.Add(g.Slot);
        }
        int lanesA = roles.Count(x => !x.Out && x.Side == Dir.E), lanesB = roles.Count(x => !x.Out && x.Side == Dir.W);
        foreach (var side in new[] { Dir.W, Dir.E })
        {
            int L = (side == Dir.W ? lanesB : lanesA) + 1;
            if (L > 3) continue;
            if (d.Cells.Any(c => !c.BeltToMachine && c.Side == side && c.Length == L && !used.Contains(c.Slot))) return true;
        }
        why = "no proven " + d.Short + " output cell behind the input lanes (" + n + " inputs)";
        return false;
    }

    class RoleReq { public string Role; public bool Out; public Dir Side; public int L; public int Item; public double PerMachine; public List<SorterGeo> Geos = new List<SorterGeo>(); }

    static List<RoleReq> Roles(int n)
    {
        var l = new List<RoleReq>();
        for (int i = 0; i < n; i++) l.Add(new RoleReq { Role = "in" + (i + 1), Side = i % 2 == 0 ? Dir.E : Dir.W, L = i < 2 ? 1 : 2 });
        return l;
    }

    // ------------------------------------------------------------------ row
    /// Row of labs (as towers) or colliders. Same fragment contract as Modules.MachineRow. Throws ArgumentException for a
    /// bad spec and InvalidOperationException when the cell table has no combination for the preset.
    public static Fragment Row(RowSpec spec)
    {
        SlotRowDef d = Def(spec.MachineItem);
        if (d == null) throw new ArgumentException("row " + spec.Name + ": machine " + spec.MachineItem + " has no slot-row cell table");
        if (spec.Count < 1) throw new ArgumentException("row " + spec.Name + ": Count must be >= 1");
        if (spec.InputItems == null || spec.InputItems.Length < 1 || spec.InputItems.Length > 4)
            throw new ArgumentException("row " + spec.Name + ": 1..4 input items required");
        if (spec.InputRates != null && spec.InputRates.Length != spec.InputItems.Length)
            throw new ArgumentException("row " + spec.Name + ": InputRates length differs from InputItems");
        if (spec.InputItems.Distinct().Count() != spec.InputItems.Length || spec.InputItems.Contains(spec.OutputItem))
            throw new ArgumentException("row " + spec.Name + ": duplicate item among inputs/output");
        if (spec.BeltItem == Ids.BeltMk3 || spec.SorterItem == Ids.SorterMk3)
            throw new ArgumentException("row " + spec.Name + ": Mk.III belt/sorter not allowed (user rule 4)");
        spec.Problems.Clear(); spec.Notes.Clear();
        GameData gd = spec.Data;
        Func<double, string> f = v => v.ToString("0.##", CultureInfo.InvariantCulture);

        int n = spec.InputItems.Length;
        var roles = Roles(n);
        RecipeDef rd = gd != null ? gd.Recipe(spec.RecipeId) : null;
        double speed = gd != null ? gd.Speed(spec.MachineItem) : 1;
        // per machine: the plan's share or the full-speed rate, whichever is higher (a machine at 100% is never starved
        // or blocked by its sorters, CONTRACT.md rule 17 / output rule)
        Func<int, bool, double, double> perMachine = (item, output, planRate) =>
        {
            double full = rd != null && rd.TimeSec > 0 ? (output ? rd.OutCount(item) : rd.InCount(item)) * 60.0 * speed / rd.TimeSec : 0;
            return Math.Max(full, planRate / spec.Count);
        };
        for (int i = 0; i < n; i++)
        {
            roles[i].Item = spec.InputItems[i];
            roles[i].PerMachine = perMachine(spec.InputItems[i], false, spec.InputRates != null ? spec.InputRates[i] : 0);
        }
        var outRole = new RoleReq { Role = "out", Out = true, Item = spec.OutputItem, PerMachine = perMachine(spec.OutputItem, true, spec.OutputRate) };
        Func<int, double> cap = L => gd != null ? gd.SorterRate(spec.SorterItem, L) : 0;
        if (gd == null) spec.Notes.Add("sorter throughput not checked (RowSpec.Data is null)");

        // ---- output side first (lengths), then the tower height (labs), then the cells
        int lanesA = roles.Count(x => x.Side == Dir.E), lanesB = roles.Count(x => x.Side == Dir.W);
        var outOptions = new List<KeyValuePair<Dir, int>>();
        foreach (var side in new[] { Dir.W, Dir.E })
        {
            int minL = (side == Dir.W ? lanesB : lanesA) + 1;
            int L = spec.OutputLength > 0 ? spec.OutputLength : minL;
            if (L < minL || L > 3) continue;
            outOptions.Add(new KeyValuePair<Dir, int>(side, L));
        }

        string why = "";
        int[] heights = null;
        foreach (var opt in outOptions)
        {
            outRole.Side = opt.Key; outRole.L = opt.Value;
            var all = new List<RoleReq>(roles); all.Add(outRole);
            // tower height: what ONE base sorter per input / output carries for the whole tower (PLAN.md decision 10)
            int h = 1;
            if (d.Towers)
            {
                h = Math.Max(1, spec.LabStackMax);
                string limit = "LabStackMax " + spec.LabStackMax;
                foreach (var r in all)
                {
                    double c = cap(r.L);
                    if (c <= 0 || r.PerMachine <= 0) continue;
                    int byS = (int)Math.Floor(c / r.PerMachine + 1e-9);
                    if (byS < h) { h = byS; limit = r.Role + " sorter " + spec.SorterItem + " L" + r.L + " " + f(c) + "/min for " + f(r.PerMachine) + "/min per lab"; }
                }
                if (h < 1)
                {
                    why += " output side " + opt.Key + ": one sorter cannot feed even one lab (" + limit + ");";
                    continue;
                }
                int towers = (spec.Count + h - 1) / h;
                heights = new int[towers];
                for (int k = 0; k < towers; k++) heights[k] = spec.Count / towers + (k < spec.Count % towers ? 1 : 0);
                spec.Notes.Add(towers + " tower(s) of " + string.Join("/", heights.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray())
                    + " labs, height cap " + h + " (" + limit + ")");
            }
            // cells: distinct slots per machine, every sorter of a role on one lane
            var used = new HashSet<int>();
            bool ok = true;
            foreach (var r in all) r.Geos.Clear();
            foreach (var r in all)
            {
                double per = r.PerMachine * h;
                double c = cap(r.L);
                int need = c > 0 ? Math.Max(1, (int)Math.Ceiling(per / c - 1e-9)) : 1;
                foreach (SorterGeo g in d.Cells)
                {
                    if (r.Geos.Count >= need) break;
                    if (g.BeltToMachine == r.Out || g.Side != r.Side || g.Length != r.L || used.Contains(g.Slot)) continue;
                    if (r.Geos.Count > 0 && g.BeltDx != r.Geos[0].BeltDx) continue;
                    r.Geos.Add(g); used.Add(g.Slot);
                }
                if (r.Geos.Count == 0) { ok = false; why += " output side " + opt.Key + ": no proven cell for " + r.Role + " (side " + r.Side + " L" + r.L + ");"; break; }
                if (r.Geos.Count < need)
                    spec.Problems.Add("row " + spec.Name + ": " + r.Role + " item " + r.Item + " needs " + f(per) + "/min per " + (d.Towers ? "tower" : "machine")
                        + ", " + r.Geos.Count + " sorter(s) " + spec.SorterItem + " L" + r.L + " carry " + f(c * r.Geos.Count) + "/min");
                else if (r.Geos.Count > 1)
                    spec.Notes.Add(r.Role + ": " + r.Geos.Count + " sorters per machine on one lane (slots " + string.Join("+", r.Geos.Select(g => g.Slot.ToString(CultureInfo.InvariantCulture)).ToArray())
                        + "): each cell is from a blueprint, the combination on one machine is not - needs an in-game test");
            }
            if (!ok) { spec.Problems.Clear(); spec.Notes.RemoveAll(x => x.Contains("tower(s) of")); continue; }
            roles = all;
            break;
        }
        if (roles.Count == n)   // output never placed
            throw new InvalidOperationException("row " + spec.Name + " (" + n + " inputs, machine " + spec.MachineItem + "):" + (why.Length > 0 ? why : " no output side left (rule 3: L <= 3)"));
        if (n >= 3) spec.Notes.Add(n + "-input " + d.Short + " preset: cells from one collider of the source blueprint, rule-2 layout");

        // ---- machine yaw (mirror = every cell turned 180 degrees around its machine: side A becomes west)
        int mq = spec.Mirror ? 2 : 0;
        int machineYaw = (int)Dirs.Norm(d.MachineYaw + 90 * mq);
        if (mq != 0) foreach (var r in roles) for (int i = 0; i < r.Geos.Count; i++) r.Geos[i] = r.Geos[i].Rotated(mq);

        // ---- pitch: >= the footprint length along the row (tile rect, so neighbours never share a tile) and >= the
        // collider pitch along the row's final axis (colliders.json)
        Bld probe = Bld.Machine(spec.MachineItem, 0, 0, machineYaw, spec.RecipeId);
        var fp = new Footprints(gd);
        Rect pr = fp.TileRect(probe);
        int rectLen = pr.IsEmpty ? 1 : pr.Height;
        int col = Modules.ColliderPitch(gd, spec.MachineItem, machineYaw + 90.0 * spec.FinalQuarters, spec.AlongEW, spec.Safe60);
        int pitch = Math.Max(rectLen, col);
        if (spec.Pitch > 0)
        {
            if (spec.Pitch < pitch) throw new ArgumentException("row " + spec.Name + ": pitch " + spec.Pitch + " < " + pitch + " (footprint " + rectLen + ", collider pitch " + col + ")");
            pitch = spec.Pitch;
        }
        int places = d.Towers ? heights.Length : spec.Count;

        // ---- build in the Flow-N frame (as Modules.MachineRow)
        var frag = new Fragment(spec.Name);
        int y0 = pr.IsEmpty ? -1 : pr.Y0, y1 = (places - 1) * pitch + (pr.IsEmpty ? 1 : pr.Y1);
        var lanes = new Dictionary<int, List<Bld>>();
        var laneStart = new Dictionary<int, int>();
        foreach (var r in roles)
        {
            int lx = r.Geos[0].BeltDx;
            if (lanes.ContainsKey(lx)) throw new InvalidOperationException("row " + spec.Name + ": two roles on lane x=" + lx);
            string chain = spec.Name + ":" + (r.Out ? "out:" : "in:") + r.Item;
            int yEnd = !r.Out ? (places - 1) * pitch + r.Geos.Max(g => g.BeltDy) : y1;
            int yStart = y0;
            if (!r.Out && spec.TailItems != null && spec.TailItems.Contains(r.Item)) yStart = Math.Min(y0, r.Geos.Min(g => g.BeltDy) - 1);
            laneStart[lx] = yStart;
            var tiles = new List<Bld>();
            for (int y = yStart; y <= yEnd; y++)
            {
                Bld b = frag.Add(Bld.Belt(spec.BeltItem, lx, y, 0, r.Item, chain));
                b.Role = spec.Name + " " + r.Role + " lane";
                if (tiles.Count > 0) Layout.Link(tiles[tiles.Count - 1], b, 1);
                tiles.Add(b);
            }
            Layout.Link(tiles[tiles.Count - 1], null, 0);
            if (!r.Out && spec.LaneIcons) tiles[0].Parameters = new[] { r.Item, 0 };
            lanes[lx] = tiles;
            frag.AddPort(new Port { Name = (r.Out ? "out:" : "in:") + r.Item, Kind = r.Out ? PortKind.Out : PortKind.In,
                X = lx, Y = r.Out ? y1 : yStart, Dir = Dir.N, Item = r.Item,
                Rate = !r.Out ? (spec.InputRates != null ? spec.InputRates[Array.IndexOf(spec.InputItems, r.Item)] : 0) : spec.OutputRate,
                Belt = r.Out ? tiles[tiles.Count - 1] : tiles[0], Chain = chain });
        }
        int label = 0;
        for (int k = 0; k < places; k++)
        {
            int my = k * pitch;
            Bld m = frag.Add(Bld.Machine(spec.MachineItem, 0, my, machineYaw, spec.RecipeId));
            m.Carries = spec.OutputItem; m.Role = spec.Name + (d.Towers ? " tower " : " machine ") + (k + 1);
            if (d.Towers) LabTowers.Stack(frag, m, heights[k]);
            label += d.Towers ? heights[k] : 1;
            foreach (var r in roles)
                foreach (var g in r.Geos)
                {
                    Bld belt = lanes[g.BeltDx][my + g.BeltDy - laneStart[g.BeltDx]];
                    Bld s = g.Make(m, belt, spec.SorterItem, r.Item);
                    s.Role = spec.Name + " " + r.Role + " L" + r.L + " (" + g.Name + ", " + g.Source + ")";
                    frag.Add(s);
                }
        }
        if (label != spec.Count) throw new InvalidOperationException("row " + spec.Name + ": placed " + label + " machines, expected " + spec.Count);
        spec.Notes.Add(d.Short + " pitch " + pitch + " (footprint " + rectLen + " tiles, collider pitch " + col + ")");

        int q = (int)spec.Flow;
        if (q != 0) frag.RotateCW(q, 0, 0);
        return frag;
    }

    // ------------------------------------------------------------------ footprint hook (Footprints.TileRect)
    /// Tile rect override for the buildings of this file: stacked labs occupy no tile, colliders use their exact
    /// (off-centre) collider boxes. false = not handled (default footprint).
    public static bool TileRect(GameData gd, Bld b, out Rect r)
    {
        r = Rect.Empty;
        if (b == null || b.Kind != Kind.Machine) return false;
        if (LabTowers.IsLab(b.ItemId) && LabTowers.IsUpper(b)) return true;
        if (Collider.IsCollider(b.ItemId)) { r = Collider.TileRect(gd, b); return true; }
        return false;
    }

    // ------------------------------------------------------------------ slot check hook (Validator)
    /// Machine slot of a sorter end on a building of this file: the slot on the face the sorter crosses (input: opposite
    /// of travel, output: along travel) whose lateral position is nearest (analysis\buildings\slots.md section 6 rule 1).
    /// false = not handled. slot = -1 when no slot of that face is within 0.6 tile.
    public static bool SlotFor(Bld m, Bld s, bool toMachine, out int slot)
    {
        slot = -1;
        SlotRowDef d = m != null ? Def(m.ItemId) : null;
        if (d == null || d.SlotFace == null) return false;
        double vx = s.X2 - s.X, vy = s.Y2 - s.Y;
        Dir travel = Math.Abs(vx) >= Math.Abs(vy) ? (vx >= 0 ? Dir.E : Dir.W) : (vy >= 0 ? Dir.N : Dir.S);
        Dir worldFace = toMachine ? Dirs.Opposite(travel) : travel;
        double wx = (toMachine ? s.X2 : s.X) - m.X, wy = (toMachine ? s.Y2 : s.Y) - m.Y;
        double a = m.Yaw * Math.PI / 180.0;
        double lx = wx * Math.Cos(a) - wy * Math.Sin(a), lz = wx * Math.Sin(a) + wy * Math.Cos(a);
        Dir face = Dirs.RotateCW(worldFace, -(int)Math.Round(Dirs.Norm(m.Yaw) / 90.0));
        double best = double.MaxValue;
        for (int i = 0; i < d.SlotFace.Length; i++)
        {
            if (d.SlotFace[i] != face) continue;
            bool ns = face == Dir.N || face == Dir.S;
            double lat = Math.Abs((ns ? lx : lz) - (ns ? d.SlotPos[i][0] : d.SlotPos[i][1]));
            if (lat < best && lat < 0.6) { best = lat; slot = i; }
        }
        return true;
    }
}
}
