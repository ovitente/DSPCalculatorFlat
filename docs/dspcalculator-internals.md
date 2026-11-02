# DSPCalculator internals used by the addon

Source read: <https://github.com/jinxOAO/DSPCalculator> master, commit `5817662` (= v0.5.25 plus a cosmetic
commit). Checked against the installed `jinxOAO-DSPCalculator\DSPCalculator.dll` 0.5.25 by reflection: every
member below exists with the listed name. DSPCalculator has no license: the addon only references and calls it.

Plugin: `DSPCalculator.DSPCalculatorPlugin`, GUID `com.GniMaerd.DSPCalculator` (const), version 0.5.25.

## Windows

- `DSPCalculator.UI.WindowsManager` (static): `windows` (open `List<UICalcWindow>`), `closedWindows` (kept for
  reuse, up to `MaxWindowsReservedAfterClose`), `temporaryCloseBecausePasteBp` (bool, see paste below).
  `OnUpdate()` calls `UICalcWindow.OnUpdate()` for every open window each frame.
- `DSPCalculator.UI.UICalcWindow`: one per window; the constructor `UICalcWindow(int idx)` builds the whole UI
  (clone of the game's Blueprint Browser window). A closed window is hidden and later reopened with `OpenWindow()`;
  the constructor runs once per window object.
  - `windowObj` (GameObject, name `calc-window <idx>`), `titleText` (Text at `panel-bg/title-text`, reset to the
    translated title only in the constructor and `OpenWindow()`), `solution` (`SolutionTree`), `isLargeWindow`.
  - `CloseWindow()`: hides the window and moves it to `closedWindows`.

## Black-box blueprint button (how the button was found)

`UI/UICalcWindow.cs` lines 722-744 (constructor): `genBpButtonObj = Instantiate(iconObj_ButtonTip, panel-bg)`,
name `gen-bp`, anchors (0,1), `anchoredPosition3D (245, -80)` ((240, -80) with GenesisBook), size 25x25,
sprite `UICalcWindow.blueprintIconSprite`, `Button.onClick -> GenerateBlackboxBpAndPaste()`, `UIButton` tips.
- Public fields `genBpButtonObj` / `genBpUIBtn`.
- `RefreshBpGenButton()` (line 2880) shows it only when `BpConnector.enabled` and `solution.targets[0].itemId > 0`.
- The size animation in `OnUpdate` (lines 1600, 1626) moves it vertically between y -80 (large) and -60 (small).
- Neighbours on the same row: target icon x 52, speed input x 90, "/min" x 210; the assembler buttons start at about
  x 312 (`assemblerSelectionObj`, centre-anchored at -358). x 245 + 28 = 273 is free in both window sizes.

Addon: Harmony postfix on the constructor `UICalcWindow(int)` clones `genBpButtonObj` (fallback: find
`panel-bg/gen-bp` under `windowObj`), offsets it by +28 px, replaces the click listener, re-tints it and sets its tip.
Runtime `Button.onClick` listeners and `UIButton` delegate fields are not copied by `Instantiate`. A postfix on
`UICalcWindow.OnUpdate` copies the original's `activeSelf` and y position every frame.

## Solution data (`DSPCalculator.Logic`)

- `SolutionTree`: `targets` (`List<ItemTarget>`), `recipeInfos` (`Dictionary<int recipeId, RecipeInfo>`),
  `itemNodes` (`Dictionary<int itemId, ItemNode>`), `userPreference`, `beltsAvailable` / `sortersAvailable`
  (`List<BpBeltInfo>` / `List<BpSorterInfo>`), `RefreshBlueprintDicts()` (rebuilds the two lists; called by `Solve()`).
- `ItemTarget { int itemId; double speed; }`: speed in items per **minute** (the window shows "/min"). Several
  targets per window are possible (`SetTargetAndSolve(index, ...)`); the black-box button only looks at `targets[0]`.
- `RecipeInfo` (one per recipe id in a solution):
  - `ID`, `count` (double, "machines at speed 1.0" in calculator units; a recipe with `count <= 1e-4` is unused),
  - `recipeNorm` (`NormalizedRecipe`: `oriProto` = game `RecipeProto`, `type` = `(int)ERecipeType`, net counts),
  - properties `assemblerItemId` (building item id: per-recipe override, else the global per-type choice, else the
    first building of the type), `assemblerCount` (= `count / speed / 60`, divided by `1 + acc` in speed-up mode),
    `incLevel` (proliferator **ability**: 0 none, 1 / 2 / 4 = Mk.I / II / III, per recipe or global), `isInc`
    (extra-products vs speed-up), `useIA` (MoreMegaStructure Interstellar Assembly),
  - `GetInputSpeedOriProto(int index, double count)` / `GetOutputSpeedOriProto(int index, double count)`: gross rate
    per original recipe slot in items per minute (what the calculator's own blueprint code uses for belt/sorter sizing),
  - `GetProliferatorUsed(out int proliferatorId, out double count)` (id via `CalcDB.proliferatorAbilityToId[incLevel]`).
- Raw / external items: `ItemNode.IsOre(UserPreference)` = item is `defaultAsOre` or has no recipe, or the user set
  `UserPreference.itemConfigs[id].consideredAsOre` ("treat as raw"); `forceNotOre` overrides it when a recipe exists.
  Same logic as `UserPreference.IsOre(int)`. The addon also treats every input that no active recipe produces as
  external, plus the proliferator when it is not produced in the line (`UserPreference.solveProliferators` false).

## Blueprint settings (`UserPreference`, per window)

- `bpBeltHighest`, `bpSorterHighest` (always use the fastest tier), `bpBeltTechLimit`, `bpSorterTechLimit` (only
  unlocked tiers), `bpStackSetting` (0 = by tech) with property `bpStack` (cargo stacking 1..4 from techs 3801-3803),
  `bpSorterMk4OutputStack`, `labMaxLevel` (lab tower height 3..15 from techs 3701-3706), `bpRowCount`,
  `bpResourceCoater`, `bpProductCoater`, `bpStationProlifSlot`, `bpConnectBlackboxCoater`.
- `SolutionTree.RefreshBlueprintDicts()` fills `beltsAvailable` / `sortersAvailable` from `BpDB.beltsAscending` /
  `sortersAscending`, keeping unlocked tiers (or all when the tech limit is off; the slowest tier always). Both lists are
  ascending by `speedPerMin` (`BpBeltInfo.speedPerMin = beltSpeed * beltSpeedToPerSecFactor * 60`, one stack layer;
  `BpSorterInfo.speedPerMin = 300000 / inserterSTT * 60`, pile sorter grade >= 4 = 240 * 3600).
  The addon uses the fastest entry of each list as the cap.

## Paste (how DSPCalculator pastes its own blueprints)

`UICalcWindow.GenerateBlackboxBpAndPaste()` (line 2867) and `UIItemNode` line 1279:

```
CloseWindow();
WindowsManager.temporaryCloseBecausePasteBp = true;
GameMain.mainPlayer.controller.OpenBlueprintPasteMode(connector.blueprintData, GameConfig.blueprintFolder + "DSPCalcBPTemp.txt");
```

`WindowsManager.OnQuitBpPasteMode` is a Harmony postfix on `UIBlueprintInspector._OnClose`: when the flag is set it
reopens the last closed window (`OpenOne()`) and clears the flag. Game signature (Assembly-CSharp):
`PlayerController.OpenBlueprintPasteMode(BlueprintData blueprint, string fullPath, bool resetBlueprintCamera = true)`.
The addon does the same with its own file name `DSPCalcFlatTemp.txt`.

DSPCalculator builds `BlueprintData` in `BpBuilder.CreateEmpty()`: `new BlueprintData()`, `ResetAsEmpty()`, one icon
layout, `Patch = 1`, one `BlueprintArea { index 0, parentIndex -1, areaSegments 200 }`, then `buildings`, `dragBoxSize`
and area width/height. `BlueprintData.Patch` is not public in the stock Assembly-CSharp (DSPCalculator compiles against
a stripped/publicized copy); the addon sets it by reflection.

## Messages

DSPCalculator uses `UIMessageBox.Show(title, text, button, 1, new UIMessageBox.Response(() => { }))` for failures
and `UIRealtimeTip.Popup(text)` for short notes; the addon uses the same two game APIs with English strings.

## Mapping to the engine's LayoutOptions (addon `EngineAdapter.ToPlanInput`)

- `BeltItemMax` / `SorterItemMax` = fastest entry of `beltsAvailable` / `sortersAvailable` (caps; pile sorter 2014 is
  clamped to Mk.III by the engine). `CheapestTiers` = true (cheapest tier inside the caps per belt run / sorter).
- `LabStackMax` = `UserPreference.labMaxLevel` (left at the engine default 15 when the field reads 0).
- `ProliferatorItemId` = the proliferator of the proliferated steps: `RecipeInfo.GetProliferatorUsed` (id from
  `CalcDB.proliferatorAbilityToId[incLevel]`), fallback = the item among 1141-1143 whose `ItemProto.Ability` equals
  `incLevel` (LDB). The engine plans one proliferator lane, so with mixed levels the strongest proliferator used is
  passed (a weaker spray would under-supply steps planned with a stronger one) and a note is added. 0 when no step is
  proliferated.
- Prototype records (`EngineAdapter.CliPrototypes`): the CLI host's set from the user's blueprints
  (`engine.host\GameCells.cs`), every record value embedded with its source record; only the model index comes from LDB
  (compared with the CLI value on load, logged on a difference). Items the CLI has no record for get none.
