# DSPCalculatorFlat

Addon for the Dyson Sphere Program mod [DSPCalculator](https://github.com/jinxOAO/DSPCalculator)
(jinxOAO). It adds a **Flat blueprint** button next to the calculator's black-box button: the
calculator's solved plan (recipes, buildings, machine counts, rates, proliferator per recipe) is
laid out by our own engine as a flat, ground-level blueprint. No vertical belt towers; short
bridges only.

Status: in development, personal use. See [PLAN.md](PLAN.md).

- `engine/` - the layout engine core (`DspFlat.Engine`, no game/file access; API in `docs/engine-api.md`).
- `engine.host/` - CLI-side game access: data files, prototype records and blueprint export through the game DLL.
- `cli/` - the `MakeBlock` command-line tool; `tests/` - self tests.
- `addon/` - the BepInEx plugin (Harmony patches into DSPCalculator).
- `docs/` - design notes and the pattern catalog.

DSPCalculator has no license, so none of its code is copied here; the addon only calls it at run
time through Harmony and reflection.

License: MIT.

## Build and test (Windows)

`build.cmd` builds `bin\DspFlat.Engine.dll`, `bin\MakeBlock.exe` and `bin\SelfTest.exe` (.NET Framework 4.x csc,
C# 5). The CLI and the tests read the game data extracted by the separate blueprint-tools folder and the user's template
blueprints; nothing of that is in this repo:

```
 bin\SelfTest.exe --data-root <blueprint-tools folder> [--bp-dir <Blueprint folder>] [--game-dll <Assembly-CSharp.dll>]
 bin\MakeBlock.exe --data-root <blueprint-tools folder> --item "Magnetic Coil" --rate 360 [--out file.txt]
```

`MAKEBLOCK_CASES=micro` runs the fast self tests only.
