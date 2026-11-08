// FloorplanComposer.cs - assembles a block from the pattern-driven parts (docs\floorplan-design.md, step "assembly"):
// Floorplan.Build (rows, stacking, columns, connections) -> row fragments -> Bands.Lay (comb, band, straight belts) ->
// product tail + buffer depot -> rotation to the real edges -> Tesla towers -> tiers -> Validator. Never throws for a
// layout problem: they come back in Layout.Errors, and FlatEngine falls back to the search Composer (EngineTuning.Floorplan).
// Pure model code: no I/O, no console, no game DLL. C# 5.
using System;
using System.Collections.Generic;
using System.Linq;

namespace DspFlat.Engine
{

public static class FloorplanComposer
{
    /// Plan -> finished abstract layout through the floorplan. Layout.Errors is empty when the layout passed the Validator.
    /// Rule 14 margin (BlockSpec.AddMargin, the CLI's own plans) mutates the plan like Composer.Compose does.
    public static Layout Compose(GameData gd, BlockSpec spec, ProductionPlan plan, List<string> notes)
    {
        if (notes == null) notes = new List<string>();
        int quarters = (((int)spec.InEdge - (int)Dir.S) % 4 + 4) % 4;
        if (Dirs.RotateCW(spec.OutEdge, -quarters) != Dir.N)
        {
            var refused = new Layout(gd);
            refused.Errors.Add("floorplan: product edge " + spec.OutEdge + " is not opposite the input edge " + spec.InEdge + " (the floorplan lays the product north of the inputs)");
            return refused;
        }
        if (Rules.Enabled && spec.AddMargin) notes.AddRange(Rules.ApplyMargin(plan));
        // a band row cannot bridge a lane next to its own corner, and some rows put a lane exactly there: the row keys are then forced
        // mirrored or not, one at a time (hill climbing on the number of problems, a fixed number of tries, deterministic)
        var force = new Dictionary<int, bool>();
        var bestNotes = new List<string>();
        Layout best = Attempt(gd, spec, plan, force, bestNotes, quarters);
        int bestScore = Score(best), tries = 1;
        List<int> keys = RowKeys(gd, spec, plan);
        for (bool improved = true; improved && bestScore > 0 && tries < MaxTries; )
        {
            improved = false;
            foreach (int key in keys)
            {
                foreach (bool v in new[] { true, false })
                {
                    bool had, old = false;
                    had = force.TryGetValue(key, out old);
                    if (had && old == v) continue;
                    force[key] = v;
                    var n = new List<string>();
                    Layout l = Attempt(gd, spec, plan, force, n, quarters);
                    tries++;
                    int s = Score(l);
                    if (s < bestScore) { best = l; bestScore = s; bestNotes = n; improved = true; }
                    else { if (had) force[key] = old; else force.Remove(key); }
                    if (bestScore == 0 || tries >= MaxTries) break;
                }
                if (bestScore == 0 || tries >= MaxTries) break;
            }
        }
        notes.AddRange(bestNotes);
        if (tries > 1) notes.Add("floorplan composer: " + tries + " attempts (row mirrors forced: " + force.Count + "), " + bestScore + " problem(s) left");
        return best;
    }

    const int MaxTries = 80;

    static int Score(Layout l) { return l.Errors.Count; }

    static List<int> RowKeys(GameData gd, BlockSpec spec, ProductionPlan plan)
    {
        FpPlan fp = Floorplan.Build(gd, spec, plan);
        return fp.Rows.Where(r => !r.FinalCell && !r.Absorbed).Select(r => r.StepIndex * 100 + r.Part).Distinct().OrderBy(k => k).ToList();
    }

