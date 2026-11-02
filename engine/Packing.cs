// Packing.cs - the user's packing rules for the grouped row mode (rules 14, 12, 8 of engine\Rules.cs; PLAN.md phase 4).
// Source: blueprint-tools\analysis\user-edit-turret75.md (R8, R12, R14) and the game-proven reference
// blueprint-tools\refs\engine-missile-turret-7.5-v1-useredit3.txt.
//
//  - rule 14 (GroupCounts): the producer rows of one step together build exactly Step.Machines machines. Before, every
//    group got ceil(MachinesExact * share), so a margin machine (Rules.ApplyMargin) or a user count above the exact one
//    (DSPCalculator PlanStep.Machines) was silently dropped and the Validator failed ("plan needs 4 machines for Copper
//    Ingot, layout has 3"). The extra machines go to the group with the least margin (the bottleneck), never taken from
//    another group (each keeps at least its ceil(exact share)).
//  - rules 8 + 12 (Pack): a producer row feeds at most 2 consumer rows through a splitter (2-way); a further consumer
//    takes the item from the TAIL of one of those consumer lanes (serial pickup: the lane runs past the first
//    consumer's machines and on to the second one). A small consumer that would otherwise get a producer row of its
//    own (gear: own ore lane + 1 smelter) joins an existing group as such a tail consumer when the group's lane has the
//    room (group total + its demand <= the group cap); the group's producer row is sized for the tail too, so the line
//    it takes from has the margin (rule 12 "only if that line has margin").
//    3-way splitter: allowed only when the 2-way + tail form is impossible (Pack falls back to it when a group would
//    need a tail but no member lane has room; useredit3 keeps one 3-way copper splitter at (28,17) #448).
//
// Composer hooks are the lines marked "PACK HOOK" in engine\Composer.cs.
//
// Margin and injected plans: Rules.ApplyMargin runs inside Compose only when BlockSpec.AddMargin is set. The CLI solver
// sets it (its plan is the engine's own); a plan injected by DSPCalculator (PlanInput) carries the user's machine counts,
// which are authoritative: LayoutOptions.AddMargin defaults to false there, and GroupCounts then lays out exactly the
// user's counts (including counts above the exact ones).
using System;
using System.Collections.Generic;
using System.Linq;

namespace DspFlat.Engine
{
    /// One consumer group of a producer step: up to 2 main consumers (fed by the producer row, by a 2-way splitter when
    /// there are 2) and tail consumers fed from the end of a main consumer's lane.
    public class PackGroup<T>
    {
        public readonly List<KeyValuePair<T, double>> Mains = new List<KeyValuePair<T, double>>();
        /// tail consumer -> the main consumer whose lane end feeds it
        public readonly List<KeyValuePair<KeyValuePair<T, double>, T>> Tails = new List<KeyValuePair<KeyValuePair<T, double>, T>>();
        public double Total { get { return Mains.Sum(x => x.Value) + Tails.Sum(x => x.Key.Value); } }
        public int Count { get { return Mains.Count + Tails.Count; } }
        /// main consumers and tails, as the old flat group list (demand per consumer)
        public List<KeyValuePair<T, double>> All() { return Mains.Concat(Tails.Select(t => t.Key)).ToList(); }
        public bool IsTail(T c) { return Tails.Any(t => EqualityComparer<T>.Default.Equals(t.Key.Key, c)); }
    }

    public static class Packing
    {
        /// Rules 8/12/14 in the Composer's grouped row mode. Off = the pre-rules packing (3-way splitters, own rows).
        public static bool Enabled = true;

        // ================================================================ rule 14: row counts follow Step.Machines
        /// Machines per producer group: at least ceil(exact * share) each; the remainder up to total (Step.Machines) goes
        /// one by one to the group with the smallest relative margin (count / (exact * share)). Sum = max(total, sum of
        /// the minimums).
        public static int[] GroupCounts(int total, double exact, double[] shares)
        {
            int n = shares.Length;
            var c = new int[n];
            for (int i = 0; i < n; i++) c[i] = Math.Max(1, (int)Math.Ceiling(exact * shares[i] - 1e-9));
            int extra = total - c.Sum();
            while (extra > 0 && n > 0)
            {
                int best = 0; double bestM = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    double need = Math.Max(1e-9, exact * shares[i]);
                    double m = c[i] / need;
                    if (m < bestM - 1e-9) { bestM = m; best = i; }
                }
                c[best]++; extra--;
            }
            return c;
        }

        // ================================================================ rule 1: tight machine columns
        /// Rule 1 target (R1, useredit3: 5-6 tiles for 3-wide machines): largest allowed pitch between neighbouring
        /// machine columns (distinct rounded machine centre x, as Rules.Check measures it).
        public const int MaxPitch = 6;
        /// Selection cost per tile of pitch above MaxPitch (Composer attempt cost is in tiles of area).
        public static int PitchCostTiles = 40;

        /// Sum over neighbouring machine columns of (pitch - MaxPitch) where positive.
        public static int PitchExcess(Layout l)
        {
            var xs = l.All.Where(b => b.Kind == Kind.Machine).Select(m => (int)Math.Round(m.X)).Distinct().OrderBy(x => x).ToList();
            int ex = 0;
            for (int i = 1; i < xs.Count; i++) ex += Math.Max(0, xs[i] - xs[i - 1] - MaxPitch);
            return ex;
        }

