> Historical contract of the blueprint-tools engine (phase 1). File split since phase 2: core in `engine/` (namespace`n> `DspFlat.Engine`), game access in `engine.host/` (`GameBp`, `Cells`, `GameEmitter`, `GameDataFiles`, `Kit`, `Bus`),`n> CLI in `cli/`. `lib\X.cs` below means `engine\X.cs` or `engine.host\X.cs`. Binding API: `docs/engine-api.md`.

# MakeBlock engine - contract for the parallel phase

`MakeBlock.exe --item X --rate N` -> validated blueprint for a full raw -> product block.
Architecture: `analysis/generator-design.md`. Geometry: `analysis/patterns.md`. Game numbers: `analysis/game-data.*`.

## Ground rules for every agent

- **Own exactly one file.** Each future file exists now as a compiling stub. Replace its bodies; keep every public
  signature listed below. Add private/internal helpers and extra public members freely **inside your own file**.
- **Do not edit `lib/Model.cs`, `lib/GameBp.cs`, `lib/GameData.cs`, `MakeBlock.cs`, `build.cmd`.** If you need a
  shared type or field, add a class in your own file, or report the request instead of editing the shared file.
- Build: `build.cmd` (compiles `lib\*.cs tests\*.cs MakeBlock.cs`, csc v4.0.30319, C# 5: no `?.`, no `$"..."`,
  no `nameof`, no expression-bodied members, no auto-property initializers). Must stay warning-free and green.
  Another agent's stub throwing `NotImplementedException` is expected; `MakeBlock` prints `NOT IMPLEMENTED YET` (exit 4).
- English only in code, comments and output strings.
- Only `GameBp.cs`, `Cells` (the prototype part of `Cells.cs`) and `Emitter.cs` touch the game DLL. Everything else is
  pure model code and must run without `GameBp.Init`.
- Never write into the user's `Documents\Dyson Sphere Program\Blueprint` folder (read only). Test outputs go to a temp/scratch
  folder. Do not modify the existing `Make*.cs` generators (they are regression oracles).
- Units: tiles, x = east, y = north, yaw clockwise from north (N 0, E 90, S 180, W 270); rates in **items/min**.

## User's layout rules (mandatory; the Validator enforces them)

1. **Input sorters have length 1** and their belt lane is directly adjacent to the machine; with two inputs, **one lane
   on each side** of the machine row.
2. **3-4 inputs:** the extra lanes sit at **length 2, directly behind the adjacent (L1) lanes** (3rd input behind side A,
   4th behind side B). This is the only exception to rule 1.
3. **Outputs may be offset:** output sorter length 1 or 2, **max 3**.
4. **Tiers: Mk.II belts (2002) and Mk.II sorters (2012) at most. No Mk.III belt or sorter anywhere.**
   (`Tiers.CheckUserRules` rejects them on the CLI; the Validator must also fail any 2003/2013 record.)
5. **Input-belt icon:** the FIRST belt tile of every block input chain gets `Parameters = [itemId, 0]`
   (e.g. `[1001,0]` iron ore, `[1002,0]` copper ore). Proof: `refs\lent-begin.txt`. Must survive re-import.
6. **Merges are side-joins, never splitters:** the joining chain's last tile `Output = target tile` (adjacent,
   perpendicular), `OutputToSlot = 2` (3 if 2 is taken); the target's own predecessor keeps slot 1.
7. **Splitters (2020) only for splitting**, with the geometry of `refs\splitters.txt` (below).
8. Any validation failure -> no file written.
9. **Input tail** (user review 2026-10-31): every block input lane starts with a tail belt on the input edge, before the
   first pickup tile (never the pickup tile itself); the tail carries the icon and is the connection point
   (`RowSpec.TailItems`; Validator "input tail").
10. **Input lanes grouped by material** along the input edge, never interleaved (iron, copper, iron); separate lanes, no
   splitters/merges inside the block (Composer `GroupByRaw`; Validator "input groups").
