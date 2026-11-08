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
    PrototypeCells Cells { get; }       // prototype building records (BpBuilding) the Emitter clones
    string GameVersion { get; }         // written into the blueprint header (null = Emitter.DefaultGameVersion)
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
  public int SorterItemMax = 2012;    // 2011/2012/2013/2014 cap (2014 pile -> Mk.III, warning)
  public bool CheapestTiers = true;   // cheapest tier per lane / sorter inside the caps (decision 8)
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
// Field names AND types mirror the game's BlueprintBuilding exactly (reflection on Assembly-CSharp.dll of the
// installed game, 0.10.35.x), so a host copies them 1:1. The game links by object reference (outputObj/inputObj);
// here the links are indices into EngineResult.Buildings in the game's own temp fields (-1 = none).
public class BpBuilding {
  public int index, areaIndex;
  public float localOffset_x, localOffset_y, localOffset_z;
  public float localOffset_x2, localOffset_y2, localOffset_z2;
  public float pitch, pitch2, yaw, yaw2, tilt, tilt2;
  public short itemId, modelIndex;                       // Int16 in the game
  public int tempOutputObjIdx = -1, tempInputObjIdx = -1; // game: outputObj / inputObj references
  public int outputToSlot, inputFromSlot, outputFromSlot, inputToSlot, outputOffset, inputOffset;
  public int recipeId, filterId;
  public int[] parameters;
  public string content;                                 // newer game field, copied from the prototype
  public BpBuilding Clone();
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

## Implementation notes and deviations (phase 2, 2026-11-02)

Code: `engine/Api.cs` (types above + `FlatEngine`), `engine/PlanMapper.cs` (plan mapping, `StepSupport`),
`engine/EngineTuning.cs`. All core files are in namespace `DspFlat.Engine`; the host side (`engine.host/`) is
`DspFlat.Host`. `build.cmd` compiles `engine/` alone into `bin/DspFlat.Engine.dll` (System + System.Core only) and
fails if a core file uses `System.IO`, `Console.`, `Environment.`, `CurrentCulture` or `GameBp.`.

- **`BpBuilding`**: real field names/types instead of the draft (`itemId`/`modelIndex` are `short`; links are
  `tempOutputObjIdx`/`tempInputObjIdx`, not `outputObjIndex`/`inputObjIndex`; `content` added). Verified by reflection:
  BlueprintBuilding has exactly index, areaIndex, localOffset_x/y/z, localOffset_x2/y2/z2, pitch, pitch2, yaw, yaw2,
  tilt, tilt2, itemId, modelIndex, outputObj, inputObj, tempOutputObjIdx, tempInputObjIdx, outputToSlot, inputFromSlot,
  outputFromSlot, inputToSlot, outputOffset, inputOffset, recipeId, filterId, parameters, content.
- **`GameData`** is plain values: public dictionaries `Items`/`Recipes`/`Machines`/`Colliders`, `IconTags` (item id ->
  ItemProto.IconTag, for the thumbnail text), helpers `AddItem`, `AddName`, `AddCollider`. The JSON loader moved to the
  CLI host (`DspFlat.Host.GameDataFiles.Load`).
- **`PrototypeCells`** (in `Api.cs`): `ByName` (string -> `BpBuilding`), `SetDefault(itemId, name)`, `ProtoFor(itemId)`,
  `Proto(name)`, `Errors`. Named cells the Emitter asks for: `depot-mk1`, `depot-in`, `depot-out1`, `depot-out2`,
  `splitter-m39`, `splitter-m40`; generic per item: arc/plane smelter, assembler Mk.I-III, belt Mk.I/II/III, sorter Mk.I/II/III,
  tesla, splitter, depot Mk.I. CLI: `DspFlat.Host.Cells` loads them from the user's templates + `refs\`.
- **`PlanStep.ExternalInputs`** (extension): inputs this step takes from a lane although another step makes them
  (CLI `--raw "Item@Consumer"`). Empty for the addon.
- **`LayoutOptions`** extensions: `InEdge`/`OutEdge` (default S/N), `Safe60`, `Buffer` (default true), `OwnSupply`,
  `MaxRow`, `ShortDesc`/`Desc` overrides. `TimeBudgetMs <= 0` = no hard limit (the CLI uses this; the composer keeps
  its per-mode-set budget `EngineTuning.ComposeBudgetMs`, 240 s). `TimeBudgetMs > 0` = hard limit for the whole run:
  the composer stops between attempts and keeps the best valid attempt, else Generate fails with "time budget reached".
- **Tier caps** (phase 4, `engine/Lanes.cs`): `BeltItemMax` / `SorterItemMax` are caps, Mk.I-III each (pile sorter
  -> Mk.III with a warning). The composer builds and splits lanes at the cap tiers; with `CheapestTiers` (default true)
  every belt run (straight/corner links; side-joins and splitters start new runs) then gets the cheapest belt tier that
  carries its highest load and every machine sorter the cheapest tier that moves max(planned, full-speed) items at its
  length; the result is validated again, else the cap tiers stay (note "tiers: ..."). Buffer-depot sorters stay Mk.II
  (named prototypes). A tier without a prototype record (`PrototypeCells.ProtoFor` null) is never used: a cap above it
  drops to the next lower tier with a warning. CLI prototypes: Sorter Mk.III = the user's COL-PAIR-3 template record
  (in3 #3, model 43); Belt Mk.III = the Mk.II record with item 2003 / model 37 from game-data.json, derived only after
  checking that the Mk.I and Mk.II records differ only by item id and model index. The Validator's rule 4 is now
  "no belt / sorter above the caps". `BlockSpec.CheapestTiers` defaults to false (CLI: `--cheapest-tiers`, tiers via
  `--belt/--sorter mk1|mk2|mk3`), so the coil-360-v3 regression keeps its Mk.II tiers.
- **Lane split** (decision 9): machines per lane = floor(lane rate / one machine's full-speed per-minute demand),
  minimum over the row's inputs and its output (`Lanes.MachinesPerLane`); a step whose machines need more lanes becomes
  parallel rows with lanes of their own, grouped by material on the input edge. Rows on block input lanes are never
  split by the composer's height cap (mode 1), only by lane capacity. Sorters: MachineRow adds a second sorter when one
  sorter of the cap tier cannot deliver a machine's demand; the tier choice above never picks a sorter that is too slow.
- **Input edge** (decision 7): every block input lane (raw, marked external, dropped-step products) starts with its icon
  tail on the outermost row of `InEdge` (composer extends lanes that start higher; Validator "input edge" fails lanes
  that do not).
- **Spray coaters** (`engine/Coaters.cs`, phase 4): every step with `IncLevel > 0` gets a spray coater (2313) on each
  input lane below its first pickup, all fed by one external proliferator lane (`ProliferatorItemId`; 0 = inferred from
  `IncLevel` = ItemProto.Ability 1/2/4, with a warning) that starts on the input edge with an icon tail and runs as a
  z 1 feed belt one tile behind every coater (game geometry: user's `7-logi\L1.txt`, game 0.10.35; see the file header).
  Proliferator demand = coated lane flow / sprays per item (ItemProto.HpMax 12/24/60; `GameData.SprayCounts`).
  Prototype record: `7-logi\L1.txt` #36 (CLI host, optional). Hook points in shared files are marked `COATERS HOOK`.
- **Proliferator rates**: with `IncLevel > 0` the machine speed is derived from the plan's numbers
  (`output * time / (outCount * 60 * MachineCount)`), so the layout carries exactly the plan's rates.
- **`EngineResult`** extensions for hosts: `GameVersion`, `Header` (`BpHeader`), `Notes` (composer notes), `Plan` (the
  internal `ProductionPlan`), `Layout` (abstract layout, `Bld.Index` = record index), `Report` (validator report).
- **Labs and colliders** (feat-labs): Matrix Lab (2901) steps become rows of lab towers (`engine/LabTowers.cs`; height =
  min(`LabStackMax`, what ONE base sorter per input and the one output sorter carry at full speed), more, lower towers
  when a sorter is short), Miniature Particle Collider (2310) steps become collider rows (`engine/Collider.cs`). Both
  are laid out by `engine/SlotRows.cs` from sorter cells of real blueprints (cited per cell; checked on load by the CLI
  host, `GameCells.CheckSlotRowCells`). Self-evolution Lab (2902), Universe Matrix (6 inputs) and byproduct recipes
  (Mass-energy Storage) stay dropped.
- **Double rows** (`engine/DoubleRows.cs`, `Modules.DoubleRow(RowSpec)`, hooks marked `PATTERNS HOOK: double rows`):
  two facing machine columns 4 tiles apart sharing the middle output lane, own input lanes per column outside it (L1 at
  2 tiles, L2 at 3 tiles), as the user draws them on the pattern sheet `refs\patterns\p-sheet-001.txt` (x29..37: 12 Arc
  smelters 2x6, pitch 3, ore lanes x29/x37 at lateral 0, shared lane x33 with the west column dropping at lateral +1 /
  slot 3 and the east column at lateral -1 / slot 9; x58..68: 6 Assembler Mk.I coils 2x3, pitch 4, L2 magnet lanes
  x58/x68, L1 copper lanes x59/x67, output x63). The sorter cells come from the sheet's middle machines (records drift
  ~0.024 along a column); the six west smelter ore sorters are drawn machine -> belt in the file, the engine uses the
  reversed segment (status "derived"). Ports: `in:<item>` (column A), `in:<item>#B` (column B, `DoubleRows.PortSuffixB`),
  `out:<item>`; each lane port carries its column's share. Tails, icons, coaters (`Coaters.AddToRow`) and the sorter
  throughput check follow `MachineRow`. Composer: when a plan has a step with 1-2 inputs that all arrive on block input
  lanes, >= 4 machines and a smelter / assembler, the grouped row modes are tried once more with mode bit 64
  (`DoubleRows.ModeBit`); such rows are built as double rows (`DoubleRows.BuildRow`, both lane ports become raw input
  lanes) and the cheaper valid attempt wins (12 smelters iron ingot 720/min: 7x41 -> 11x22). The Composer side is opt-in
  (`DoubleRows.Enabled`, default false): a double row has two lanes per raw item separated by the output column, which
  the decision-9 lane expectations (selftests "micro lanes": one lane per 720/min, lanes of one item adjacent on the
  edge) do not allow; with it on, those two cases fail and everything else passes. Known limits: a 2-input double row on two raw items puts its tails as A, B | B, A along the
  edge, which rule 10 ("input groups") rejects - the sheet's coil block has exactly that order (x61 magnet, x62 copper, x64
  copper, x65 magnet), so the rule is stricter than the user's drawing; and the coaters are laid as the engine's COATER
  cell (one coater line 2 below the first pickup across all lanes, ramp column outside side A, feed row across every lane),
  not as drawn: the sheet bundles the four input lanes at x61..65 below the machines with the coaters at y5 on the bundle,
  the lanes jog out to x58/59/67/68 at y8/y9, and the proliferator ramp sits inside the bundle (x60) with a 5-tile feed
  ending after the last coater. To draw it like that, `Coaters.cs` would need lanes as tile paths (corners below the
  coater line) instead of `lanes[x]` + `laneStart[x]`, a bundle mode for the coater line, and the ramp column next to the
  bundle instead of outside the outermost lane.
- **Unsupported steps** (`StepSupport.WhyNot`): building not in {2302, 2315, 2303, 2304, 2305, 2901, 2310}, or a recipe with more
  than one output, or a step whose product is in `ExternalItems` -> dropped with a warning, its products become input
  lanes. Steps that only fed dropped steps are removed too (warning "removed: nothing left in the block consumes X").
  A dropped target, two steps for one item, unknown recipes and recipe cycles are errors.
- **Ordering**: steps are sorted topologically with the input order as tie-break (an already topological input comes
  back unchanged); the target step goes last; lane items are ordered by rate (highest first), then id.
- **Tuning**: the former `MAKEBLOCK_*` environment variables are fields of `EngineTuning` (process-wide); the CLI maps
  the variables in `DspFlat.Host.EnvTuning.Apply`.
- **Logging**: the core never prints. `ValidationReport.Print(Action<string>)` takes the host's line writer; composer
  progress goes through `Composer.OnProgress` / the `progress` callback, details through `EngineResult.Notes`.
- **Culture**: the core does not change the thread culture. Header/desc numbers are formatted with
  `InvariantCulture`; some diagnostic messages concatenate doubles with the current culture, so hosts should run the
  worker thread with the invariant culture (the CLI does).
- **Serialization** (MD5F, `BlueprintData`, export string) is host-side: CLI `DspFlat.Host.GameEmitter.Serialize(records,
  header, layout)` + `GameEmitter.CheckExport` (former `Validator.CheckExport`). Kit and bus modes (`Kit`, `Bus`) moved
  to `engine.host/` as CLI-only legacy modes (they export through the game DLL).
