// DSPCalculatorFlat - adds a "Flat blueprint" button to every DSPCalculator window. The button turns the window's
// solution (recipes, buildings, machine counts, proliferator, tier limits) into a flat production block with the
// DspFlat engine on a background thread and enters the game's blueprint paste mode with the result.
// DSPCalculator is only referenced and called, never copied. C# 5 (built with the .NET Framework csc).
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DSPCalculator.UI;
using HarmonyLib;

namespace DSPCalculatorFlat
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(CalculatorGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ovitente.dsp.calculatorflat";
        public const string Name = "DSPCalculatorFlat";
        public const string Version = "0.1.0";
        public const string CalculatorGuid = "com.GniMaerd.DSPCalculator";

        internal static ManualLogSource Log;
        internal static ConfigEntry<int> TimeBudgetSeconds;

        void Awake()
        {
            Log = Logger;
            TimeBudgetSeconds = Config.Bind("Engine", "TimeBudgetSeconds", 60,
                "Time budget of one layout search in seconds (the search runs in the background).");
            new Harmony(Guid).PatchAll(typeof(Patches));
            Log.LogInfo(Name + " " + Version + " loaded" + (EngineAdapter.UsingStub ? " (STUB ENGINE: demo layouts only)" : ""));
        }

        void Update()
        {
            try { FlatUi.Tick(); }
            catch (Exception e) { Log.LogError(e); }
        }
    }

    internal static class Patches
    {
        // UICalcWindow(int idx) builds the whole window including the black-box button (genBpButtonObj).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UICalcWindow), MethodType.Constructor, new Type[] { typeof(int) })]
        static void AfterWindowCreated(UICalcWindow __instance)
        {
            FlatUi.Attach(__instance);
        }

        // Called every frame by WindowsManager.OnUpdate for each open window.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UICalcWindow), "OnUpdate")]
        static void AfterWindowUpdate(UICalcWindow __instance)
        {
            try { FlatUi.Sync(__instance); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        }
    }
}
