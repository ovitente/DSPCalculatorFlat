# Pattern-driven composer ("floorplan"): design and work split (2026-11-08)

Goal (PLAN.md decision 13, user 2026-11-08 "компоновка от паттернов"): lay the block out deterministically in the
user's style, from the drawn patterns and the game-proven turret edit, and keep search only for the stitches. The
search `Composer` stays as the fallback when the floorplan does not validate.

## The style to reproduce (oracle: `blueprint-tools\refs\engine-missile-turret-7.5-v1-useredit3.txt`, 40x26, in game)

Render: `blueprint-tools\work\preview\oracle\engine-missile-turret-7.5-v1-useredit3.png`. Rules: `blueprint-tools\analysis\user-edit-turret75.md`
(15 rules, Russian list at the top). What the picture shows:

1. **Columns.** Every step is a vertical column of machines (pitch 3 smelters, 4 assemblers), all columns side by side
   west -> east, no corridors: between neighbouring columns only their own lanes (2-3 belts), column pitch 5-6 (rule 1).
2. **Vertical stacking.** A producer whose only consumer is one row sits directly under it in the same column, its
   output lane continuing straight into the consumer's input lane (magnet -> coil, smelters -> gear). Otherwise rows
   are separate columns.
3. **South comb.** Block inputs (ore, external items) are tails on the south edge, adjacent (pitch 1 where they
   belong to one column, P12), each lane runs straight north along its column. A lane that must reach a farther column
   turns along the edge and the straight lanes bridge over it (P12 sheet fragment x3..7).
4. **North band.** Products that go to a non-adjacent consumer run up their column, turn into a horizontal lane above
   the columns (one band lane per product, later-joining lane higher: z 0 / 0.5 / 1 stacking is rule 2/3, but a flat
   band of separate y rows is the first version), run east/west and descend into the consumer column from the north.
   Vertical lanes cross the band with single-tile bridges (rule 10, P13 adjacent bridges). Splits: one 2-way splitter at
   the fork (rule 8, P14 corner 2 tiles after the splitter), the third consumer takes from the lane tail.
5. **Consumer lanes fed from the north flow SOUTH** inside the consumer column while its output lane flows north
   (useredit3 motor column: iron ingot and coil come down from the band, motors go up to the band). This needs
   per-lane flow in `RowSpec` (`LaneFlows`).
6. **Final cell** (existing `engine/FinalCell.cs`): the target machine at the north-west, fed from up to 4 sides with L1
   sorters, a dedicated feeder machine hands over machine -> machine (2 x L3), the steel column next to it, buffer
   depot north of it (rule 7), product tail on the north edge.
7. **Tesla towers** in the gaps, minimum by coverage (existing `Modules.TeslaGrid`).

## Pipeline

```
ProductionPlan --Floorplan.Build--> FpPlan (columns, rows placed, connections classified, band/comb rows reserved)
                --Bands.Lay-->      belts for every connection (comb, straight, band with splitters + bridges)
                --Coaters.AddToRow / TeslaGrid / Validator--> Layout, else fall back to Composer.Compose
```
Contract types: `engine/FloorplanModel.cs` (`FpPlan`, `FpColumn`, `FpRow`, `FpConnection`, `FpRoute`). Rows are built
with the existing modules (`Modules.MachineRow`, `Modules.DoubleRow`, `SlotRows`, `FinalCell`), lanes with
`Modules.BeltRun` (tiles with optional z for raised nodes), splitters with `Modules.Splitter` / `SplitterV`, merges
with `Modules.SideMerge`, tails with `Lanes.TailsToEdge` conventions (icon on the first tile, rule 5 / 9).

## Work split (one worktree each, branch `feat-fp-<name>`)

