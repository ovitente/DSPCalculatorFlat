// GameEmitter.cs - CLI-side blueprint serialization through the game's own code (Assembly-CSharp by reflection):
// engine BpBuilding records -> game BlueprintBuilding objects -> template BlueprintData (areas/header carrier) ->
// FillHeader -> BlueprintData.ToBase64String -> re-import + MD5F -> record-by-record round-trip check.
// Also the export check against the abstract layout (former Validator.CheckExport, check 8). Never writes a file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DspFlat.Engine;

namespace DspFlat.Host
{
public class GameEmitter
{
    readonly GameData gd;
    readonly Cells cells;

    public GameEmitter(GameData gd, Cells cells) { this.gd = gd; this.cells = cells; }

    /// Abstract layout -> engine records (DspFlat.Engine.Emitter) -> blueprint string through the game. Problems are
    /// returned in ExportResult.Errors (Text stays null when nothing could be exported). Sets Bld.Game for each building.
    public ExportResult Emit(Layout layout, BpHeader header)
    {
        var pre = new ExportResult();
        if (cells == null) { pre.Errors.Add("emitter: no prototype cells"); return pre; }
        List<BpBuilding> recs = new Emitter(gd, cells.Prototypes).Records(layout, pre.Errors);
        if (recs == null) return pre;
        return Serialize(recs, header, layout);
    }

    /// Engine records -> blueprint string through the game. layout (optional): the layout the records came from
    /// (record i = layout.All[i]); its Bld.Game is set to the written game object.
    public ExportResult Serialize(IList<BpBuilding> recs, BpHeader header, Layout layout)
    {
        var pre = new ExportResult();
        if (cells == null) { pre.Errors.Add("emitter: no prototype cells"); return pre; }
        List<object> objs;
        try { objs = GameBp.FromRecords(recs, pre.Errors); }
        catch (Exception e) { pre.Errors.Add("emitter: cannot build game records: " + (e.InnerException ?? e).Message); return pre; }
        if (pre.Errors.Count > 0) return pre;
        if (layout != null)
        {
            if (layout.All.Count != objs.Count) { pre.Errors.Add("emitter: " + objs.Count + " records for " + layout.All.Count + " buildings"); return pre; }
            for (int i = 0; i < objs.Count; i++) layout.All[i].Game = objs[i];
        }

        object bp = cells.NewTemplate();
        if (bp == null) { pre.Errors.Add("emitter: no template blueprint"); return pre; }
        GameBp.SetBuildings(bp, objs);
        GameBp.FillHeader(bp, header ?? new BpHeader());
        ExportResult r = GameBp.Export(bp);
        if (r.Ok) CheckRoundTrip(objs, r);
        return r;
    }

    /// Re-reads the re-imported blueprint and compares every record with the written one: item, position, yaw,
    /// recipe, slots, offsets, parameters and link targets (by index).
    static void CheckRoundTrip(List<object> written, ExportResult r)
    {
        Dictionary<int, object> back;
        try { back = GameBp.ByIndex(r.Reimported); }
        catch (Exception e) { r.Errors.Add("round trip: cannot read re-imported records: " + e.Message); return; }
        int bad = 0;
        for (int idx = 0; idx < written.Count; idx++)
        {
            object w = written[idx], g;
            if (!back.TryGetValue(idx, out g)) { Diff(r, ref bad, "round trip: record " + idx + " missing"); continue; }
            foreach (string f in new[] { "itemId", "modelIndex", "recipeId", "filterId", "outputToSlot", "inputFromSlot", "outputFromSlot", "inputToSlot", "outputOffset", "inputOffset" })
                if (GameBp.In(g, f) != GameBp.In(w, f)) Diff(r, ref bad, "round trip: record " + idx + " " + f + " " + GameBp.In(g, f) + ", written " + GameBp.In(w, f));
            foreach (string f in new[] { "localOffset_x", "localOffset_y", "localOffset_z", "localOffset_x2", "localOffset_y2", "localOffset_z2", "yaw", "yaw2" })
                if (Math.Abs(GameBp.Fl(g, f) - GameBp.Fl(w, f)) > 1e-3f) Diff(r, ref bad, "round trip: record " + idx + " " + f + " " + GameBp.Fl(g, f) + ", written " + GameBp.Fl(w, f));
            if (ParamText((int[])GameBp.Get(g, "parameters")) != ParamText((int[])GameBp.Get(w, "parameters")))
                Diff(r, ref bad, "round trip: record " + idx + " parameters " + ParamText((int[])GameBp.Get(g, "parameters")) + ", written " + ParamText((int[])GameBp.Get(w, "parameters")));
            object wo = GameBp.Get(w, "outputObj"), wi = GameBp.Get(w, "inputObj");
            int wantOut = wo != null ? GameBp.In(wo, "index") : -1, wantIn = wi != null ? GameBp.In(wi, "index") : -1;
            object o = GameBp.Get(g, "outputObj"), i = GameBp.Get(g, "inputObj");
            int gotOut = o != null ? GameBp.In(o, "index") : -1, gotIn = i != null ? GameBp.In(i, "index") : -1;
            if (gotOut != wantOut || gotIn != wantIn) Diff(r, ref bad, "round trip: record " + idx + " links out " + gotOut + "/in " + gotIn + ", expected " + wantOut + "/" + wantIn);
        }
        if (bad > 10) r.Errors.Add("round trip: " + bad + " differences in total");
    }

