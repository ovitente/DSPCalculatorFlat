// SelfTestPatterns.cs - self tests of the pattern loader (engine\BpImport.cs, engine\Patterns.cs; docs\patterns-design.md).
// Part of SelfTest (MAKEBLOCK_CASES filter "micro patterns"). Oracles: the user's drawings in blueprint-tools\refs\patterns
// (P08 / P09 rows, P12 comb, the multi-pattern sheet) and the game-proven turbine file for the import regression.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

public static partial class SelfTest
{
    const string PatP08 = @"refs\patterns\p08-asm-row-3in.txt";
    const string PatP09 = @"refs\patterns\p09-asm-row-4in.txt";
    const string PatP12 = @"refs\patterns\p12-input-edge-comb.txt";
    const string PatSheet = @"refs\patterns\p-sheet-001.txt";

    static partial void RunPatternCases()
    {
        Case("micro patterns: Analyze(p08-asm-row-3in) = one Row of 4 Asm Mk.II recipe 97, pitch 4, flow N, lanes W L1/L2 in, W L3 out, E L1 in with items", MicroPatternsP08);
        Case("micro patterns: Analyze(p09-asm-row-4in) = the P08 frame plus E L2 in (slot 4), 4 inputs", MicroPatternsP09);
        Case("micro patterns: Analyze(p12-input-edge-comb) = one Comb of 5 lanes, pitch 1, S edge", MicroPatternsP12);
        Case("micro patterns: Analyze(p-sheet-001) finds the comb, the 3x3 bridge bundle, two splitter turns at 2, the 6+6 smelter double column, the coater row, the 6 coil assemblers", MicroPatternsSheet);
        Case("micro patterns: BpImport.ToLayout(turbine userupgrade) == the old game-object FullLayout (counts, ValidateBelts), RowCells + Geo.Register, catalog status", MicroPatternsImportAndRegister);
    }

    static Layout PatternLayout(string rel, out List<PatternInfo> infos, out string desc)
    {
        string path = Cells.Resolve(bpDir, rel);
        if (!File.Exists(path)) throw new SkipException("missing " + path);
        object bp = GameBp.Load(path);
        desc = GameBp.Get(bp, "desc") as string;
        Layout l = BpImport.ToLayout(GameBp.ToRecords(bp), gd, true);
        infos = Patterns.Analyze(l, gd);
        return l;
    }

    static LaneInfo FindLane(PatternInfo row, Dir side, int length, bool input)
    {
        return row.Lanes.FirstOrDefault(x => x.Side == side && x.Length == length && x.Input == input);
    }

    static void CheckLane(Errs e, PatternInfo row, Dir side, int length, bool input, int slot, string item)
    {
        LaneInfo li = FindLane(row, side, length, input);
        string w = side + " L" + length + " " + (input ? "in" : "out") + ": ";
        if (li == null) { e.L.Add(w + "lane missing (have " + string.Join(" | ", row.Lanes.Select(x => x.Describe(gd)).ToArray()) + ")"); return; }
        e.Check(li.Slot == slot, w + "slot " + li.Slot + ", expected " + slot);
        int want = gd.ResolveItem(item);
        e.Check(li.Item == want, w + "item " + gd.ItemName(li.Item) + ", expected " + item);
        e.Check(li.Machines == row.MachineCount, w + "touches " + li.Machines + " machines of " + row.MachineCount);
        e.Check(li.Note == null, w + "note: " + li.Note);
    }

    static PatternInfo OnlyRow(Errs e, List<PatternInfo> infos, string what)
    {
        var rows = infos.Where(i => i.Kind == PatternKind.Row).ToList();
        e.Check(rows.Count == 1, what + ": " + rows.Count + " rows, expected 1 (" + string.Join(" / ", infos.Select(i => i.Describe(gd)).ToArray()) + ")");
        e.Check(infos.All(i => i.Kind == PatternKind.Row || i.Kind == PatternKind.Comb), what + ": unexpected infos: " + string.Join(" / ", infos.Where(i => i.Kind != PatternKind.Row && i.Kind != PatternKind.Comb).Select(i => i.Describe(gd)).ToArray()));
        return rows.FirstOrDefault();
    }

