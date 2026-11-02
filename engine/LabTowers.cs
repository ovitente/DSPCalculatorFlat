// LabTowers.cs - Matrix Lab (2901) towers: cell table, stacking and tower checks (PLAN.md decision 10).
// Stacked labs pass items between levels by themselves; only the bottom lab of a stack takes sorters (game:
// BuildTool.GetLocalSlots returns no slots when connection 14 - the link to the building below - is occupied,
// analysis\buildings\slots.md section 2). Row layout: SlotRows.Row (one tower per row position).
//
// Geometry source (no number is guessed):
//  * 4-sci\bluematrix.txt (the user's own blueprint, game 0.10.35.29151, header "240/min"): Electromagnetic Matrix
//    towers of 6 labs (recipe 9), towers 5 tiles apart along the row (x 53 / 58), Sorter Mk.II.
//      - tower base #557 at (53,8,z 0) yaw 270; levels #577 (z 3) -> #367 (z 6) -> #467 (z 9) -> #470 (z 12) -> #663 (z 15),
//        each record inputObj = the lab one level below, outputObj none, slots outToSlot 14 / inFromSlot 15 /
//        outFromSlot 15 / inToSlot 14 (game multi-level defaults, slots.md table 4), recipe 9, parameters [1,0].
//      - sorters of #557: in #422 slot 4 L1 (belt (53,5)), in #563 slot 10 L1 (belt (53,11)),
//        out #561 slot 11 L2 (belt (52,12)), out #647 slot 5 L2 (belt (54,4)).
//  * x-imp\Planetary Science\Matrix Labs 37.5.txt (community, research mode): same stacking record pattern up to
//    15 levels (z 0..42, e.g. #24 z 0 -> #25 z 3 -> #29 z 6), parameters [2,0] = research mode (not used here).
//  * Level step: z 3 blueprint units = 4.0 m (game lab stack pitch, analysis\buildings\conditions.md F17;
//    GameData.BeltLevelM 1.3333 m per unit).
using System;
using System.Collections.Generic;
using System.Linq;

namespace DspFlat.Engine
{

public static class LabTowers
{
    /// Blueprint z step between two stacked labs (4-sci\bluematrix.txt #557 z 0, #577 z 3, #367 z 6, ...).
    public const double LevelZ = 3.0;
    /// Lab record defaults (4-sci\bluematrix.txt #557 / #577): multi-level slot fields and production mode.
    public const int SlotOutTo = 14, SlotInFrom = 15, SlotOutFrom = 15, SlotInTo = 14;
    public static readonly int[] ProductionParameters = { 1, 0 };
    public const string SourceKey = "bluematrix";
    public const string SourceFile = @"4-sci\bluematrix.txt";

    public static bool IsLab(int item) { return item == Ids.MatrixLab; }
    /// A lab above the ground level of a tower (it occupies no tile and takes no sorters).
    public static bool IsUpper(Bld b) { return b != null && b.Kind == Kind.Machine && IsLab(b.ItemId) && (b.Z > 0.25 || (b.Input != null && b.Input.Kind == Kind.Machine)); }

    static SlotRowDef def;
    /// Cell table, canonical lab yaw 0 in the Flow-N frame (row along y, lanes at x = +-3 (L1) / +-4 (L2)).
    public static SlotRowDef Def
    {
        get
        {
            if (def != null) return def;
            var d = new SlotRowDef { MachineItem = Ids.MatrixLab, Short = "lab", MachineYaw = 0, Towers = true };
            // slot poses (tiles, yaw 0), counter-clockwise from NE: analysis\buildings\slots.md section 2
            d.SlotFace = new[] { Dir.N, Dir.N, Dir.N, Dir.W, Dir.W, Dir.W, Dir.S, Dir.S, Dir.S, Dir.E, Dir.E, Dir.E };
            d.SlotPos = new[] { new[] { 0.796, 1.83 }, new[] { 0.0, 1.83 }, new[] { -0.796, 1.83 }, new[] { -1.83, 0.796 }, new[] { -1.83, 0.0 }, new[] { -1.83, -0.796 },
                                new[] { -0.796, -1.83 }, new[] { 0.0, -1.83 }, new[] { 0.796, -1.83 }, new[] { 1.83, -0.796 }, new[] { 1.83, 0.0 }, new[] { 1.83, 0.796 } };
            // sorter records of tower base #557 (53,8) yaw 270, numbers relative to the lab, turned to yaw 0
            // in E L1 (side A): #563 belt (53,11) -> slot 10
            d.Add(SourceKey, SourceFile, 557, 563, 270, true, 0, 3, -0.004, 3, 0, 1.829, 10, 1, null);
            // in W L1 (side B): #422 belt (53,5) -> slot 4
            d.Add(SourceKey, SourceFile, 557, 422, 270, true, 0, -3, -0.004, -3, 0, -1.829, 4, 1, null);
            // out W L2 (behind side B): slot 5 -> #647 belt (54,4)
            d.Add(SourceKey, SourceFile, 557, 647, 270, false, 1, -4, 0.822, -1.828, 0.825, -4, 5, 2, null);
            // out E L2 (behind side A): slot 11 -> #561 belt (52,12)
            d.Add(SourceKey, SourceFile, 557, 561, 270, false, -1, 4, -0.817, 1.829, -0.826, 4, 11, 2, null);
            def = d;
            return d;
        }
    }

