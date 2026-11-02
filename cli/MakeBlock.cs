// MakeBlock.cs - declarative DSP block generator (CLI host of DspFlat.Engine): target item + rate -> validated blueprint
// for a full raw -> product block.
// Pipeline: GameData (FileGameSource) -> Solver -> PlanInput -> FlatEngine.Generate (Composer + Validator + Emitter
// records) -> GameEmitter (game BlueprintData, MD5F, re-import check) -> .txt
// Build: build.cmd at the repo root (Windows, .NET Framework csc, C# 5). See docs\engine-api.md.
//
// Usage:
//   MakeBlock.exe --item "Magnetic Coil" --rate 360 [--out file.txt] [--plan-only]
//                 [--asm mk1|mk2|mk3] [--smelter arc|plane] [--belt mk1|mk2] [--sorter mk1|mk2]
//                 [--raw "Copper Ingot,Iron Ingot"] [--recipe <item>=<recipeId>] [--clean]
//                 [--in-edge S] [--out-edge N] [--bp-dir <Blueprint folder>] [--game-dll <Assembly-CSharp.dll>]
//                 [--data-root <blueprint-tools folder>]
//   MakeBlock.exe --kit --item "Missile Turret" --rate 22.5 [--kit-dir <dir>] [--kit-work-dir <dir>] [--max-row 12]
//   MakeBlock.exe --selftest
//   MakeBlock.exe --check-gamebp <blueprint.txt>    (round-trips a blueprint through GameBp, writes nothing)
// --data-root: folder with analysis\game-data.json, item-ids.json and refs\ (blueprint-tools); default: env
// DSPFLAT_DATA_ROOT, else the exe folder.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DspFlat.Engine;
using DspFlat.Host;

