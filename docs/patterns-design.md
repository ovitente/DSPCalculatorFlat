# Pattern loader: design and work split (2026-11-08)

PLAN.md decision 13: the layout is assembled from patterns the user draws in game instead of long blind search.
`docs/pattern-catalog.md` lists the patterns; this file says how a drawing becomes engine knowledge and who builds what.

## Drawings available (copies in `blueprint-tools\refs\patterns\`, originals in the user's Blueprint folder)

| file | pattern | what it proves |
|---|---|---|
| `p08-asm-row-3in.txt` | P08 | 4 Assembler Mk.II (2304), recipe 97 Electric Motor, 7x19. Machines at (4,5) (4,9) (4,13) (4,17), yaw 0, pitch 4; tesla (4,11). Belts flow N, all lanes end at y 18. West: x2 Iron Ingot L1 in (slot 9), x1 Magnetic Coil L2 in (slot 10), x0 Electric Motor L3 **out** (slot 11, lane starts at the first machine). East: x6 Gear L1 in (slot 5). |
| `p09-asm-row-4in.txt` | P09 | same frame, recipe 144 Missile Set, 8x19. West as P08 (x2 Copper Ingot L1 slot 9, x1 Circuit Board L2 slot 10, x0 Missile Set L3 out slot 11). East: x6 Combustible Unit L1 (slot 5), x7 Engine L2 (slot 4). |
| `p12-input-edge-comb.txt` | P12 | 5 input lanes (iron ore, copper ore, stone, coal, proliferator) with icon tails on one edge, **pitch 1** (adjacent). |
| `p-sheet-001.txt` | P12, P13, P14, P10/P11, P01/P02 + techniques | 69x53 sheet, several disconnected fragments (coordinates x east, y north): |

Sheet fragments:
- **x3..7, y35..47 (P12 split):** 5 adjacent inputs; copper and stone turn west at y41/y42, the iron lane bridges over them (z 0.5), coal and proliferator shift into the freed columns, no crossing.
- **x9..20, y40..42 (P13):** 3 adjacent horizontal lanes (coal, proliferator, energetic graphite) cross 3 adjacent vertical lanes (x13..15) with **adjacent bridges** (raised z 0.5 nodes on consecutive tiles of neighbouring lanes).
- **x1..5 and x11..15, y20..30 (P14):** splitter at (3,25)/(13,25) fed from the south; side outputs leave at x 2.795 / 3.205 (splitter cell belts), the first ground tile is the corner at x1 / x5 (2 tiles from the splitter centre), lanes then run north to the machines. Second variant with diagonal (yaw 319/40) corners.
- **x29..37, y10..27 (P10/P11):** 12 Arc smelters as **two columns of 6 facing each other**, each column has its own ore lane outside (icon tails at the south, y8), one shared output lane in the middle (L1 sorters from both sides, x33). So 6 smelters per lane, not 12.
- **x58..68, y0..22 (P01/P02):** 6 Assembler Mk.I Magnetic Coil as two columns of 3. Per column: Magnet lane outside (L2), Copper Ingot lane inside it (L1), output lane in the middle (x63). **Spray coaters (2313) on all 4 input lanes in one row at y5**; ONE proliferator lane (x60, tail at y0) rises by a ramp (z 0 at y1, 0.5 at y2, 1 at y3), turns east at y4 on z 1 and runs behind the 4 coaters (y4, z 1.002..1.005, x61..65), ending dead after the last coater.
- Techniques only (not patterns): x27..31 long raised run with a turn; x38..39 side approach to a raised lane; x47..51 three lanes merging + bridge over a cross lane; x47..53 three Asm Mk.I fed by 4 smelters through L2 sorters directly (no recipes set).

P08 sorter records of the first machine (centre (4,17)), from `DumpBp.exe` (start -> end, world frame):
```
#8  west L1 in : (2, 16.206) -> (3.122, 16.205)  yaw 90   outToSlot 9   params [1]  inputOffset 4   belt x2
#6  west L2 in : (1, 17.002) -> (3.123, 17.0)    yaw 90   outToSlot 10  params [2]  inputOffset 0   belt x1
#5  west L3 out: (3.123, 17.795) -> (0, 17.799)  yaw 270  inFromSlot 11 params [3]  outputOffset -5 belt x0  (last machine #17: outputOffset 0)
#7  east L1 in : (6, 16.206) -> (4.878, 16.205)  yaw 270  outToSlot 5   params [1]  inputOffset 4   belt x6
P09 #6 east L2 in : (7, 17.002) -> (4.877, 17.0) yaw 270  outToSlot 4   params [2]  inputOffset 0   belt x7
```
Relative to the machine centre: L1 inputs sit at lateral -0.8 (y-0.794), L2 inputs at lateral 0, the L3 output at
lateral +0.8. Read the numbers from the files, not from this table, when you put them into code.

