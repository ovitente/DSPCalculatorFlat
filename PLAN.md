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
   Check: selftest cases per feature.
5. **Pattern catalog** - list of layout situations for the user to draw in game, and a loader that
   turns a drawn blueprint into an engine pattern. Blocked on the user's drawings.
6. **Integration** - build all, selftest, install into the r2modman Default profile
   (`mods.yml` entry), in-game test by the user.

## Known limits at start

- The engine has geometry only for arc/plane smelters and assemblers Mk.I-III.
- 3- and 4-input rows are not game-proven yet.
- Search can take minutes on large chains (to be replaced by patterns).
