// SelfTestMain.cs - entry point of bin\SelfTest.exe (the same cases as MakeBlock.exe --selftest).
// Usage: SelfTest.exe --data-root <blueprint-tools folder> [--bp-dir <Blueprint folder>] [--game-dll <Assembly-CSharp.dll>]
// MAKEBLOCK_CASES=micro (comma-separated substrings) runs only the matching cases.
using System;
using System.Globalization;
using DspFlat.Host;

static class SelfTestMain
{
    static int Main(string[] args)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var opt = MakeBlock.ParseArgs(args);
            string root = MakeBlock.DataRoot(opt);
            Cells.ToolsRoot = root;
            EnvTuning.Apply();
            GameBp.Init(MakeBlock.Opt(opt, "game-dll"));
            return SelfTest.Run(root, MakeBlock.Opt(opt, "bp-dir") ?? MakeBlock.DefaultBpDir);
        }
        catch (ArgumentException e) { Console.WriteLine("error: " + e.Message); return 2; }
    }
}
