// Api.cs - the engine <-> host contract (docs\engine-api.md): game data snapshot (IGameSource, PrototypeCells),
// the injected plan (PlanInput / PlanStep / LayoutOptions), the result (EngineResult with BpBuilding records) and
// the entry point FlatEngine.Generate. No file, console, environment or culture side effects; no Unity references.
using System;
using System.Collections.Generic;
using System.Linq;

namespace DspFlat.Engine
{
// ------------------------------------------------------------------ game data
/// Read-only snapshot of the game data the engine needs. Built once on the host side (CLI: game-data.json, the user's
/// template blueprints and reflection; addon: LDB on the Unity main thread), then handed to a worker thread.
/// The engine never calls back into the game.
public interface IGameSource
{
    GameData Data { get; }              // items, recipes, buildings (speed, power, footprint, colliders, icon tags)
    PrototypeCells Cells { get; }       // prototype building records the Emitter clones (per item id and named cells)
    string GameVersion { get; }         // written into the blueprint header (null = Emitter.DefaultGameVersion)
}

/// One blueprint building. Field names and types mirror the game's BlueprintBuilding exactly (Assembly-CSharp,
/// checked by reflection on game build 0.10.35.x), so a host copies them 1:1. The game links buildings by object
/// reference (outputObj / inputObj); here the links are the indices tempOutputObjIdx / tempInputObjIdx into
/// EngineResult.Buildings (the same fields the game's own Import fills before it resolves the references), -1 = none.
public class BpBuilding
{
    public int index, areaIndex;
    public float localOffset_x, localOffset_y, localOffset_z;
    public float localOffset_x2, localOffset_y2, localOffset_z2;
    public float pitch, pitch2, yaw, yaw2, tilt, tilt2;
    public short itemId, modelIndex;
    public int tempOutputObjIdx = -1, tempInputObjIdx = -1;
    public int outputToSlot, inputFromSlot, outputFromSlot, inputToSlot, outputOffset, inputOffset;
    public int recipeId, filterId;
    public int[] parameters;
    public string content;

    /// Deep copy (parameters array copied).
    public BpBuilding Clone()
    {
        var c = (BpBuilding)MemberwiseClone();
        if (parameters != null) c.parameters = (int[])parameters.Clone();
        return c;
    }
}

/// Prototype records for the Emitter: one proven record per building item (generic) plus named cells (depot buffer
/// sorters with the game's pitch/tilt, splitter variants "splitter-m39"/"splitter-m40", ...). Plain values: the CLI fills
/// it from the user's blueprints (DspFlat.Host.Cells), the addon from LDB (ItemProto model index, default parameters).
public class PrototypeCells
{
    public readonly Dictionary<string, BpBuilding> ByName = new Dictionary<string, BpBuilding>();
    readonly Dictionary<int, string> byItem = new Dictionary<int, string>();
    /// Problems found while building the prototypes; FlatEngine refuses to emit while this is not empty.
    public readonly List<string> Errors = new List<string>();

    public void Add(string name, BpBuilding record) { ByName[name] = record; }
    /// The named cell used for every building of this item (first registration wins).
    public void SetDefault(int itemId, string name) { if (!byItem.ContainsKey(itemId)) byItem[itemId] = name; }

    /// Generic prototype for a building item; null if none (a belt/sorter tier without one is never used, Lanes).
    public BpBuilding ProtoFor(int itemId)
    {
        string n; BpBuilding b;
        return byItem.TryGetValue(itemId, out n) && ByName.TryGetValue(n, out b) ? b : null;
    }