        /// Extra attempt cost for rule 1 (0 when packing is off).
        public static long PitchCost(Layout l) { return Enabled ? (long)PitchCostTiles * PitchExcess(l) : 0; }

        // ================================================================ rules 8 + 12: 2-way splitters + tail pickup
        /// Packs consumer slots (consumer, demand) into groups: at most 2 mains (2-way splitter) and at most one tail per
        /// main, group total <= cap. Slots are taken by descending demand; a slot joins the first group with room as a
        /// main, else as a tail behind the main with the most lane room, else starts a new group. Own-supply consumers
        /// (--own) always start a group of their own. Fallback (no tail possible because the member lane would exceed
        /// laneCap): the old 3-main group (3-way splitter, rule 8 exception) when the total fits.
        /// Returns null when packing is disabled (caller uses Composer.PackConsumers).
        public static List<PackGroup<T>> Pack<T>(List<KeyValuePair<T, double>> slots, double cap, double laneCap, HashSet<int> ownSupply, Func<T, int> itemOf)
        {
            var groups = new List<PackGroup<T>>();
            var own = new HashSet<PackGroup<T>>();
            foreach (var sl in slots.OrderByDescending(x => x.Value))
            {
                bool mine = ownSupply != null && ownSupply.Contains(itemOf(sl.Key));
                PackGroup<T> g = null; bool asTail = false; T donor = default(T);
                if (!mine)
                {
                    foreach (var x in groups)
                    {
                        if (own.Contains(x) || x.Total + sl.Value > cap + 1e-9) continue;
                        if (x.Mains.Count < 2) { g = x; break; }
                        // tail behind the main with the most room on its lane (main demand + tail demand <= laneCap)
                        var free = x.Mains.Where(m => !x.Tails.Any(t => EqualityComparer<T>.Default.Equals(t.Value, m.Key)) && m.Value + sl.Value <= laneCap + 1e-9)
                            .OrderBy(m => m.Value).ToList();
                        if (free.Count > 0) { g = x; asTail = true; donor = free[0].Key; break; }
                        if (x.Mains.Count < 3 && x.Tails.Count == 0) { g = x; break; }   // rule 8 exception: 3-way
                    }
                }
                if (g == null) { g = new PackGroup<T>(); groups.Add(g); if (mine) own.Add(g); }
                if (asTail) g.Tails.Add(new KeyValuePair<KeyValuePair<T, double>, T>(sl, donor));
                else g.Mains.Add(sl);
            }
            return groups;
        }

        /// Rule 12 in the split row mode (one producer row per consumer row): a consumer whose own producer row would be a
        /// single machine (exact * demand / total <= 1) takes the item from the lane end of another consumer instead, when
        /// that lane has room (root row demand incl. its tails + this demand <= laneCap). Roots are consumers whose own row
        /// has more than one machine; tails chain (the 2nd tail of a root takes from the 1st tail's lane end, serial pickup).
        /// Returns the donor index per consumer (-1 = keeps its own row); Root() gives the consumer whose row feeds the chain.
        public static int[] SplitTails(IList<double> dem, double exact, double laneCap)
        {
            int n = dem.Count; var donor = Enumerable.Repeat(-1, n).ToArray();
            double sum = dem.Sum(); if (sum <= 0 || n < 2) return donor;
            var load = dem.ToArray(); var last = Enumerable.Range(0, n).ToArray();   // per root: lane load, chain end
            Func<int, bool> big = i => exact * dem[i] / sum > 1 + 1e-9;
            foreach (int j in Enumerable.Range(0, n).OrderBy(i => dem[i]))
            {
                if (big(j)) continue;
                int best = -1;
                for (int k = 0; k < n; k++)
                    if (k != j && big(k) && load[k] + dem[j] <= laneCap + 1e-9 && (best < 0 || load[k] < load[best])) best = k;
                if (best < 0) continue;
                donor[j] = last[best]; last[best] = j; load[best] += dem[j];
            }
            return donor;
        }

        /// The root consumer of a tail chain (SplitTails result).
        public static int Root(int[] donor, int j) { int g = 0; while (donor[j] >= 0 && g++ < donor.Length) j = donor[j]; return j; }

        /// Splitter/straight feeds of a packed group for one producer part (share ps): each main consumer's lane carries
        /// its own demand plus the demand of its tail consumers.
        public static List<KeyValuePair<T, double>> MainFeeds<T>(PackGroup<T> g, double ps)
        {
            return g.Mains.Select(m => new KeyValuePair<T, double>(m.Key,
                (m.Value + g.Tails.Where(t => EqualityComparer<T>.Default.Equals(t.Value, m.Key)).Sum(t => t.Key.Value)) * ps)).ToList();
        }

        /// Rule 8/12 tail pickup: the end of a row's input lane for item (follow the lane from port "in:<item>" to its
        /// last belt) as an Out port flowing on, the source of the tail consumer's route. null when the row has no such lane.
        public static Port LaneEnd(Fragment f, int item)
        {
            if (f == null) return null;
            Port ip = f.Ports.FirstOrDefault(p => p.Name == "in:" + item);
            if (ip == null || ip.Belt == null) return null;
            Bld b = ip.Belt; var seen = new HashSet<Bld>();
            while (b.Output != null && b.Output.Kind == Kind.Belt && seen.Add(b)) b = b.Output;
            return new Port { Name = "tail:" + item, Kind = PortKind.Out, X = b.TileX, Y = b.TileY, Dir = b.FlowDir, Item = item, Belt = b, Chain = b.Chain };
        }
    }
}
