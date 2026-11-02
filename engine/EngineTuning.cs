// EngineTuning.cs - composer / router tuning knobs. They used to be read from MAKEBLOCK_* environment variables inside
// the composer; the engine core no longer reads the environment, so hosts set these fields (the CLI maps the same
// environment variables in DspFlat.Host.EnvTuning). Set them before Generate/Compose; they are process-wide.
namespace DspFlat.Engine
{
public static class EngineTuning
{
    /// Composer time budget per mode set, ms (MAKEBLOCK_BUDGET_MS). FlatEngine overrides it with LayoutOptions.TimeBudgetMs.
    public static int ComposeBudgetMs = 240000;
    /// Attempt selection cost in tiles of area: per raised run, raised node, corner belt, belt percent
    /// (MAKEBLOCK_RUN_AREA, MAKEBLOCK_RAISED_AREA, MAKEBLOCK_TURN_AREA, MAKEBLOCK_BELT_AREA_PCT).
    public static int RunAreaCost = 8, RaisedAreaCost = 1, TurnAreaCost = 15, BeltAreaPct = 50;
    /// Router costs in tenths of a tile (MAKEBLOCK_TURN, MAKEBLOCK_RAMP, MAKEBLOCK_RAISED).
    public static int RouterTurnCost = 40, RouterRampCost = 10, RouterRaisedCost = 2;
    /// Debug: only this composer row mode (0..31), -1 = all (MAKEBLOCK_MODE).
    public static int ForcedMode = -1;
    /// Debug: composer notes include every failed and valid attempt (MAKEBLOCK_DEBUG).
    public static bool DebugNotes;
}
}