11. **Compactness:** a single-input consumer stacks flush on its producer (gap only where the machine colliders need it);
   a raw-only row feeding another line is raised to just below its consumer (`raise` variant); line gaps are squeezed one
   by one while the layout stays valid; smallest area wins.

12. **Bridges** (cell BRIDGE, `analysis\bridges.md`): a route may cross 1..3 straight belts with raised nodes at z 0.5
   on the crossed tiles (one-tile ramps, approach-foot-raised-foot-approach straight); only when no flat route exists.
13. **Splitter distribution**: one consolidated producer row per intermediate, split to <= 3 consumer rows with one
   splitter (cell SPLIT); a second row only above 720/min or 3 consumers; no splitter cascades (unproven).
14. **Game paste limits** (`analysis\buildings\conditions.md`): exact colliders (`colliders.json`), belt link <= 2.302 m,
   slope <= 36.87 deg, sorter 0.6..5.5 m / <= 3.499 cells, power nodes >= 3.5 m (accumulators included), all with the
   E-W tile factor 0.8557..1.1409 (up to 60 deg latitude). Sorter slot = face by direction first (`Geo.SlotAt(.., face)`).

15. **Splitter clearance** (in-game test 2026-10-31: turbine-60-v1 / missile-90-v1 refused): a pasted belt probes a
   0.23 m sphere and collides with a pasted splitter's arm boxes unless it is within 3 belt hops of it (game rule, the
   tile straight next to a splitter collides). Engine rule from the proven files: no belt that is not on the splitter's
   own chains within 2.0 tiles of its centre (was 3.0; 2.0 proven by refs\engine-turbine-60-v2-userupgrade.txt); the 4 side tiles are only the cell's own belts (`GameData.BeltProbeGapM`,
   `SplitterForeignBeltMin`; Validator "splitter clearance"; Composer zones).
16. **Clean routing**: attempts are ranked by area + 25 per raised bridge node + 4 per corner belt; router turn cost 3 tiles,
   so straight bridges beat jogs ("circuit board" style). Layout variants: line rows mirrored, producer lines reversed.
17. **Sizing**: every machine is supplied so its production cycle never stops - exactly enough input, not less (idle) and
   not more (waste); unavoidable rounding up is fine. N final machines = N machines at 100% with the full chain sized
   behind them (the Solver does this; a row split in two parts rounds each part up, e.g. coil 1080: magnets 13.5 + 13.5
   -> 14 + 14).

Upgrade 2026-10-31 (single owner of lib\, tests\, MakeBlock.cs): `GameData` loads `analysis\buildings\colliders.json`
(`ColliderGap`, `MinPitch`, `BoxesAt`, placement constants); `Bld.IsRaised` (raised belts occupy no grid tile);
`BlockSpec.Safe60` / `--safe60`; `RowSpec.AlongEW/Safe60/FinalQuarters`; `Modules.BeltRun` accepts `{x,y,1}` raised tiles
(`Modules.BridgeShapeError`); `Validator.ValidateBelts`; `Composer.Map` / `--map`.

Upgrade 2026-10-31 (turret fixes + kit): `Bld.Load` (per-row machine load, Validator per-machine rate = full x Load);
`BlockSpec.RawFor` / `OwnSupply` / `MaxRow`, `Step.External` / `IsExternal`; `Composer.PackConsumers`, `RowLoad`,
`RotateLayout` (splitters stay yaw 0, side slots turn); `Modules.TeslaAllowed` (tower tile filter); MachineRow output
sorters sized for full-speed output; new `lib\Kit.cs` (kit mode, README "Kit mode"). The user lifted "never write into
Blueprint" for explicit `--kit-dir` / copied outputs in `START\`.

