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
            if (p.LabMaxLevel > 0) o.LabStackMax = p.LabMaxLevel;
            o.TimeBudgetMs = timeBudgetMs;
            o.ProliferatorItemId = p.ProliferatorItemId;
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

        /// Prototype records the engine's Emitter clones. The Emitter overwrites position, yaw, slots, offsets, recipe,
        /// filter and (when the layout sets them) parameters; what the prototype contributes is the model index, pitch /
        /// tilt and the default parameter array. Generic record per buildable item from LDB, plus the named cells the
        /// engine asks for by name (Emitter: "depot-*", "splitter-m39" / "splitter-m40").
        static E.PrototypeCells LoadPrototypes()
        {
            var pc = new E.PrototypeCells();
            foreach (ItemProto it in LDB.items.dataArray)
            {
                if (it == null || !it.CanBuild || it.prefabDesc == null) continue;
                PrefabDesc pd = it.prefabDesc;
                if (it.ID > short.MaxValue || pd.modelIndex > short.MaxValue) continue;
                var b = Record(it.ID, pd.modelIndex);
                if (pd.isBelt || pd.isPowerNode) b.parameters = new int[0];        // proven: belts [], tesla []
                else if (pd.isInserter) b.parameters = new int[] { 1 };            // sorter [length]; the layout sets it
                else if (pd.isSplitter) { b.parameters = new int[6]; Slots(b, 14, 15, 15, 14); }
                else if (pd.isStorage) { b.parameters = new int[DepotParamCount]; Slots(b, 14, 15, 15, 14); }
                else if (pd.isAssembler || pd.isLab) b.parameters = new int[] { 0 }; // proven: smelters, assemblers [0]
                else b.parameters = new int[0];
                string name = "item-" + it.ID;
                pc.Add(name, b);
                pc.SetDefault(it.ID, name);
            }

            // depot buffer cell (user's in-game edit, refs\engine-turbine-60-v2-userupgrade.txt #482, #491-#493):
            // the sorters into / out of a depot carry the game's own pitch / tilt values
            E.BpBuilding depot = pc.ProtoFor(2101);
            if (depot != null) pc.Add("depot-mk1", depot.Clone());
            ItemProto sorter2 = LDB.items.Select(2012);
            if (sorter2 != null && sorter2.prefabDesc != null)
            {
                int m = sorter2.prefabDesc.modelIndex;
                pc.Add("depot-in", Sorter(m, 359.929f, 359.792f, -0.001f, 0.286f));
                pc.Add("depot-out1", Sorter(m, 0.208f, 0f, 0f, 0.001f));
                pc.Add("depot-out2", Sorter(m, 0.208f, 0.04f, 0.286f, 0.054f));
            }
            // splitter Tab variants: same record, the layout sets modelIndex 39 / 40
            E.BpBuilding split = pc.ProtoFor(2020);
            if (split != null)
            {
                E.BpBuilding s39 = split.Clone(); s39.modelIndex = 39; pc.Add("splitter-m39", s39);
                E.BpBuilding s40 = split.Clone(); s40.modelIndex = 40; pc.Add("splitter-m40", s40);
            }
            return pc;
        }

        const int DepotParamCount = 110;   // Depot Mk.I record in the user's in-game blueprint: 110 zero parameters

        static E.BpBuilding Record(int itemId, int model)
        {
            var b = new E.BpBuilding();
            b.itemId = (short)itemId; b.modelIndex = (short)model;
            b.tempOutputObjIdx = -1; b.tempInputObjIdx = -1;
            b.parameters = new int[0];
            return b;
        }

        static E.BpBuilding Sorter(int model, float pitch, float pitch2, float tilt, float tilt2)
        {
            var b = Record(2012, model);
            b.pitch = pitch; b.pitch2 = pitch2; b.tilt = tilt; b.tilt2 = tilt2;
            b.parameters = new int[] { 1 };
            return b;
        }

        static void Slots(E.BpBuilding b, int outTo, int inFrom, int outFrom, int inTo)
        {
            b.outputToSlot = outTo; b.inputFromSlot = inFrom; b.outputFromSlot = outFrom; b.inputToSlot = inTo;
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
