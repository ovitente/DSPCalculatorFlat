// PlanMapper.cs - injected plan (PlanInput, e.g. the calculator's solution) -> the engine's internal ProductionPlan +
// BlockSpec, without re-solving. Steps the engine cannot place are dropped and their products become input lanes
// (warning, not an error). What "can place" means lives in one place (StepSupport) so later phases can widen it
// (labs, collider, proliferator) without touching the mapping itself.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DspFlat.Engine
{
/// Which plan steps the layout engine can build today. Extend here when the Composer learns new buildings.
public static class StepSupport
{
    /// Buildings with proven geometry (sorter cells, pitches, prototypes): arc/plane smelter, assembler Mk.I-III,
    /// Matrix Lab (towers, LabTowers.cs) and Miniature Particle Collider (Collider.cs).
    public static readonly HashSet<int> Buildings = new HashSet<int> { Ids.ArcSmelter, Ids.PlaneSmelter, Ids.AsmMk1, Ids.AsmMk2, Ids.AsmMk3,
                                                                       Ids.MatrixLab, Ids.Collider };

    /// null when the step can be built, else the reason it is dropped (its products then arrive on input lanes).
    public static string WhyNot(GameData gd, RecipeDef r, int building)
    {
        if (!Buildings.Contains(building)) return "building " + Name(gd, building) + " is not supported yet (only arc/plane smelters, assemblers Mk.I-III, Matrix Lab, Miniature Particle Collider)";
        if (r.Outputs.Count != 1) return "recipe has byproducts";
        // labs / colliders: only the input counts their cell tables cover (e.g. Universe Matrix with 6 inputs is dropped)
        if (SlotRows.Handles(building))
        {
            string why = SlotRows.WhyNot(gd, r, building);
            if (why != null) return Name(gd, building) + ": " + why;
        }
        return null;
    }

    static string Name(GameData gd, int id) { return gd != null ? gd.ItemName(id) : id.ToString(CultureInfo.InvariantCulture); }
}

public static class PlanMapper
{
    const double Eps = 1e-9;

    public class Result
    {
        public ProductionPlan Plan;
        public BlockSpec Spec;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        /// Items whose producing step was dropped (they arrive on input lanes now).
        public readonly HashSet<int> DroppedItems = new HashSet<int>();
    }

    /// PlanInput -> ProductionPlan (topological: steps eating only lane items first, target last) and BlockSpec.
    public static Result Map(GameData gd, PlanInput input)
    {
        var res = new Result();
        LayoutOptions opt = input.Options ?? new LayoutOptions();
        Func<int, string> n = id => gd.ItemName(id);
        Func<double, string> f = v => v.ToString("0.##", CultureInfo.InvariantCulture);

        if (gd.Item(input.TargetItemId) == null) res.Errors.Add("unknown target item " + input.TargetItemId);
        if (!(input.TargetPerMin > 0)) res.Errors.Add("target rate must be > 0");
        if (res.Errors.Count > 0) return res;

        // ---- spec (lanes hook: tier caps Mk.I-III from the options, pile sorter -> Mk.III; cheapest tier inside the caps)
        var spec = new BlockSpec { Item = input.TargetItemId, Rate = input.TargetPerMin };
        Lanes.ApplyCaps(gd, opt, spec.Tiers, res.Warnings);
        spec.CheapestTiers = opt.CheapestTiers;
        spec.InEdge = opt.InEdge; spec.OutEdge = opt.OutEdge; spec.Safe60 = opt.Safe60; spec.Buffer = opt.Buffer;
        spec.MaxRow = opt.MaxRow > 0 ? opt.MaxRow : 12;
        spec.ShortDesc = opt.ShortDesc; spec.Desc = opt.Desc;
        spec.LabStackMax = opt.LabStackMax > 0 ? opt.LabStackMax : 15;   // feat-labs
        if (opt.OwnSupply != null) spec.OwnSupply.UnionWith(opt.OwnSupply);
        // COATERS HOOK: proliferator on the external proliferator lane (engine\Coaters.cs); inferred from the steps' inc
        // level (= ItemProto.Ability 1/2/4) when the options name none
        if (opt.ProliferatorItemId != 0 && !Coaters.IsProliferator(opt.ProliferatorItemId))
            res.Warnings.Add("proliferator item " + opt.ProliferatorItemId + " is not Proliferator Mk.I-III; no spray coaters");
        else spec.ProliferatorItem = opt.ProliferatorItemId;
        if (spec.ProliferatorItem == 0 && opt.ProliferatorItemId == 0)
        {
            int lvl = input.Steps.Where(s => s.IncLevel > 0).Select(s => s.IncLevel).DefaultIfEmpty(0).Max();
            int guess = Coaters.ItemForLevel(lvl);
            if (lvl > 0 && guess != 0) { spec.ProliferatorItem = guess; res.Warnings.Add("proliferator not given: using " + n(guess) + " from inc level " + lvl); }
            else if (lvl > 0) res.Warnings.Add("proliferator not given and inc level " + lvl + " matches no proliferator: no spray coaters");
        }
        res.Spec = spec;

        // ---- steps: keep the placeable ones, drop the rest (products -> input lanes)
        var external = new HashSet<int>(input.ExternalItems ?? new List<int>());
        var kept = new List<KeyValuePair<PlanStep, RecipeDef>>();
        foreach (PlanStep ps in input.Steps)
        {
            RecipeDef r = gd.Recipe(ps.RecipeId);
            if (r == null) { res.Errors.Add("unknown recipe " + ps.RecipeId); continue; }
            string why = StepSupport.WhyNot(gd, r, ps.BuildingItemId);
            if (why == null && r.Buildings.Length > 0 && !r.Buildings.Contains(ps.BuildingItemId))
                why = "recipe " + r.Name + " cannot run in " + n(ps.BuildingItemId);
            if (why == null && external.Contains(r.Outputs[0].Item)) why = "its product is marked external";
            if (why != null)
            {
                var outs = r.Outputs.Select(o => o.Item).ToList();
                foreach (int o in outs) res.DroppedItems.Add(o);
                res.Warnings.Add("step " + r.Name + " (" + n(ps.BuildingItemId) + ") dropped: " + why + "; "
                    + string.Join(", ", outs.Select(o => n(o)).ToArray()) + " arrive(s) on input lanes");
                continue;
            }
            kept.Add(new KeyValuePair<PlanStep, RecipeDef>(ps, r));
        }
        if (res.Errors.Count > 0) return res;

        // one step per item (the internal model looks steps up by their product)
        var byItem = new Dictionary<int, int>();
        for (int i = 0; i < kept.Count; i++)
        {
            int item = kept[i].Value.Outputs[0].Item;
            if (byItem.ContainsKey(item)) res.Errors.Add("two steps make " + n(item) + " (" + kept[byItem[item]].Value.Name + ", " + kept[i].Value.Name + "): merge them before calling the engine");
            else byItem[item] = i;
        }
        if (!byItem.ContainsKey(input.TargetItemId))
            res.Errors.Add("no buildable step makes the target " + n(input.TargetItemId)
                + (res.DroppedItems.Contains(input.TargetItemId) ? " (its step was dropped, see warnings)" : ""));
        if (res.Errors.Count > 0) return res;

        // steps that only fed dropped steps are not needed any more (the dropped step's products arrive on lanes)
        for (bool pruned = true; pruned; )
        {
            pruned = false;
            for (int i = 0; i < kept.Count; i++)
            {
                int item = kept[i].Value.Outputs[0].Item;
                if (item == input.TargetItemId) continue;
                bool used = false;
                for (int j = 0; j < kept.Count && !used; j++)
                    used = j != i && kept[j].Value.Inputs.Any(x => x.Item == item)
                        && (kept[j].Key.ExternalInputs == null || !kept[j].Key.ExternalInputs.Contains(item));
                if (used) continue;
                res.Warnings.Add("step " + kept[i].Value.Name + " (" + n(kept[i].Key.BuildingItemId) + ") removed: nothing left in the block consumes " + n(item));
                kept.RemoveAt(i);
                pruned = true;
                break;
            }
        }

        // ---- internal steps
        var steps = new List<Step>();
        foreach (var kv in kept)
        {
            PlanStep ps = kv.Key; RecipeDef r = kv.Value;
            int item = r.Outputs[0].Item, outCount = r.Outputs[0].Count;
            double baseSpeed = gd.Speed(ps.BuildingItemId);
            double outRate;
            if (!ps.OutputsPerMin.TryGetValue(item, out outRate) || outRate <= 0)
                outRate = ps.MachineCount * outCount * 60.0 * baseSpeed / r.TimeSec;
            double speed = baseSpeed;
            if (ps.IncLevel > 0 && ps.MachineCount > Eps && r.TimeSec > 0)
            {
                // proliferator speed-up / extra products: the plan's numbers are the truth, the machine speed follows them
                double derived = outRate * r.TimeSec / (outCount * 60.0 * ps.MachineCount);
                if (Math.Abs(derived - baseSpeed) > 1e-6 * baseSpeed) speed = derived;
            }
            var s = new Step { Item = item, Recipe = r, MachineItem = ps.BuildingItemId, Speed = speed };
            if (ps.IncLevel > 0 && spec.ProliferatorItem != 0) s.IncLevel = ps.IncLevel;   // COATERS HOOK
            s.MachinesExact = ps.MachineCount > 0 ? ps.MachineCount : Solver.MachinesFor(r, item, outRate, speed);
            s.Machines = Math.Max(Math.Max(ps.Machines, (int)Math.Ceiling(s.MachinesExact - Eps)), 1);
            s.Outputs.Add(new ItemRate(item, outRate));
            double crafts = outRate / outCount;
            foreach (ItemCount x in r.Inputs)
            {
                double q;
                if (!ps.InputsPerMin.TryGetValue(x.Item, out q)) q = crafts * x.Count;
                s.Inputs.Add(new ItemRate(x.Item, q));
                if (ps.ExternalInputs != null && ps.ExternalInputs.Contains(x.Item)) s.External.Add(x.Item);
            }
            s.MaxOutputRate = s.Machines * outCount * 60.0 * speed / r.TimeSec;
            steps.Add(s);
        }

        // ---- topological order: a step comes after the steps it eats from; ties keep the input order (an input that is
        // already topological comes back unchanged)
        var producer = new Dictionary<int, int>();
        for (int i = 0; i < steps.Count; i++) producer[steps[i].Item] = i;
        var deps = new List<HashSet<int>>();
        for (int i = 0; i < steps.Count; i++)
        {
            var d = new HashSet<int>();
            foreach (ItemRate x in steps[i].Inputs)
            {
                int p;
                if (!steps[i].External.Contains(x.Item) && producer.TryGetValue(x.Item, out p) && p != i) d.Add(p);
            }
            deps.Add(d);
        }
        var order = new List<int>(); var done = new HashSet<int>();
        while (order.Count < steps.Count)
        {
            int next = -1;
            for (int i = 0; i < steps.Count; i++) if (!done.Contains(i) && deps[i].All(done.Contains)) { next = i; break; }
            if (next < 0)
            {
                res.Errors.Add("recipe cycle between " + string.Join(", ", Enumerable.Range(0, steps.Count).Where(i => !done.Contains(i)).Select(i => steps[i].Recipe.Name).ToArray()));
                return res;
            }
            order.Add(next); done.Add(next);
        }
        // the target step last (nothing may consume the product inside the block)
        int ti = producer[input.TargetItemId];
        if (order[order.Count - 1] != ti)
        {
            if (steps.Any(s => s.Inputs.Any(x => x.Item == input.TargetItemId && !s.External.Contains(x.Item))))
                res.Warnings.Add("the target " + n(input.TargetItemId) + " is also consumed inside the block");
            else { order.Remove(ti); order.Add(ti); }
        }

        var plan = new ProductionPlan { TargetItem = input.TargetItemId, TargetRate = input.TargetPerMin };
        foreach (int i in order) plan.Steps.Add(steps[i]);
        for (int i = 0; i < plan.Steps.Count; i++)
            for (int j = 0; j < plan.Steps.Count; j++)
                if (plan.Steps[j].Inputs.Any(x => x.Item == plan.Steps[i].Item) && !plan.Steps[j].External.Contains(plan.Steps[i].Item)) plan.Steps[i].ConsumerSteps.Add(j);

        // ---- lane items: everything a step eats that no step makes (external, raw, dropped) or that is External for it
        var raw = new Dictionary<int, double>();
        foreach (Step s in plan.Steps)
            foreach (ItemRate x in s.Inputs)
                if (s.External.Contains(x.Item) || plan.StepFor(x.Item) == null)
                {
                    double v; raw.TryGetValue(x.Item, out v); raw[x.Item] = v + x.Rate;
                }
        foreach (int item in raw.Keys.OrderByDescending(k => raw[k]).ThenBy(k => k)) plan.RawInputs.Add(new ItemRate(item, raw[item]));
        foreach (Step s in plan.Steps)
            foreach (int e in s.External)
            {
                HashSet<int> set;
                if (!spec.RawFor.TryGetValue(s.Item, out set)) { set = new HashSet<int>(); spec.RawFor[s.Item] = set; }
                set.Add(e);
            }

        // ---- consistency notes (the engine builds what the plan says; mismatches are the planner's to fix)
        foreach (Step s in plan.Steps)
        {
            double need = plan.Steps.Where(c => !c.External.Contains(s.Item)).SelectMany(c => c.Inputs).Where(x => x.Item == s.Item).Sum(x => x.Rate);
            if (s.Item == plan.TargetItem) need += plan.TargetRate;
            if (need > s.Outputs[0].Rate * 1.01 + 1e-6)
                res.Warnings.Add("plan: consumers need " + f(need) + "/min of " + n(s.Item) + ", its step makes " + f(s.Outputs[0].Rate) + "/min");
        }
        foreach (int item in raw.Keys)
            if (!external.Contains(item) && !res.DroppedItems.Contains(item) && !(gd.Item(item) != null && gd.Item(item).IsResource)
                && !plan.Steps.Any(s => s.External.Contains(item)))
                res.Warnings.Add("plan: " + n(item) + " has no step and is not marked external; it arrives on an input lane");

        res.Plan = plan;
        return res;
    }

    /// The CLI's solved plan -> PlanInput (so both hosts share one code path after planning).
    public static PlanInput FromPlan(ProductionPlan plan, BlockSpec spec)
    {
        var p = new PlanInput { TargetItemId = plan.TargetItem, TargetPerMin = plan.TargetRate };
        foreach (Step s in plan.Steps)
        {
            var ps = new PlanStep { RecipeId = s.Recipe != null ? s.Recipe.Id : 0, BuildingItemId = s.MachineItem, MachineCount = s.MachinesExact, Machines = s.Machines, IncLevel = s.IncLevel };
            foreach (ItemRate x in s.Inputs) { double v; ps.InputsPerMin.TryGetValue(x.Item, out v); ps.InputsPerMin[x.Item] = v + x.Rate; }
            foreach (ItemRate x in s.Outputs) { double v; ps.OutputsPerMin.TryGetValue(x.Item, out v); ps.OutputsPerMin[x.Item] = v + x.Rate; }
            ps.ExternalInputs.AddRange(s.External);
            p.Steps.Add(ps);
        }
        // lane items that no step makes (per-step externals of --raw "Item@Consumer" travel as PlanStep.ExternalInputs:
        // marking them external here would drop their producer step)
        foreach (ItemRate r in plan.RawInputs) if (plan.StepFor(r.Item) == null && !p.ExternalItems.Contains(r.Item)) p.ExternalItems.Add(r.Item);
        if (spec != null)
        {
            LayoutOptions o = p.Options;
            o.BeltItemMax = spec.Tiers.Belt; o.SorterItemMax = spec.Tiers.Sorter; o.CheapestTiers = spec.CheapestTiers;
            o.InEdge = spec.InEdge; o.OutEdge = spec.OutEdge; o.Safe60 = spec.Safe60; o.Buffer = spec.Buffer;
            o.OwnSupply.AddRange(spec.OwnSupply); o.MaxRow = spec.MaxRow;
            o.ShortDesc = spec.ShortDesc; o.Desc = spec.Desc;
            o.LabStackMax = spec.LabStackMax;   // feat-labs
            o.ProliferatorItemId = spec.ProliferatorItem;
            o.TimeBudgetMs = 0;   // CLI: no hard limit, the composer keeps its per-mode-set budget (EngineTuning.ComposeBudgetMs)
        }
        return p;
    }

}
}