    /// One complete layout with these row mirrors. Errors in Layout.Errors.
    static Layout Attempt(GameData gd, BlockSpec spec, ProductionPlan plan, Dictionary<int, bool> force, List<string> notes, int quarters)
    {
        var layout = new Layout(gd);
        Action<string> fail = m => { layout.Errors.Add(m); };
        try
        {
            FpPlan fp = Floorplan.Build(gd, spec, plan, force);
            notes.AddRange(fp.Notes);
            if (!fp.Ok) { foreach (string e in fp.Errors) fail(e); return layout; }

            Layout work = new Layout(gd);
            var added = new HashSet<Fragment>();
            foreach (FpRow r in fp.Rows) if (r.Frag != null && added.Add(r.Frag)) work.Add(r.Frag);
            if (work.Errors.Count > 0) { foreach (string e in work.Errors) fail("floorplan: " + e); return layout; }

            int topBand = fp.BandLanes > 0 ? fp.BandY0 + fp.BandLanes - 1 : fp.BandY0 - 1;
            var ports = new List<Port>();
            if (!Product(gd, spec, plan, fp, work, topBand, ports, fail)) return layout;

            List<string> errors = Bands.Lay(gd, spec, fp, work);
            notes.AddRange(fp.Notes.Where(n => n.StartsWith("bands:", StringComparison.Ordinal)));
            if (errors.Count > 0) { work.Errors.AddRange(errors); return work; }   // the partial layout helps diagnostics (--map)
            ports.AddRange(work.Ports);

            // rotate to the real edges, normalize (bounding box from (0,0)), towers
            var blds = work.All.ToList();
            if (quarters != 0) Composer.RotateLayout(blds, quarters);
            foreach (Port p in ports)
                for (int i = 0; i < quarters; i++) { int rx = p.X, ry = p.Y; p.X = ry; p.Y = -rx; p.Dir = Dirs.Right(p.Dir); }
            Rect box = Rect.Empty;
            foreach (Bld b in blds) if (b.Kind != Kind.Sorter) box = box.Union(work.Footprints.TileRect(b));
            foreach (Bld b in blds) b.Translate(-box.X0, -box.Y0);
            foreach (Port p in ports) { p.X -= box.X0; p.Y -= box.Y0; }
            var final = new Layout(gd);
            foreach (Bld b in blds) final.Add(b);
            final.Ports.AddRange(ports);
            layout = final;
            if (layout.Errors.Count > 0) return layout;
            List<Bld> towers = Modules.TeslaGrid(final, gd, true);
            if (towers != null) foreach (Bld t in towers) final.Add(t);
            if (layout.Errors.Count > 0) return layout;

            var validator = new Validator(gd, spec);
            ValidationReport rep = validator.Validate(final, plan);
            if (!rep.Ok) { foreach (string e in rep.Errors) fail("floorplan validator: " + e); return layout; }
            if (Rules.Enabled)
            {
                var cut = new List<Bld>();
                int n = Rules.PruneTowers(final, cut);
                if (n > 0 && !new Validator(gd, spec).Validate(final, plan).Ok) { foreach (Bld t in cut) final.Add(t); n = 0; }
                if (n > 0) notes.Add("rule 13: " + n + " redundant tower(s) removed");
            }
            if (spec.CheapestTiers) Lanes.AssignTiers(gd, spec, final, plan, notes);
            Rect bx = final.Bounds();
            notes.Add("floorplan composer: " + fp.Columns.Count + " columns, " + fp.BandLanes + " band lane(s), " + fp.CombTails + " comb tail(s), " + bx.Width + "x" + bx.Height + ", routing: " + Composer.RouteStats(final));
        }
        catch (Exception e)
        {
            fail("floorplan: internal error " + e.GetType().Name + ": " + e.Message);
        }
        return layout;
    }

