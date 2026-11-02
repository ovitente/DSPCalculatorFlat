# DSPCalculatorFlat

Addon for the Dyson Sphere Program mod [DSPCalculator](https://github.com/jinxOAO/DSPCalculator)
(jinxOAO). It adds a **Flat blueprint** button next to the calculator's black-box button: the
calculator's solved plan (recipes, buildings, machine counts, rates, proliferator per recipe) is
laid out by our own engine as a flat, ground-level blueprint. No vertical belt towers; short
bridges only.

Status: in development, personal use. See [PLAN.md](PLAN.md).

- `engine/` - the layout engine (shared with the `MakeBlock` command-line tool).
- `addon/` - the BepInEx plugin (Harmony patches into DSPCalculator).
- `docs/` - design notes and the pattern catalog.

DSPCalculator has no license, so none of its code is copied here; the addon only calls it at run
time through Harmony and reflection.

License: MIT.
