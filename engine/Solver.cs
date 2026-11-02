// Solver.cs - recipe solver: target item + rate/min + tiers -> ProductionPlan (no game DLL).
// One Step per produced item (intermediates shared by several consumers are merged: DAG, not tree).
// Machine counts are ceil(exact); inputs are propagated at the EXACT (unrounded) rate.
// Steps are topologically ordered: raw-consuming steps first, the target step last.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DspFlat.Engine
{

public class Solver
{
    readonly GameData gd;
    readonly BlockSpec spec;

    const double Eps = 1e-9;

    public Solver(GameData gd, BlockSpec spec) { this.gd = gd; this.spec = spec; }

    /// Recipe tree for spec.Item at spec.Rate -> topologically ordered plan (raw-consuming steps first).
    public ProductionPlan Solve()
    {
        if (spec == null) throw new ArgumentException("solver: no block spec");
        if (spec.Rate <= 0) throw new ArgumentException("solver: rate must be > 0");
        if (gd.Item(spec.Item) == null) throw new ArgumentException("solver: unknown item " + spec.Item);
        if (IsRaw(spec.Item)) throw new ArgumentException("solver: target " + gd.ItemName(spec.Item) + " is raw (resource, --raw, or no recipe)");

        var plan = SolveAt(spec.Rate, true);
        if (spec.CleanRatio) plan.SuggestedRate = FindCleanRate(spec.Rate);
        return plan;
    }

    /// Machines needed for ratePerMin of item with recipe r at the given speed multiplier (unrounded).
    public static double MachinesFor(RecipeDef r, int item, double ratePerMin, double speed)
    {
        return ratePerMin * r.TimeSec / (r.OutCount(item) * 60.0 * speed);
    }

    /// Total rounding waste sum(Machines - MachinesExact) of a plan.
    public static double Waste(ProductionPlan plan)
    {
        double w = 0;
        foreach (var s in plan.Steps) w += s.Machines - s.MachinesExact;
        return w;
    }

    /// Human-readable plan for --plan-only: steps with names, machine counts, per-item flows, raw inputs, warnings.
    public static string PlanText(ProductionPlan plan, GameData gd)
    {
        var c = CultureInfo.InvariantCulture;
        Func<int, string> n = id => gd != null ? gd.ItemName(id) : "item" + id;
        Func<double, string> f = v => v.ToString("0.##", c);
        var sb = new StringBuilder();
        sb.AppendLine("target: " + n(plan.TargetItem) + " " + f(plan.TargetRate) + "/min"
            + (plan.SuggestedRate > 0 ? "   (clean-ratio suggestion: " + f(plan.SuggestedRate) + "/min)" : ""));
        sb.AppendLine("raw inputs:");
        foreach (var r in plan.RawInputs) sb.AppendLine("  " + n(r.Item) + " (" + r.Item + ") " + f(r.Rate) + "/min");
        sb.AppendLine("steps (raw side first):");
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            sb.AppendLine(string.Format(c, "  [{0}] {1} x{2} ({3:0.###}) {4} recipe {5} \"{6}\" -> {7} {8}/min (max {9}/min)",
                i, n(s.MachineItem), s.Machines, s.MachinesExact, s.MachineItem, s.Recipe.Id, s.Recipe.Name,
                n(s.Item), f(s.Outputs[0].Rate), f(s.MaxOutputRate)));
            foreach (var x in s.Inputs) sb.AppendLine("        in  " + n(x.Item) + " " + f(x.Rate) + "/min" + (s.External.Contains(x.Item) ? " (block input, --raw " + n(x.Item) + "@" + n(s.Item) + ")" : ""));
            for (int k = 1; k < s.Outputs.Count; k++) sb.AppendLine("        byproduct " + n(s.Outputs[k].Item) + " " + f(s.Outputs[k].Rate) + "/min");
            sb.AppendLine("        to  " + (s.ConsumerSteps.Count == 0 ? "block output"
                : string.Join(", ", s.ConsumerSteps.Select(k => "[" + k + "] " + n(plan.Steps[k].Item)).ToArray())));
        }
        int total = plan.Steps.Sum(s => s.Machines);
        sb.AppendLine(string.Format(c, "machines total {0}, rounding waste {1:0.###}", total, Waste(plan)));
        foreach (var w in plan.Warnings) sb.AppendLine("WARN " + w);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ core

    bool IsRaw(int item)
    {
        ItemDef d = gd.Item(item);
        if (d != null && d.IsResource) return true;
        if (spec.TreatAsRaw.Contains(item)) return true;
        return RecipeFor(item) == null;
    }

    RecipeDef RecipeFor(int item)
    {
        int rid;
        if (spec.RecipeOverrides.TryGetValue(item, out rid))
        {
            RecipeDef r = gd.Recipe(rid);
            if (r == null || r.OutCount(item) == 0) throw new ArgumentException("recipe override " + rid + " does not make " + gd.ItemName(item));
            return r;
        }
        return gd.DefaultRecipe(item);
    }

    ProductionPlan SolveAt(double rate, bool withWarnings)
    {
        var plan = new ProductionPlan { TargetItem = spec.Item, TargetRate = rate };
        var warn = new List<string>();

        // 1. topological order of produced items (DFS post-order = inputs before consumers); cycles are cut as raw.
        var order = new List<int>();
        var state = new Dictionary<int, int>();   // 1 visiting, 2 done
        var cutRaw = new HashSet<int>();
        Visit(spec.Item, order, state, cutRaw, warn);

        // 2. demand propagation in reverse topological order (each item's total demand is final before it expands).
        var demand = new Dictionary<int, double>();
        var raw = new Dictionary<int, double>();
        var rawOrder = new List<int>();
        demand[spec.Item] = rate;
        var stepOf = new Dictionary<int, Step>();
        for (int i = order.Count - 1; i >= 0; i--)
        {
            int item = order[i];
            double need;
            if (!demand.TryGetValue(item, out need) || need <= Eps) continue;
            RecipeDef r = RecipeFor(item);
            int machine = PickMachine(r, warn);
            double speed = gd.Speed(machine);
            var s = new Step { Item = item, Recipe = r, MachineItem = machine, Speed = speed };
            s.MachinesExact = MachinesFor(r, item, need, speed);
            s.Machines = (int)Math.Ceiling(s.MachinesExact - Eps);
            if (s.Machines < 1) s.Machines = 1;
            double crafts = need / r.OutCount(item);   // crafts per minute
            s.Outputs.Add(new ItemRate(item, need));
            foreach (var o in r.Outputs)
                if (o.Item != item)
                {
                    s.Outputs.Add(new ItemRate(o.Item, crafts * o.Count));
                    warn.Add("byproduct: " + gd.ItemName(o.Item) + " " + F(crafts * o.Count) + "/min from " + r.Name + " is not consumed in the block (needs a sink)");
                }
            s.MaxOutputRate = s.Machines * r.OutCount(item) * 60.0 * speed / r.TimeSec;
            HashSet<int> extFor;
            spec.RawFor.TryGetValue(item, out extFor);
            foreach (var x in r.Inputs)
            {
                double q = crafts * x.Count;
                s.Inputs.Add(new ItemRate(x.Item, q));
                bool ext = extFor != null && extFor.Contains(x.Item);
                if (ext) s.External.Add(x.Item);
                if (IsRaw(x.Item) || cutRaw.Contains(x.Item) || ext)
                {
                    if (!raw.ContainsKey(x.Item)) { raw[x.Item] = 0; rawOrder.Add(x.Item); }
                    raw[x.Item] += q;
                }
                else
                {
                    double d; demand.TryGetValue(x.Item, out d); demand[x.Item] = d + q;
                }
            }
            stepOf[item] = s;
        }

        // 3. steps in topological order (raw-consuming first, target last), consumer links.
        foreach (int item in order) { Step s; if (stepOf.TryGetValue(item, out s)) plan.Steps.Add(s); }
        for (int i = 0; i < plan.Steps.Count; i++)
            for (int j = 0; j < plan.Steps.Count; j++)
                if (plan.Steps[j].Inputs.Any(x => x.Item == plan.Steps[i].Item) && !plan.Steps[j].External.Contains(plan.Steps[i].Item)) plan.Steps[i].ConsumerSteps.Add(j);

        // raw inputs: biggest first (ties by id) - stable order for the composer's lanes
        foreach (int item in rawOrder.OrderByDescending(k => raw[k]).ThenBy(k => k)) plan.RawInputs.Add(new ItemRate(item, raw[item]));

        if (withWarnings) AddRateWarnings(plan, warn);
        plan.Warnings.AddRange(warn.Distinct());
        return plan;
    }

    void Visit(int item, List<int> order, Dictionary<int, int> state, HashSet<int> cutRaw, List<string> warn)
    {
        int st;
        if (state.TryGetValue(item, out st))
        {
            if (st == 1) { cutRaw.Add(item); warn.Add("recipe cycle at " + gd.ItemName(item) + ": supplied as raw input"); }
            return;
        }
        if (IsRaw(item)) return;
        state[item] = 1;
        RecipeDef r = RecipeFor(item);
        foreach (var x in r.Inputs) Visit(x.Item, order, state, cutRaw, warn);
        state[item] = 2;
        order.Add(item);
    }

    int PickMachine(RecipeDef r, List<string> warn)
    {
        int m = spec.Tiers.MachineFor(r.Type);
        if (m != 0 && r.Buildings.Contains(m)) return m;
        if (r.Buildings.Length == 0) throw new ArgumentException("recipe " + r.Id + " " + r.Name + " has no building");
        int alt = r.Buildings[0];
        warn.Add("recipe " + r.Id + " " + r.Name + " (" + r.Type + "): tier machine " + m + " not allowed, using " + alt + " " + gd.ItemName(alt));
        return alt;
    }

    void AddRateWarnings(ProductionPlan plan, List<string> warn)
    {
        double belt = gd.BeltRate(spec.Tiers.Belt);
        double sorter1 = gd.SorterRate(spec.Tiers.Sorter, 1);
        Action<int, double, string> lane = (item, r, what) =>
        {
            if (belt > 0 && r > belt + Eps)
                warn.Add("belt: " + what + " " + gd.ItemName(item) + " " + F(r) + "/min > one lane of " + gd.ItemName(spec.Tiers.Belt)
                    + " (" + F(belt) + "/min): needs " + (int)Math.Ceiling(r / belt - Eps) + " lanes or parallel rows");
        };
        foreach (var r in plan.RawInputs) lane(r.Item, r.Rate, "raw input");
        foreach (var s in plan.Steps)
        {
            // every input is either a raw input or another step's output: its total is checked there
            lane(s.Item, s.Outputs[0].Rate, "step output");
            // per machine at full speed (worst case a sorter must feed)
            double cpm = 60.0 * s.Speed / s.Recipe.TimeSec;
            foreach (var x in s.Recipe.Inputs)
                if (sorter1 > 0 && x.Count * cpm > sorter1 + Eps)
                    warn.Add("sorter: " + s.Recipe.Name + " machine needs " + F(x.Count * cpm) + "/min of " + gd.ItemName(x.Item)
                        + " > one L1 " + gd.ItemName(spec.Tiers.Sorter) + " (" + F(sorter1) + "/min)");
            foreach (var o in s.Recipe.Outputs)
                if (sorter1 > 0 && o.Count * cpm > sorter1 + Eps)
                    warn.Add("sorter: " + s.Recipe.Name + " machine makes " + F(o.Count * cpm) + "/min of " + gd.ItemName(o.Item)
                        + " > one L1 " + gd.ItemName(spec.Tiers.Sorter) + " (" + F(sorter1) + "/min)");
        }
    }

    /// Searches rates near the target (+-25%, on a grid of 1/min, or 0.1/min for small targets) for the minimal
    /// total rounding waste; ties go to the rate closest to the target. Returns the target itself when it is already clean.
    double FindCleanRate(double target)
    {
        double step = target >= 20 ? 1.0 : 0.1;
        double lo = Math.Max(step, Math.Floor(target * 0.75 / step) * step), hi = Math.Ceiling(target * 1.25 / step) * step;
        double best = target, bestWaste = Waste(SolveAt(target, false));
        if (bestWaste < 1e-6) return target;
        for (double r = lo; r <= hi + 1e-9; r += step)
        {
            double rr = Math.Round(r / step) * step;
            double w = Waste(SolveAt(rr, false));
            double dBest = Math.Abs(best - target), dR = Math.Abs(rr - target);
            if (w < bestWaste - 1e-6 || (Math.Abs(w - bestWaste) <= 1e-6 && dR < dBest)) { best = rr; bestWaste = w; }
        }
        return best;
    }

    static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
}
}
