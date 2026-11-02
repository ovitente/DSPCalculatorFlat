// Emitter.cs - abstract Layout -> BpBuilding records (plain mirror of the game's BlueprintBuilding) + header text.
// Every Bld is a field copy onto a clone of the proven prototype record for its item id (PrototypeCells.ProtoFor),
// links are indices into the record list. Serializing to a blueprint string (the game's BlueprintData, MD5F, re-import
// check) is the host's job: CLI DspFlat.Host.GameEmitter, addon through the game's own paste code. No game DLL here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DspFlat.Engine
{

public class Emitter
{
    readonly GameData gd;
    readonly PrototypeCells cells;

    /// Tolerance for the grid snap: a coordinate within this distance of a 0.5 step is snapped; anything further
    /// away (splitter belts at 0.199 / 1.073 / 0.927 from the splitter center) is kept as given.
    const double SnapTolerance = 0.02;

    /// Game version written into the header by HeaderFor when the caller gives none. Hosts set it once from the
    /// installed game (CLI: GameBp Init -> InstalledGameVersion; FlatEngine: IGameSource.GameVersion).
    public static string DefaultGameVersion = "0.10.35.29151";

    public Emitter(GameData gd, PrototypeCells cells) { this.gd = gd; this.cells = cells; }

    /// Abstract layout -> blueprint building records: every Bld is a field copy onto a clone of the proven prototype
    /// record for its item id (cells.ProtoFor) or named cell; links become indices (tempOutputObjIdx / tempInputObjIdx)
    /// into the returned list, which is in layout.All order (layout.Reindex()). Problems go to errors (null returned).
    public List<BpBuilding> Records(Layout layout, List<string> errors)
    {
        if (errors == null) errors = new List<string>();
        if (cells == null) { errors.Add("emitter: no prototype cells"); return null; }
        int start = errors.Count;
        layout.Reindex();
        var inLayout = new HashSet<Bld>(layout.All);
        var records = new List<BpBuilding>(layout.All.Count);

        // 1. clone + field copy
        foreach (Bld b in layout.All)
        {
            // named prototypes of the user's in-game cells: depot buffer sorters (game pitch/tilt), splitter Tab variants
            BpBuilding proto = null;
            if (b.Cell != null && b.Cell.StartsWith("depot-", StringComparison.Ordinal)) proto = cells.Proto(b.Cell);
            if (b.Kind == Kind.Splitter && (b.ModelIndex == 39 || b.ModelIndex == 40)) proto = cells.Proto("splitter-m" + b.ModelIndex);
            if (proto == null) proto = cells.ProtoFor(b.ItemId);
            if (proto == null) { errors.Add("emitter: no prototype for item " + b.ItemId + " (" + b + ")"); continue; }
            if (proto.itemId != b.ItemId) { errors.Add("emitter: prototype for item " + b.ItemId + " is item " + proto.itemId); continue; }
            BpBuilding g = proto.Clone();
            bool snap = b.Kind != Kind.Sorter && b.Cell != SplitterGeo.BeltCell;
            g.index = b.Index; g.areaIndex = 0;
            g.localOffset_x = Snap(b.X, snap); g.localOffset_y = Snap(b.Y, snap); g.localOffset_z = (float)b.Z;
            g.localOffset_x2 = Snap(b.X2, snap); g.localOffset_y2 = Snap(b.Y2, snap); g.localOffset_z2 = (float)b.Z2;
            g.yaw = (float)b.Yaw; g.yaw2 = (float)b.Yaw2;
            g.itemId = (short)b.ItemId;
            g.modelIndex = b.ModelIndex > 0 ? (short)b.ModelIndex : proto.modelIndex;
            g.recipeId = b.RecipeId; g.filterId = b.FilterId;
            g.outputToSlot = b.OutputToSlot; g.inputFromSlot = b.InputFromSlot;
            g.outputFromSlot = b.OutputFromSlot; g.inputToSlot = b.InputToSlot;
            g.outputOffset = b.OutputOffset; g.inputOffset = b.InputOffset;
            g.parameters = ParametersFor(b, proto.parameters);
            g.tempOutputObjIdx = -1; g.tempInputObjIdx = -1;
            records.Add(g);
        }
        if (errors.Count > start) return null;

        // 2. links (indices into the record list = Bld.Index)
        for (int i = 0; i < layout.All.Count; i++)
        {
            Bld b = layout.All[i];
            records[i].tempOutputObjIdx = LinkTarget(b, b.Output, "output", inLayout, errors);
            records[i].tempInputObjIdx = LinkTarget(b, b.Input, "input", inLayout, errors);
        }
        return errors.Count > start ? null : records;
    }

    static float Snap(double v, bool snap)
    {
        if (!snap) return (float)v;
        double s = Math.Round(v * 2.0) / 2.0;
        return Math.Abs(s - v) <= SnapTolerance ? (float)s : (float)v;
    }

    /// Bld.Parameters when set; otherwise a deep copy of the prototype's. A belt without Parameters never inherits
    /// an icon from its prototype (the icon marks a block input chain only, rule 5).
    static int[] ParametersFor(Bld b, int[] proto)
    {
        if (b.Parameters != null) return (int[])b.Parameters.Clone();
        if (proto == null) return null;
        int[] p = (int[])proto.Clone();
        if (b.Kind == Kind.Belt) Array.Clear(p, 0, p.Length);
        return p;
    }

    static int LinkTarget(Bld from, Bld to, string what, HashSet<Bld> inLayout, List<string> errors)
    {
        if (to == null) return -1;
        if (!inLayout.Contains(to) || to.Index < 0) { errors.Add("emitter: " + what + " link of " + from + " points to a building outside the layout (" + to + ")"); return -1; }
        return to.Index;
    }
    // ------------------------------------------------------------------ header

    /// Header from the layout bounds and the plan: size, icon0 = product, icon1 = machine of the final step,
    /// shortDesc = thumbnail icon text (BpText.BlockThumb: machines of the final step, machine icon, input lanes, belt
    /// icon), desc = Composer.Describe (title line + "- " fact lines, real line breaks). gameVersion = installed game.
    public static BpHeader HeaderFor(Layout layout, ProductionPlan plan, BlockSpec spec, GameData gd)
    {
        return HeaderFor(layout, plan, spec, gd, null);
    }

    /// HeaderFor with an explicit game version (null = DefaultGameVersion).
    public static BpHeader HeaderFor(Layout layout, ProductionPlan plan, BlockSpec spec, GameData gd, string gameVersion)
    {
        var c = CultureInfo.InvariantCulture;
        var h = new BpHeader();
        Rect bounds = layout.Bounds();
        h.Width = bounds.Width; h.Height = bounds.Height;
        int target = plan != null && plan.TargetItem != 0 ? plan.TargetItem : spec != null ? spec.Item : 0;
        double rate = plan != null && plan.TargetRate > 0 ? plan.TargetRate : spec != null ? spec.Rate : 0;
        h.Icons = new[] { target, 0, 0, 0, 0 };
        h.ShortDesc = Name(gd, target) + " " + rate.ToString("0.##", c) + "/min";
        h.GameVersion = gameVersion ?? DefaultGameVersion ?? h.GameVersion;

        var sb = new StringBuilder();
        if (spec != null && plan != null)
        {
            Step last = plan.StepFor(target);
            int lanes = layout.Ports.Count(p => p.Kind == PortKind.In);
            if (last != null)
            {
                h.Icons[1] = last.MachineItem;
                string thumb = BpText.BlockThumb(gd, last.Machines, last.MachineItem, lanes, spec.Tiers != null ? spec.Tiers.Belt : 0);
                if (thumb != null) h.ShortDesc = thumb;
            }
            // title + fact lines: Composer's description (one wording for the plan and the header)
            sb.Append(Composer.Describe(layout, plan, spec, gd));
            if (plan.SuggestedRate > 0 && Math.Abs(plan.SuggestedRate - plan.TargetRate) > 1e-6)
                sb.Append("\n- clean ratio: ").Append(plan.SuggestedRate.ToString("0.##", c)).Append("/min");
            h.Desc = sb.ToString();
            return h;
        }
        // ports, grouped by kind and edge: "IN (S edge): Copper Ore 180/min, Iron Ore 360/min."
        foreach (PortKind k in new[] { PortKind.In, PortKind.Out })
        {
            var byEdge = new SortedDictionary<int, List<string>>();
            foreach (Port p in layout.Ports.Where(p => p.Kind == k))
            {
                Dir edge = k == PortKind.In ? Dirs.Opposite(p.Dir) : p.Dir;
                if (!byEdge.ContainsKey((int)edge)) byEdge[(int)edge] = new List<string>();
                byEdge[(int)edge].Add(Name(gd, p.Item) + " " + p.Rate.ToString("0.##", c) + "/min at (" + p.X + "," + p.Y + ")");
            }
            foreach (var kv in byEdge)
                sb.Append(k == PortKind.In ? "IN" : "OUT").Append(" (").Append(((Dir)kv.Key).ToString()).Append(" edge): ")
                  .Append(string.Join(", ", kv.Value.ToArray())).Append(". ");
        }
        // machines per (item, recipe), in layout order
        var order = new List<string>(); var count = new Dictionary<string, int>();
        foreach (Bld b in layout.All)
        {
            if (b.Kind != Kind.Machine) continue;
            RecipeDef r = gd != null ? gd.Recipe(b.RecipeId) : null;
            string key = Name(gd, b.ItemId) + " (" + (r != null ? r.Name : "recipe " + b.RecipeId) + ")";
            if (!count.ContainsKey(key)) { count[key] = 0; order.Add(key); }
            count[key]++;
        }
        if (order.Count > 0) sb.Append("Machines: ").Append(string.Join(", ", order.Select(k => count[k] + " " + k).ToArray())).Append(". ");
        AppendSorters(sb, layout);
        Tiers t = spec != null ? spec.Tiers : null;
        if (t != null) sb.Append("Tiers: ").Append(Name(gd, t.Belt)).Append(", ").Append(Name(gd, t.Sorter)).Append(". ");
        if (plan != null && plan.SuggestedRate > 0 && Math.Abs(plan.SuggestedRate - plan.TargetRate) > 1e-6)
            sb.Append("Clean-ratio rate: ").Append(plan.SuggestedRate.ToString("0.##", c)).Append("/min. ");
        sb.Append("Generated by MakeBlock.");
        h.Desc = sb.ToString();
        return h;
    }

    /// "Sorters: 20 input L1, 12 output L1, 4 output L2. "
    static void AppendSorters(StringBuilder sb, Layout layout)
    {
        var sorters = new SortedDictionary<string, int>();
        foreach (Bld s in layout.All)
        {
            if (s.Kind != Kind.Sorter) continue;
            bool input = s.Input != null && s.Input.Kind == Kind.Belt;
            string key = (input ? "input" : "output") + " L" + s.SorterLength;
            sorters[key] = sorters.ContainsKey(key) ? sorters[key] + 1 : 1;
        }
        if (sorters.Count > 0) sb.Append("Sorters: ").Append(string.Join(", ", sorters.Select(kv => kv.Value + " " + kv.Key).ToArray())).Append(". ");
    }

    static string Name(GameData gd, int id) { return gd != null ? gd.ItemName(id) : "item " + id; }
}

/// Blueprint header text in the game's own formats (analysis\blueprint-text-tags.md).
/// Icon tags: "\" + IconTag + ";" is drawn as that icon by the game's Text (UnityEngine.UI.dll
/// Youthcat.TextGenerator.Populate -> TextIconMapping.GetIconInString, tag of 1..6 chars). Signals 600..609 are
/// the digits 0..9 (\s600; .. \s609;). The browser shows only the first 40 characters of shortDesc
/// (UIBlueprintFileItem.SetThumbnailLayout: Substring(0, 40)), so a thumbnail is kept at <= 40 characters and
/// never cut inside a tag. desc keeps real '\n' line breaks; the game URL-escapes the header fields
/// (Uri.EscapeDataString -> "%0A").
public static class BpText
{
    public const int ThumbMax = 40;


    /// Digit icons of n, left-padded with zeros to minDigits: 4 -> "\s604;", (4, 2) -> "\s600;\s604;".
    public static string Digits(int n, int minDigits)
    {
        string s = Math.Max(0, n).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, minDigits), '0');
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(@"\s60").Append(ch).Append(';');
        return sb.ToString();
    }
    public static string Digits(int n) { return Digits(n, 1); }

    /// Icon tag of an item ("\zzt1;" for Assembling Machine Mk.I); null when the item's tag is unknown. Tags come
    /// from GameData.IconTags (ItemProtoSet IconTag; the CLI host reads analysis\raw-protos.json, the addon LDB).
    public static string Icon(GameData gd, int itemId)
    {
        string t;
        return itemId > 0 && Tags(gd).TryGetValue(itemId, out t) && t.Length >= 1 && t.Length <= 6 ? "\\" + t + ";" : null;
    }

    static readonly Dictionary<int, string> NoTags = new Dictionary<int, string>();
    static Dictionary<int, string> Tags(GameData gd) { return gd != null ? gd.IconTags : NoTags; }
    /// Joins thumbnail parts (null parts skipped); drops parts from the end until the text fits ThumbMax.
    public static string Thumb(params string[] parts)
    {
        var list = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        while (list.Count > 0 && string.Concat(list.ToArray()).Length > ThumbMax) list.RemoveAt(list.Count - 1);
        return list.Count > 0 ? string.Concat(list.ToArray()) : null;
    }

    /// Block thumbnail in the user's template style ("\s608;\zzt1;\s601;\csd2;" = 8 Mk.I assemblers, 1 lane, Mk.II
    /// belt): machine count of the final step, its machine icon, number of input lanes, belt icon.
    public static string BlockThumb(GameData gd, int machines, int machineItem, int lanes, int beltItem)
    {
        return Thumb(Digits(machines), Icon(gd, machineItem), lanes > 0 ? Digits(lanes) : null, lanes > 0 ? Icon(gd, beltItem) : null);
    }

    /// Kit module thumbnail: module number (2 digits), machine icon, machine count: 04 + 9 arc smelters ->
    /// "\s600;\s604;\<smelter tag>;\s609;".
    public static string KitThumb(GameData gd, int moduleNo, int machineItem, int machines)
    {
        return Thumb(Digits(moduleNo, 2), Icon(gd, machineItem), Digits(machines));
    }

    /// Title line plus one "- fact" line per entry, '\n' between lines.
    public static string Lines(string title, IEnumerable<string> facts)
    {
        var sb = new StringBuilder(title ?? "");
        foreach (string f in facts) if (!string.IsNullOrEmpty(f)) sb.Append("\n- ").Append(f);
        return sb.ToString();
    }

    /// Tier name without the family prefix: "Conveyor Belt Mk.II" -> "Mk.II", "Sorter Mk.II" -> "Mk.II".
    public static string Tier(string name)
    {
        if (name == null) return "?";
        int i = name.LastIndexOf(' ');
        string last = i >= 0 ? name.Substring(i + 1) : name;
        return last.StartsWith("Mk.", StringComparison.Ordinal) ? last : name;
    }
}

/// Blueprint header values (area size, icons, texts, game version). The host writes them into the game's BlueprintData.
public class BpHeader
{
    public int Width, Height;                 // area size in tiles (also drag box; cursor = center)
    public int[] Icons = new int[5];          // icon0..icon4 (item ids, 0 = none)
    public string ShortDesc = "", Desc = "";
    public string GameVersion = "0.10.35.29151";
}
}