    /// Product lanes: the final cell's inline depot tail is already a block output; every other target row gets a straight rise
    /// through the band rows (bridged by the band) and, when spec.Buffer, the standard DEPOT_BUFFER cell above the band.
    static bool Product(GameData gd, BlockSpec spec, ProductionPlan plan, FpPlan fp, Layout work, int topBand, List<Port> ports, Action<string> fail)
    {
        var rows = fp.Rows.Where(r => r.Step != null && r.Step.Item == plan.TargetItem && !r.Absorbed).ToList();
        if (rows.Count == 0) { fail("floorplan: no row makes the target item"); return false; }
        foreach (FpRow r in rows)
        {
            Port o = r.Frag.Ports.FirstOrDefault(p => p.Kind == PortKind.Out && p.Item == plan.TargetItem);
            if (o == null || o.Belt == null) { fail("floorplan: row " + r.Name + " has no product port"); return false; }
            string pname = "out:" + plan.TargetItem + "#" + ports.Count;
            if (r.FinalCell)
            {
                ports.Add(new Port { Name = pname, Kind = PortKind.Out, X = o.X, Y = o.Y, Dir = o.Dir, Item = o.Item, Rate = r.Step.Outputs.Count > 0 ? r.Step.Outputs[0].Rate : r.Step.MaxOutputRate, Belt = o.Belt, Chain = o.Chain });
                continue;
            }
            // rise: up to one tile above the top band row (the crossed tile must be a straight-through belt), then the buffer tiles
            double rate = r.Step.Outputs.Count > 0 ? r.Step.Outputs[0].Rate : r.Step.MaxOutputRate;
            rate *= r.Frag.Blds.Count(b => b.Kind == Kind.Machine) / Math.Max(1.0, r.Step.Machines);
            int x = o.X;
            int y1 = Math.Max(o.Y, fp.BandLanes > 0 ? topBand + 1 : o.Y);
            int extra = spec.Buffer ? 3 : 0;
            int yEnd = y1 + extra;
            Bld last = o.Belt;
            var tiles = new List<Bld>();
            string chain = "product:" + plan.TargetItem + "#" + ports.Count;
            for (int y = o.Y + 1; y <= yEnd; y++)
            {
                if (!work.Grid.IsFree(x, y)) { fail("floorplan: product rise of " + r.Name + " blocked at (" + x + "," + y + ") by " + work.Grid.At(x, y)); return false; }
                Bld b = Bld.Belt(spec.Tiers.Belt, x, y, Dirs.Yaw(Dir.N), plan.TargetItem, chain);
                b.Role = "product lane";
                work.Add(b);
                Layout.Link(last, b, 1);
                tiles.Add(b); last = b;
            }
            if (spec.Buffer)
            {
                // DEPOT_BUFFER on the last three tiles, depot beside them (E first, then W): the same cell as the search composer's straight shape
                int n = tiles.Count;
                Bld t0 = n >= 3 ? tiles[n - 3] : null, t1 = n >= 3 ? tiles[n - 2] : null, t2 = n >= 3 ? tiles[n - 1] : null;
                if (t0 == null) { fail("floorplan: no tiles for the buffer depot of " + r.Name); return false; }
                bool placed = false;
                foreach (bool right in new[] { true, false })
                {
                    Dir side = right ? Dirs.Right(Dir.N) : Dirs.Left(Dir.N);
                    int dx = t1.TileX + 2 * Dirs.Dx(side), dy = t1.TileY + 2 * Dirs.Dy(side);
                    bool free = true;
                    for (int ax = dx - 1; ax <= dx + 1 && free; ax++) for (int ay = dy - 1; ay <= dy + 1 && free; ay++) free = work.Grid.IsFree(ax, ay);
                    if (!free) continue;
                    Fragment f = Modules.DepotBuffer(t0, t1, t2, Dir.N, right, Lanes.DepotSorter(spec.Tiers.Sorter), plan.TargetItem);
                    work.Add(f);
                    placed = true;
                    break;
                }
                if (!placed) { fail("floorplan: no room for the buffer depot of " + r.Name); return false; }
            }
            ports.Add(new Port { Name = pname, Kind = PortKind.Out, X = last.TileX, Y = last.TileY, Dir = Dir.N, Item = plan.TargetItem, Rate = rate, Belt = last, Chain = last.Chain });
        }
        return true;
    }
}

}