Upgrade 2026-10-31 (bus kit): new `lib\Bus.cs` (`Bus`, `BusFloor`, `BusLane`, `BusGroup`, `BusBand`, `BusLink`;
`MakeBlock --bus`): floorplan first (bus lanes + machine columns + bands), assembled layout + one validated module
per band with aligned bus seams; `Bus.ApplyMarginRule`. README "Bus kit mode".

## Shared contract (already written; read, do not edit)

- `lib/Model.cs`: `Ids`, `Dir`/`Dirs`, `Kind`, `Bld` (mirrors BlueprintBuilding fields + metadata `Carries`, `Chain`,
  `Role`, `Cell`, `Index`, `Game`; factories `Machine/Belt/Sorter/Tesla/Splitter`; rigid `Translate`/`RotateCW`),
  `Port`/`PortKind`, `Fragment`, `Rect`, `Footprints`, `Grid`, `Layout` (`Add`, `BeltAt`, static `Link`, `Reindex`,
  `Bounds`, `Errors`), `Tiers`, `BlockSpec`, `ItemRate`, `Step`, `ProductionPlan`, `ValidationReport`.
- `lib/GameData.cs`: `GameData.Load(gameDataJson, itemIdsJson)`, `ItemDef`, `RecipeDef`, `MachineDef`, `ItemCount`;
  `ResolveItem`, `DefaultRecipe`, `RecipesFor`, `Speed`, `BeltRate` (Mk.II 720/min), `SorterRate(item, L)`
  (Mk.II 180/90/60 per min at L1/2/3), `TeslaCoverTiles` (8.36), `TeslaLinkTiles` (17.9),
  `GameTeslaCoverTiles` 8.36 / `GameTeslaLinkTiles` 17.9 (validator, game PowerSystem rule), `PlaceTeslaCover` 7.9 /
  `PlaceTeslaLink` 16 (TeslaGrid margin), `PowerPoints(b)` (machine center, sorter start + midpoint). The old
  `ProvenTeslaCover = 7` / `ProvenTeslaLink = 12` are no longer used.
- `lib/GameBp.cs`: `Init(dll)`, `Get/Set/Fl/In/Clone`, `Load(path)`, `Import(text, what)`, `Buildings`, `ByIndex`,
  `SetBuildings(bp, records)` (renumbers index, areaIndex 0, temp idx -1), `FillHeader(bp, BpHeader)`, `Md5F`,
  `Export(bp)` -> `ExportResult` (game `ToBase64String` + re-import + count + MD5F; writes nothing), `Write(path, text)`.
  Verified: `MakeBlock --check-gamebp refs\coil-360-v3.txt` -> 176 in/out, signature valid.

Conventions of `Bld` records (from patterns.md section 0, encoded in the factories):
- Belt: `Yaw = Yaw2 = flow` (corner = bisector of in/out, e.g. N->E 45), `Output` = next tile, `OutputToSlot` 1
  (0 at the chain end, 2 for a side-join), `InputFromSlot 0, OutputFromSlot 0, InputToSlot 1`, offsets 0.
- Sorter belt->machine: `Input = belt, Output = machine, OutputToSlot = machine slot, InputFromSlot = -1,
  InputOffset = floor(20*d)`; machine->belt mirrored. `Parameters = [L]`. (X,Y) start, (X2,Y2) end.
- Machine slots: 3x3 table in the machine's LOCAL frame, clockwise from NW corner (patterns.md section 0). Rigid rotation
  of a fragment keeps slots unchanged.

## File-by-file contract

### `lib/Solver.cs` - `Solver` (no DLL)
```
public Solver(GameData gd, BlockSpec spec)
public ProductionPlan Solve()
public static double MachinesFor(RecipeDef r, int item, double ratePerMin, double speed)   // already implemented
```
- Recipe per item: `spec.RecipeOverrides[item]` else `gd.DefaultRecipe(item)`. Raw = `ItemDef.IsResource` or in
  `spec.TreatAsRaw` or no recipe. Machine = `spec.Tiers.MachineFor(recipe.Type)`; must be in `recipe.Buildings`.
