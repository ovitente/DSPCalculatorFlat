# Plan

Agreed with the user on 2026-11-02 (grilling session). Each phase ends with a check.

## Decisions

1. Addon to DSPCalculator: an extra **Flat blueprint** button; the original black-box generator stays.
2. Numbers come from the calculator's solution as they are: recipe, building (`assemblerItemId`),
   machine count (`assemblerCount`, already includes proliferator), rates, proliferator level and
   mode per recipe (`incLevel`, `isInc`). The engine does not re-solve.
3. One blueprint per target (one target per calculator window in v1; several targets -> message).
4. The whole chain is built down to raw materials and items the calculator marks as external,
   including the Miniature Particle Collider and Matrix Labs.
5. Oil and chemistry products (refinery, chemical plant, fractionator recipes) are not built:
   they arrive as external input lanes. Recipes with byproducts are treated the same way.
6. Proliferator: a spray coater is placed on each input lane of every recipe where the calculator
   enables proliferator (in practice the final step). The proliferator itself arrives on an external
   lane.
7. All external input lanes end on one edge, each with a one-belt tail carrying the item icon.
8. Belt and sorter tiers: the calculator window's limits are the cap; inside the cap the cheapest
   tier that carries the flow is used.
9. Lane split: machines per lane = floor(lane rate / one machine's demand for that input), also
   limited by sorter rate per machine. Flow above Mk.II (720/min): Mk.III if allowed, else
   parallel Mk.II lanes.
10. Labs are stacked in towers; tower height is capped by the base sorter's rate (calculator's
    sorter limit) and by the vertical construction tech; more, lower towers when a sorter is short.
11. Flat layout only: short bridges and straight raised runs (game-proven) are allowed, vertical
    belt towers never. Routing failure -> clear error, never a silent fallback.
12. Search runs off the main thread, budget 60 s, progress shown in the calculator window.
13. One engine library shared by the `MakeBlock` CLI and the addon; game access behind an
    interface. Layout is assembled from patterns drawn by the user (pattern catalog) instead of
    long blind search.
14. Repository `ovitente/DSPCalculatorFlat`, public, MIT, personal use first (Thunderstore later).

## Phases

1. **Repo** - skeleton, plan, license, GitHub remote.
2. **Engine move + decoupling** - copy `blueprint-tools/lib` into `engine/`; split game access
   behind `IGameSource` (file/reflection implementation for the CLI, `LDB` implementation for the
   game); no `File`/`Console`/env-var/culture side effects in the core; a public API that takes an
   injected plan (recipe, building, machine count, rates, proliferator) instead of item + rate.
   Check: CLI builds, selftest passes as before.
3. **Addon skeleton** - BepInEx plugin, Harmony patch on DSPCalculator's `UICalcWindow`: add the
   button, read the window's solution (`RecipeInfo` list), map it to the engine plan, run the engine
   on a worker thread (60 s budget), paste the result with the game's paste mode. Check: builds
   against the installed DSPCalculator 0.5.25.
4. **Engine features** (can run in parallel on separate files where possible):
   - external lanes for unsupported recipes and byproducts; all inputs on one edge with icon tails;
   - lane split formula, Mk.III, parallel lanes; tier choice under the calculator's caps;
   - spray coaters + proliferator lane;
   - Matrix Lab towers; Miniature Particle Collider geometry.
   - **15 layout rules** approved by the user on 2026-11-02 (handed over from the blueprint session):
     source `blueprint-tools/analysis/user-edit-turret75.md` (Russian list at the top, evidence below),
     game-proven references `blueprint-tools/refs/engine-missile-turret-7.5-v1-useredit*.txt`
     (useredit3 is the latest, works in game). Summary: tight machine columns (pitch 5-6); long lanes
     to the final machine in one edge bus stacked z 0/0.5/1; later-joining lane runs higher; a machine
     feeding only the final machine hands over machine-to-machine (2 x L3 sorters); its column sits next
     to the final machine; final machine fed from 4 sides, L1 inputs; buffer line final -> L2 -> Depot
     Mk.I -> L1 -> 3-tile tail; 2-way splitters only, third consumer takes from the lane tail; machine
     output faces the bus; splitter outputs run as a horizontal band crossed by single-tile bridges;
     last machine on two lanes fed from both dead ends on one side; no separate ore+smelter for one
     small machine when the main line has margin; tesla towers in gaps, minimum by coverage; margin
     sized by the bottleneck, never borrowed from another chain; a second machine of the same product
     side-merges into the first one's output lane before the splitter.
   Check: selftest cases per feature.
5. **Pattern catalog** - list of layout situations for the user to draw in game, and a loader that
   turns a drawn blueprint into an engine pattern. Blocked on the user's drawings.
6. **Integration** - build all, selftest, install into the r2modman Default profile
   (`mods.yml` entry), in-game test by the user.

## Known limits at start

- The engine has geometry only for arc/plane smelters and assemblers Mk.I-III.
- 3- and 4-input rows are not game-proven yet.
- Search can take minutes on large chains (to be replaced by patterns).

## After v1

- Full missile turret 22.5 kit (bus/floorplan style) through the shared engine, each module
  self-reviewed with `blueprint-tools/tools-preview/RenderBp.exe` before it goes to the user.

## Status (2026-11-02)

- Phases 1-3 done; phase 4: lanes / Mk.III / lane split, spray coaters, lab towers, collider done (code-checked,
  not yet pasted in game); phase 5 catalog written, waiting for the user's drawings.
- 15 rules: generated and checked on the turret 7.5 - 4, 6, 7, 9, 11, 12, 13; partial - 1 (pitch 7), 5, 8 (2-way
  splitters on small fixtures only); coded but off because the search router then finds no valid turret - 2/3
  (`EdgeBus.Enabled`), 14 (`Rules.MarginInCompose`); not started - 10, 15. Rule checker: `Rules.Check`.
- The search router still produces long perimeter loops (`docs/turret-7.5-engine-2026-11-02.png`, 48x30 vs the
  user's 40x26). Remaining rules are expected to come from the pattern catalog rather than more search.
- Selftest full: 83 pass / 0 fail / 1 skip (missing user file).
