// BpImport.cs - plain blueprint records (BpBuilding) -> linked abstract Layout (docs\patterns-design.md, Architecture 1).
// Pure model code: no game DLL, no I/O. Hosts turn game objects into BpBuilding first (CLI: the host's ToRecords; addon:
// the pasted / loaded blueprint). Moved from tests\SelfTest.cs FullLayout, which is now a thin wrapper around ToLayout.
using System;
using System.Collections.Generic;

namespace DspFlat.Engine
{

public static class BpImport
{
    /// Every record as a linked Bld (belts with icon parameters, splitters with model + z, sorters with both ends and
    /// offsets, spray coaters as Kind.Other, depots, towers, machines with their recipe). Links follow
    /// tempOutputObjIdx / tempInputObjIdx, which address the records by their "index" field when those are unique,
    /// else by list position. Bld.Index = the record's index field (so a cell can cite "file #index").
    /// dropStackedSplitters: leave out a splitter stacked on another splitter (z > 1.5, inputObj = splitter) that no belt
    /// links to (the user's accidental one in refs\engine-turbine-60-v2-userupgrade.txt (28,22) z 2).
    public static Layout ToLayout(IList<BpBuilding> recs, GameData gd, bool dropStackedSplitters)
    {
        var l = new Layout(gd);
        if (recs == null) return l;
        // record key -> record position (the game's index field, unless duplicated / missing)
        var byKey = new Dictionary<int, int>();
        bool uniqueIdx = true;
        for (int i = 0; i < recs.Count; i++)
        {
            if (recs[i] == null) continue;
            if (byKey.ContainsKey(recs[i].index)) { uniqueIdx = false; break; }
            byKey[recs[i].index] = i;
        }
        Func<int, int> pos = key =>
        {
            if (key < 0) return -1;
            if (!uniqueIdx) return key < recs.Count ? key : -1;
            int p; return byKey.TryGetValue(key, out p) ? p : -1;
        };
        // which records a belt links to (for the stacked-splitter rule)
        var linkedTo = new HashSet<int>();
        for (int i = 0; i < recs.Count; i++)
        {
            BpBuilding r = recs[i];
            if (r == null || !Ids.IsBelt(r.itemId)) continue;
            int o = pos(r.tempOutputObjIdx), n = pos(r.tempInputObjIdx);
            if (o >= 0) linkedTo.Add(o);
            if (n >= 0) linkedTo.Add(n);
        }
        var blds = new Bld[recs.Count];
        for (int i = 0; i < recs.Count; i++)
        {
            BpBuilding r = recs[i];
            if (r == null) continue;
            int item = r.itemId;
            double x = r.localOffset_x, y = r.localOffset_y, z = r.localOffset_z, yaw = r.yaw;
            int[] par = r.parameters;
            Bld n;
            if (Ids.IsBelt(item))
            {
                n = Bld.Belt(item, x, y, yaw, 0, null);
                if (Math.Abs(x - Math.Round(x)) > 0.01 || Math.Abs(y - Math.Round(y)) > 0.01) n.Cell = SplitterGeo.BeltCell;
                if (par != null && par.Length == 2 && par[0] != 0) n.Parameters = (int[])par.Clone();
            }
            else if (item == Ids.Splitter)
            {
                int inp = pos(r.tempInputObjIdx);
                if (dropStackedSplitters && z > 1.5 && inp >= 0 && recs[inp] != null && recs[inp].itemId == Ids.Splitter && !linkedTo.Contains(i)) continue;
                n = Bld.Splitter(x, y, par != null && par.Length > 0 && par[0] == 1);
                n.ModelIndex = r.modelIndex;
            }
            else if (Ids.IsSorter(item))
                n = new Bld { Kind = Kind.Sorter, ItemId = item, X = x, Y = y, X2 = r.localOffset_x2, Y2 = r.localOffset_y2, Z2 = r.localOffset_z2,
                    Yaw = yaw, Yaw2 = r.yaw2, Parameters = par != null ? (int[])par.Clone() : new[] { 1 },
                    InputOffset = r.inputOffset, OutputOffset = r.outputOffset };
            else if (item == Coaters.SprayCoater) n = Coaters.Coater(x, y, yaw);
            else if (item == Ids.DepotMk1) n = Bld.Depot(x, y);
            else if (item == Ids.Tesla) n = Bld.Tesla(x, y);
            else n = Bld.Machine(item, x, y, yaw, r.recipeId);
            n.Z = z; if (n.Kind != Kind.Sorter) n.Z2 = z;
            n.OutputToSlot = r.outputToSlot; n.InputFromSlot = r.inputFromSlot;
            n.OutputFromSlot = r.outputFromSlot; n.InputToSlot = r.inputToSlot;
            n.Index = r.index;
            blds[i] = n;
            l.Add(n);
        }
        for (int i = 0; i < recs.Count; i++)
        {
            Bld n = blds[i];
            if (n == null) continue;
            int o = pos(recs[i].tempOutputObjIdx), p = pos(recs[i].tempInputObjIdx);
            if (o >= 0 && blds[o] != null) n.Output = blds[o];
            if (p >= 0 && blds[p] != null) n.Input = blds[p];
        }
        return l;
    }
}
}
