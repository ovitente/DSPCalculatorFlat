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
  **Shape since 2026-11-08 (feat-fp-coaters)** = the user's drawn coater block `refs\patterns\p-sheet-001.txt` x58..68:
  all coaters of a row in ONE line c = lowest first pickup - 2; coated lanes with a tail start at c - `Coaters.CoaterRowOffset`
  (5: sheet tails y0, coaters y5) so the tails sit on the input edge; the proliferator lane's ramp column is the tile
  NEXT TO the bundle of coated lanes (`Coaters.RampColumn`: -x side of the lowest coated lane first - sheet x60 west of
  x61..65 - then +x side of the highest when another lane, e.g. the output lane of a 2-input row, takes the -x tile;
  only when both are lanes it falls back to the pre-2026-11-08 place outside all lanes); profile tail z 0 (icon) ->
  foot z 0 -> z 0.5 -> z 1 straight -> z 1 corner at c - 1, feed on z 1 one tile behind every coater, dead end over the
  far coated lane. The old shape (ramp outside side A, no straight z 1 node before the corner, tails at c - 4) is no
  longer generated but `Coaters.Validate` accepts both (older fixtures). Selftests: `micro coaters` (plain 2-input coil
  row: ramp on the +x side, `tests\SelfTestCoaters.cs CheckDrawnCoaterShape`), `micro double c` (coil double row: ramp
  on the -x side as drawn; the engine's lanes stay straight at x58/59/67/68, so the sheet comparison is relative).
- **Drawn patterns, Validator relaxations** (feat-pat-drawn, 2026-11-08; `tests/SelfTestDrawn.cs`, filter `micro drawn`):
  the user's drawn files `refs\patterns\p08-asm-row-3in.txt`, `p09-asm-row-4in.txt`, `p12-input-edge-comb.txt`,
  `p-sheet-001.txt` are game-proven, so every rule they broke was relaxed to what they show: belt yaw = chord from the
  predecessor to the successor (diagonal corners); diagonal / off-grid belt links accepted within the game F4/F5 limits
  (`BeltMaxLinkM`, slope); a sole slot-2/3 predecessor is a belt's input (`MainPred`); sorter yaw may skew up to F11
  (`GameData.SorterMaxSkewDeg`, was 5 deg); raised runs may start at an icon tail / end dead in the air and sit over a
  stacked tail / end, raised nodes may be off the grid; a turn on a ramp foot or first/last raised node (S4) is a warning;
  input lanes may run past the last pickup while flush with the last machine's footprint; coater feed chains of imported
  layouts are tagged from the geometry (`Coaters.MarkFeeds`, also called by `ValidateBelts`), may start at a proliferator
  icon tail instead of a block port, and feed nodes tolerate `Coaters.FeedGridTol` (0.02) of game noise. Open item: the
  sheet's x47..49 three-level stack with diagonal lane shifts is EdgeBus territory (rules 2/3) and is still rejected there.
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
  edge; the sheet's coil block has exactly that order (x61 magnet, x62 copper, x64 copper, x65 magnet), so rule 10
  ("input groups") is a warning since 2026-11-08 (user: the order along the edge is not important); and the coaters follow
  the drawn shape relative to the engine's straight lanes (see "Spray coaters" above; feat-fp-coaters): the sheet bundles
  the four input lanes at x61..65 below the machines with the coaters at y5 on the bundle and the lanes jog out to
  x58/59/67/68 at y8/y9, while the engine keeps the lanes straight at x58/59/67/68 and puts the ramp column at x57 (one
  tile on the -x side of the lowest coated lane, as x60 is for the bundle). Bundling the lanes themselves (tile paths with
  corners below the coater line instead of `lanes[x]` + `laneStart[x]`) is still open.
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

## Pattern rows (P08/P09, 2026-11-08)

The user's in-game drawings `refs\patterns\p08-asm-row-3in.txt` (3 inputs) and `p09-asm-row-4in.txt` (4 inputs) are
the proof of the 3/4-input `Modules.MachineRow` preset (`docs/patterns-design.md`). `Geo.SourceFiles` keys `p08` /
`p09`; the cells sit in a delimited "PATTERN ROWS" region at the end of the `Geo` table (status `game`, source record
cited, re-read by the CLI host's `Cells.Load` self-check). Preset for 3-4 inputs: side A carries in1 (L1) and in3 (L2),
side B in2 (L1) and in4 (L2), the output goes on side A at **L3** beyond the two input lanes; the output lane starts at
the first machine's pickup tile. Side B with the output at L2/L3 remains the fallback when no proven cell combination
exists (note "output placed on side B ..."), then the unproven derived/inferred cells. Pitch stays 4 (L2/L3 sorters,
assembler collider). Output sorters whose belt tile has a predecessor get the game's NEGATIVE belt offset
(`SorterGeo.Make(..., laneContinuesBehind)`: floor(20 d), P08 -5; inputs keep the clamp at 0). The 1- and 2-input presets
are unchanged. Self tests: `tests/SelfTestRows34.cs` (`MAKEBLOCK_CASES=micro rows34`).
## Patterns (feat-pat-loader, 2026-11-08; design in `docs/patterns-design.md`, catalog in `docs/pattern-catalog.md`)

Pure model, namespace `DspFlat.Engine`:

- **Import** `engine/BpImport.cs`: `BpImport.ToLayout(IList<BpBuilding> recs, GameData gd, bool dropStackedSplitters)`
  turns plain records into a linked `Layout` (belts with icon parameters and `splitter-belt` cells, splitters with model
  index + z, sorters with both ends / offsets, spray coaters as `Kind.Other`, depots, towers, machines with recipe).
  Links follow `tempOutputObjIdx` / `tempInputObjIdx` (by the records' `index` field when unique, else by position);
  `Bld.Index` = the record's index. Hosts convert game objects first: CLI `GameBp.ToRecords(bp)` (inverse of
  `FromRecords`); `tests/SelfTest.cs FullLayout` is now `BpImport.ToLayout(GameBp.ToRecords(bp), gd, drop)`.
- **Recognition** `engine/Patterns.cs`: `Patterns.Analyze(Layout, GameData)` -> `List<PatternInfo>`. Sheet-level
  passes first (they span several disconnected lanes): `Comb` (icon-tail chain heads on one edge line, clustered with
  gaps <= 2: `LaneCount`, `LanePitch`, `Edge`, `Items`), `Bridge` (maximal runs of raised belts bundled by tile
  adjacency: `Runs`, `Crossed` lanes per run; coater feeds excluded), `SplitterTurn` (per splitter with outgoing belts:
  `CornerDistances` = distance from the splitter centre to the first off-axis tile of each output, -1 = no turn).
  Then connectivity groups (belt links + sorter links + coater-over-belt / coater feed + touching machine footprints;
  teslas ignored) are classified: `CoaterRow` (coaters on one line: `Coaters`, `FeedCount`, `FeedZProfile` e.g.
  `0/0.5/1`, `FeedOffset` -1 = one tile behind, `FeedDeadEnds`), machine lines grouped by item/recipe/yaw and column
  (or row): two lines of one item 3..6 tiles apart sharing a lane -> `DoubleColumn` (`MachineCount`+`MachineCount2`,
  `SharedLanes`, `LaneOffsets` from the midpoint), else `Row` (`MachineItem`, `RecipeId`, `MachineCount`, `MachineYaw`,
  `Pitch`, `Flow`, `Lanes`). A `LaneInfo` is the first machine's sorter on that lane: `Side`, `Length`, `Lateral`,
  `Slot`, `BeltDx/Dy`, `Sx..Ey` relative to the machine centre, `Input`, `Item` (icon tail first, else recipe output /
  the one unclaimed recipe input; `ItemFromIcon`), `Note` when the other machines' sorters differ. Groups without
  machines, coaters or a sheet-level match -> `Unknown` (bounds + counts). `Analyze` never throws (an exception becomes
  an `Unknown` with `Note` "analysis error").
- **Registration**: `Patterns.RowCells(PatternInfo row, string source)` -> `SorterGeo` cells (status "game", `Source` =
  file + " #" + record index, `SourceKey` null so `Cells.Load` does not re-check them, `Name` suffixed with the file so
  it never collides with a table cell); `Geo.Register(cells, preferred)` puts them before (preferred) or after the
  built-in table for `Find` / `FindAny` / `All`; the same name registered again replaces the earlier cell.
- **Catalog**: `Patterns.Catalog` = P01..P17 (`Id`, `FilePattern` `pNN-*.txt`, `Purpose`, `ProofKind`, `Proves`
  predicate); `Patterns.Status(IEnumerable<PatternFile>)` (file name, header desc, infos) -> per entry
  `missing | drawn | proven`: a file named `pNN-*` draws PNN, else any file whose analysis satisfies `Proves` (a sheet
  with several patterns); proven when that file's desc contains the word `ok` (`Patterns.IsOk`).
- **CLI**: `MakeBlock.exe --patterns [<folder>] [--register]` (default folder `<data root>\refs\patterns`): one line per
  `PatternInfo` per file, the catalog table and, with `--register`, the registered row cells and what
  `Geo.Find(2304, ...)` returns for the 3/4-input lanes. Self tests: `tests/SelfTestPatterns.cs` (`MAKEBLOCK_CASES=micro
  patterns`).
