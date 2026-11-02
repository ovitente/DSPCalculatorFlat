// CalcReader.cs - reads one DSPCalculator window (UICalcWindow) into an engine-neutral CalcPlan.
// Everything is read by member name through Reflect (DSPCalculator has no license: nothing is copied, only read).
// Must run on the Unity main thread (some calculator properties query GameMain.history).
//
// Members used (DSPCalculator 0.5.25, see docs\dspcalculator-internals.md):
//   UICalcWindow.solution                       SolutionTree
//   SolutionTree.targets                        List<ItemTarget{itemId, speed (/min)}>
//   SolutionTree.recipeInfos                    Dictionary<int recipeId, RecipeInfo>
//   SolutionTree.itemNodes                      Dictionary<int itemId, ItemNode>; ItemNode.IsOre(UserPreference)
//   SolutionTree.userPreference                 UserPreference (bp* fields, labMaxLevel, bpStack, solveProliferators)
//   SolutionTree.beltsAvailable/sortersAvailable  List<BpBeltInfo/BpSorterInfo{itemId, speedPerMin}>, ascending,
//                                               already filtered by bpBeltTechLimit / bpSorterTechLimit
//   SolutionTree.RefreshBlueprintDicts()        rebuilds the two lists
//   RecipeInfo.ID, count, recipeNorm{oriProto, type}, assemblerItemId, assemblerCount, incLevel, isInc, useIA,
//   RecipeInfo.GetInputSpeedOriProto(i, count) / GetOutputSpeedOriProto(i, count)  (/min, gross, per oriProto slot)
//   RecipeInfo.GetProliferatorUsed(out int id, out double count)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace DSPCalculatorFlat
{
    /// One production step as the calculator solved it. Rates in items per minute.
    public class CalcStep
    {
        public int RecipeId;
        public int RecipeType;              // (int)ERecipeType of the recipe
        public string RecipeName;
        public int BuildingItemId;          // RecipeInfo.assemblerItemId
        public double MachineCount;         // RecipeInfo.assemblerCount (speed-up proliferator already applied)
        public int IncLevel;                // RecipeInfo.incLevel: proliferator ability (0 none, 1/2/4 Mk.I/II/III)
        public bool IsInc;                  // RecipeInfo.isInc: extra products (true) vs speed-up (false)
        public bool UseIA;                  // MoreMegaStructure Interstellar Assembly (not buildable here)
        public int ProliferatorItemId;      // from GetProliferatorUsed, 0 when none
        public Dictionary<int, double> InputsPerMin = new Dictionary<int, double>();
        public Dictionary<int, double> OutputsPerMin = new Dictionary<int, double>();
    }

    /// Everything the addon takes from one calculator window. Engine-neutral (EngineAdapter maps it to PlanInput).
    public class CalcPlan
    {
        public int TargetItemId;
        public double TargetPerMin;
        public List<CalcStep> Steps = new List<CalcStep>();
        public List<int> ExternalItems = new List<int>();   // raw / "treat as ore" / not produced by any step
        public int BeltCapItemId;                           // fastest belt allowed by the window (beltsAvailable.Last)
        public int SorterCapItemId;                         // fastest sorter allowed by the window (sortersAvailable.Last)
        public bool BeltHighest, SorterHighest;             // window flags "always use the highest tier" (informational)
        public int CargoStack;                              // UserPreference.bpStack (belt stacking 1..4)
        public int LabMaxLevel;                             // UserPreference.labMaxLevel (vertical construction tech)
        public bool SolveProliferators;                     // proliferator produced inside the line
        public int ProliferatorItemId;                      // most used proliferator of the plan, 0 = none
        public List<string> Notes = new List<string>();
    }

    internal static class CalcReader
    {
        const double Eps = 1e-4;

        /// Returns null and sets error when the window cannot be turned into a single-target plan.
        public static CalcPlan Read(object window, out string error)
        {
            error = null;
            object solution = Reflect.Get(window, "solution");
            object pref = Reflect.Get(solution, "userPreference");

            // ---- target(s)
            var targets = Reflect.Get(solution, "targets") as IList;
            var plan = new CalcPlan();
            int targetCount = 0;
            if (targets != null)
                foreach (object t in targets)
                {
                    int id = Reflect.GetInt(t, "itemId");
                    double speed = Reflect.GetDouble(t, "speed");
                    if (id <= 0 || speed <= 0) continue;
                    targetCount++;
                    if (targetCount == 1) { plan.TargetItemId = id; plan.TargetPerMin = speed; }
                }
            if (targetCount == 0) { error = "Pick a target item and a rate in the calculator first."; return null; }
            if (targetCount > 1)
            {
                error = "This window has " + targetCount + " target items. The flat blueprint supports one target per window: "
                      + "remove the extra targets or use a separate calculator window for each.";
                return null;
            }

            // ---- steps
            var recipeInfos = Reflect.Get(solution, "recipeInfos") as IDictionary;
            if (recipeInfos == null || recipeInfos.Count == 0) { error = "The calculator has no solution for this target."; return null; }

            foreach (DictionaryEntry e in recipeInfos)
            {
                object ri = e.Value;
                double count = Reflect.GetDouble(ri, "count");
                if (count <= Eps) continue;                      // recipe kept in the dictionary but not used
                var step = new CalcStep();
                step.RecipeId = Reflect.GetInt(ri, "ID");
                object norm = Reflect.Get(ri, "recipeNorm");
                step.RecipeType = Reflect.GetInt(norm, "type");
                var proto = Reflect.Get(norm, "oriProto") as RecipeProto;
                if (proto == null) { error = "Recipe " + step.RecipeId + " has no game prototype."; return null; }
                step.RecipeName = proto.name;
                step.BuildingItemId = Reflect.GetInt(ri, "assemblerItemId");
                step.MachineCount = Reflect.GetDouble(ri, "assemblerCount");
                step.IncLevel = Reflect.GetInt(ri, "incLevel");
                step.IsInc = Reflect.GetBool(ri, "isInc");
                object useIA = Reflect.TryGet(ri, "useIA");
                step.UseIA = useIA != null && Convert.ToBoolean(useIA);

                for (int i = 0; i < proto.Items.Length; i++)
                {
                    double v = Convert.ToDouble(Reflect.Call(ri, "GetInputSpeedOriProto", i, count));
                    if (v > Eps) Add(step.InputsPerMin, proto.Items[i], v);
                }
                for (int i = 0; i < proto.Results.Length; i++)
                {
                    double v = Convert.ToDouble(Reflect.Call(ri, "GetOutputSpeedOriProto", i, count));
                    if (v > Eps) Add(step.OutputsPerMin, proto.Results[i], v);
                }

                if (step.IncLevel > 0 && Reflect.HasMethod(ri, "GetProliferatorUsed", 2))
                {
                    var args = new object[] { 0, 0.0 };
                    Reflect.Call(ri, "GetProliferatorUsed", args);
                    step.ProliferatorItemId = Convert.ToInt32(args[0]);
                }
                if (step.UseIA)
                    plan.Notes.Add("Recipe " + step.RecipeName + " uses the Interstellar Assembly (MoreMegaStructure); it cannot be laid out flat.");
                plan.Steps.Add(step);
            }
            if (plan.Steps.Count == 0) { error = "The calculator solution has no active recipes."; return null; }

            // ---- external items: calculator ores (incl. user "treat as raw") + anything no step produces
            var produced = new HashSet<int>();
            var consumed = new HashSet<int>();
            foreach (var s in plan.Steps)
            {
                foreach (int id in s.OutputsPerMin.Keys) produced.Add(id);
                foreach (int id in s.InputsPerMin.Keys) consumed.Add(id);
            }
            var external = new HashSet<int>();
            var itemNodes = Reflect.TryGet(solution, "itemNodes") as IDictionary;
            if (itemNodes != null)
                foreach (DictionaryEntry e in itemNodes)
                {
                    int id = Convert.ToInt32(e.Key);
                    if (!consumed.Contains(id)) continue;
                    object isOre = Reflect.Call(e.Value, "IsOre", pref);
                    if (Convert.ToBoolean(isOre)) external.Add(id);
                }
            foreach (int id in consumed)
                if (!produced.Contains(id)) external.Add(id);

            // ---- proliferator
            plan.SolveProliferators = Reflect.GetBool(pref, "solveProliferators");
            var prolifUse = new Dictionary<int, double>();
            foreach (var s in plan.Steps)
                if (s.ProliferatorItemId > 0) Add(prolifUse, s.ProliferatorItemId, s.MachineCount);
            double best = 0;
            foreach (var kv in prolifUse)
                if (kv.Value > best) { best = kv.Value; plan.ProliferatorItemId = kv.Key; }
            if (prolifUse.Count > 1)
                plan.Notes.Add("The plan uses " + prolifUse.Count + " proliferator kinds; only one proliferator lane is planned.");
            if (plan.ProliferatorItemId > 0 && !produced.Contains(plan.ProliferatorItemId))
                external.Add(plan.ProliferatorItemId);

            plan.ExternalItems.AddRange(external);
            plan.ExternalItems.Sort();

            // ---- blueprint tier settings of this window
            plan.BeltHighest = Reflect.GetBool(pref, "bpBeltHighest");
            plan.SorterHighest = Reflect.GetBool(pref, "bpSorterHighest");
            plan.CargoStack = Reflect.GetInt(pref, "bpStack");
            plan.LabMaxLevel = Reflect.GetInt(pref, "labMaxLevel");

            var belts = Reflect.TryGet(solution, "beltsAvailable") as IList;
            var sorters = Reflect.TryGet(solution, "sortersAvailable") as IList;
            if ((belts == null || belts.Count == 0 || sorters == null || sorters.Count == 0)
                && Reflect.HasMethod(solution, "RefreshBlueprintDicts", 0))
            {
                Reflect.Call(solution, "RefreshBlueprintDicts");
                belts = Reflect.TryGet(solution, "beltsAvailable") as IList;
                sorters = Reflect.TryGet(solution, "sortersAvailable") as IList;
            }
            plan.BeltCapItemId = Fastest(belts);
            plan.SorterCapItemId = Fastest(sorters);
            return plan;
        }

        static int Fastest(IList ascending)
        {
            if (ascending == null || ascending.Count == 0) return 0;
            int best = 0; double bestSpeed = -1;
            foreach (object o in ascending)
            {
                double sp = Reflect.GetDouble(o, "speedPerMin");
                if (sp > bestSpeed) { bestSpeed = sp; best = Reflect.GetInt(o, "itemId"); }
            }
            return best;
        }

        static void Add(Dictionary<int, double> d, int key, double v)
        {
            double old;
            d[key] = d.TryGetValue(key, out old) ? old + v : v;
        }

        public static string Describe(CalcPlan p)
        {
            var sb = new StringBuilder();
            sb.Append("target ").Append(p.TargetItemId).Append(" @ ").Append(p.TargetPerMin.ToString("0.###")).Append("/min, ");
            sb.Append(p.Steps.Count).Append(" steps, external [").Append(string.Join(",", p.ExternalItems.ConvertAll(i => i.ToString()).ToArray()));
            sb.Append("], belt cap ").Append(p.BeltCapItemId).Append(", sorter cap ").Append(p.SorterCapItemId);
            sb.Append(", lab max ").Append(p.LabMaxLevel).Append(", stack ").Append(p.CargoStack);
            sb.Append(", proliferator ").Append(p.ProliferatorItemId);
            foreach (var s in p.Steps)
                sb.Append("\n  recipe ").Append(s.RecipeId).Append(" (").Append(s.RecipeName).Append(") bld ").Append(s.BuildingItemId)
                  .Append(" x").Append(s.MachineCount.ToString("0.###")).Append(" inc ").Append(s.IncLevel).Append(s.IsInc ? " extra" : " speed");
            return sb.ToString();
        }
    }
}
