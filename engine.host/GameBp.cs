// GameBp.cs - the only bridge to the game's own blueprint code (Assembly-CSharp.dll, loaded read-only through
// reflection). Load/Import a blueprint string, reflection field helpers, header fill, Export through the game's
// BlueprintData.ToBase64String with a re-import and an MD5F signature check. Extracted from MakeCoilFactoryV3.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Linq;
using System.Text;
using DspFlat.Engine;

namespace DspFlat.Host
{

public class ExportResult
{
    public string Text;                       // full blueprint string (header + base64 + "\"" + MD5F)
    public object Reimported;                 // BlueprintData parsed back from Text by the game's Import
    public int BuildingsWritten, BuildingsReimported;
    public bool SignatureValid;
    public readonly List<string> Errors = new List<string>();
    public bool Ok { get { return Errors.Count == 0; } }
}

public static class GameBp
{
    public const string DefaultManaged = @"C:\Program Files\Steam\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed\Assembly-CSharp.dll";
    public const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    public static Assembly Asm;
    public static Type TBp, TBuilding;
    public static bool Ready { get { return Asm != null; } }

    /// Loads the game assembly once. Sets the invariant culture (the game formats floats with the current one).
    public static void Init(string managedDll)
    {
        if (Asm != null) return;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        Asm = Assembly.LoadFrom(managedDll ?? DefaultManaged);
        TBp = Asm.GetType("BlueprintData");
        TBuilding = Asm.GetType("BlueprintBuilding");
        if (TBp == null || TBuilding == null) throw new Exception("BlueprintData/BlueprintBuilding not found in " + (managedDll ?? DefaultManaged));
        string v = InstalledGameVersion();
        if (v != null) Emitter.DefaultGameVersion = v;   // header version of every blueprint this process writes
    }
    static void Need() { if (Asm == null) throw new InvalidOperationException("GameBp.Init was not called"); }

    // ---- reflection helpers ----
    public static object Get(object o, string n) { return Field(o, n).GetValue(o); }
    public static void Set(object o, string n, object v)
    {
        FieldInfo f = Field(o, n);
        if (v != null && f.FieldType.IsPrimitive && v.GetType() != f.FieldType) v = Convert.ChangeType(v, f.FieldType);
        f.SetValue(o, v);
    }
    public static float Fl(object o, string n) { return (float)Get(o, n); }
    public static int In(object o, string n) { return Convert.ToInt32(Get(o, n)); }
    public static object Clone(object b) { return typeof(object).GetMethod("MemberwiseClone", BF).Invoke(b, null); }
    static FieldInfo Field(object o, string n)
    {
        FieldInfo f = o.GetType().GetField(n, BF);
        if (f == null) throw new MissingFieldException(o.GetType().Name, n);
        return f;
    }

    // ---- load ----
    /// Parses a blueprint file with the game's BlueprintData.Import (fails on trailing bytes).
    public static object Load(string path) { return Import(File.ReadAllText(path), path); }