- `MachinesExact = rate * TimeSec / (outCount * 60 * speed)`, `Machines = ceil(MachinesExact - 1e-9)`; recurse on inputs at
  the EXACT rate. Merge identical items across branches (DAG): one Step per item, summed demand.
- Steps topologically ordered (raw consumers first, target last); fill `Inputs`, `Outputs`, `MaxOutputRate`,
  `ConsumerSteps`; `RawInputs` summed. Byproducts: warn. Warn when any item's rate > `gd.BeltRate(tiers.Belt)`
  (one lane can't carry it) and when a per-machine input rate > `gd.SorterRate(tiers.Sorter, 1)`.
- `CleanRatio`: set `SuggestedRate` (search near the target for minimal total `Machines - MachinesExact`); do not change
  `TargetRate`.
- **Test case:** Magnetic Coil 360/min, arc + Mk.I -> magnet 9 (exact 9.0), copper ingot 3 (3.0), coil asm 4 (4.0);
  raw iron ore 360, copper ore 180.

### `lib/Cells.cs` - `SorterGeo`, `Geo` (no DLL) and `UnitCell`, `Cells` (DLL)
```
public class SorterGeo { Name, MachineItem, BeltToMachine, BeltDx, BeltDy, Sx, Sy, Ex, Ey, Yaw, Slot, Length, Source;
                         public Bld Make(Bld machine, Bld belt, int sorterItem, int carries); }
public static class Geo { public static Dictionary<string, SorterGeo> Sorters();
                          public static SorterGeo Find(int machineItem, bool beltToMachine, Dir side, int length, int lateral); }
public class UnitCell { Name, SourceFile, Index, ItemId, X, Y, Proto }
public class Cells { Errors; ByName; public static Cells Load(string blueprintDir);
                     public object ProtoFor(int itemId); public object NewTemplate(); }
```
- `Geo`: every sorter placement the modules may use, numbers copied from patterns.md / the proven blueprints, relative to
  a machine center. Geometry is per machine class (smelter edge 0.79-0.81, assembler 0.84-0.88). `Make` builds the
  `Bld.Sorter` with slot, L, belt offset `floor(20*d)` (d = belt-side end displacement along belt flow).
  `SorterGeo.Make` must throw if `belt` is not at `(machine + BeltDx/BeltDy)`.
  Cells that MUST exist (proven in game in coil-360-v3, yaw shown is the machine's):
  | cell | machine | belt tile | start -> end (rel.) | yaw | slot | L |
  |---|---|---|---|---|---|---|
  | smelter in E (SMELT-COL-1) | 2302 yaw 0 | (+2,-1) | (+2,-0.797) -> (+0.811,-0.795) | 270 | 5 | 1 (inOff 4) |
  | smelter out W | 2302 yaw 0 | (-2,0) | (-0.811,0) -> (-2,0) | 270 | 10 | 1 |
  | asm in E L1 | 2303 yaw 90 | (+2,-1) | (+2,-0.842) -> (+0.874,-0.841) | 270 | 2 | 1 |
  | asm in W L1 | 2303 yaw 90 | (-2,+1) | (-2,+0.842) -> (-0.874,+0.841) | 90 | 8 | 1 |
  | asm out W L2 | 2303 yaw 90 | (-3,0) | (-0.874,0) -> (-3,0) | 270 | 7 | 2 |
  Smelter mirror (in W / out E) from SMELT-COL-1 mirror; L2 behind-lane inputs from COL-PAIR-3 / SMELT-COL-PAIR-2
  (belt at distance 3, row 0, slot 4/10); L3 from COL-PAIR-3. Anything not in patterns.md = not proven: leave it out
  and let `Find` return null.
- `Cells.Load(bpDir)`: prototype records via `GameBp.Load` + `ByIndex`, self-checked (item id + position), errors into
  `Errors` (never throw on a mismatch). Known good indices (from MakeCoilFactoryV3/MakeMissileFactory):
  `t-tpl\Smelters\smelter-arc-1.txt`: smelter [52] (2,2), sorter Mk.II [40] (4,1.203), belt Mk.II [64] (4,1), tesla [39] (3,0);
  `t-tpl\Assemblers\assemblers-8.txt`: assembler Mk.I [8] (2,2); splitter from `refs\splitters.txt` [0] (7,0).
  Mk.II/III assemblers, plane smelter, Mk.I belt/sorter: find a proven record or report it missing.
- `NewTemplate()`: a fresh import of `assemblers-8.txt` (header/areas carrier, like V3's `bpA8`).

### `lib/Modules.cs` - `RowSpec`, `Modules` (no DLL)
```
public static Fragment MachineRow(RowSpec spec)
public static Fragment BeltRun(string chain, int beltItem, int carries, IList<int[]> tiles, Bld mergeInto)
public static int[][] Path(params int[] xy)
public static void SideMerge(Bld joiningLast, Bld target)
public static Fragment Splitter(int x, int y, Dir inFrom, Dir[] outs, int beltItem, int carries, bool priority)
public static List<Bld> TeslaGrid(Layout layout, GameData gd)
```
- Modules never invent sorter geometry: all sorters come from `Geo.Find(...)` / `SorterGeo.Make`.
- **MachineRow presets** (column of machines stacked along `Flow`, lanes parallel to it; side A = east, B = west for
  Flow N, swapped when `Mirror`). Lane = full-length belt run past every machine; ports `in:<item>` = first tile,
  `out:<item>` = last tile. Origin: first machine center (0,0), Flow N; Composer moves/rotates the fragment.
  | inputs | side A | side B | output | proof |
  |---|---|---|---|---|
  | 1 | in1 L1 | - | B L1 | SMELT-COL-1 (coil-360-v3 smelters, pitch 3) |
  | 2 | in1 L1 | in2 L1 | B L2 (behind in2) | coil-360-v3 assemblers (yaw 90, pitch 4) |
  | 3 | in1 L1, in3 L2 behind | in2 L1 | B L2 behind in2 | rule 2; L2 geometry from COL-PAIR-3 - needs in-game test |
  | 4 | in1 L1, in3 L2 behind | in2 L1, in4 L2 behind | L3 behind a L2 lane (max) | rule 2+3; unproven - needs in-game test |
  `InputItems` are ordered by rate (highest first gets in1). L1 and L2 sorters on one side use different machine
  rows (slots) of that side; output sorter uses a third row. Pitch: smallest proven pitch (`Modules.ProvenPitches`:
  arc smelter 3/4, plane smelter 3, assemblers 4 only) that is >= 3 with only L1 sorters, else >= 4. Assemblers at
  pitch 3 collide in game (collider 3.04 tiles). Input lanes end at the last machine's pickup tile (no stub).
  Check sorter throughput: per machine input rate <= `gd.SorterRate(SorterItem, L)`; else add a second sorter on a free
  slot of the same lane or report it (Mk.II L2 = 90/min exactly matched the Mk.I coil output in v3).
- `BeltRun`: straight + 90-degree corners, corner yaw = bisector (as `Chain` in MakeCoilFactoryV3); every tile
  `Carries = carries`, `Chain = chain`; last tile `OutputToSlot 0` or `SideMerge` into `mergeInto`. Ports `start`/`end`.
- `SideMerge`: rule 6. Throw if not adjacent or not perpendicular to the target's flow.
- `Splitter` geometry (`refs\splitters.txt`, splitter yaw 0 at (sx,sy)): splitter record slots 14/15/15/14,
  params `[priority,0,0,0,0,0]` (priority variant params[0]=1). Belt touching the splitter sits **0.199-0.2 tiles from
  its center** on the side axis (e.g. (sx+0.199, sy), (sx, sy+0.199)); the next belt is at 1.073 / 0.927 from center
  (z 0.003), then integer tiles. Incoming belt: `Output = splitter`, `OutputToSlot` = side (0 N, 1 E, 2 S, 3 W;
  slot 2 seen for the S input). Outgoing belt: `Input = splitter`, `InputFromSlot` = side (0 N, 3 W seen), its
  `Output` = next belt. Only for splitting (rule 7).
- `TeslaGrid`: towers on free tiles (`layout.Grid.IsFree`), every machine and sorter end within
  every `GameData.PowerPoints` within `PlaceTeslaCover` (7.9) of a tower, links <= `PlaceTeslaLink` (16), no two towers
  closer than `GameData.MinPowerNodeTiles` (4.0; game PowerTooClose 3.5 m). Systematic: lattices of spacing 8..11 snapped
  to free tiles (gap rows / between lanes preferred), holes filled greedily, redundant towers pruned, fewest towers win
  (`TeslaGrid(layout, gd, false)` = greedy only, used while the Composer compares attempts). Returns the towers (caller adds them).

### `lib/Composer.cs` - `Composer` (no DLL)
```
public Composer(GameData gd, BlockSpec spec)
public Layout Compose(ProductionPlan plan)
```
- One `MachineRow` per Step (split very long rows into parallel rows when a lane exceeds belt rate). Placement v1 =
  the coil-360-v3 scheme: raw-input lanes enter at `spec.InEdge`, step k output lane is routed (BeltRun, Manhattan,
  no crossings) into the input port of its consumer row; product leaves at `spec.OutEdge`.
- Set the input-belt icon (rule 5) on the first tile of every block input chain; add block ports to `Layout.Ports`
  (Kind, item, rate, edge tile). Items with several consumers: split with `Modules.Splitter` (rule 7), joins with
  `SideMerge` (rule 6); if not supported yet, put a clear message in `Layout.Errors`.
- Add towers from `Modules.TeslaGrid` last. Record problems in `Layout.Errors`; do not throw for layout problems.

### `lib/Validator.cs` - `Validator` (no DLL for `Validate`)
```
public Validator(GameData gd, BlockSpec spec)
public ValidationReport Validate(Layout layout, ProductionPlan plan)
public void CheckExport(ExportResult export, Layout layout, ValidationReport report)
```
Layout-agnostic (no coordinates or chain names hard-coded). Copy every `layout.Errors` entry as a failure, then:
1. Overlap via `Footprints` (sorters excluded), plus real game colliders (`footprintTiles`, gap >= 0, except
   belt-belt / belt-splitter pairs); input-lane belts beyond the last pickup fail. 2. Belt chains: links adjacent (or splitter geometry), no tile twice,
   no cycles, chain end must not point straight into a foreign belt unless it is a side-join (slot 2/3).
3. Sorters: both links, start inside input footprint, end inside output footprint, `L == round(len - 0.3)`, slot matches
   geometry (`SlotAt` in MakeCoilFactoryV3, machine yaw aware), no two sorters on one machine slot, yaw = direction,
   not passing through a third machine. 4. Flow: walk chains (follow side-joins/splitters); every machine input sorter
   sits on a chain carrying a recipe input; every output sorter's chain reaches its consumer or a block output port.
5. Recipe set, machine allowed for it (`RecipeDef.Buildings`). 6. Rates: lane <= belt rate, per sorter <= sorter rate,
   supply >= demand per item (from the plan and machine counts). 7. Power: `GameTeslaCoverTiles`/`GameTeslaLinkTiles` on `PowerPoints` (`ValidatePower` runs this check alone); power nodes >= `GameData.MinPowerNodeTiles` (4.0) apart (game PowerTooClose, 3.5 m).
User rules: 1-3 (input L1 except rule-2 lanes at L2 directly behind an L1 lane of the same machine; output L <= 3),
4 (no 2003/2013), 5 (icon on first tile of every block input chain), 6 (no splitter used as merge), 7.
`CheckExport` (check 8): `export.Ok`, re-import count == `layout.All.Count`, input-belt icons present in
`export.Reimported` at the same tiles.

### `lib/Emitter.cs` - `Emitter` (DLL)
```
public Emitter(GameData gd, Cells cells)
public ExportResult Emit(Layout layout, BpHeader header)
public static BpHeader HeaderFor(Layout layout, ProductionPlan plan, BlockSpec spec, GameData gd)
```
- `layout.Reindex()`; for each Bld in `All` order: clone `cells.ProtoFor(ItemId)` (`GameBp.Clone`), set
  `localOffset_x/y/z`, `localOffset_x2/y2/z2`, `yaw/yaw2`, `recipeId`, `filterId`, the 4 slot fields, `outputOffset`,
  `inputOffset`, `parameters` (copy of `Bld.Parameters`, or a deep copy of the prototype's when null), `itemId`,
  `modelIndex` from the prototype; then wire `outputObj/inputObj` from `Bld.Output/Input` `.Game`.
  Snap machines/belts/towers to 0.5 unless the Bld is a fractional splitter belt.
- Records -> `GameBp.SetBuildings(template, records)`; `FillHeader`; `GameBp.Export`. Do not write the file.
- `HeaderFor`: `Width/Height` from `layout.Bounds()`, `Icons[0] = plan.TargetItem`, shortDesc e.g. "Magnetic Coil 360/min",
  desc lists block ports per edge with item and rate, tiers, machine counts (like V3's desc, generated).

### `tests/SelfTest.cs` - `SelfTest`
```
public static int Run(string root, string bpDir)   // 0 = all pass; GameBp.Init already called by MakeBlock --selftest
```
1. Solver: coil 360 -> 9/3/4 (see Solver). 2. Model: Fragment rotation keeps slots, `Footprints.TileRect` of a 3x3.
3. Modules: 1- and 2-input MachineRow geometry equals the coil-360-v3 cells above. 4. Validator: negative cases (L2 input
   not behind an L1 lane, Mk.III belt, missing icon, overlap) must fail.
5. **Regression target: regenerate coil-360-v3 equivalently.** Run the full pipeline for
   `--item "Magnetic Coil" --rate 360` (arc smelter, asm Mk.I, belt/sorter Mk.II) and compare with
   `refs\coil-360-v3.txt` (176 buildings: 118 belt 2002, 36 sorter 2012, 9 arc smelter recipe 2,
   3 arc smelter recipe 3, 4 asm Mk.I 2303 recipe 6, 6 tesla 2201; area 13x29; icons `[1002,0]` copper-ore chain,
   `[1001,0]` iron-ore chain; inputs S edge (copper ore 180/min, iron ore 360/min), coil out N edge 360/min).
   Required: same machine counts per (item, recipe), sorter counts per (direction, L) = 24 smelter L1 + 8 asm in L1 +
   4 asm out L2, validator ok, export ok (re-import + MD5F), icons round-trip. Belt and tower counts may differ (report
   the diff). Desired: identical building multiset after translation. Writes only to a temp folder.

## CLI (`MakeBlock.cs`, done)
`--item <name|id> --rate <n> [--out f.txt] [--plan-only] [--asm mk1|mk2|mk3] [--smelter arc|plane] [--belt mk1|mk2]
[--sorter mk1|mk2] [--raw "a,b"] [--recipe item=id;...] [--clean] [--in-edge S] [--out-edge N] [--short-desc t] [--desc t]
[--bp-dir d] [--game-dll p]`, `--selftest`, `--check-gamebp <file>`. Pipeline order: Solver -> (plan-only stop) ->
Composer -> Validator.Validate -> GameBp.Init -> Cells.Load -> Emitter.HeaderFor/Emit -> Validator.CheckExport -> write.
Exit codes: 0 ok, 1 validation failed, 2 bad arguments, 3 prototype errors, 4 a stub is not implemented yet.