    /// Prototype by cell name; null if missing.
    public BpBuilding Proto(string cellName) { BpBuilding b; return cellName != null && ByName.TryGetValue(cellName, out b) ? b : null; }
}

// ------------------------------------------------------------------ input
/// The plan to lay out, as the calculator (or the CLI solver) computed it. The engine does not re-solve.
public class PlanInput
{
    public int TargetItemId;
    public double TargetPerMin;
    public List<PlanStep> Steps = new List<PlanStep>();   // any order; the engine sorts topologically
    public List<int> ExternalItems = new List<int>();     // arrive on input lanes (raw + marked external)
    public LayoutOptions Options = new LayoutOptions();
}

public class PlanStep
{
    public int RecipeId;
    public int BuildingItemId;      // 2302 arc smelter, 2303 assembler Mk.I, ... as chosen by the calculator
    public double MachineCount;     // exact (calculator assemblerCount, proliferator already applied)
    public int Machines;            // rounded up count to build (0 = ceil(MachineCount))
    public int IncLevel;            // -1/0 = no proliferator; >0 = spray coaters on this step's inputs (engine\Coaters.cs)
    public bool ExtraProducts;      // calculator isInc (true) vs speed-up (false)
    public Dictionary<int, double> InputsPerMin = new Dictionary<int, double>();   // itemId -> rate
    public Dictionary<int, double> OutputsPerMin = new Dictionary<int, double>();
    /// Engine extension (CLI --raw "Item@Consumer"): inputs of this step that come from a block input lane although
    /// another step of the plan makes them. Empty for the addon.
    public List<int> ExternalInputs = new List<int>();
}

public class LayoutOptions
{
    public int BeltItemMax = 2002;      // 2001/2002/2003 Mk.I/II/III cap
    public int SorterItemMax = 2012;    // 2011/2012/2013/2014 cap (pile sorter not supported yet: clamped to Mk.III)
    /// Cheapest belt/sorter tier per lane / sorter inside the caps (PLAN.md decision 8); false = everything at the caps.
    public bool CheapestTiers = true;
    public int LabStackMax = 15;        // vertical construction tech cap: Matrix Lab tower height (labs in one stack)
    public int TimeBudgetMs = 60000;
    public int ProliferatorItemId = 0;  // 1141/1142/1143 on the external proliferator lane; 0 = inferred from IncLevel
    // ---- engine extensions (defaults = the calculator addon's behaviour; the CLI maps its flags here) ----
    public Dir InEdge = Dir.S, OutEdge = Dir.N;       // block edges for input lanes / product
    public bool Safe60;                               // E-W pitch/colliders safe up to 60 deg latitude
    public bool Buffer = true;                        // buffer depot at the product end
    public List<int> OwnSupply = new List<int>();     // consumer items whose inputs get producer rows of their own
    public int MaxRow = 12;                           // kit mode limit (unused by Generate)
    public string ShortDesc, Desc;                    // header text override; null = generated
}

// ------------------------------------------------------------------ output
public class EngineResult
{
    public bool Ok;
    public List<string> Errors = new List<string>();
    public List<string> Warnings = new List<string>();
    public List<BpBuilding> Buildings = new List<BpBuilding>();
    public int AreaWidth, AreaHeight;       // tiles
    public string ShortDesc, Desc;          // thumbnail text and description (game tag syntax)
    public int[] Icons = new int[5];
    // ---- engine extensions for hosts (diagnostics, CLI summary / map / export check) ----
    public string GameVersion;              // header version (IGameSource.GameVersion or Emitter.DefaultGameVersion)
    public List<string> Notes = new List<string>();   // composer notes (attempts, squeeze, towers, routing)
    public ProductionPlan Plan;             // the internal plan built from PlanInput (dropped steps removed)
    public Layout Layout;                   // the abstract layout the records were emitted from (Bld.Index = record index)
    public ValidationReport Report;         // validator report of the final layout
    public BpHeader Header;                 // the header values above as one object
}

// ------------------------------------------------------------------ entry point
public static class FlatEngine
{
    /// Runs on a worker thread. progress(0..1, text) may be called often; cancel is polled. Never throws for plan or
    /// layout problems: they come back in EngineResult.Errors (Ok = false). TimeBudgetMs (PlanInput.Options) is a hard
    /// limit: when it runs out the composer keeps the best valid attempt so far, or fails with "time budget".
    public static EngineResult Generate(IGameSource game, PlanInput plan, Action<double, string> progress, Func<bool> cancel)
    {
        var res = new EngineResult();
        Action<double, string> say = (f, s) => { if (progress != null) progress(Math.Max(0, Math.Min(1, f)), s); };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // TimeBudgetMs > 0: hard limit for the whole run; <= 0: no hard limit (CLI), the composer keeps its own
        // per-mode-set budget EngineTuning.ComposeBudgetMs
        int budget = plan != null && plan.Options != null ? plan.Options.TimeBudgetMs : 60000;
        bool timedOut = false;
        Func<bool> stop = () =>
        {
            if (cancel != null && cancel()) return true;
            if (budget > 0 && clock.ElapsedMilliseconds > budget) { timedOut = true; return true; }
            return false;
        };
        try
        {
            if (game == null || game.Data == null) { res.Errors.Add("no game data"); return res; }
            if (plan == null) { res.Errors.Add("no plan"); return res; }
            GameData gd = game.Data;
            say(0, "mapping the plan");
            PlanMapper.Result mapped = PlanMapper.Map(gd, plan);
            res.Warnings.AddRange(mapped.Warnings);
            res.Errors.AddRange(mapped.Errors);
            res.Plan = mapped.Plan;
            if (res.Errors.Count > 0) return res;
            BlockSpec spec = mapped.Spec;
            // lanes hook: tiers without a prototype record are never used (cap drops below them, warning)
            Lanes.ClampToPrototypes(gd, spec, game.Cells, res.Warnings);

            if (stop()) { res.Errors.Add(StopText(timedOut)); return res; }
            say(0.05, "laying out rows and routing belts");
            var composer = new Composer(gd, spec);
            composer.TimeBudgetMs = budget > 0 ? Math.Min(budget, EngineTuning.ComposeBudgetMs) : EngineTuning.ComposeBudgetMs;
            composer.Cancel = stop;
            int attempts = 0;
            composer.OnProgress = s => { attempts++; say(0.05 + 0.8 * (1 - 1.0 / (1 + attempts / 40.0)), s); };
            Layout layout = composer.Compose(mapped.Plan);
            res.Layout = layout;
            res.Notes.AddRange(composer.Notes);
            if (composer.Stopped && cancel != null && cancel() && !timedOut) { res.Errors.Add(StopText(false)); return res; }

            say(0.86, "validating");
            ValidationReport report = new Validator(gd, spec).Validate(layout, mapped.Plan);
            res.Report = report;
            res.Warnings.AddRange(report.Warnings);
            if (!report.Ok)
            {
                if (composer.Stopped) res.Errors.Add(StopText(timedOut) + ": no valid layout found in time");
                res.Errors.AddRange(report.Errors);
                return res;
            }

            say(0.92, "emitting buildings");
            if (game.Cells == null) { res.Errors.Add("no prototype cells"); return res; }
            if (game.Cells.Errors.Count > 0) { res.Errors.AddRange(game.Cells.Errors.Select(e => "prototype: " + e)); return res; }
            var emitErrors = new List<string>();
            List<BpBuilding> recs = new Emitter(gd, game.Cells).Records(layout, emitErrors);
            if (recs == null) { res.Errors.AddRange(emitErrors); return res; }
            res.Buildings = recs;

            BpHeader h = Emitter.HeaderFor(layout, mapped.Plan, spec, gd, game.GameVersion);
            if (spec.ShortDesc != null) h.ShortDesc = spec.ShortDesc;
            if (spec.Desc != null) h.Desc = spec.Desc;
            res.Header = h;
            res.AreaWidth = h.Width; res.AreaHeight = h.Height;
            res.ShortDesc = h.ShortDesc; res.Desc = h.Desc; res.GameVersion = h.GameVersion;
            for (int i = 0; i < 5; i++) res.Icons[i] = h.Icons != null && i < h.Icons.Length ? h.Icons[i] : 0;
            res.Ok = true;
            say(1, "done: " + recs.Count + " buildings, " + h.Width + "x" + h.Height);
            return res;
        }
        catch (Exception e)
        {
            // a bug, not a plan problem: report it instead of killing the host's worker thread
            res.Ok = false;
            res.Errors.Add("engine error: " + e.GetType().Name + ": " + e.Message);
            return res;
        }
    }

    static string StopText(bool timedOut) { return timedOut ? "time budget reached" : "cancelled"; }
}
}