static class MakeBlock
{
    public static readonly string DefaultBpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), @"Dyson Sphere Program\Blueprint");

    static int Main(string[] args)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        try { return Run(args); }
        catch (NotImplementedException e) { Console.WriteLine("NOT IMPLEMENTED YET: " + e.Message); return 4; }
        catch (ArgumentException e) { Console.WriteLine("error: " + e.Message); return 2; }
    }

    /// Data folder (blueprint-tools): --data-root, env DSPFLAT_DATA_ROOT, else the exe folder.
    public static string DataRoot(Dictionary<string, string> opt)
    {
        string r = Opt(opt, "data-root") ?? Environment.GetEnvironmentVariable("DSPFLAT_DATA_ROOT") ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.GetFullPath(r).TrimEnd('\\') + "\\";
    }

    static int Run(string[] args)
    {
        var opt = ParseArgs(args);
        string root = DataRoot(opt);
        Cells.ToolsRoot = root;
        EnvTuning.Apply();
        string bpDir = Opt(opt, "bp-dir") ?? DefaultBpDir;
        string gameDll = Opt(opt, "game-dll");

        if (opt.ContainsKey("check-gamebp")) return CheckGameBp(opt["check-gamebp"], gameDll);
        if (opt.ContainsKey("stats")) return Stats(opt["stats"], gameDll);

        if (opt.ContainsKey("selftest"))
        {
            GameBp.Init(gameDll);
            return SelfTest.Run(root, bpDir);
        }

        if (!opt.ContainsKey("item") || !opt.ContainsKey("rate")) { Usage(); return 2; }
        Progress.Plain = Console.IsOutputRedirected || opt.ContainsKey("quiet");
        Progress.Verbose = Progress.Plain || opt.ContainsKey("verbose");
        bool kitMode = opt.ContainsKey("kit");
        Progress.Total = kitMode ? 4 : 6;

        Progress.Begin(1, "Planning production chain");
        var gd = FileGameSource.LoadData(root);
        var spec = BuildSpec(opt, gd);
        var plan = new Solver(gd, spec).Solve();
        Progress.Done(plan.Steps.Count + " steps, " + plan.Steps.Sum(s => s.Machines) + " machines");
        Progress.Detail(Solver.PlanText(plan, gd));
        if (opt.ContainsKey("bus")) return RunBus(opt, gd, spec, plan, root, bpDir, gameDll);
        if (kitMode) return RunKit(opt, gd, spec, plan, root, bpDir, gameDll);
        if (opt.ContainsKey("plan-only")) { Summary(gd, spec, plan, null, null); return 0; }

        Progress.Begin(2, "Loading prototypes from the game files");
        var source = new FileGameSource(gd);
        source.LoadPrototypes(root, bpDir, gameDll);
        if (source.GameCells.Errors.Count > 0) { Progress.Fail("prototype errors"); foreach (var e in source.GameCells.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("file NOT written"); return 3; }
        Progress.Done(null);

        // one code path with the addon: the solved plan becomes an injected PlanInput
        Progress.Begin(3, "Laying out rows and routing belts");
        PlanInput input = PlanMapper.FromPlan(plan, spec);
        EngineResult res = FlatEngine.Generate(source, input, (f, s) => Progress.Update(s), null);
        Layout layout = res.Layout;
        if (layout != null)
        {
            Rect lb = layout.Bounds();
            Progress.Done(lb.Width + "x" + lb.Height + ", " + Composer.RouteStats(layout));
        }
        else Progress.Fail(res.Errors.Count > 0 ? res.Errors[0] : "no layout");
        foreach (string n in res.Notes) Progress.Detail("compose: " + n + "\n");
        foreach (string w in res.Warnings.Where(w => res.Report == null || !res.Report.Warnings.Contains(w))) Console.WriteLine("WARN: " + w);
        if (layout != null && opt.ContainsKey("map")) Console.Write(Composer.Map(layout));
        if (layout == null) { foreach (var e in res.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("file NOT written"); return 1; }

        Progress.Begin(4, "Validating");
        var report = res.Report ?? new ValidationReport();
        if (res.Report == null) foreach (var e in res.Errors) report.Fail(e);
        if (Progress.Verbose) report.Print(Console.WriteLine);
        if (!report.Ok) { Progress.Fail("validation: " + report.Errors.Count + " error(s)"); PrintErrors(report); Console.WriteLine("file NOT written"); return 1; }
        if (!res.Ok) { Progress.Fail(res.Errors.Count + " error(s)"); foreach (var e in res.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("file NOT written"); return 1; }
        Progress.Done(report.Warnings.Count + " warning(s)");

        Progress.Begin(5, "Exporting via game code");
        BpHeader header = res.Header;
        if (spec.Desc == null && plan.SuggestedRate > 0 && Math.Abs(plan.SuggestedRate - plan.TargetRate) > 1e-6)
            header.Desc += "\n- clean ratio: " + plan.SuggestedRate.ToString("0.##", CultureInfo.InvariantCulture) + "/min";
        var export = new GameEmitter(gd, source.GameCells).Serialize(res.Buildings, header, layout);
        Progress.Done(export.BuildingsWritten + " buildings");

        Progress.Begin(6, "Re-import + hash check");
        GameEmitter.CheckExport(export, layout, report);
        foreach (var e in export.Errors) report.Fail(e);
        if (!report.Ok) { Progress.Fail("export check failed"); PrintErrors(report); Console.WriteLine("file NOT written"); return 1; }
        string written = null;
        if (spec.OutPath != null) { GameBp.Write(spec.OutPath, export.Text); written = spec.OutPath; }
        Progress.Done(export.BuildingsReimported + " re-imported, signature " + (export.SignatureValid ? "valid" : "INVALID"));
        Summary(gd, spec, res.Plan ?? plan, layout, written);
        if (written == null) Console.WriteLine("ok, no --out given, nothing written");
        else Console.WriteLine("written " + written + " (" + export.BuildingsWritten + " buildings, " + export.Text.Length + " chars)");
        return 0;
    }
    static void PrintErrors(ValidationReport r)
    {
        if (Progress.Verbose) return;   // already printed with the report
        foreach (var m in r.Errors.Take(20)) Console.WriteLine("FAIL: " + m);
        if (r.Errors.Count > 20) Console.WriteLine("... " + (r.Errors.Count - 20) + " more");
    }

    /// Short final summary: item, rate, machines per step, raw inputs per minute with their edge, size, file.
    static void Summary(GameData gd, BlockSpec spec, ProductionPlan plan, Layout layout, string file)
    {
        var c = CultureInfo.InvariantCulture;
        Console.WriteLine("---- summary ----");
        Console.WriteLine("product:  " + gd.ItemName(plan.TargetItem) + " " + plan.TargetRate.ToString("0.##", c) + "/min, out on the " + spec.OutEdge + " edge" + (spec.Buffer ? " (buffer depot at the end)" : ""));
        foreach (var s in plan.Steps)
            Console.WriteLine("machines: " + s.Machines + " x " + gd.ItemName(s.MachineItem) + " -> " + gd.ItemName(s.Item) + (s.Recipe != null ? " (" + s.Recipe.Name + ")" : ""));
        foreach (var r in plan.RawInputs)
            Console.WriteLine("input:    " + gd.ItemName(r.Item) + " " + r.Rate.ToString("0.##", c) + "/min on the " + spec.InEdge + " edge"
                + (layout != null ? " (" + layout.Ports.Count(p => p.Kind == PortKind.In && p.Item == r.Item) + " belt(s))" : ""));
        if (layout != null)
        {
            Rect b = layout.Bounds();
            Console.WriteLine("size:     " + b.Width + "x" + b.Height + " tiles, " + layout.All.Count + " buildings (" + layout.Count(Kind.Belt, 0) + " belts, " + layout.Count(Kind.Tesla, 0) + " towers)");
        }
        if (file != null) Console.WriteLine("file:     " + file);
    }

    // ---- bus kit mode (lib\Bus.cs): assembled bus blueprint + one module per band ----
    static int RunBus(Dictionary<string, string> opt, GameData gd, BlockSpec spec, ProductionPlan plan, string root, string bpDir, string gameDll)
    {
        var bus = new Bus(gd, spec);
        bus.OnStep = s => Progress.Line(s);
        bus.Build(plan);
        if (bus.Assembled != null && opt.ContainsKey("map")) Console.Write(Composer.Map(bus.Assembled));
        if (bus.AssembledReport != null && Progress.Verbose) foreach (var w in bus.AssembledReport.Warnings) Console.WriteLine("WARN assembled: " + w);
        if (bus.Floor != null) foreach (var b in bus.Floor.Bands) if (b.Report != null && Progress.Verbose) foreach (var w in b.Report.Warnings) Console.WriteLine("WARN module " + b.No + ": " + w);
        if (bus.Errors.Count > 0) { Console.Write(bus.Summary()); foreach (var e in bus.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("nothing written"); return 1; }
        GameBp.Init(gameDll);
        var cells = Cells.Load(bpDir);
        if (cells.Errors.Count > 0) { foreach (var e in cells.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("nothing written"); return 3; }
        bus.Export(cells);
        Console.Write(bus.Summary());
        if (bus.Errors.Count > 0) { foreach (var e in bus.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("nothing written"); return 1; }
        string outPath = Opt(opt, "out"), kitDir = Opt(opt, "kit-dir");
        bus.Write(outPath, kitDir);
        if (outPath != null) Console.WriteLine("written " + outPath);
        if (kitDir != null) Console.WriteLine("written " + bus.Floor.Bands.Count + " modules + _intro_ to " + kitDir);
        return 0;
    }

    // ---- kit mode (analysis\modules-design.md section 1) ----
    static int RunKit(Dictionary<string, string> opt, GameData gd, BlockSpec spec, ProductionPlan plan, string root, string bpDir, string gameDll)
    {
        Progress.Begin(2, "Cutting the plan into module rows");
        var kit = new Kit(gd, spec);
        kit.MakeRows(plan);
        kit.MakeModules();
        Progress.Done(kit.Rows.Count + " rows, " + kit.Modules.Count + " modules, " + kit.Rows.Sum(r => r.Count) + " machines");
        if (opt.ContainsKey("plan-only") || kit.Errors.Count > 0)
        {
            Console.Write(kit.Summary());
            foreach (var e in kit.Errors) Console.WriteLine("FAIL: " + e);
            return kit.Errors.Count > 0 ? 1 : 0;
        }
        kit.OnModule = s => Progress.Line(s);
        Progress.Begin(3, "Building and validating modules");
        Progress.Done("");
        kit.BuildLayouts();
        foreach (var m in kit.Modules)
        {
            if (m.Report == null) continue;
            foreach (var w in m.Report.Warnings) Progress.Detail("WARN " + m.FileName + ": " + w + "\n");
            foreach (var n in m.Notes.Distinct()) Progress.Detail("note " + m.FileName + ": " + n + "\n");
        }
        if (kit.Errors.Count > 0) { Console.Write(kit.Summary()); foreach (var e in kit.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("kit NOT written"); return 1; }

        Progress.Begin(4, "Exporting via game code + re-import + hash check");
        Progress.Done("");
        GameBp.Init(gameDll);
        var cells = Cells.Load(bpDir);
        if (cells.Errors.Count > 0) { foreach (var e in cells.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("kit NOT written"); return 3; }
        kit.Export(cells);
        if (opt.ContainsKey("map")) foreach (var m in kit.Modules) if (m.Layout != null) Console.Write(m.FileName + " (frame " + m.W + "x" + m.H + ")\n" + Composer.Map(m.Layout));
        Console.Write(kit.Summary());
        if (kit.Errors.Count > 0) { foreach (var e in kit.Errors) Console.WriteLine("FAIL: " + e); Console.WriteLine("kit NOT written"); return 1; }

        string kitDir = Opt(opt, "kit-dir");
        string workDir = Opt(opt, "kit-work-dir") ?? Path.Combine(root, @"work\out\kits\" + kit.KitName);
        if (string.Equals(Path.GetFullPath(workDir).TrimEnd('\\'), kitDir != null ? Path.GetFullPath(kitDir).TrimEnd('\\') : "", StringComparison.OrdinalIgnoreCase)) kitDir = null;
        kit.Write(kitDir, workDir);
        if (kitDir != null) Console.WriteLine("written " + kit.Modules.Count + " module blueprints + _intro_ to " + kitDir);
        Console.WriteLine("written " + kit.Modules.Count + " module blueprints + _intro_ + _kit.md to " + workDir);
        return 0;
    }

    // ---- spec ----
    static BlockSpec BuildSpec(Dictionary<string, string> opt, GameData gd)
    {
        var spec = new BlockSpec();
        spec.Item = gd.ResolveItem(opt["item"]);
        if (spec.Item < 0) throw new ArgumentException("unknown item '" + opt["item"] + "'");
        if (!double.TryParse(opt["rate"], NumberStyles.Float, CultureInfo.InvariantCulture, out spec.Rate) || spec.Rate <= 0)
            throw new ArgumentException("bad --rate '" + opt["rate"] + "'");
        spec.OutPath = Opt(opt, "out");
        spec.CleanRatio = opt.ContainsKey("clean");
        spec.Safe60 = opt.ContainsKey("safe60");
        spec.Buffer = !opt.ContainsKey("no-buffer");

        string v;
        if ((v = Opt(opt, "asm")) != null) spec.Tiers.Assembler = Pick(v, "asm", new[] { "mk1", "mk2", "mk3" }, new[] { Ids.AsmMk1, Ids.AsmMk2, Ids.AsmMk3 });
        if ((v = Opt(opt, "smelter")) != null) spec.Tiers.Smelter = Pick(v, "smelter", new[] { "arc", "plane" }, new[] { Ids.ArcSmelter, Ids.PlaneSmelter });
        if ((v = Opt(opt, "belt")) != null) spec.Tiers.Belt = Pick(v, "belt", new[] { "mk1", "mk2" }, new[] { Ids.BeltMk1, Ids.BeltMk2 });
        if ((v = Opt(opt, "sorter")) != null) spec.Tiers.Sorter = Pick(v, "sorter", new[] { "mk1", "mk2" }, new[] { Ids.SorterMk1, Ids.SorterMk2 });
        spec.Tiers.CheckUserRules();

        if ((v = Opt(opt, "raw")) != null)
            foreach (string n in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // "Item" = raw everywhere; "Item@Consumer" = raw only for that consumer step (its own input lane)
                string[] pair = n.Split('@');
                int id = gd.ResolveItem(pair[0].Trim());
                if (id < 0) throw new ArgumentException("unknown raw item '" + pair[0] + "'");
                if (pair.Length == 1) { spec.TreatAsRaw.Add(id); continue; }
                int cons = gd.ResolveItem(pair[1].Trim());
                if (pair.Length != 2 || cons < 0) throw new ArgumentException("bad --raw entry '" + n + "' (want Item or Item@ConsumerItem)");
                if (!spec.RawFor.ContainsKey(cons)) spec.RawFor[cons] = new HashSet<int>();
                spec.RawFor[cons].Add(id);
            }
        if ((v = Opt(opt, "own")) != null)
            foreach (string n in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int id = gd.ResolveItem(n.Trim());
                if (id < 0) throw new ArgumentException("unknown --own item '" + n + "'");
                spec.OwnSupply.Add(id);
            }
        if ((v = Opt(opt, "max-row")) != null && (!int.TryParse(v, out spec.MaxRow) || spec.MaxRow < 1)) throw new ArgumentException("bad --max-row '" + v + "'");
        if ((v = Opt(opt, "recipe")) != null)
            foreach (string pair in v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.LastIndexOf('='); int rid;
                if (eq < 0 || !int.TryParse(pair.Substring(eq + 1), out rid)) throw new ArgumentException("bad --recipe '" + pair + "' (want item=recipeId)");
                int item = gd.ResolveItem(pair.Substring(0, eq).Trim());
                if (item < 0) throw new ArgumentException("unknown item in --recipe '" + pair + "'");
                if (gd.Recipe(rid) == null || gd.Recipe(rid).OutCount(item) == 0) throw new ArgumentException("recipe " + rid + " does not make " + gd.ItemName(item));
                spec.RecipeOverrides[item] = rid;
            }
        if ((v = Opt(opt, "in-edge")) != null) spec.InEdge = ParseDir(v);
        if ((v = Opt(opt, "out-edge")) != null) spec.OutEdge = ParseDir(v);
        spec.ShortDesc = Opt(opt, "short-desc");
        spec.Desc = Opt(opt, "desc");
        return spec;
    }

    static int Pick(string v, string what, string[] names, int[] ids)
    {
        int i = Array.IndexOf(names, v.ToLowerInvariant());
        if (i < 0) throw new ArgumentException("--" + what + " must be one of " + string.Join("|", names) + (what == "belt" || what == "sorter" ? " (Mk.III not allowed)" : ""));
        return ids[i];
    }

    static Dir ParseDir(string v)
    {
        switch (v.ToUpperInvariant()) { case "N": case "NORTH": return Dir.N; case "E": case "EAST": return Dir.E; case "S": case "SOUTH": return Dir.S; case "W": case "WEST": return Dir.W; }
        throw new ArgumentException("bad edge '" + v + "' (N/E/S/W)");
    }

    // ---- phase-1 check of the GameBp extraction ----
    static int CheckGameBp(string path, string gameDll)
    {
        GameBp.Init(gameDll);
        object bp = GameBp.Load(path);
        int n = GameBp.Buildings(bp).Length;
        var r = GameBp.Export(bp);
        Console.WriteLine("loaded " + n + " buildings; export " + r.BuildingsWritten + ", re-import " + r.BuildingsReimported + ", signature " + (r.SignatureValid ? "valid" : "INVALID"));
        foreach (var e in r.Errors) Console.WriteLine("FAIL: " + e);
        return r.Ok && r.BuildingsReimported == n ? 0 : 1;
    }

    /// Comparison numbers of a blueprint file: buildings, belts, corner belts (ground + raised), raised nodes / runs,
    /// splitters per model, depots, tile size (x/y extent of the building centres + 1).
    static int Stats(string path, string gameDll)
    {
        GameBp.Init(gameDll);
        object bp = GameBp.Load(path);
        var recs = GameBp.Buildings(bp).Cast<object>().ToList();
        int belts = 0, corners = 0, raised = 0, runs = 0, depots = 0;
        var split = new SortedDictionary<int, int>();
        var raisedSet = new HashSet<object>();
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        foreach (object r in recs)
        {
            int item = GameBp.In(r, "itemId");
            double x = GameBp.Fl(r, "localOffset_x"), y = GameBp.Fl(r, "localOffset_y"), z = GameBp.Fl(r, "localOffset_z");
            int h = new Footprints(null).Size(item)[0] / 2;   // 3x3 machines / depots reach one tile beyond their centre
            if (!Ids.IsSorter(item)) { x0 = Math.Min(x0, x - h); x1 = Math.Max(x1, x + h); y0 = Math.Min(y0, y - h); y1 = Math.Max(y1, y + h); }
            if (item == Ids.DepotMk1 || item == Ids.DepotMk2) depots++;
            if (item == Ids.Splitter) { int m = GameBp.In(r, "modelIndex"); int c; split.TryGetValue(m, out c); split[m] = c + 1; }
            if (!Ids.IsBelt(item)) continue;
            belts++;
            double yw = Dirs.Norm(GameBp.Fl(r, "yaw")) % 90.0;
            bool frac = Math.Abs(x - Math.Round(x)) > 0.01 || Math.Abs(y - Math.Round(y)) > 0.01;
            if (!frac && yw > 1 && yw < 89) corners++;
            if (!frac && z > 0.25) { raised++; raisedSet.Add(r); }
        }
        foreach (object r in raisedSet)
        {
            bool predRaised = recs.Any(p => raisedSet.Contains(p) && GameBp.Get(p, "outputObj") == r);
            if (!predRaised) runs++;
        }
        Console.WriteLine(Path.GetFileName(path) + ": " + recs.Count + " buildings, " + belts + " belts, " + corners + " corner belts, "
            + raised + " raised nodes in " + runs + " runs, splitters " + (split.Count == 0 ? "0" : string.Join(" ", split.Select(kv => "m" + kv.Key + "x" + kv.Value).ToArray()))
            + ", depots " + depots + ", size " + (int)Math.Round(x1 - x0 + 1) + "x" + (int)Math.Round(y1 - y0 + 1));
        return 0;
    }

    // ---- args ----
    static readonly HashSet<string> Flags = new HashSet<string> { "plan-only", "selftest", "clean", "safe60", "map", "kit", "bus", "no-buffer", "quiet", "verbose" };

    public static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException("unexpected argument '" + args[i] + "'");
            string k = args[i].Substring(2);
            if (Flags.Contains(k)) { d[k] = "1"; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException("--" + k + " needs a value");
            d[k] = args[++i];
        }
        return d;
    }

    public static string Opt(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : null; }

    static void Usage()
    {
        Console.WriteLine("usage: MakeBlock.exe --item <name|id> --rate <per min> [--out file.txt] [--plan-only]");
        Console.WriteLine("       [--asm mk1|mk2|mk3] [--smelter arc|plane] [--belt mk1|mk2] [--sorter mk1|mk2]");
        Console.WriteLine("       [--raw \"item,item\"] [--recipe item=recipeId;...] [--clean] [--in-edge S] [--out-edge N]");
        Console.WriteLine("       [--short-desc text] [--desc text] [--bp-dir dir] [--game-dll path] [--data-root blueprint-tools dir]");
        Console.WriteLine("       [--safe60] (E-W machine pitch/colliders safe up to 60 deg latitude) [--map] (ASCII map)");
        Console.WriteLine("       [--no-buffer] (no buffer depot at the product end) [--quiet] (plain progress lines) [--verbose] (full report)");
        Console.WriteLine("       MakeBlock.exe --stats <blueprint.txt>   (buildings, belts, corners, raised runs, splitter models, size)");
        Console.WriteLine("       [--raw \"Iron Ingot@Steel\"] (raw for one consumer step only) [--own \"Steel\"] (own supply rows)");
        Console.WriteLine("       MakeBlock.exe --kit --item <name> --rate <n> [--kit-dir <dir>] [--kit-work-dir <dir>] [--max-row 12] [tier options]");
        Console.WriteLine("       MakeBlock.exe --selftest | --check-gamebp <blueprint.txt>");
    }
}

/// Console progress for a person running MakeBlock by hand: numbered stages with elapsed time ("[2/6] Laying out rows
/// and routing belts (attempt 3) | 12.4 s"), an animated spinner on the stage line only when stdout is an interactive
/// console; plain lines without animation when redirected or with --quiet (agents, logs).
static class Progress
{
    public static bool Plain = true;      // redirected / --quiet: no animation, no carriage returns
    public static bool Verbose = true;    // print the detailed plan / compose notes / validation report
    public static int Total = 6;
    static readonly object sync = new object();
    static readonly System.Diagnostics.Stopwatch all = System.Diagnostics.Stopwatch.StartNew();
    static System.Diagnostics.Stopwatch stage;
    static string head, detail;
    static System.Threading.Timer timer;
    static int frame, lastLen;
    static readonly char[] Spin = { '|', '/', '-', '\\' };

    static string Secs(System.Diagnostics.Stopwatch w) { return (w.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s"; }

    public static void Begin(int n, string text)
    {
        lock (sync)
        {
            head = "[" + n + "/" + Total + "] " + text;
            detail = null; stage = System.Diagnostics.Stopwatch.StartNew();
            if (Plain) { Console.WriteLine(head + "..."); return; }
            Draw();
            if (timer == null) timer = new System.Threading.Timer(_ => { lock (sync) { if (head != null) { frame++; Draw(); } } }, null, 150, 150);
        }
    }

    /// Stage detail (e.g. "attempt 12, best valid 31x43"): shown on the spinner line; plain mode stays silent.
    public static void Update(string text) { lock (sync) { detail = text; } }

    static void Draw()
    {
        string s = head + (detail != null ? " (" + detail + ")" : "") + " " + Spin[frame % 4] + " " + Secs(stage);
        int w = 0;
        try { w = Console.WindowWidth - 1; } catch (Exception) { }
        if (w > 10 && s.Length > w) s = s.Substring(0, w);
        Console.Write("\r" + s + (lastLen > s.Length ? new string(' ', lastLen - s.Length) : ""));
        lastLen = s.Length;
    }

    static void Finish(string tail)
    {
        lock (sync)
        {
            if (head == null) return;
            string s = head + " " + tail + " (" + Secs(stage) + ", total " + Secs(all) + ")";
            if (Plain) Console.WriteLine("    " + tail + " (" + Secs(stage) + ", total " + Secs(all) + ")");
            else { Console.Write("\r" + s + (lastLen > s.Length ? new string(' ', lastLen - s.Length) : "") + "\n"); lastLen = 0; }
            head = null; detail = null;
        }
    }

    public static void Done(string result) { Finish("done" + (string.IsNullOrEmpty(result) ? "" : ": " + result)); }
    public static void Fail(string reason) { Finish("FAILED: " + reason); }

    /// A line of its own (kit per-module progress), never overwritten by the spinner.
    public static void Line(string text) { lock (sync) { Console.WriteLine(text); } }

    /// Detailed output (plan text, compose notes, validator report): printed in plain/verbose mode only.
    public static void Detail(string text) { if (Verbose && text != null) lock (sync) { Console.Write(text); } }
}