    static string MicroPatternsP08()
    {
        var e = new Errs();
        List<PatternInfo> infos; string desc;
        PatternLayout(PatP08, out infos, out desc);
        PatternInfo row = OnlyRow(e, infos, "p08");
        if (row == null) return e.Result();
        Console.WriteLine("      " + row.Describe(gd));
        e.Check(row.MachineItem == Ids.AsmMk2 && row.RecipeId == 97 && row.MachineCount == 4, "row " + row.MachineCount + " x " + row.MachineItem + " recipe " + row.RecipeId);
        e.Check(row.Pitch == 4 && row.Flow == Dir.N && NearYaw(row.MachineYaw, 0, 1), "pitch " + row.Pitch + " flow " + row.Flow + " yaw " + row.MachineYaw);
        e.Check(row.Lanes.Count == 4 && row.InputLanes == 3 && row.OutputLanes == 1, row.Lanes.Count + " lanes, " + row.InputLanes + " in / " + row.OutputLanes + " out");
        CheckLane(e, row, Dir.W, 1, true, 9, "Iron Ingot");
        CheckLane(e, row, Dir.W, 2, true, 10, "Magnetic Coil");
        CheckLane(e, row, Dir.W, 3, false, 11, "Electric Motor");
        CheckLane(e, row, Dir.E, 1, true, 5, "Gear");
        LaneInfo outL = FindLane(row, Dir.W, 3, false);
        if (outL != null) e.Check(!outL.ItemFromIcon && outL.BeltDx == -4 && outL.BeltDy == 1, "output lane: belt (" + outL.BeltDx + "," + outL.BeltDy + "), icon " + outL.ItemFromIcon);
        // cells: one per lane, geometry relative to the machine centre as in the file (#8: (2,16.206)->(3.122,16.205) for machine (4,17))
        List<SorterGeo> cells = Patterns.RowCells(row, "p08-asm-row-3in.txt");
        e.Check(cells.Count == 4 && cells.All(g => g.Status == "game" && g.SourceKey == null && g.Source.StartsWith("p08-asm-row-3in.txt #")), "cells: " + string.Join(" / ", cells.Select(g => g.ToString()).ToArray()));
        SorterGeo w1 = cells.FirstOrDefault(g => g.BeltToMachine && g.Side == Dir.W && g.Length == 1);
        if (w1 != null)
            e.Check(Near(w1.Sx, -2, 0.01) && Near(w1.Sy, -0.794, 0.01) && Near(w1.Ex, -0.878, 0.01) && Near(w1.Ey, -0.795, 0.01) && w1.Slot == 9 && w1.Lateral == -1 && NearYaw(w1.Yaw, 90, 1), "W L1 cell " + w1);
        return e.Result();
    }

    static string MicroPatternsP09()
    {
        var e = new Errs();
        List<PatternInfo> infos; string desc;
        PatternLayout(PatP09, out infos, out desc);
        PatternInfo row = OnlyRow(e, infos, "p09");
        if (row == null) return e.Result();
        Console.WriteLine("      " + row.Describe(gd));
        e.Check(row.MachineItem == Ids.AsmMk2 && row.RecipeId == 144 && row.MachineCount == 4 && row.Pitch == 4 && row.Flow == Dir.N, "frame: " + row.Describe(gd));
        e.Check(row.Lanes.Count == 5 && row.InputLanes == 4 && row.OutputLanes == 1, row.Lanes.Count + " lanes, " + row.InputLanes + " in / " + row.OutputLanes + " out");
        CheckLane(e, row, Dir.W, 1, true, 9, "Copper Ingot");
        CheckLane(e, row, Dir.W, 2, true, 10, "Circuit Board");
        CheckLane(e, row, Dir.W, 3, false, 11, "Missile Set");
        CheckLane(e, row, Dir.E, 1, true, 5, "Combustible Unit");
        CheckLane(e, row, Dir.E, 2, true, 4, "Engine");
        return e.Result();
    }

    static string MicroPatternsP12()
    {
        var e = new Errs();
        List<PatternInfo> infos; string desc;
        PatternLayout(PatP12, out infos, out desc);
        foreach (PatternInfo i in infos) Console.WriteLine("      " + i.Describe(gd));
        var combs = infos.Where(i => i.Kind == PatternKind.Comb).ToList();
        e.Check(combs.Count == 1, combs.Count + " combs, expected 1");
        if (combs.Count == 1) e.Check(combs[0].LaneCount == 5 && combs[0].LanePitch == 1 && combs[0].Edge == Dir.S, "comb: " + combs[0].Describe(gd));
        // the drawing also holds two lone arc smelters (markers, no lanes): rows of 1 machine, nothing else
        e.Check(infos.All(i => i.Kind == PatternKind.Comb || (i.Kind == PatternKind.Row && i.MachineItem == Ids.ArcSmelter && i.MachineCount == 1 && i.Lanes.Count == 0)),
            "unexpected infos: " + string.Join(" / ", infos.Where(i => i.Kind != PatternKind.Comb).Select(i => i.Describe(gd)).ToArray()));
        return e.Result();
    }

