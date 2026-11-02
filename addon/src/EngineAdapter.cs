// EngineAdapter.cs - the ONLY file of the addon that touches the DspFlat engine types (docs\engine-api.md).
// When the engine contract changes, adjust this file; the rest of the addon works with CalcPlan / BlueprintData.
// Engine types are always written as E.X: the game's Assembly-CSharp has a global class GameData that would
// otherwise win over a using directive.
//
//   main thread : EngineAdapter.Prepare(plan, budget)  -> LDB snapshot (IGameSource) + PlanInput
//   worker      : PreparedRun.Run(progress, cancel)     -> FlatEngine.Generate
//   main thread : PreparedRun.ToBlueprint(out error)    -> game BlueprintData (validated by a base64 round trip)
using System;
using System.Collections.Generic;
using E = DspFlat.Engine;
using UnityEngine;

namespace DSPCalculatorFlat
{
    internal class PreparedRun
    {
        internal E.IGameSource Game;
        internal E.PlanInput Input;
        E.EngineResult result;

        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok { get { return result != null && result.Ok && Errors.Count == 0; } }
        public int BuildingCount { get { return result != null ? result.Buildings.Count : 0; } }

        /// Worker thread. Never throws: exceptions become Errors.
        public void Run(Action<double, string> progress, Func<bool> cancel)
        {
            try
            {
                result = E.FlatEngine.Generate(Game, Input, progress, cancel);
                if (result == null) { Errors.Add("The engine returned no result."); return; }
                Errors.AddRange(result.Errors);
                Warnings.AddRange(result.Warnings);
                if (!result.Ok && Errors.Count == 0) Errors.Add("The engine reported a failure without a message.");
                if (result.Ok && result.Buildings.Count == 0) Errors.Add("The engine returned an empty layout.");
            }
            catch (Exception e)
            {
                Errors.Add("Engine exception: " + e.GetType().Name + ": " + e.Message);
                Plugin.Log.LogError(e);
            }
        }