## Architecture

1. **Import** (`engine/BpImport.cs`, pure model): `BpImport.ToLayout(IList<BpBuilding> recs, GameData gd)` builds a linked
   `Layout` from plain records (the logic of `tests/SelfTest.cs FullLayout`, which stays as a thin wrapper). Hosts turn
   game objects into `BpBuilding` (CLI: `GameBp`; addon: the pasted/loaded blueprint).
2. **Recognition** (`engine/Patterns.cs`): `Patterns.Analyze(Layout)` -> `PatternInfo` list: machine rows (item,
   recipe, count, pitch, flow, per-lane side/length/lateral/slot/item, output side/length), input combs (tail pitch),
   bridges (adjacent runs), splitter turns (corner distance), coater rows (feed z, offset). Lane items come from icon
   tails first, else from the recipe (sorter carries).
3. **Registration**: row patterns become `SorterGeo` cells (status "game", Source = file) registered in `Geo`
   (`Geo.Register`), so `Modules.MachineRow` uses the user's geometry; measured constants (comb pitch, splitter corner
   distance, bridge adjacency) feed `EngineTuning` / the Validator.
4. **Catalog** (`Patterns.Catalog`): P01..P17 -> file names, status (drawn / proven `ok` / missing), what each covers.
   `MakeBlock --patterns <dir>` prints the analysis of every file in a folder. The addon loads the same folder
   (`Documents\Dyson Sphere Program\Blueprint\t-tpl\patterns`, `t-tpl`, `lab`) at start-up.
5. **Acceptance tests** (`tests/SelfTestDrawn.cs`): every drawn file must pass the Validator (belts-only for the sheet,
   full for the rows). A drawing the Validator rejects means a rule is stricter than the game: relax it, cite the file.

## Work split (one worktree each, branch `feat-pat-<name>`)

| agent | owns | delivers |
|---|---|---|
| rows | `engine/Cells.cs` (table, SourceFiles), `engine/Modules.cs` MachineRow presets, `tests/SelfTestRows34.cs` | P08/P09 cells as "game" cells with source self-check; 3/4-input preset = output L3 on side A behind the two inputs (the user's choice), pitch 4; selftest: `MachineRow` for Electric Motor x4 / Missile Set x4 (Mk.II) reproduces the drawn sorter records (coords, slots, lengths, offsets) and the Validator accepts the two files. |
| drawn | `engine/Validator.cs`, `engine/Coaters.cs` (validation side), `engine/EngineTuning.cs`, `tests/SelfTestDrawn.cs` | acceptance tests for all 4 files; fix every rejection that the drawing disproves (adjacent bridges, tail pitch 1, splitter corner at 2 tiles, coater feed ramp z 0/0.5/1 and dead end). Report which rules changed. |
| loader | `engine/BpImport.cs`, `engine/Patterns.cs`, `cli/MakeBlock.cs` (`--patterns`), `engine.host` glue, `tests/SelfTestPatterns.cs`, `Geo.Register` (small addition in Cells.cs) | import + recognition + catalog + CLI; selftest: Analyze(P08) = 3 inputs W L1 / W L2 / E L1, out W L3, pitch 4; Analyze(sheet) finds the comb (pitch 1), the 3x3 bundle bridge, the splitter corner distance 2, the 2x6 smelter double column with a shared output, the coater row with one z 1 feed. |
| double | `engine/DoubleRows.cs` (new), `Modules.DoubleRow` entry, one marked hook in `Composer.cs`, `tests/SelfTestDouble.cs` | two facing columns sharing the middle output lane (P10 smelters, P01/P02 coil block) as one Fragment with ports; the composer uses it for 1-2 input steps with >= 4 machines when it is not wider than two separate rows; coaters stay on the input lanes (Coaters.AddToRow). Selftest: fragment for 12 arc smelters iron ingot matches the sheet's column geometry (pitch 3, lane x offsets, sorter cells), validator ok, export ok. |

Rules for every agent: C# 5 (no `?.`, `$""`, `nameof`, expression bodies); English only in code/comments/output;
the core (`engine/`) must not use System.IO / Console / Environment / culture / GameBp (`build.cmd` fails otherwise);
never write into the user's `Documents\Dyson Sphere Program\Blueprint` folder; only touch the files you own, plus
one-line marked hooks (`// PATTERNS HOOK`) elsewhere when unavoidable; keep the full selftest green
(`DSPFLAT_DATA_ROOT=C:\x7\tools\play\dyson-sphere-program\blueprint-tools`, `MAKEBLOCK_CASES=<filter>` for a subset);
commit on your branch; report: what changed, test counts before/after, open questions.