    static void Diff(ExportResult r, ref int bad, string m) { if (++bad <= 10) r.Errors.Add(m); }

    static string ParamText(int[] p)
    {
        if (p == null) return "[]";
        // trailing zeros do not matter to the game ([0] == [] for machines)
        int n = p.Length; while (n > 0 && p[n - 1] == 0) n--;
        var sb = new StringBuilder("[");
        for (int k = 0; k < n; k++) { if (k > 0) sb.Append(','); sb.Append(p[k].ToString(CultureInfo.InvariantCulture)); }
        return sb.Append(']').ToString();
    }

    /// Check 8: export result (re-import count, MD5F, links and input-belt icons survived) against the layout.
    public static void CheckExport(ExportResult export, Layout layout, ValidationReport report)
    {
        if (report == null) throw new ArgumentNullException("report");
        if (export == null) { report.Fail("export: no result"); return; }
        if (!export.Ok) report.Fail("export: round trip failed (" + export.Errors.Count + " error(s))");
        if (!export.SignatureValid) report.Fail("export: MD5F signature invalid");
        int n = layout != null ? layout.All.Count : 0;
        if (export.BuildingsWritten != n) report.Fail("export: wrote " + export.BuildingsWritten + " buildings, layout has " + n);
        if (export.BuildingsReimported != n) report.Fail("export: re-import has " + export.BuildingsReimported + " buildings, layout has " + n);
        if (export.Reimported == null || layout == null) { report.Fail("export: nothing re-imported to compare"); return; }
        try
        {
            Dictionary<int, object> recs = GameBp.ByIndex(export.Reimported);
            int bad = 0, badLinks = 0;
            foreach (Bld b in layout.All)
            {
                object rec;
                if (b.Index < 0 || !recs.TryGetValue(b.Index, out rec)) { report.Fail("export: no re-imported record for " + b + " (index " + b.Index + ")"); bad++; continue; }
                int item = GameBp.In(rec, "itemId");
                double rx = GameBp.Fl(rec, "localOffset_x"), ry = GameBp.Fl(rec, "localOffset_y");
                if (item != b.ItemId || Math.Abs(rx - b.X) > 0.51 || Math.Abs(ry - b.Y) > 0.51)
                {
                    if (bad++ < 10) report.Fail("export: record " + b.Index + " is item " + item + " at " + F(rx) + "," + F(ry) + ", layout has " + b);
                    continue;
                }
                if (!SameLink(GameBp.Get(rec, "outputObj"), b.Output)) { if (badLinks++ < 10) report.Fail("export: output link of " + b + " did not round-trip"); }
                if (!SameLink(GameBp.Get(rec, "inputObj"), b.Input)) { if (badLinks++ < 10) report.Fail("export: input link of " + b + " did not round-trip"); }
            }
            if (bad > 10) report.Fail("export: " + bad + " record mismatches in total");
            if (badLinks > 10) report.Fail("export: " + badLinks + " broken links in total");
            int icons = 0;
            foreach (Port p in layout.Ports)
            {
                if (p.Kind != PortKind.In || p.Belt == null || p.Seam) continue;
                object rec;
                if (!recs.TryGetValue(p.Belt.Index, out rec)) continue;   // reported above
                int[] par = GameBp.Get(rec, "parameters") as int[];
                double rx = GameBp.Fl(rec, "localOffset_x"), ry = GameBp.Fl(rec, "localOffset_y");
                bool sameTile = (int)Math.Round(rx) == p.X && (int)Math.Round(ry) == p.Y;
                if (!sameTile || par == null || par.Length < 2 || par[0] != p.Item || par[1] != 0)
                    report.Fail("export: input-belt icon [" + p.Item + ",0] missing after re-import at (" + p.X + "," + p.Y + ")");
                else icons++;
            }
            report.Note("export: " + export.BuildingsReimported + " buildings re-imported, signature " + (export.SignatureValid ? "valid" : "INVALID") + ", " + icons + " input-belt icons round-tripped");
        }
        catch (Exception e)
        {
            report.Fail("export: comparison crashed: " + (e.InnerException ?? e).Message);
        }
    }

    static bool SameLink(object recLink, Bld want)
    {
        if (want == null) return recLink == null;
        return recLink != null && GameBp.In(recLink, "index") == want.Index;
    }

    static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
}
}