    static string MicroPatternsSheet()
    {
        var e = new Errs();
        List<PatternInfo> infos; string desc;
        Layout l = PatternLayout(PatSheet, out infos, out desc);
        foreach (PatternInfo i in infos) Console.WriteLine("      " + i.Describe(gd));
        e.Check(infos.Any(i => i.Kind == PatternKind.Comb && i.LaneCount == 5 && i.LanePitch == 1), "no comb of 5 lanes at pitch 1");
        e.Check(infos.Any(i => i.Kind == PatternKind.Bridge && i.Runs == 3 && i.Crossed.All(x => x == 3)), "no bridge bundle of 3 runs each crossing 3 lanes");
        var turns = infos.Where(i => i.Kind == PatternKind.SplitterTurn).ToList();
        e.Check(turns.Count == 2 && turns.All(t => t.CornerDistances.Count == 2 && t.CornerDistances.All(d => d == 2)), "splitter turns: " + string.Join(" / ", turns.Select(t => t.Describe(gd)).ToArray()));
        var dc = infos.FirstOrDefault(i => i.Kind == PatternKind.DoubleColumn && i.MachineItem == Ids.ArcSmelter);
        e.Check(dc != null && dc.MachineCount == 6 && dc.MachineCount2 == 6 && dc.SharedLanes == 1 && dc.Pitch == 3, "smelter double column: " + (dc == null ? "none" : dc.Describe(gd)));
        if (dc != null)
        {
            var shared = dc.Lanes.Where(x => x.Belt.TileX == (dc.Coord + dc.Coord2) / 2).ToList();
            e.Check(shared.Count == 2 && shared.Select(x => x.Column).Distinct().Count() == 2, "shared middle lane not fed from both columns: " + shared.Count);
        }
        var cr = infos.FirstOrDefault(i => i.Kind == PatternKind.CoaterRow);
        e.Check(cr != null && cr.Coaters == 4 && cr.FeedCount == 1 && cr.FeedZProfile == "0/0.5/1" && cr.FeedDeadEnds && cr.FeedOffset == -1, "coater row: " + (cr == null ? "none" : cr.Describe(gd)));
        var coil = infos.FirstOrDefault(i => (i.Kind == PatternKind.Row || i.Kind == PatternKind.DoubleColumn) && i.MachineItem == Ids.AsmMk1 && i.RecipeId == Ids.RecipeMagneticCoil);
        e.Check(coil != null && coil.MachineCount + coil.MachineCount2 == 6, "coil assemblers: " + (coil == null ? "none" : coil.Describe(gd)));
        e.Check(l.All.Count(Coaters.IsCoater) == 4 && l.All.Where(Coaters.IsCoater).All(b => b.Kind == Kind.Other), "coaters imported as Kind.Other: " + l.All.Count(Coaters.IsCoater));
        e.Check(!infos.Any(i => i.Note != null && i.Note.StartsWith("analysis error")), "analysis error reported");
        return e.Result();
    }

