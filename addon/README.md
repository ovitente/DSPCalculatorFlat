# DSPCalculatorFlat

Addon for [DSPCalculator](https://github.com/jinxOAO/DSPCalculator) (Dyson Sphere Program, BepInEx 5).

Every calculator window gets a green **Flat blueprint** button right of the black-box blueprint button.
It takes the window's solution as it is (recipes, buildings, machine counts, proliferator mode and level,
belt / sorter limits of the window's blueprint settings, items you marked as raw), lays the chain out as a
flat block with the DspFlat engine and enters the game's blueprint paste mode with the result.

- One target per window. A window with several targets shows a message.
- The layout search runs in the background; progress is shown in the window title. Click the button again
  or close the window to cancel. Errors are shown in a message box, warnings go to the BepInEx log.
- Config `BepInEx\config\ovitente.dsp.calculatorflat.cfg`: `TimeBudgetSeconds` (default 60).

DSPCalculator is required and is only called, not copied.

## Build

`build.ps1` (Windows, .NET Framework `csc`, C# 5). References come from the game's `Managed` folder, the
r2modman profile's `BepInEx\core` and the installed `jinxOAO-DSPCalculator\DSPCalculator.dll`.
The engine is taken from `..\bin\DspFlat.Engine.dll`, else `..\engine\*.cs`, else `EngineStub.cs`
(demo layouts only; `-Stub` forces it). `-Install` copies the plugin into
`BepInEx\plugins\ovitente-DSPCalculatorFlat\` of the Default profile.
