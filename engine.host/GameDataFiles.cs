// GameDataFiles.cs - CLI-side loader of DspFlat.Engine.GameData from the files extracted from the game:
// analysis\game-data.json (items, recipes, buildings), analysis\buildings\colliders.json (exact build colliders),
// analysis\raw-protos.json (item icon tags) and item-ids.json (extra name aliases). Moved out of the engine core
// (former GameData.Load) so the core does no file I/O. Windows / .NET Framework (System.Web.Extensions).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using DspFlat.Engine;

namespace DspFlat.Host
{
public static class GameDataFiles
{
    /// Loads analysis\game-data.json, colliders.json and raw-protos.json next to it, and (optional) item-ids.json.
    public static GameData Load(string gameDataJson, string itemIdsJson)
    {
        var gd = new GameData { SourcePath = gameDataJson };
        var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(gameDataJson, Encoding.UTF8));

        foreach (Dictionary<string, object> o in (object[])root["items"])
            gd.AddItem(new ItemDef { Id = I(o, "id"), Name = S(o, "name"), Type = S(o, "type"), IsFluid = B(o, "isFluid"),
                StackSize = I(o, "stackSize"), CanBuild = B(o, "canBuild") });

        foreach (Dictionary<string, object> o in (object[])root["recipes"])
        {
            var r = new RecipeDef { Id = I(o, "id"), Name = S(o, "name"), Type = S(o, "type"), TimeSec = D(o, "timeSec"), Explicit = B(o, "explicit") };
            foreach (Dictionary<string, object> x in Arr(o, "inputs")) r.Inputs.Add(new ItemCount(I(x, "item"), I(x, "count")));
            foreach (Dictionary<string, object> x in Arr(o, "outputs")) r.Outputs.Add(new ItemCount(I(x, "item"), I(x, "count")));
            r.Buildings = Arr(o, "buildings").Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray();
            gd.Recipes[r.Id] = r;
        }

        foreach (Dictionary<string, object> o in (object[])root["buildings"])
        {
            var m = new MachineDef { ItemId = I(o, "itemId"), Name = S(o, "name"), ModelIndex = I(o, "modelIndex"),
                RecipeType = S(o, "recipeType"), SpeedMultiplier = D(o, "speedMultiplier"), BeltItemsPerSec = D(o, "beltItemsPerSec"),
                SorterMaxLength = I(o, "sorterMaxLength"), PowerConnectDistanceM = D(o, "powerConnectDistance"),
                PowerCoverRadiusM = D(o, "powerCoverRadius"), IsSplitter = B(o, "isSplitter") };
            object ft;
            if (o.TryGetValue("footprintTiles", out ft) && ft is object[])
                m.FootprintTiles = ((object[])ft).Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
            object trips;
            if (o.TryGetValue("sorterTripsPerSec", out trips) && trips is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)trips)
                    m.SorterTripsPerSec[int.Parse(kv.Key, CultureInfo.InvariantCulture)] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
            gd.Machines[m.ItemId] = m;
        }

        // exact build colliders (analysis\buildings\colliders.json, AnalyzeColliders.exe)
        string dir = Path.GetDirectoryName(Path.GetFullPath(gameDataJson));
        string colPath = Path.Combine(Path.Combine(dir, "buildings"), "colliders.json");
        if (File.Exists(colPath)) LoadColliders(gd, colPath, js);

        // icon tags (ItemProtoSet IconTag) for the thumbnail text, analysis\raw-protos.json next to game-data.json
        string protos = Path.Combine(dir, "raw-protos.json");
        if (File.Exists(protos)) LoadIconTags(gd, protos);

        // item-ids.json (wiki names, e.g. "Iron ore") as extra aliases; game names win on conflict
        if (itemIdsJson != null && File.Exists(itemIdsJson))
        {
            var ids = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(itemIdsJson, Encoding.UTF8));
            foreach (var kv in ids)
            {
                int id; string name = kv.Value as string;
                if (name != null && int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) gd.AddName(name, id);
            }
        }
        return gd;
    }

    static void LoadColliders(GameData gd, string path, JavaScriptSerializer js)
    {
        gd.CollidersPath = path;
        var root = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
        foreach (Dictionary<string, object> b in Arr(root, "buildings"))
        {
            int item = I(b, "itemId");
            MachineDef md = gd.Machine(item);
            Dictionary<string, object> pick = null;
            foreach (Dictionary<string, object> m in Arr(b, "models"))
            {
                if (pick == null) pick = m;
                if (md != null && I(m, "modelId") == md.ModelIndex) { pick = m; break; }
            }
            if (pick == null) continue;
            var der = V(pick, "derived") as Dictionary<string, object>;
            if (der == null) continue;
            var def = new GameData.ColliderDef { ItemId = item, ModelId = I(pick, "modelId") };
            object bc = V(der, "buildColliders");
            var boxes = bc is object[] ? (object[])bc : bc is Dictionary<string, object> ? new object[] { bc } : new object[0];
            foreach (Dictionary<string, object> box in boxes)
            {
                double[] c = Nums(V(box, "center")), s = Nums(V(box, "size"));
                if (c.Length < 3 || s.Length < 3) continue;
                def.Boxes.Add(new GameData.ColliderBox { Cx = c[0], Cz = c[2], Hx = s[0] / 2, Hz = s[2] / 2 });
            }
            double[] fp = Nums(V(der, "footprintXZ_m"));
            if (fp.Length == 2) { def.FootW = fp[0]; def.FootH = fp[1]; }
            var dp = V(pick, "derived_pitch") as Dictionary<string, object>;
            if (dp != null)
                foreach (var kv in dp)
                {
                    var p = kv.Value as Dictionary<string, object>;
                    if (p == null) continue;
                    if (V(p, "minIntegerPitch") != null) def.Pitch[kv.Key] = I(p, "minIntegerPitch");
                    if (V(p, "minIntegerPitchEW_upTo60deg") != null) def.PitchEW60[kv.Key] = I(p, "minIntegerPitchEW_upTo60deg");
                }
            gd.AddCollider(def);
        }
    }

    /// ItemProtoSet IconTag per item id (the part of raw-protos.json before "RecipeProtoSet").
    static void LoadIconTags(GameData gd, string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        int end = text.IndexOf("\"RecipeProtoSet\"", StringComparison.Ordinal);
        if (end > 0) text = text.Substring(0, end);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
            "\"ID\":\\s*(\\d+),[^{}]*?\"IconTag\":\\s*\"([A-Za-z0-9]*)\""))
            gd.IconTags[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Groups[2].Value;
    }

    static double[] Nums(object o)
    {
        var a = o as object[];
        return a == null ? new double[0] : a.Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray();
    }

    // ---- json helpers ----
    static object V(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) ? v : null; }
    static int I(Dictionary<string, object> o, string k) { object v = V(o, k); return v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
    static double D(Dictionary<string, object> o, string k) { object v = V(o, k); return v == null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
    static string S(Dictionary<string, object> o, string k) { return V(o, k) as string; }
    static bool B(Dictionary<string, object> o, string k) { object v = V(o, k); return v is bool && (bool)v; }
    static object[] Arr(Dictionary<string, object> o, string k) { return V(o, k) as object[] ?? new object[0]; }
}
}