    /// Puts height-1 labs on top of the tower base (same tile, z + LevelZ per level, each linked to the one below) and
    /// sets the multi-level record fields on every level.
    public static void Stack(Fragment f, Bld baseLab, int height)
    {
        SetRecord(baseLab);
        Bld below = baseLab;
        for (int j = 1; j < height; j++)
        {
            Bld up = Bld.Machine(baseLab.ItemId, baseLab.X, baseLab.Y, baseLab.Yaw, baseLab.RecipeId);
            up.Z = up.Z2 = j * LevelZ;
            up.Input = below;
            up.Carries = baseLab.Carries;
            up.Role = baseLab.Role + " level " + (j + 1);
            SetRecord(up);
            f.Add(up);
            below = up;
        }
    }

    static void SetRecord(Bld lab)
    {
        lab.OutputToSlot = SlotOutTo; lab.InputFromSlot = SlotInFrom; lab.OutputFromSlot = SlotOutFrom; lab.InputToSlot = SlotInTo;
        lab.Parameters = (int[])ProductionParameters.Clone();
    }

    /// Tower index of a building set: upper levels, and for every tower base the number of labs in its tower.
    public static void Index(IEnumerable<Bld> all, out HashSet<Bld> upper, out Dictionary<Bld, int> size)
    {
        upper = new HashSet<Bld>(); size = new Dictionary<Bld, int>();
        var above = new Dictionary<Bld, Bld>();
        var labs = all.Where(b => b.Kind == Kind.Machine && IsLab(b.ItemId)).ToList();
        foreach (Bld b in labs)
        {
            if (!IsUpper(b)) continue;
            upper.Add(b);
            if (b.Input != null && !above.ContainsKey(b.Input)) above[b.Input] = b;
        }
        foreach (Bld b in labs)
        {
            if (upper.Contains(b)) continue;
            int n = 1; Bld c = b; var seen = new HashSet<Bld> { b };
            Bld u;
            while (above.TryGetValue(c, out u) && seen.Add(u)) { n++; c = u; }
            size[b] = n;
        }
    }

    /// Tower structure checks (Validator hook): every upper lab sits exactly one level above the lab it links to (same
    /// tile, yaw and recipe), no forks, no floating labs, height <= spec.LabStackMax, no sorter on an upper lab.
    public static void Validate(IList<Bld> all, BlockSpec spec, Action<string> fail, Action<string> note)
    {
        HashSet<Bld> upper; Dictionary<Bld, int> size;
        Index(all, out upper, out size);
        if (upper.Count == 0 && size.Count == 0) return;
        var linkedFrom = new Dictionary<Bld, int>();
        foreach (Bld u in upper)
        {
            string at = " at (" + u.X + "," + u.Y + ", z " + u.Z + ")";
            Bld b = u.Input;
            if (b == null || b.Kind != Kind.Machine || !IsLab(b.ItemId)) { fail("lab tower: lab" + at + " is above ground but not linked to a lab below"); continue; }
            if (Math.Abs(b.X - u.X) > 0.01 || Math.Abs(b.Y - u.Y) > 0.01 || Math.Abs(u.Z - b.Z - LevelZ) > 0.01)
                fail("lab tower: lab" + at + " is not one level (z +" + LevelZ + ") above the lab it links to (" + b.X + "," + b.Y + ", z " + b.Z + ")");
            if (b.RecipeId != u.RecipeId) fail("lab tower: recipe " + u.RecipeId + at + " differs from the lab below (" + b.RecipeId + ")");
            if (Math.Abs(Dirs.Norm(b.Yaw - u.Yaw)) > 0.5 && Math.Abs(Dirs.Norm(b.Yaw - u.Yaw) - 360) > 0.5) fail("lab tower: yaw " + u.Yaw + at + " differs from the lab below");
            int c; linkedFrom.TryGetValue(b, out c); linkedFrom[b] = c + 1;
            if (c + 1 > 1) fail("lab tower: two labs stacked on the lab at (" + b.X + "," + b.Y + ", z " + b.Z + ")");
        }
        foreach (var kv in size)
        {
            if (kv.Key.Z > 0.01) fail("lab tower: base lab at (" + kv.Key.X + "," + kv.Key.Y + ") floats at z " + kv.Key.Z);
            if (spec != null && kv.Value > spec.LabStackMax) fail("lab tower at (" + kv.Key.X + "," + kv.Key.Y + "): " + kv.Value + " labs > LabStackMax " + spec.LabStackMax);
        }
        foreach (Bld s in all.Where(x => x.Kind == Kind.Sorter))
            if (upper.Contains(s.Input) || upper.Contains(s.Output))
                fail("lab tower: sorter at (" + s.X + "," + s.Y + ") on an upper lab (only the bottom lab of a stack takes sorters)");
        if (size.Count > 0)
            note("lab towers: " + size.Count + ", heights " + string.Join(",", size.Values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
    }
}
}
