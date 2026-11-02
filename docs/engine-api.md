# Engine API contract (engine <-> hosts)

Both hosts (the `MakeBlock` CLI and the in-game addon) talk to the engine only through this API.
C# 5, .NET Framework 4.x, no Unity references inside `engine/`.

## Game data

```csharp
namespace DspFlat.Engine {
  // Read-only snapshot of the game data the engine needs. Built once on the host side
  // (CLI: from game-data.json / reflection; addon: from LDB on the Unity main thread),
  // then handed to a worker thread. The engine never calls back into the game.
  public interface IGameSource {
    GameData Data { get; }              // items, recipes, buildings (speed, power, footprint)
    PrototypeCells Cells { get; }       // sorter/belt prototype geometry now loaded from template blueprints
    string GameVersion { get; }         // written into the blueprint header
  }
}
```
The engine decides the exact shape of `GameData` / `PrototypeCells`; both must be constructible
from plain values (no file I/O inside the engine).

## Input: an injected plan

```csharp
public class PlanInput {
  public int TargetItemId;
  public double TargetPerMin;
  public List<PlanStep> Steps = new List<PlanStep>();   // any order; the engine sorts topologically
  public List<int> ExternalItems = new List<int>();     // arrive on input lanes (raw + marked external)
  public LayoutOptions Options = new LayoutOptions();
}
public class PlanStep {
  public int RecipeId;
  public int BuildingItemId;      // 2302 arc smelter, 2303 assembler Mk.I, ... as chosen by the calculator
  public double MachineCount;     // exact (calculator assemblerCount, proliferator already applied)
  public int Machines;            // rounded up count to build
  public int IncLevel;            // -1/0 = no proliferator; >0 = spray coaters on this step's inputs
  public bool ExtraProducts;      // calculator isInc (true) vs speed-up (false)
  public Dictionary<int,double> InputsPerMin = new Dictionary<int,double>();   // itemId -> rate
  public Dictionary<int,double> OutputsPerMin = new Dictionary<int,double>();
}
public class LayoutOptions {
  public int BeltItemMax = 2002;      // 2001/2002/2003 Mk.I/II/III cap
  public int SorterItemMax = 2012;    // 2011/2012/2013/2014 cap
  public int LabStackMax = 15;        // vertical construction tech cap
  public int TimeBudgetMs = 60000;
  public int ProliferatorItemId = 0;  // 1141/1142/1143; 0 = none
}
```

Steps whose building the engine cannot place (refinery, chemical plant, fractionator, byproduct
recipes) are not an error: the engine drops them, turns their products into external lanes and
reports a warning.

## Output

```csharp
public class EngineResult {
  public bool Ok;
  public List<string> Errors = new List<string>();
  public List<string> Warnings = new List<string>();
  public List<BpBuilding> Buildings = new List<BpBuilding>();
  public int AreaWidth, AreaHeight;       // tiles
  public string ShortDesc, Desc;          // thumbnail text and description (game tag syntax)
  public int[] Icons = new int[5];
}
// Field names mirror the game's BlueprintBuilding so a host copies them 1:1.
public class BpBuilding {
  public int index, areaIndex, itemId, modelIndex, recipeId, filterId;
  public float localOffset_x, localOffset_y, localOffset_z, yaw;
  public float localOffset_x2, localOffset_y2, localOffset_z2, yaw2;
  public float tilt, pitch, tilt2, pitch2;
  public int outputObjIndex = -1, inputObjIndex = -1;
  public int outputToSlot, inputFromSlot, outputFromSlot, inputToSlot, outputOffset, inputOffset;
  public int[] parameters;
}
```

## Entry point

```csharp
public static class FlatEngine {
  // Runs on a worker thread. progress(0..1, text) may be called often; cancel is polled.
  public static EngineResult Generate(IGameSource game, PlanInput plan,
                                      Action<double,string> progress, Func<bool> cancel);
}
```
The CLI keeps its `--item/--rate` mode by running its own solver and converting the result to a
`PlanInput`, so both hosts share one code path after planning.