| agent | owns | delivers |
|---|---|---|
| laneflow | `engine/Modules.cs` (MachineRow lane direction), `engine/DoubleRows.cs` (same), `tests/SelfTestLaneFlow.cs` | `RowSpec.LaneFlows` (per input index and output: `Dir.N` / `Dir.S`, default = `Flow`): a lane flowing against the row's flow has its port at the other end, its tail (if any) at that end, belts yawed that way, sorter belt offsets computed from the real flow (`SorterGeo.Make` uses the belt yaw already). Validator must accept it (ports, tails, flow). Selftest: motor row with iron ingot + coil lanes flowing S and the output lane N validates and exports; the P08 reproduction stays exact with default flows. |
| floorplan | `engine/Floorplan.cs`, `tests/SelfTestFloorplan.cs` | `Floorplan.Build(GameData, BlockSpec, ProductionPlan) -> FpPlan`: rows per step (lane split by `Lanes.MachinesPerLane`, DoubleRow when it is narrower, final cell for the target), vertical stacking (producer under its only consumer when lane items match), column order (final cell west; then a greedy order that puts each producer next to its heaviest consumer; steel-like dedicated feeders adjacent to the final cell, rule 5), column x positions with the user's pitch (machines + own lanes, 1 free tile between neighbouring columns' lanes only where a belt needs to pass), rows translated into place; connections classified (`Straight` when stacked, `Comb` for block inputs, `MachineToMachine` for the dedicated feeder of the final cell, else `Band`), band lanes assigned (one y per product, later-joining higher), comb tails assigned (pitch 1, grouped by column, order by column x). No belts are laid here. Selftest: turret 7.5 plan (`SelfTestRules.TurretPlan` style) gives <= 9 columns, final cell west, steel next to it, magnet under coil, 3 comb tails, and the band has <= 4 lanes; motor 120 and coil 360 plans give 2-3 columns. |
| bands | `engine/Bands.cs`, `tests/SelfTestBands.cs` | `Bands.Lay(GameData, BlockSpec, FpPlan, Layout work) -> List<string> errors`: for every connection in a fixed order (Straight first, then Comb, then Band by slot) lay belts with `Modules.BeltRun`: Comb = tail on `CombY` at the slot x, straight north to the target port (bridge over a lane that turns along the edge, P12 geometry: raised z 0.5 nodes on the crossed tiles, feet straight); Band = from the source port up to the band row `BandY0 + slot`, along the band to the consumer column, down into the target port; crossing a vertical lane = one-tile bridge (z 0.5, feet straight, adjacent bridges allowed per P13); forks = `Modules.Splitter` 2-way at the band with the P14 corner geometry (corner 2 tiles after the splitter), third consumer from the lane tail; merges of two sources into one target lane = `Modules.SideMerge`. Occupancy comes from `work.Grid`; never route through machines, sorters or other lanes except by bridge. Tests use hand-made `FpPlan`s (two rows + one band connection with two crossings; comb with a turning lane; 2-way fork) and check `Validator.ValidateBelts` + the bridge rules; the raised-run geometry must match `refs\patterns\p-sheet-001.txt` x13..15 / x3 and `refs\bridges-splitters-user.txt`. |
| coaters | `engine/Coaters.cs` (generator side), `tests/SelfTestCoaters.cs` additions | Coater placement as the user drew it (sheet x58..68): coater line on the bundled lane segment near the input edge (5 tiles below the first pickup in the drawing; use "as close to the input edge as the ramp allows" with the ramp column next to the bundle), one proliferator lane: tail on the edge, ramp z 0 / 0.5 / 1 in the column next to the bundle, feed on z 1 one tile behind the coaters, dead end after the last coater. Keep `Coaters.Validate` accepting both the old and the new shape. Selftest: generated coil block with coaters compared to the sheet (coater y relative to the tails, ramp column position, feed z profile). |

After these four: **assembly** (`engine/FloorplanComposer.cs`, CLI `--floorplan` / `EngineTuning.Floorplan`, hook in
`FlatEngine.Generate`: floorplan first, search fallback with a note) with fixtures coil 360, motor 120, turbine 60,
missile 90, turret 7.5: area and route counts vs the search result and vs useredit3 (40x26), PNG renders.

Rules for every agent: C# 5 (no `?.`, `$""`, `nameof`, expression bodies); English only in code/comments/output;
the core (`engine/`) must not use System.IO / Console / Environment / culture / GameBp (`build.cmd` fails otherwise);
never write into the user's `Documents\Dyson Sphere Program\Blueprint`; touch only the files you own plus one-line
marked hooks (`// FLOORPLAN HOOK`) where unavoidable; keep the full selftest green (baseline 101 pass / 0 fail / 1 skip;
`DSPFLAT_DATA_ROOT=C:\x7\tools\play\dyson-sphere-program\blueprint-tools`, `MAKEBLOCK_CASES=<filter>` for a subset);
commit on your branch; report: what changed, test counts before/after, open questions.