        /// Main thread. Converts the engine layout to the game's BlueprintData.
        public BlueprintData ToBlueprint(out string error)
        {
            error = null;
            if (!Ok) { error = "no successful result"; return null; }
            try { return EngineAdapter.ToBlueprint(result); }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                error = "Blueprint conversion failed: " + e.Message;
                return null;
            }
        }
    }

    internal static class EngineAdapter
    {
#if ENGINE_STUB
        public static readonly bool UsingStub = true;
#else
        public static readonly bool UsingStub = false;
#endif

        static E.IGameSource cachedGame;
        static object cachedItemsSet;

        /// Main thread: snapshot the game data and map the calculator plan. Returns null with errors on failure.
        public static PreparedRun Prepare(CalcPlan plan, int timeBudgetMs, List<string> errors)
        {
            var run = new PreparedRun();
            try { run.Game = GameSource(); }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                errors.Add("Reading the game data failed: " + e.Message);
                return null;
            }
            run.Input = ToPlanInput(plan, timeBudgetMs);
            run.Warnings.AddRange(plan.Notes);
            return run;
        }

        // ------------------------------------------------------------------ CalcPlan -> PlanInput
        static E.PlanInput ToPlanInput(CalcPlan p, int timeBudgetMs)
        {
            var pi = new E.PlanInput();
            pi.TargetItemId = p.TargetItemId;
            pi.TargetPerMin = p.TargetPerMin;
            foreach (var s in p.Steps)
            {
                var ps = new E.PlanStep();
                ps.RecipeId = s.RecipeId;
                ps.BuildingItemId = s.BuildingItemId;
                ps.MachineCount = s.MachineCount;
                ps.Machines = (int)Math.Ceiling(s.MachineCount - 1e-6);
                if (ps.Machines < 1) ps.Machines = 1;
                ps.IncLevel = s.IncLevel;
                ps.ExtraProducts = s.IsInc;
                foreach (var kv in s.InputsPerMin) ps.InputsPerMin[kv.Key] = kv.Value;
                foreach (var kv in s.OutputsPerMin) ps.OutputsPerMin[kv.Key] = kv.Value;
                pi.Steps.Add(ps);
            }
            pi.ExternalItems.AddRange(p.ExternalItems);
            var o = pi.Options;
            if (p.BeltCapItemId > 0) o.BeltItemMax = p.BeltCapItemId;
            if (p.SorterCapItemId > 0) o.SorterItemMax = p.SorterCapItemId;
            o.CheapestTiers = true;                                   // cheapest belt / sorter tier inside the caps
            if (p.LabMaxLevel > 0) o.LabStackMax = p.LabMaxLevel;     // UserPreference.labMaxLevel (3..15 by tech)
            o.TimeBudgetMs = timeBudgetMs;
            o.ProliferatorItemId = p.ProliferatorItemId;              // 0 only when no step is proliferated
            // Not in the contract yet: p.CargoStack (belt stacking), p.BeltHighest / p.SorterHighest.
            return pi;
        }

        // ------------------------------------------------------------------ LDB -> IGameSource
        class LdbGameSource : E.IGameSource
        {
            readonly E.GameData data; readonly E.PrototypeCells cells; readonly string version;
            public LdbGameSource(E.GameData d, E.PrototypeCells c, string v) { data = d; cells = c; version = v; }
            public E.GameData Data { get { return data; } }
            public E.PrototypeCells Cells { get { return cells; } }
            public string GameVersion { get { return version; } }
        }

        /// Built once per loaded proto set (protos do not change while the game runs).
        static E.IGameSource GameSource()
        {
            object items = LDB.items;
            if (cachedGame != null && ReferenceEquals(items, cachedItemsSet)) return cachedGame;
            cachedGame = new LdbGameSource(BuildGameData(), LoadPrototypes(), GameConfig.gameVersion.ToString());
            cachedItemsSet = items;
            return cachedGame;
        }

        const double MetersPerTile = 1.2566;   // same constant as the engine's GameData.MetersPerTile

        static E.GameData BuildGameData()
        {
            var gd = new E.GameData();
            gd.SourcePath = "LDB (in game)";
            var byRecipeType = new Dictionary<string, List<int>>();

            foreach (ItemProto it in LDB.items.dataArray)
            {
                if (it == null) continue;
                var def = new E.ItemDef();
                def.Id = it.ID; def.Name = it.name; def.Type = it.Type.ToString();
                def.IsFluid = it.IsFluid; def.StackSize = it.StackSize; def.CanBuild = it.CanBuild;
                gd.AddItem(def);
                if (!string.IsNullOrEmpty(it.IconTag)) gd.IconTags[it.ID] = it.IconTag;

                PrefabDesc pd = it.prefabDesc;
                if (pd == null || !it.CanBuild) continue;
                var m = new E.MachineDef();
                m.ItemId = it.ID; m.Name = it.name; m.ModelIndex = pd.modelIndex;
                if (pd.isAssembler) { m.RecipeType = pd.assemblerRecipeType.ToString(); m.SpeedMultiplier = pd.assemblerSpeed / 10000.0; }
                else if (pd.isLab) { m.RecipeType = "Research"; m.SpeedMultiplier = pd.labAssembleSpeed / 10000.0; }
                else if (pd.isFractionator) { m.RecipeType = "Fractionate"; }
                if (m.RecipeType != null)
                {
                    List<int> l;
                    if (!byRecipeType.TryGetValue(m.RecipeType, out l)) byRecipeType[m.RecipeType] = l = new List<int>();
                    l.Add(it.ID);
                }
                if (pd.isBelt) m.BeltItemsPerSec = pd.beltSpeed * 60 / 10.0;            // cargo = 10 buffer units
                if (pd.isInserter && pd.inserterSTT > 0)
                {
                    m.SorterMaxLength = 3;
                    for (int len = 1; len <= 3; len++)                                   // 1 / (2 * sttf * L), sttf = STT / 600000
                        m.SorterTripsPerSec[len] = 300000.0 / (pd.inserterSTT * (double)len);
                }
                if (pd.isPowerNode) { m.PowerConnectDistanceM = pd.powerConnectDistance; m.PowerCoverRadiusM = pd.powerCoverRadius; }
                m.IsSplitter = pd.isSplitter;

                ColliderData[] boxes = pd.buildColliders;
                if ((boxes == null || boxes.Length == 0) && pd.hasBuildCollider) boxes = new[] { pd.buildCollider };
                if (boxes != null && boxes.Length > 0)
                {
                    var cd = new E.GameData.ColliderDef();
                    cd.ItemId = it.ID; cd.ModelId = pd.modelIndex;
                    double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
                    foreach (ColliderData c in boxes)
                    {
                        var b = new E.GameData.ColliderBox();
                        b.Cx = c.pos.x; b.Cz = c.pos.z; b.Hx = c.ext.x; b.Hz = c.ext.z;   // rotation ignored, like the CLI loader
                        cd.Boxes.Add(b);
                        minX = Math.Min(minX, b.Cx - b.Hx); maxX = Math.Max(maxX, b.Cx + b.Hx);
                        minZ = Math.Min(minZ, b.Cz - b.Hz); maxZ = Math.Max(maxZ, b.Cz + b.Hz);
                    }
                    cd.FootW = maxX - minX; cd.FootH = maxZ - minZ;
                    gd.AddCollider(cd);   // no derived pitch tables in game: the engine's proven pitch tables apply
                }
                if (pd.hasBuildCollider)
                {
                    // same rule as the CLI's game-data.json: 2 * ext of buildCollider (the summary box) after its rotation
                    Vector3 e = pd.buildCollider.ext;
                    Quaternion q = pd.buildCollider.q;
                    if (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0) q = Quaternion.identity;
                    Vector3 ax = q * Vector3.right, ay = q * Vector3.up, az = q * Vector3.forward;
                    double wx = Math.Abs(ax.x) * e.x + Math.Abs(ay.x) * e.y + Math.Abs(az.x) * e.z;
                    double wz = Math.Abs(ax.z) * e.x + Math.Abs(ay.z) * e.y + Math.Abs(az.z) * e.z;
                    m.FootprintTiles = new[] { Math.Round(wx * 2 / MetersPerTile, 2), Math.Round(wz * 2 / MetersPerTile, 2) };
                }
                gd.Machines[m.ItemId] = m;
            }

            foreach (RecipeProto rp in LDB.recipes.dataArray)
            {
                if (rp == null) continue;
                var r = new E.RecipeDef();
                r.Id = rp.ID; r.Name = rp.name; r.Type = rp.Type.ToString();
                r.TimeSec = rp.TimeSpend / 60.0; r.Explicit = rp.Explicit;
                for (int i = 0; i < rp.Items.Length; i++) r.Inputs.Add(new E.ItemCount(rp.Items[i], rp.ItemCounts[i]));
                for (int i = 0; i < rp.Results.Length; i++) r.Outputs.Add(new E.ItemCount(rp.Results[i], rp.ResultCounts[i]));
                List<int> bl;
                r.Buildings = byRecipeType.TryGetValue(r.Type, out bl) ? bl.ToArray() : new int[0];
                gd.Recipes[r.Id] = r;
            }
            return gd;
        }

        // ------------------------------------------------------------------ prototype records
        // The CLI host (engine.host\GameCells.cs) copies these records from the user's blueprints; the addon cannot read
        // those files, so every value that comes from a blueprint record is embedded below with its CLI source
        // (cell name = CLI cell name, "file #index" = the record in the user's Blueprint folder or blueprint-tools\refs).
        // Values: field dump of the CLI's FileGameSource (blueprint-tools data, game 0.10.35.29151, 2026-11-02).
        // Only the model index comes from LDB; the CLI's value (checked there against game-data.json) is kept as
        // CliModel and compared on load: a difference is logged and LDB wins.
        //
        // What the Emitter (engine\Emitter.cs) takes from a prototype: modelIndex (unless the layout sets one), pitch,
        // pitch2, tilt, tilt2, content and the default parameters (when the layout sets none: machines, collider, coater,
        // tesla, depot). It overwrites position (x/y/z, x2/y2/z2), yaw/yaw2, item, recipe, filter, all four slots, both
        // offsets, links and index; slots and offsets are still copied from the CLI records so both hosts hand the
        // engine identical records. Position, yaw and recipe of the source records are not copied (record specific,
        // always overwritten). content is null in every CLI record.

        /// One prototype record as the CLI host builds it (all values from the CLI record except the model index).
        class ProtoSpec
        {
            public string Name; public int ItemId; public int CliModel; public int FixedModel; public bool Generic;
            public int[] Params = new int[0];
            public int OutTo, InFrom, OutFrom, InTo, OutOff, InOff;
            public float Pitch, Pitch2, Tilt, Tilt2;
            public string Source;
        }

        static ProtoSpec P(string name, int item, int cliModel, bool generic, string source, int[] prms)
        {
            var s = new ProtoSpec();
            s.Name = name; s.ItemId = item; s.CliModel = cliModel; s.Generic = generic; s.Source = source;
            s.Params = prms ?? new int[0];
            return s;
        }

        static ProtoSpec Sl(ProtoSpec s, int outTo, int inFrom, int outFrom, int inTo, int outOff, int inOff)
        {
            s.OutTo = outTo; s.InFrom = inFrom; s.OutFrom = outFrom; s.InTo = inTo; s.OutOff = outOff; s.InOff = inOff;
            return s;
        }

        static ProtoSpec Pt(ProtoSpec s, float pitch, float pitch2, float tilt, float tilt2)
        {
            s.Pitch = pitch; s.Pitch2 = pitch2; s.Tilt = tilt; s.Tilt2 = tilt2;
            return s;
        }

        const int DepotParamCount = 110;   // Depot Mk.I record (refs\engine-turbine-60-v2-userupgrade.txt #482): 110 zeros

        /// The CLI's prototype set. Generic = the record ProtoFor(item) returns (CLI Cells byItem); the others are named
        /// only. Items the CLI has no record for (pile sorter 2014, every other building) get none here either, so the
        /// engine never uses them (Lanes.ClampToPrototypes; StepSupport drops their steps).
        static List<ProtoSpec> CliPrototypes()
        {
            var l = new List<ProtoSpec>();
            // machines: parameters [0], slots 0
            l.Add(P("smelter-arc", 2302, 62, true, @"t-tpl\Smelters\smelter-arc-1.txt #52", new[] { 0 }));
            l.Add(P("smelter-plane", 2315, 194, true, @"2-smelt\Smelters.txt #8", new[] { 0 }));
            l.Add(P("asm-mk1", 2303, 65, true, @"t-tpl\Assemblers\assemblers-8x1.txt #8", new[] { 0 }));
            l.Add(P("asm-mk2", 2304, 66, true, @"t-tpl\Assemblers\distrubution\(assembler, 2 inputs; Cyrillic name).txt #93", new[] { 0 }));
            l.Add(P("asm-mk3", 2305, 67, true, @"t-tpl\Assemblers mk III.txt #9", new[] { 0 }));
            // feat-labs: Matrix Lab tower base (recipe 9, production mode [1,0], multi-level slots 14/15/15/14; levels are
            // stacked by the engine, LabTowers) and the Deuterium collider of the community blueprint (recipe 40, [0])
            l.Add(Sl(P("lab", 2901, 70, true, @"4-sci\bluematrix.txt #557", new[] { 1, 0 }), 14, 15, 15, 14, 0, 0));
            l.Add(P("collider", 2310, 69, true, @"x-imp\Old Science Designs (still work)\Science - Gravity Matrix\Green Science + Proliferator.txt #903", new[] { 0 }));
            // spray coater (engine\Coaters.cs, CLI Cells.SoftCoaterCell): no parameters, slots 14/15/15/14, no links
            l.Add(Sl(P("spray-coater", 2313, 120, true, @"7-logi\L1.txt #36", null), 14, 15, 15, 14, 0, 0));
            // belts: parameters [] (an icon [item,0] is set by the layout), slots 1/0/0/1
            l.Add(Sl(P("belt-mk1", 2001, 35, true, @"5-chem\Fuel.txt #0", null), 1, 0, 0, 1, 0, 0));
            l.Add(Sl(P("belt-mk2", 2002, 36, true, @"t-tpl\Smelters\smelter-arc-1.txt #64", null), 1, 0, 0, 1, 0, 0));
            // Mk.III belt: CLI Cells.DeriveBeltMk3 = the belt-mk2 record with item 2003 and the game's model index
            l.Add(Sl(P("belt-mk3", 2003, 37, true, "belt-mk2 record, item 2003 (CLI Cells.DeriveBeltMk3)", null), 1, 0, 0, 1, 0, 0));
            // sorters: the layout always sets parameters [length] (Bld.Sorter); pitch / tilt are copied from the records
            l.Add(Pt(Sl(P("sorter-mk1", 2011, 41, true, @"5-chem\Fuel.txt #4", new[] { 2 }), 10, 7, 0, 1, 0, 0),
                     0.2011857f, 359.7922f, -1.00052212E-07f, -4.148834E-06f));
            l.Add(Sl(P("sorter-mk2", 2012, 42, true, @"t-tpl\Smelters\smelter-arc-1.txt #40", new[] { 1 }), 5, -1, 0, 1, 0, 4));
            l.Add(Sl(P("sorter-mk3", 2013, 43, true, @"t-tpl\Assemblers\distrubution\(assembler, 3 inputs; Cyrillic name).txt #3 (COL-PAIR-3)", new[] { 1 }), 3, -1, 0, 1, 0, 0));
            l.Add(P("tesla", 2201, 44, true, @"t-tpl\Smelters\smelter-arc-1.txt #39", null));
            l.Add(Sl(P("splitter", 2020, 38, true, @"refs\splitters.txt #0", new[] { 0, 0, 0, 0, 0, 0 }), 14, 15, 15, 14, 0, 0));
            l.Add(Sl(P("depot-mk1", 2101, 51, true, @"refs\engine-turbine-60-v2-userupgrade.txt #482", new int[DepotParamCount]), 14, 15, 15, 14, 0, 0));
            // named: depot buffer sorters of the user's in-game edit (the game's own pitch / tilt values)
            l.Add(Pt(Sl(P("depot-in", 2012, 42, false, @"refs\engine-turbine-60-v2-userupgrade.txt #491", new[] { 1 }), 0, -1, 0, 1, 0, 4),
                     359.92868f, 359.7922f, -0.0005999628f, 0.286192179f));
            l.Add(Pt(Sl(P("depot-out1", 2012, 42, false, @"refs\engine-turbine-60-v2-userupgrade.txt #492", new[] { 1 }), -1, 1, 0, 1, -1, 0),
                     0.207797408f, 7.45867237E-06f, 5.38734957E-06f, 0.0007558026f));
            l.Add(Pt(Sl(P("depot-out2", 2012, 42, false, @"refs\engine-turbine-60-v2-userupgrade.txt #493", new[] { 1 }), -1, 2, 0, 1, -5, 0),
                     0.207802f, 0.0395599939f, 0.286199331f, 0.0544653051f));
            // named: splitter Tab variants; the records carry model 39 / 40, not the item's LDB model 38
            ProtoSpec m39 = Sl(P("splitter-m39", 2020, 39, false, @"refs\engine-turbine-60-v2-userupgrade.txt #279", new[] { 0, 0, 0, 0, 0, 0 }), 14, 15, 15, 14, 0, 0);
            m39.FixedModel = 39; l.Add(m39);
            ProtoSpec m40 = Sl(P("splitter-m40", 2020, 40, false, @"refs\engine-turbine-60-v2-userupgrade.txt #280", new[] { 0, 0, 0, 0, 0, 0 }), 14, 15, 15, 14, 0, 0);
            m40.FixedModel = 40; l.Add(m40);
            // named only and not requested by the engine today (parity with the CLI's ByName)
            l.Add(Sl(P("splitter-priority", 2020, 38, false, @"refs\splitters.txt #2", new[] { 1, 0, 0, 0, 0, 0 }), 14, 15, 15, 14, 0, 0));
            l.Add(Sl(P("splitter-belt", 2002, 36, false, @"refs\splitters.txt #10", null), 1, 0, 0, 1, 0, 0));
            l.Add(Sl(P("belt-icon", 2002, 36, false, @"refs\lent-begin.txt #0", new[] { 1002, 0 }), 1, 0, 0, 1, 0, 0));
            return l;
        }

        /// Prototype records the engine's Emitter clones: exactly the CLI host's set (CliPrototypes), model index from LDB.
        static E.PrototypeCells LoadPrototypes()
        {
            var pc = new E.PrototypeCells();
            foreach (ProtoSpec s in CliPrototypes())
            {
                ItemProto it = LDB.items.Select(s.ItemId);
                if (it == null || it.prefabDesc == null)
                {
                    Plugin.Log.LogWarning("prototype " + s.Name + ": item " + s.ItemId + " not in LDB, no record (the engine will not use it)");
                    continue;
                }
                int model = s.FixedModel > 0 ? s.FixedModel : it.prefabDesc.modelIndex;
                if (model <= 0 || model > short.MaxValue)
                {
                    Plugin.Log.LogWarning("prototype " + s.Name + ": LDB model index " + model + " unusable, no record");
                    continue;
                }
                if (model != s.CliModel)
                    Plugin.Log.LogWarning("prototype " + s.Name + ": LDB model " + model + " differs from the CLI record's " + s.CliModel
                        + " (" + s.Source + "); using LDB");
                var b = Record(s.ItemId, model);
                b.parameters = (int[])s.Params.Clone();
                b.outputToSlot = s.OutTo; b.inputFromSlot = s.InFrom; b.outputFromSlot = s.OutFrom; b.inputToSlot = s.InTo;
                b.outputOffset = s.OutOff; b.inputOffset = s.InOff;
                b.pitch = s.Pitch; b.pitch2 = s.Pitch2; b.tilt = s.Tilt; b.tilt2 = s.Tilt2;
                pc.Add(s.Name, b);
                if (s.Generic) pc.SetDefault(s.ItemId, s.Name);
            }
            return pc;
        }

        static E.BpBuilding Record(int itemId, int model)
        {
            var b = new E.BpBuilding();
            b.itemId = (short)itemId; b.modelIndex = (short)model;
            b.tempOutputObjIdx = -1; b.tempInputObjIdx = -1;
            b.parameters = new int[0];
            return b;
        }

        // ------------------------------------------------------------------ EngineResult -> BlueprintData
        internal static BlueprintData ToBlueprint(E.EngineResult r)
        {
            var bp = new BlueprintData();
            bp.ResetAsEmpty();
            bp.gameVersion = GameConfig.gameVersion.ToString();
            bp.shortDesc = string.IsNullOrEmpty(r.ShortDesc) ? "DSPCalculatorFlat" : r.ShortDesc;
            bp.desc = r.Desc ?? "";
            SetPatch(bp, 1);                           // same patch level DSPCalculator writes for generated blueprints

            int[] icons = r.Icons ?? new int[5];
            int iconCount = 0;
            for (int i = 0; i < 5; i++) if (i < icons.Length && icons[i] > 0) iconCount++;
            bp.icon0 = icons.Length > 0 ? icons[0] : 0;
            bp.icon1 = icons.Length > 1 ? icons[1] : 0;
            bp.icon2 = icons.Length > 2 ? icons[2] : 0;
            bp.icon3 = icons.Length > 3 ? icons[3] : 0;
            bp.icon4 = icons.Length > 4 ? icons[4] : 0;
            bp.layout = iconCount <= 1 ? EIconLayout.OneIcon : iconCount == 2 ? EIconLayout.TwoIcon46
                      : iconCount == 3 ? EIconLayout.ThreeIcon813 : iconCount == 4 ? EIconLayout.FourIcon7913 : EIconLayout.FiveIcon57913;

            int w = Math.Max(1, r.AreaWidth), h = Math.Max(1, r.AreaHeight);
            var area = new BlueprintArea();
            area.index = 0; area.parentIndex = -1; area.tropicAnchor = 0; area.areaSegments = 200;
            area.anchorLocalOffsetX = 0; area.anchorLocalOffsetY = 0; area.width = w; area.height = h;
            bp.areas = new[] { area };
            bp.primaryAreaIdx = 0;
            bp.cursorTargetArea = 0;
            bp.dragBoxSize_x = w; bp.dragBoxSize_y = h;
            bp.cursorOffset_x = (int)Math.Round(w * 0.5 - 0.01);
            bp.cursorOffset_y = (int)Math.Round(h * 0.5 - 0.01);

            int n = r.Buildings.Count;
            var arr = new BlueprintBuilding[n];
            for (int i = 0; i < n; i++)
            {
                E.BpBuilding s = r.Buildings[i];
                var b = new BlueprintBuilding();
                b.index = i;
                b.areaIndex = 0;
                b.itemId = s.itemId;
                int model = s.modelIndex;
                if (model <= 0)
                {
                    ItemProto ip = LDB.items.Select(s.itemId);
                    if (ip == null || ip.prefabDesc == null) throw new Exception("unknown building item " + s.itemId);
                    model = ip.prefabDesc.modelIndex;
                }
                b.modelIndex = (short)model;
                b.recipeId = s.recipeId; b.filterId = s.filterId;
                b.localOffset_x = s.localOffset_x; b.localOffset_y = s.localOffset_y; b.localOffset_z = s.localOffset_z;
                b.localOffset_x2 = s.localOffset_x2; b.localOffset_y2 = s.localOffset_y2; b.localOffset_z2 = s.localOffset_z2;
                b.yaw = s.yaw; b.yaw2 = s.yaw2; b.tilt = s.tilt; b.tilt2 = s.tilt2; b.pitch = s.pitch; b.pitch2 = s.pitch2;
                b.outputToSlot = s.outputToSlot; b.inputFromSlot = s.inputFromSlot;
                b.outputFromSlot = s.outputFromSlot; b.inputToSlot = s.inputToSlot;
                b.outputOffset = s.outputOffset; b.inputOffset = s.inputOffset;
                b.parameters = s.parameters != null ? (int[])s.parameters.Clone() : new int[0];
                b.content = s.content;
                b.tempOutputObjIdx = -1; b.tempInputObjIdx = -1;
                arr[i] = b;
            }
            for (int i = 0; i < n; i++)
            {
                E.BpBuilding s = r.Buildings[i];
                if (s.tempOutputObjIdx >= 0) arr[i].outputObj = Ref(arr, s.tempOutputObjIdx, i, "output");
                if (s.tempInputObjIdx >= 0) arr[i].inputObj = Ref(arr, s.tempInputObjIdx, i, "input");
            }
            bp.buildings = arr;

            // validate: export and re-import with the game's own codec
            string text = bp.ToBase64String();
            var check = new BlueprintData();
            BlueprintDataIOError err = check.FromBase64String(text);
            if (err != BlueprintDataIOError.OK) throw new Exception("the game rejected the generated blueprint (" + err + ")");
            if (check.buildings == null || check.buildings.Length != n)
                throw new Exception("re-import returned " + (check.buildings == null ? 0 : check.buildings.Length) + " of " + n + " buildings");
            return bp;
        }

        static BlueprintBuilding Ref(BlueprintBuilding[] arr, int idx, int from, string what)
        {
            if (idx >= arr.Length) throw new Exception("building " + from + " has " + what + " link to missing building " + idx);
            return arr[idx];
        }

        /// BlueprintData.Patch is not public in the stock Assembly-CSharp.
        static void SetPatch(BlueprintData bp, int patch)
        {
            var f = HarmonyLib.AccessTools.Field(typeof(BlueprintData), "Patch");
            if (f != null) f.SetValue(bp, patch);
        }
    }
}