    /// Parses a blueprint string (header,"base64"MD5F) with the game's BlueprintData.Import.
    public static object Import(string text, string what)
    {
        Need();
        text = text.Trim();
        int q1 = text.IndexOf('"'), q2 = text.LastIndexOf('"');
        if (q1 < 0 || q2 <= q1) throw new FormatException("not a blueprint string: " + what);
        var ms = new MemoryStream();
        using (var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(text.Substring(q1 + 1, q2 - q1 - 1))), CompressionMode.Decompress)) gz.CopyTo(ms);
        ms.Position = 0;
        object bp = Activator.CreateInstance(TBp);
        TBp.GetMethod("Import").Invoke(bp, new object[] { new BinaryReader(ms) });
        if (ms.Position != ms.Length) throw new Exception("trailing bytes in " + what);
        return bp;
    }

    public static Array Buildings(object bp) { return (Array)Get(bp, "buildings"); }

    /// Building records keyed by their "index" field (the index other records link to).
    public static Dictionary<int, object> ByIndex(object bp)
    {
        var d = new Dictionary<int, object>();
        foreach (object b in Buildings(bp)) d[In(b, "index")] = b;
        return d;
    }

    // ---- build ----
    /// Replaces the building array of bp with the given records, renumbering index (0..n-1), areaIndex 0
    /// and clearing the temp link indices; links stay as object references (outputObj/inputObj).
    public static void SetBuildings(object bp, IList<object> records)
    {
        Need();
        for (int i = 0; i < records.Count; i++)
        {
            object b = records[i];
            Set(b, "index", i); Set(b, "areaIndex", 0); Set(b, "tempOutputObjIdx", -1); Set(b, "tempInputObjIdx", -1);
        }
        Array arr = Array.CreateInstance(TBuilding, records.Count);
        for (int i = 0; i < records.Count; i++) arr.SetValue(records[i], i);
        Set(bp, "buildings", arr);
    }

    /// Header fill as in MakeCoilFactoryV3: one area of w x h, cursor at its center, layout 10, icons, version, texts.
    /// bp must be an imported blueprint (it supplies the areas array); its buildings are not touched.
    public static void FillHeader(object bp, BpHeader h)
    {
        Need();
        object area = ((Array)Get(bp, "areas")).GetValue(0);
        Set(area, "width", h.Width); Set(area, "height", h.Height);
        Set(bp, "cursorOffset_x", h.Width / 2); Set(bp, "cursorOffset_y", h.Height / 2); Set(bp, "cursorTargetArea", 0);
        Set(bp, "dragBoxSize_x", h.Width); Set(bp, "dragBoxSize_y", h.Height); Set(bp, "primaryAreaIdx", 0);
        Set(bp, "reformData", null);
        Set(bp, "layout", Enum.ToObject(TBp.GetField("layout", BF).FieldType, 10));
        for (int i = 0; i < 5; i++) Set(bp, "icon" + i, h.Icons != null && i < h.Icons.Length ? h.Icons[i] : 0);
        Set(bp, "time", DateTime.Now);
        Set(bp, "gameVersion", h.GameVersion);
        Set(bp, "shortDesc", h.ShortDesc ?? ""); Set(bp, "author", ""); Set(bp, "customVersion", ""); Set(bp, "externalFields", "");
        Set(bp, "desc", h.Desc ?? "");
    }

    /// MD5F signature the game computes over everything before the last quote.
    public static string Md5F(string content)
    {
        Need();
        return (string)Asm.GetType("MD5F").GetMethod("Compute", SF, null, new[] { typeof(string) }, null).Invoke(null, new object[] { content });
    }

    /// Serializes bp with the game's BlueprintData.ToBase64String, re-imports the string with the game's Import
    /// and checks building count and MD5F. Nothing is written to disk; check result.Ok before writing result.Text.
    public static ExportResult Export(object bp)
    {
        Need();
        var r = new ExportResult();
        r.BuildingsWritten = Buildings(bp).Length;
        r.Text = (string)TBp.GetMethod("ToBase64String").Invoke(bp, null);
        try
        {
            r.Reimported = Import(r.Text, "export");
            r.BuildingsReimported = Buildings(r.Reimported).Length;
            if (r.BuildingsReimported != r.BuildingsWritten) r.Errors.Add("re-import building count " + r.BuildingsReimported + ", written " + r.BuildingsWritten);
        }
        catch (Exception e) { r.Errors.Add("re-import failed: " + (e.InnerException ?? e).Message); }
        int last = r.Text.LastIndexOf('"');
        string content = r.Text.Substring(0, last), sig = r.Text.Substring(last + 1);
        r.SignatureValid = sig == Md5F(content);
        if (!r.SignatureValid) r.Errors.Add("signature mismatch");
        return r;
    }

    /// Writes a blueprint string as UTF-8 without BOM (what the game expects).
    public static void Write(string path, string text) { File.WriteAllText(path, text, new UTF8Encoding(false)); }

    // ---- engine records (DspFlat.Engine.BpBuilding mirrors every BlueprintBuilding field) ----
    /// Field names copied 1:1 between the game's BlueprintBuilding and BpBuilding (links handled separately).
    public static readonly string[] RecordFields = { "index", "areaIndex", "localOffset_x", "localOffset_y", "localOffset_z",
        "localOffset_x2", "localOffset_y2", "localOffset_z2", "pitch", "pitch2", "yaw", "yaw2", "tilt", "tilt2", "itemId", "modelIndex",
        "tempOutputObjIdx", "tempInputObjIdx", "outputToSlot", "inputFromSlot", "outputFromSlot", "inputToSlot", "outputOffset", "inputOffset",
        "recipeId", "filterId", "parameters", "content" };

    /// Game BlueprintBuilding -> engine record (parameters deep-copied; outputObj/inputObj become their "index" or -1).
    public static BpBuilding ToRecord(object g)
    {
        Need();
        var r = new BpBuilding();
        Type t = typeof(BpBuilding);
        foreach (string n in RecordFields)
        {
            FieldInfo gf = TBuilding.GetField(n, BF);
            if (gf == null) continue;   // older/newer game build without the field
            object v = gf.GetValue(g);
            if (v is int[]) v = ((int[])v).Clone();
            t.GetField(n).SetValue(r, v);
        }
        object o = Get(g, "outputObj"), i = Get(g, "inputObj");
        r.tempOutputObjIdx = o != null ? In(o, "index") : -1;
        r.tempInputObjIdx = i != null ? In(i, "index") : -1;
        return r;
    }

    /// Every building of an imported blueprint as engine records, in the game's array order (the inverse of FromRecords;
    /// links are the linked records' "index" fields, which BpImport.ToLayout resolves).
    public static List<BpBuilding> ToRecords(object bp)
    {
        var l = new List<BpBuilding>();
        foreach (object g in Buildings(bp)) l.Add(ToRecord(g));
        return l;
    }

    /// Engine records -> new game BlueprintBuilding objects with outputObj/inputObj wired from tempOutputObjIdx /
    /// tempInputObjIdx (indices into the list). Errors (unknown field, bad link index) go to errors.
    public static List<object> FromRecords(IList<BpBuilding> recs, List<string> errors)
    {
        Need();
        var objs = new List<object>(recs.Count);
        Type t = typeof(BpBuilding);
        foreach (BpBuilding r in recs)
        {
            object g = Activator.CreateInstance(TBuilding, true);
            foreach (string n in RecordFields)
            {
                if (TBuilding.GetField(n, BF) == null) continue;
                object v = t.GetField(n).GetValue(r);
                if (v is int[]) v = ((int[])v).Clone();
                Set(g, n, v);
            }
            objs.Add(g);
        }
        for (int k = 0; k < recs.Count; k++)
        {
            Set(objs[k], "outputObj", Link(objs, recs[k].tempOutputObjIdx, k, "output", errors));
            Set(objs[k], "inputObj", Link(objs, recs[k].tempInputObjIdx, k, "input", errors));
        }
        return objs;
    }

    static object Link(List<object> objs, int idx, int from, string what, List<string> errors)
    {
        if (idx < 0) return null;
        if (idx >= objs.Count) { errors.Add("record " + from + ": " + what + " link index " + idx + " outside the record list"); return null; }
        return objs[idx];
    }

    /// Version string the installed game writes into blueprints: GameConfig.gameVersion (major.minor.release) plus the
    /// build number of the last line of <game>\Updates\Versions.txt (what GlobalObject.ReadVersionList does at start-up).
    /// null when GameBp is not initialised or the files do not agree.
    public static string InstalledGameVersion()
    {
        if (!Ready) return null;
        try
        {
            object ver = Asm.GetType("GameConfig").GetProperty("gameVersion", SF).GetValue(null, null);
            string mmr = ver.ToString();                                   // "0.10.35"
            string gameDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Asm.Location), @"..\.."));
            string list = Path.Combine(gameDir, @"Updates\Versions.txt");
            if (!File.Exists(list)) return null;
            string last = File.ReadAllLines(list).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            if (last == null) return null;
            string full = last.Split(',')[0].Trim();                       // "0.10.35.29151"
            return full.StartsWith(mmr + ".") ? full : null;
        }
        catch (Exception) { return null; }
    }
}
}
