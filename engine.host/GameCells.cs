// GameCells.cs - CLI-side prototype records (former lib\Cells.cs part 2): UnitCell / Cells load the proven building
// records from the user's template blueprints and blueprint-tools\refs through the game DLL (GameBp), self-check them
// against the engine's geometry tables (Geo, BeltGeo, SplitterGeo) and convert them into the engine's plain-value
// PrototypeCells (BpBuilding records) for DspFlat.Engine.Emitter. The user's blueprints are read, never copied.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DspFlat.Engine;

namespace DspFlat.Host
{
/// A named prototype record from a user blueprint (source file + building index), self-checked on load.
public class UnitCell
{
    public string Name, SourceFile;
    public int Index, ItemId;
    public double X, Y;              // expected position in the source (self-check)
    public object Proto;             // BlueprintBuilding (game type)
    public string Status;            // "game" (used in a block built in game), "template" (from a user template, not yet
                                     // used by a generated block)
    public string Note;
}

/// Prototype records for the Emitter (needs GameBp.Init). Replaces Check/CheckSorter of the old generators.
public class Cells
{
    public readonly List<string> Errors = new List<string>();
    public readonly Dictionary<string, UnitCell> ByName = new Dictionary<string, UnitCell>();
    public readonly List<string> Log = new List<string>();     // what was checked (for --selftest / reports)
    public string BlueprintDir;
    readonly Dictionary<string, Dictionary<int, object>> files = new Dictionary<string, Dictionary<int, object>>();
    readonly Dictionary<int, string> byItem = new Dictionary<int, string>();

    /// blueprint-tools folder (holds refs\); defaults to the exe folder. MakeBlock/SelfTest set it from --data-root.
    public static string ToolsRoot = AppDomain.CurrentDomain.BaseDirectory;