    /// The pre-move FullLayout (game objects, tests\SelfTest.cs before feat-pat-loader), kept here as the oracle.
    static Layout OldFullLayout(object bp, bool dropStacked)
    {
        var l = new Layout(gd);
        var byIdx = new Dictionary<int, Bld>();
        var recs = GameBp.Buildings(bp).Cast<object>().ToList();
        var linkedTo = new HashSet<object>();
        foreach (object r in recs) { object o = GameBp.Get(r, "outputObj"), i = GameBp.Get(r, "inputObj"); if (o != null && Ids.IsBelt(GameBp.In(r, "itemId"))) linkedTo.Add(o); if (i != null && Ids.IsBelt(GameBp.In(r, "itemId"))) linkedTo.Add(i); }
        foreach (object r in recs)
        {
            int item = GameBp.In(r, "itemId");
            double x = GameBp.Fl(r, "localOffset_x"), y = GameBp.Fl(r, "localOffset_y"), z = GameBp.Fl(r, "localOffset_z");
            double yaw = GameBp.Fl(r, "yaw");
            int[] par = GameBp.Get(r, "parameters") as int[];
            Bld n;
            if (Ids.IsBelt(item))
            {
                n = Bld.Belt(item, x, y, yaw, 0, null);
                if (Math.Abs(x - Math.Round(x)) > 0.01 || Math.Abs(y - Math.Round(y)) > 0.01) n.Cell = SplitterGeo.BeltCell;
                if (par != null && par.Length == 2 && par[0] != 0) n.Parameters = (int[])par.Clone();
            }
            else if (item == Ids.Splitter)
            {
                object inp = GameBp.Get(r, "inputObj");
                if (dropStacked && z > 1.5 && inp != null && GameBp.In(inp, "itemId") == Ids.Splitter && !linkedTo.Contains(r)) continue;
                n = Bld.Splitter(x, y, par != null && par.Length > 0 && par[0] == 1);
                n.ModelIndex = GameBp.In(r, "modelIndex");
            }
            else if (Ids.IsSorter(item))
                n = new Bld { Kind = Kind.Sorter, ItemId = item, X = x, Y = y, X2 = GameBp.Fl(r, "localOffset_x2"), Y2 = GameBp.Fl(r, "localOffset_y2"), Z2 = GameBp.Fl(r, "localOffset_z2"),
                    Yaw = yaw, Yaw2 = GameBp.Fl(r, "yaw2"), Parameters = par != null ? (int[])par.Clone() : new[] { 1 },
                    InputOffset = GameBp.In(r, "inputOffset"), OutputOffset = GameBp.In(r, "outputOffset") };
            else if (item == Ids.DepotMk1) n = Bld.Depot(x, y);
            else if (item == Ids.Tesla) n = Bld.Tesla(x, y);
            else n = Bld.Machine(item, x, y, yaw, GameBp.In(r, "recipeId"));
            n.Z = z; if (n.Kind != Kind.Sorter) n.Z2 = z;
            n.OutputToSlot = GameBp.In(r, "outputToSlot"); n.InputFromSlot = GameBp.In(r, "inputFromSlot");
            n.OutputFromSlot = GameBp.In(r, "outputFromSlot"); n.InputToSlot = GameBp.In(r, "inputToSlot");
            byIdx[GameBp.In(r, "index")] = n;
            l.Add(n);
        }
        foreach (object r in recs)
        {
            Bld n, t;
            if (!byIdx.TryGetValue(GameBp.In(r, "index"), out n)) continue;
            object o = GameBp.Get(r, "outputObj"), i = GameBp.Get(r, "inputObj");
            if (o != null && byIdx.TryGetValue(GameBp.In(o, "index"), out t)) n.Output = t;
            if (i != null && byIdx.TryGetValue(GameBp.In(i, "index"), out t)) n.Input = t;
        }
        return l;
    }

