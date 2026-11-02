// FileGameSource.cs - IGameSource for the CLI and the self tests: game numbers from blueprint-tools\analysis\*.json,
// prototype records from the user's template blueprints + blueprint-tools\refs through the game DLL (read only),
// game version from the installed game. Also EnvTuning: the MAKEBLOCK_* environment variables -> EngineTuning.
using System;
using System.IO;
using DspFlat.Engine;

namespace DspFlat.Host
{
public class FileGameSource : IGameSource
{
    public GameData Data { get; private set; }
    public PrototypeCells Cells { get { return GameCells != null ? GameCells.Prototypes : null; } }
    public string GameVersion { get; private set; }
    /// The game-object side of the prototypes (needed by GameEmitter to serialize).
    public Cells GameCells { get; private set; }

    public FileGameSource(GameData gd) { Data = gd; }

    /// dataRoot = blueprint-tools folder (analysis\game-data.json, item-ids.json, refs\); bpDir = the user's Blueprint
    /// folder (template blueprints, read only); gameDll = Assembly-CSharp.dll (null = default Steam path).
    /// withPrototypes = false skips the game DLL (plan-only runs).
    public static FileGameSource Load(string dataRoot, string bpDir, string gameDll, bool withPrototypes)
    {
        var s = new FileGameSource(LoadData(dataRoot));
        if (withPrototypes) s.LoadPrototypes(dataRoot, bpDir, gameDll);
        return s;
    }

    public static GameData LoadData(string dataRoot)
    {
        string gdPath = Path.Combine(dataRoot, @"analysis\game-data.json");
        if (!File.Exists(gdPath)) throw new ArgumentException("game data not found: " + gdPath + " (pass --data-root <blueprint-tools folder>)");
        return GameDataFiles.Load(gdPath, Path.Combine(dataRoot, "item-ids.json"));
    }

    /// Loads the game DLL (GameBp.Init) and the prototype cells; errors end up in Cells.Errors.
    public void LoadPrototypes(string dataRoot, string bpDir, string gameDll)
    {
        GameBp.Init(gameDll);
        DspFlat.Host.Cells.ToolsRoot = dataRoot;
        GameCells = DspFlat.Host.Cells.Load(bpDir, Data);
        GameVersion = GameBp.InstalledGameVersion();
    }
}

/// MAKEBLOCK_* environment variables (debug / tuning knobs of the old CLI) -> EngineTuning. CLI side only.
public static class EnvTuning
{
    public static void Apply()
    {
        EngineTuning.ComposeBudgetMs = I("MAKEBLOCK_BUDGET_MS", EngineTuning.ComposeBudgetMs);
        EngineTuning.RunAreaCost = I("MAKEBLOCK_RUN_AREA", EngineTuning.RunAreaCost);
        EngineTuning.RaisedAreaCost = I("MAKEBLOCK_RAISED_AREA", EngineTuning.RaisedAreaCost);
        EngineTuning.TurnAreaCost = I("MAKEBLOCK_TURN_AREA", EngineTuning.TurnAreaCost);
        EngineTuning.BeltAreaPct = I("MAKEBLOCK_BELT_AREA_PCT", EngineTuning.BeltAreaPct);
        EngineTuning.RouterTurnCost = I("MAKEBLOCK_TURN", EngineTuning.RouterTurnCost);
        EngineTuning.RouterRampCost = I("MAKEBLOCK_RAMP", EngineTuning.RouterRampCost);
        EngineTuning.RouterRaisedCost = I("MAKEBLOCK_RAISED", EngineTuning.RouterRaisedCost);
        int m = I("MAKEBLOCK_MODE", -1);
        EngineTuning.ForcedMode = m >= 0 && m <= 31 ? m : -1;
        EngineTuning.DebugNotes = Environment.GetEnvironmentVariable("MAKEBLOCK_DEBUG") != null;
    }

    static int I(string n, int def) { int v; string s = Environment.GetEnvironmentVariable(n); return s != null && int.TryParse(s, out v) ? v : def; }
}
}
