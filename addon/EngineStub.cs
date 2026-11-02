// EngineStub.cs - stand-in for the DspFlat engine, compiled only with /define:ENGINE_STUB
// (build.ps1 picks it when neither ..\bin\DspFlat.Engine.dll nor an engine with FlatEngine exists).
// It mirrors docs\engine-api.md and the members of the engine's GameData that EngineAdapter.cs fills,
// so the addon compiles and its UI / paste path can be smoke-tested in game. The "layout" it returns is a
// demo row of the final step's machines without belts, sorters or power: NOT a working factory.
#if ENGINE_STUB
using System;
using System.Collections.Generic;

namespace DspFlat.Engine
{
    public interface IGameSource
    {
        GameData Data { get; }
        PrototypeCells Cells { get; }
        string GameVersion { get; }
    }

    public class ItemCount
    {
        public int Item; public int Count;
        public ItemCount(int item, int count) { Item = item; Count = count; }
    }

    public class ItemDef
    {
        public int Id; public string Name; public string Type; public bool IsFluid; public int StackSize; public bool CanBuild;
    }

    public class RecipeDef
    {
        public int Id; public string Name; public string Type;
        public double TimeSec;
        public List<ItemCount> Inputs = new List<ItemCount>();
        public List<ItemCount> Outputs = new List<ItemCount>();
        public int[] Buildings = new int[0];
        public bool Explicit;
    }

    public class MachineDef
    {
        public int ItemId; public string Name; public int ModelIndex;
        public string RecipeType;
        public double SpeedMultiplier;
        public double BeltItemsPerSec;
        public Dictionary<int, double> SorterTripsPerSec = new Dictionary<int, double>();
        public int SorterMaxLength;
        public double PowerConnectDistanceM, PowerCoverRadiusM;
        public double[] FootprintTiles;
        public bool IsSplitter;
    }

    public class GameData
    {
        public readonly Dictionary<int, ItemDef> Items = new Dictionary<int, ItemDef>();
        public readonly Dictionary<int, RecipeDef> Recipes = new Dictionary<int, RecipeDef>();
        public readonly Dictionary<int, MachineDef> Machines = new Dictionary<int, MachineDef>();
        public readonly Dictionary<int, string> IconTags = new Dictionary<int, string>();
        public readonly Dictionary<int, ColliderDef> Colliders = new Dictionary<int, ColliderDef>();
        public string SourcePath;

        public class ColliderBox { public double Cx, Cz, Hx, Hz; }
        public class ColliderDef
        {
            public int ItemId, ModelId;
            public List<ColliderBox> Boxes = new List<ColliderBox>();
            public double FootW, FootH;
            public Dictionary<string, int> Pitch = new Dictionary<string, int>();
            public Dictionary<string, int> PitchEW60 = new Dictionary<string, int>();
        }

        public void AddItem(ItemDef it) { Items[it.Id] = it; }
        public void AddName(string name, int id) { }
        public void AddCollider(ColliderDef def) { Colliders[def.ItemId] = def; }
    }

    public class PrototypeCells
    {
        public readonly Dictionary<string, BpBuilding> ByName = new Dictionary<string, BpBuilding>();
        readonly Dictionary<int, string> byItem = new Dictionary<int, string>();
        public readonly List<string> Errors = new List<string>();
        public void Add(string name, BpBuilding record) { ByName[name] = record; }
        public void SetDefault(int itemId, string name) { if (!byItem.ContainsKey(itemId)) byItem[itemId] = name; }
        public BpBuilding ProtoFor(int itemId)
        {
            string n; BpBuilding b;
            return byItem.TryGetValue(itemId, out n) && ByName.TryGetValue(n, out b) ? b : null;
        }
        public BpBuilding Proto(string cellName) { BpBuilding b; return cellName != null && ByName.TryGetValue(cellName, out b) ? b : null; }
    }

    public class PlanInput
    {
        public int TargetItemId;
        public double TargetPerMin;
        public List<PlanStep> Steps = new List<PlanStep>();
        public List<int> ExternalItems = new List<int>();
        public LayoutOptions Options = new LayoutOptions();
    }

    public class PlanStep
    {
        public int RecipeId;
        public int BuildingItemId;
        public double MachineCount;
        public int Machines;
        public int IncLevel;
        public bool ExtraProducts;
        public Dictionary<int, double> InputsPerMin = new Dictionary<int, double>();
        public Dictionary<int, double> OutputsPerMin = new Dictionary<int, double>();
    }

    public class LayoutOptions
    {
        public int BeltItemMax = 2002;
        public int SorterItemMax = 2012;
        public int LabStackMax = 15;
        public int TimeBudgetMs = 60000;
        public int ProliferatorItemId = 0;
    }

    public class EngineResult
    {
        public bool Ok;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<BpBuilding> Buildings = new List<BpBuilding>();
        public int AreaWidth, AreaHeight;
        public string ShortDesc, Desc;
        public int[] Icons = new int[5];
    }

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
        public BpBuilding Clone()
        {
            var c = (BpBuilding)MemberwiseClone();
            if (parameters != null) c.parameters = (int[])parameters.Clone();
            return c;
        }
    }

    public static class FlatEngine
    {
        public const bool IsStub = true;

        public static EngineResult Generate(IGameSource game, PlanInput plan, Action<double, string> progress, Func<bool> cancel)
        {
            var r = new EngineResult();
            PlanStep final = null;
            foreach (var s in plan.Steps)
                if (s.OutputsPerMin.ContainsKey(plan.TargetItemId)) { final = s; break; }
            if (final == null)
            {
                r.Errors.Add("Stub engine: no step produces the target item " + plan.TargetItemId + ".");
                return r;
            }
            int n = Math.Max(1, Math.Min(final.Machines, 30));
            for (int i = 0; i < n; i++)
            {
                if (cancel != null && cancel()) { r.Errors.Add("Cancelled."); return r; }
                if (progress != null) progress((i + 1.0) / n, "stub: placing machine " + (i + 1) + "/" + n);
                System.Threading.Thread.Sleep(50);
                BpBuilding proto = game != null && game.Cells != null ? game.Cells.ProtoFor(final.BuildingItemId) : null;
                var b = proto != null ? proto.Clone() : new BpBuilding();
                b.index = i;
                b.itemId = (short)final.BuildingItemId;
                b.recipeId = final.RecipeId;
                b.localOffset_x = b.localOffset_x2 = 2 + 4 * i;
                b.localOffset_y = b.localOffset_y2 = 2;
                if (b.parameters == null) b.parameters = new int[0];
                r.Buildings.Add(b);
            }
            r.AreaWidth = 4 * n + 1;
            r.AreaHeight = 5;
            r.Icons[0] = plan.TargetItemId;
            r.ShortDesc = "STUB DEMO - not a real layout";
            r.Desc = "DSPCalculatorFlat was built without the engine (EngineStub.cs). This blueprint only proves the paste path.";
            r.Warnings.Add("Built with the stub engine: demo row of " + n + " machines, no belts, sorters or power.");
            r.Ok = true;
            return r;
        }
    }
}
#endif