    static string MicroPatternsImportAndRegister()
    {
        var e = new Errs();
        string path = Cells.Resolve(bpDir, UserTurbine);
        if (!File.Exists(path)) return "missing " + path;
        object bp = GameBp.Load(path);
        foreach (bool drop in new[] { true, false })
        {
            Layout old = OldFullLayout(bp, drop), now = FullLayout(bp, drop);
            string w = "dropStacked " + drop + ": ";
            e.Check(old.All.Count == now.All.Count, w + "buildings " + now.All.Count + ", old " + old.All.Count);
            e.Check(old.Errors.Count == now.Errors.Count, w + "import errors " + now.Errors.Count + ", old " + old.Errors.Count);
            for (int i = 0; i < Math.Min(old.All.Count, now.All.Count); i++)
            {
                Bld a = old.All[i], b = now.All[i];
                if (a.Kind != b.Kind || a.ItemId != b.ItemId || !Near(a.X, b.X, 1e-6) || !Near(a.Y, b.Y, 1e-6) || !Near(a.Z, b.Z, 1e-6) || !Near(a.X2, b.X2, 1e-6) || !Near(a.Y2, b.Y2, 1e-6)
                    || !Near(a.Yaw, b.Yaw, 1e-6) || a.RecipeId != b.RecipeId || a.ModelIndex != b.ModelIndex || a.OutputToSlot != b.OutputToSlot || a.InputFromSlot != b.InputFromSlot
                    || a.InputOffset != b.InputOffset || a.OutputOffset != b.OutputOffset || (a.Cell ?? "") != (b.Cell ?? "") || a.SorterLength != b.SorterLength
                    || (a.Output == null) != (b.Output == null) || (a.Input == null) != (b.Input == null)
                    || (a.Output != null && old.All.IndexOf(a.Output) != now.All.IndexOf(b.Output)) || (a.Input != null && old.All.IndexOf(a.Input) != now.All.IndexOf(b.Input)))
                { e.L.Add(w + "record " + i + " differs: old " + a + " new " + b); break; }
            }
            ValidationReport ro = new Validator(gd, null).ValidateBelts(old), rn = new Validator(gd, null).ValidateBelts(now);
            e.Check(ro.Errors.SequenceEqual(rn.Errors) && ro.Warnings.SequenceEqual(rn.Warnings) && ro.Info.SequenceEqual(rn.Info),
                w + "ValidateBelts differs: " + rn.Errors.Count + "/" + rn.Warnings.Count + "/" + rn.Info.Count + " vs old " + ro.Errors.Count + "/" + ro.Warnings.Count + "/" + ro.Info.Count);
            Console.WriteLine("      " + w + now.All.Count + " buildings, ValidateBelts " + rn.Errors.Count + " errors / " + rn.Warnings.Count + " warnings, same as before the move");
        }
        // register the P08 row cells: Geo.Find for the drawn lanes returns the drawing's cells first
        List<PatternInfo> infos; string desc;
        PatternLayout(PatP08, out infos, out desc);
        PatternInfo row = infos.FirstOrDefault(i => i.Kind == PatternKind.Row);
        if (row == null) { e.L.Add("p08: no row"); return e.Result(); }
        List<SorterGeo> cells = Patterns.RowCells(row, "p08-asm-row-3in.txt");
        Geo.Register(cells, true);
        foreach (SorterGeo g in cells)
        {
            SorterGeo f = Geo.Find(Ids.AsmMk2, g.BeltToMachine, g.Side, g.Length, g.Lateral, 0);
            e.Check(f != null && f.Source == g.Source, "Geo.Find for " + g.Name + " returns " + (f == null ? "null" : f.Name + " (" + f.Source + ")"));
        }
        e.Check(Geo.All().Count(g => g.Source != null && g.Source.StartsWith("p08-asm-row-3in.txt")) == 4, "registered cells not in Geo.All");
        Geo.Register(cells, true);   // registering again replaces, never duplicates
        e.Check(Geo.All().Count(g => g.Source != null && g.Source.StartsWith("p08-asm-row-3in.txt")) == 4, "re-registration duplicated cells");
        // catalog: P08 drawn by file name, P12 / P13 / P14 / P10 / P01 by the sheet's analysis, P15 missing; "ok" marks proven
        var files = new List<PatternFile> { new PatternFile { Name = "p08-asm-row-3in.txt", Desc = "", Infos = infos } };
        List<PatternInfo> sheet; string sdesc;
        PatternLayout(PatSheet, out sheet, out sdesc);
        files.Add(new PatternFile { Name = "p-sheet-001.txt", Desc = sdesc, Infos = sheet });
        var st = Patterns.Status(files).ToDictionary(s => s.Entry.Id);
        e.Check(st.Count == 17, "catalog has " + st.Count + " entries");
        e.Check(st["P08"].State == "drawn" && st["P08"].File == "p08-asm-row-3in.txt", "P08: " + st["P08"].State + " " + st["P08"].Why);
        foreach (string id in new[] { "P12", "P13", "P14", "P10", "P01", "P02" }) e.Check(st[id].State == "drawn" && st[id].File == "p-sheet-001.txt", id + ": " + st[id].State + " (" + st[id].Why + ")");
        e.Check(st["P15"].State == "missing" && st["P09"].State == "missing", "P15/P09: " + st["P15"].State + "/" + st["P09"].State);
        files[0].Desc = "tested 10 min, ok";
        e.Check(Patterns.Status(files).First(s => s.Entry.Id == "P08").State == "proven", "desc 'ok' does not mark P08 proven");
        e.Check(!Patterns.IsOk("looks okay") && Patterns.IsOk("OK") && Patterns.IsOk("ok."), "IsOk word match");
        return e.Result();
    }
}