    /// Source path: "refs\x" -> ToolsRoot\refs\x, anything else relative to the user's Blueprint folder.
    public static string Resolve(string blueprintDir, string rel)
    {
        if (rel.StartsWith(@"refs\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(ToolsRoot ?? "", rel);
        return Path.Combine(blueprintDir ?? "", rel);
    }

    /// The loaded prototypes as engine values (BpBuilding copies of the game records), keyed like ByName / ProtoFor.
    public readonly PrototypeCells Prototypes = new PrototypeCells();

    /// Loads prototypes from blueprintDir (Documents\Dyson Sphere Program\Blueprint) and self-checks them.
    /// Never throws on a mismatch: problems go to Errors.
    public static Cells Load(string blueprintDir)
    {
        var c = new Cells();
        c.BlueprintDir = blueprintDir;
        if (!GameBp.Ready) { c.Errors.Add("Cells.Load: GameBp.Init was not called"); return c; }
        try { c.LoadAll(); }
        catch (Exception e) { c.Errors.Add("Cells.Load: " + (e.InnerException ?? e).Message); }
        c.Prototypes.Errors.AddRange(c.Errors);
        return c;
    }

    void LoadAll()
    {
        // name, item, source key, index, expected x, y, status, note
        Cell("smelter-arc", Ids.ArcSmelter, "arc1", 52, 2, 2, "game", null);
        Cell("sorter-mk2", Ids.SorterMk2, "arc1", 40, 4, 1.203, "game", null);
        Cell("belt-mk2", Ids.BeltMk2, "arc1", 64, 4, 1, "game", null);
        Cell("tesla", Ids.Tesla, "arc1", 39, 3, 0, "game", null);
        // depot buffer cell of the user's in-game edit (game-proven): depot #482 (model 51) + in sorter #491 + out #492/#493
        Cell("depot-mk1", Ids.DepotMk1, "turb2u", 482, 6, 40, "game", "Depot Mk.I, end-of-block buffer");
        Cell("depot-in", Ids.SorterMk2, "turb2u", 491, 5.203, 42, "game", "belt -> depot slot 0, L1");
        Cell("depot-out1", Ids.SorterMk2, "turb2u", 492, 6, 40.795, "game", "depot slot 1 -> belt, L1");
        Cell("depot-out2", Ids.SorterMk2, "turb2u", 493, 6.796, 40.795, "game", "depot slot 2 -> belt, L1");
        Cell("splitter-m39", Ids.Splitter, "turb2u", 279, 18, 35, "game", "Tab variant 39: N/S ports low + high");
        Cell("splitter-m40", Ids.Splitter, "turb2u", 280, 21, 35, "game", "Tab variant 40: E/W low, N/S high");
        Cell("asm-mk1", Ids.AsmMk1, "a8x1", 8, 2, 2, "game", "assemblers-8x1.txt = former assemblers-8.txt");
        Cell("asm-mk2", Ids.AsmMk2, "in2", 93, 3, 2, "template", "not yet used by a generated block");
        Cell("asm-mk3", Ids.AsmMk3, "mk3", 9, 4, 4, "template", "not yet used by a generated block");
        Cell("smelter-plane", Ids.PlaneSmelter, "plane", 8, 13, 2, "template", "not yet used by a generated block");
        Cell("belt-mk1", Ids.BeltMk1, "fuel", 0, 9, 2, "template", "not yet used by a generated block");
        Cell("sorter-mk1", Ids.SorterMk1, "fuel", 4, 2.958, 4.001, "template", "not yet used by a generated block");
        Cell("splitter", Ids.Splitter, "split", 0, 7, 0, "game", "params [0,0,0,0,0,0]");
        Cell("splitter-priority", Ids.Splitter, "split", 2, 7, 20, "game", "params [1,0,0,0,0,0]");
        Cell(SplitterGeo.BeltCell, Ids.BeltMk2, "split", 10, 7, 0.199, "game", "fractional belt next to a splitter");
        Cell("belt-icon", Ids.BeltMk2, "lent", 0, 2, 0, "game", "input-belt icon [1002,0] (rule 5)");
        // generic prototype per item id (first registration wins; splitter-belt/belt-icon/priority are named-only)
        foreach (string n in new[] { "smelter-arc", "sorter-mk2", "belt-mk2", "tesla", "depot-mk1", "asm-mk1", "asm-mk2", "asm-mk3",
                                     "smelter-plane", "belt-mk1", "sorter-mk1", "splitter" })
            if (ByName.ContainsKey(n) && !byItem.ContainsKey(ByName[n].ItemId)) byItem[ByName[n].ItemId] = n;

        SoftCoaterCell();

        foreach (var kv in ByName) Prototypes.Add(kv.Key, GameBp.ToRecord(kv.Value.Proto));
        foreach (var kv in byItem) Prototypes.SetDefault(kv.Key, kv.Value);

        CheckParams("splitter", new[] { 0, 0, 0, 0, 0, 0 });
        CheckParams("splitter-priority", new[] { 1, 0, 0, 0, 0, 0 });
        CheckParams("belt-icon", new[] { Ids.CopperOre, 0 });
        CheckSorterTable();
        CheckBeltAndSplitter();
    }

    /// Spray coater prototype (engine\Coaters.cs): the user's game-0.10.35 blueprint 7-logi\L1.txt, coater #36 at (3,7)
    /// (model 120, slots 14/15/15/14, no links). Optional: when the file is missing or differs, only layouts with coaters
    /// fail to emit ("no prototype for item 2313"); every other block keeps working.
    public const string CoaterSource = @"7-logi\L1.txt";
    public const int CoaterIndex = 36;
    void SoftCoaterCell()
    {
        string path = Resolve(BlueprintDir, CoaterSource);
        if (!File.Exists(path)) { Log.Add("spray coater prototype: " + path + " not found (coater layouts cannot be emitted)"); return; }
        try
        {
            Dictionary<int, object> d = GameBp.ByIndex(GameBp.Load(path));
            object r;
            if (!d.TryGetValue(CoaterIndex, out r) || GameBp.In(r, "itemId") != Coaters.SprayCoater
                || Math.Abs(GameBp.Fl(r, "localOffset_x") - 3) > 0.01 || Math.Abs(GameBp.Fl(r, "localOffset_y") - 7) > 0.01)
            { Log.Add("spray coater prototype: " + CoaterSource + " #" + CoaterIndex + " is not the coater at (3,7)"); return; }
            ByName["spray-coater"] = new UnitCell { Name = "spray-coater", SourceFile = CoaterSource, Index = CoaterIndex, ItemId = Coaters.SprayCoater, X = 3, Y = 7,
                Proto = r, Status = "template", Note = "spray coater, user's blueprint (game 0.10.35)" };
            byItem[Coaters.SprayCoater] = "spray-coater";
            Log.Add("cell spray-coater = " + CoaterSource + " #" + CoaterIndex + " item 2313 ok");
        }
        catch (Exception e) { Log.Add("spray coater prototype: cannot read " + path + ": " + (e.InnerException ?? e).Message); }
    }

    // ---- source access ----
    Dictionary<int, object> Source(string key)
    {
        Dictionary<int, object> d;
        if (files.TryGetValue(key, out d)) return d;
        d = null;
        string tried = "";
        foreach (string rel in Geo.SourceFiles[key].Split('|'))
        {
            string path = Resolve(BlueprintDir, rel);
            tried += (tried.Length > 0 ? ", " : "") + path;
            if (!File.Exists(path)) continue;
            try { d = GameBp.ByIndex(GameBp.Load(path)); Log.Add("loaded " + rel + " (" + d.Count + " buildings)"); }
            catch (Exception e) { Errors.Add("cannot read " + path + ": " + (e.InnerException ?? e).Message); }
            break;
        }
        if (d == null && tried.Length > 0 && !Errors.Exists(m => m.Contains(tried))) Errors.Add("source blueprint missing: " + tried);
        files[key] = d;
        return d;
    }

    object Record(string key, int idx, string what)
    {
        var d = Source(key);
        if (d == null) return null;
        object r;
        if (!d.TryGetValue(idx, out r)) { Errors.Add(what + ": " + key + " has no record #" + idx); return null; }
        return r;
    }

    void Cell(string name, int item, string key, int idx, double x, double y, string status, string note)
    {
        object r = Record(key, idx, "cell " + name);
        if (r == null) return;
        int it = GameBp.In(r, "itemId");
        float rx = GameBp.Fl(r, "localOffset_x"), ry = GameBp.Fl(r, "localOffset_y");
        if (it != item || Math.Abs(rx - x) > 0.01 || Math.Abs(ry - y) > 0.01)
        {
            Errors.Add("prototype " + name + " mismatch: " + key + " #" + idx + " is item " + it + " at " + F(rx) + "," + F(ry)
                + ", expected " + item + " at " + F(x) + "," + F(y));
            return;
        }
        ByName[name] = new UnitCell { Name = name, SourceFile = Geo.SourceFiles[key].Split('|')[0], Index = idx, ItemId = item, X = x, Y = y,
            Proto = r, Status = status, Note = note };
        Log.Add("cell " + name + " = " + key + " #" + idx + " item " + item + " ok");
    }

    void CheckParams(string name, int[] want)
    {
        UnitCell u;
        if (!ByName.TryGetValue(name, out u)) return;
        int[] p = (int[])GameBp.Get(u.Proto, "parameters");
        if (p == null || p.Length < want.Length) { Errors.Add("cell " + name + ": parameters missing"); return; }
        for (int i = 0; i < want.Length; i++)
            if (p[i] != want[i]) { Errors.Add("cell " + name + ": parameters[" + i + "] = " + p[i] + ", expected " + want[i]); return; }
    }

    /// Re-reads the source record of every checkable sorter cell and compares it with the Geo table.
    void CheckSorterTable()
    {
        int ok = 0;
        foreach (SorterGeo g in Geo.All())
        {
            if (g.SourceKey == null) continue;
            object s = Record(g.SourceKey, g.SourceIndex, "sorter cell " + g.Name);
            if (s == null) continue;
            string bad = CompareSorter(g, s);
            if (bad != null) Errors.Add("sorter cell " + g.Name + " (" + g.Source + "): " + bad); else ok++;
        }
        Log.Add("sorter cells checked against their blueprints: " + ok + " ok");
    }

    static string CompareSorter(SorterGeo g, object s)
    {
        if (!Ids.IsSorter(GameBp.In(s, "itemId"))) return "record is item " + GameBp.In(s, "itemId") + ", not a sorter";
        object i = GameBp.Get(s, "inputObj"), o = GameBp.Get(s, "outputObj");
        if (i == null || o == null) return "record has no links";
        bool toM = Ids.IsBelt(GameBp.In(i, "itemId"));
        object m = toM ? o : i, b = toM ? i : o;
        if (toM != g.BeltToMachine) return "direction differs";
        if (GameBp.In(m, "itemId") != g.MachineItem) return "machine item " + GameBp.In(m, "itemId");
        double myaw = GameBp.Fl(m, "yaw");
        if (Math.Abs(((myaw - g.MachineYaw) % 360.0 + 540.0) % 360.0 - 180.0) > 1.0) return "machine yaw " + F(myaw);
        double mx = GameBp.Fl(m, "localOffset_x"), my = GameBp.Fl(m, "localOffset_y");
        int bdx = (int)Math.Round(GameBp.Fl(b, "localOffset_x") - mx), bdy = (int)Math.Round(GameBp.Fl(b, "localOffset_y") - my);
        if (bdx != g.BeltDx || bdy != g.BeltDy) return "belt at (" + bdx + "," + bdy + ")";
        double[] got = { GameBp.Fl(s, "localOffset_x") - mx, GameBp.Fl(s, "localOffset_y") - my, GameBp.Fl(s, "localOffset_x2") - mx, GameBp.Fl(s, "localOffset_y2") - my };
        double[] want = { g.Sx, g.Sy, g.Ex, g.Ey };
        for (int k = 0; k < 4; k++)
            if (Math.Abs(got[k] - want[k]) > 0.01) return "geometry (" + F(got[0]) + "," + F(got[1]) + ")->(" + F(got[2]) + "," + F(got[3]) + ")";
        int slot = toM ? GameBp.In(s, "outputToSlot") : GameBp.In(s, "inputFromSlot");
        if (slot != g.Slot) return "slot " + slot;
        if (Geo.SlotAt(g.MachineYaw, toM ? g.Ex : g.Sx, toM ? g.Ey : g.Sy, g.Face) != g.Slot) return "slot " + g.Slot + " disagrees with Geo.SlotAt";
        int len = ((int[])GameBp.Get(s, "parameters"))[0];
        if (len != g.Length) return "length " + len;
        double syaw = GameBp.Fl(s, "yaw");
        if (Math.Abs(((syaw - g.Yaw) % 360.0 + 540.0) % 360.0 - 180.0) > 2.0) return "sorter yaw " + F(syaw);
        return null;
    }

    /// Belt corner yaws (coil-360-v3), side-merge slots and splitter belt geometry (splitters.txt).
    void CheckBeltAndSplitter()
    {
        var v3 = Source("v3");
        if (v3 != null)
        {
            CheckCorner(v3, 6, 27, BeltGeo.CornerYaw(Dir.N, Dir.W));
            CheckCorner(v3, 4, 27, BeltGeo.CornerYaw(Dir.W, Dir.S));
        }
        var sp = Source("split");
        if (sp == null) return;
        // side merge: #33 (2,9) N -> #30 (2,10) slot 2, #31 (3,10) -> #30 slot 1
        Link(sp, 33, 30, BeltGeo.SideMergeSlot, "side merge");
        Link(sp, 31, 30, BeltGeo.StraightSlot, "side merge main input");
        // splitter #0 at (7,0): in E k=1..4 = #3,#4,#5,#6; out N k=1..2 = #10,#9; out W k=1..2 = #18,#17; splitter #1 in S = #19,#20
        object s0 = sp.ContainsKey(0) ? sp[0] : null, s1 = sp.ContainsKey(1) ? sp[1] : null;
        SplitBelt(sp, 3, s0, Dir.E, 1, true); SplitBelt(sp, 4, s0, Dir.E, 2, true); SplitBelt(sp, 5, s0, Dir.E, 3, true); SplitBelt(sp, 6, s0, Dir.E, 4, true);
        SplitBelt(sp, 10, s0, Dir.N, 1, false); SplitBelt(sp, 9, s0, Dir.N, 2, false);
        SplitBelt(sp, 18, s0, Dir.W, 1, false); SplitBelt(sp, 17, s0, Dir.W, 2, false);
        SplitBelt(sp, 19, s1, Dir.S, 1, true); SplitBelt(sp, 20, s1, Dir.S, 2, true);
        Log.Add("belt corners, side merge and splitter belts checked");
    }

    void CheckCorner(Dictionary<int, object> d, int x, int y, double want)
    {
        foreach (object b in d.Values)
        {
            if (!Ids.IsBelt(GameBp.In(b, "itemId"))) continue;
            if (Math.Abs(GameBp.Fl(b, "localOffset_x") - x) > 0.01 || Math.Abs(GameBp.Fl(b, "localOffset_y") - y) > 0.01) continue;
            double yaw = GameBp.Fl(b, "yaw");
            if (Math.Abs(((yaw - want) % 360.0 + 540.0) % 360.0 - 180.0) > 2.0) Errors.Add("corner (" + x + "," + y + ") yaw " + F(yaw) + ", BeltGeo says " + F(want));
            return;
        }
        Errors.Add("corner check: no belt at (" + x + "," + y + ") in coil-360-v3");
    }

    void Link(Dictionary<int, object> d, int from, int to, int slot, string what)
    {
        if (!d.ContainsKey(from) || !d.ContainsKey(to)) { Errors.Add(what + ": records missing"); return; }
        object b = d[from];
        if (GameBp.Get(b, "outputObj") != d[to] || GameBp.In(b, "outputToSlot") != slot)
            Errors.Add(what + ": #" + from + " -> #" + to + " slot " + GameBp.In(b, "outputToSlot") + ", expected slot " + slot);
    }

    void SplitBelt(Dictionary<int, object> d, int idx, object splitter, Dir side, int k, bool incoming)
    {
        if (splitter == null || !d.ContainsKey(idx)) { Errors.Add("splitter belt #" + idx + ": records missing"); return; }
        object b = d[idx];
        var sb = Bld.Splitter(GameBp.Fl(splitter, "localOffset_x"), GameBp.Fl(splitter, "localOffset_y"), false);
        Bld want = SplitterGeo.Belt(sb, side, k, incoming, Ids.BeltMk2, 0, "check");
        string where = "splitter belt #" + idx + " (" + side + " k" + k + ")";
        if (Math.Abs(GameBp.Fl(b, "localOffset_x") - want.X) > 0.006 || Math.Abs(GameBp.Fl(b, "localOffset_y") - want.Y) > 0.006
            || Math.Abs(GameBp.Fl(b, "localOffset_z") - want.Z) > 0.002)
            Errors.Add(where + ": at (" + F(GameBp.Fl(b, "localOffset_x")) + "," + F(GameBp.Fl(b, "localOffset_y")) + "), SplitterGeo says (" + F(want.X) + "," + F(want.Y) + ")");
        if (Math.Abs(((GameBp.Fl(b, "yaw") - want.Yaw) % 360.0 + 540.0) % 360.0 - 180.0) > 2.0) Errors.Add(where + ": yaw " + F(GameBp.Fl(b, "yaw")));
        if (k == 1 && incoming && (GameBp.Get(b, "outputObj") != splitter || GameBp.In(b, "outputToSlot") != want.OutputToSlot))
            Errors.Add(where + ": not linked into the splitter slot " + want.OutputToSlot);
        if (k == 1 && !incoming && (GameBp.Get(b, "inputObj") != splitter || GameBp.In(b, "inputFromSlot") != want.InputFromSlot))
            Errors.Add(where + ": not linked from the splitter slot " + want.InputFromSlot);
    }

    static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    /// Generic prototype record for a building item id (belt, sorter, machine, tesla, splitter); null if none.
    /// Mk.III belt/sorter (2003/2013) are never provided (user rule 4).
    public object ProtoFor(int itemId)
    {
        if (itemId == Ids.BeltMk3 || itemId == Ids.SorterMk3) return null;
        string n;
        UnitCell u;
        return byItem.TryGetValue(itemId, out n) && ByName.TryGetValue(n, out u) ? u.Proto : null;
    }

    /// Prototype by cell name ("splitter-belt", "splitter-priority", ...); null if missing.
    public object Proto(string cellName)
    {
        UnitCell u;
        return cellName != null && ByName.TryGetValue(cellName, out u) ? u.Proto : null;
    }

    /// A freshly imported blueprint used as header/areas carrier for GameBp.FillHeader/Export.
    public object NewTemplate()
    {
        if (!GameBp.Ready) throw new InvalidOperationException("GameBp.Init was not called");
        foreach (string rel in Geo.SourceFiles["a8x1"].Split('|'))
        {
            string path = Resolve(BlueprintDir, rel);
            if (File.Exists(path)) return GameBp.Load(path);
        }
        throw new FileNotFoundException("template blueprint not found: " + Geo.SourceFiles["a8x1"]);
    }
}
}
