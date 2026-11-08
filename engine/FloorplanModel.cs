// FloorplanModel.cs - shared contract of the pattern-driven composer (docs\floorplan-design.md).
// Pure data: the Floorplan (engine\Floorplan.cs) fills it from a ProductionPlan, Bands (engine\Bands.cs) lays the belts
// for it, FloorplanComposer (engine\FloorplanComposer.cs) assembles, validates and falls back to the search Composer.
// Frame: canonical, inputs at the south edge (y small), product at the north edge; x east. Rows flow north unless a
// lane says otherwise (RowSpec.LaneFlows). No game DLL.
using System;
using System.Collections.Generic;
using System.Linq;

namespace DspFlat.Engine
{

/// How a connection is laid: straight up inside one column (producer output lane continues into the consumer input
/// lane), through the north band (horizontal lane per product above the columns, one bridge per crossed vertical lane),
/// from the south comb (block input tails, pitch 1), or machine to machine (two L3 sorters, rule 4).
public enum FpRoute { Straight, Band, Comb, MachineToMachine }

/// One vertical column of the block: 1..n rows stacked south -> north (a producer directly under its only consumer).
public class FpColumn
{
    public int Index;                                  // west -> east order
    public int X0;                                     // west edge of the column's tiles (after placement)
    public int Width;                                  // tiles, machines + their lanes
    public readonly List<FpRow> Rows = new List<FpRow>();   // south -> north
    public override string ToString() { return "column " + Index + " x" + X0 + " w" + Width + ": " + string.Join(" / ", Rows.Select(r => r.Name).ToArray()); }
}

/// One placed row fragment (MachineRow / DoubleRow / lab towers / collider / final cell).
public class FpRow
{
    public string Name;
    public Step Step;
    public int StepIndex;
    public Fragment Frag;                              // placed in the canonical frame (absolute tiles)
    public FpColumn Column;
    public int Y0, Y1;                                 // south / north tile rows the fragment occupies
    public bool FinalCell;                             // the target step (fed from up to 4 sides, buffer depot north of it)
    public override string ToString() { return Name + " y" + Y0 + ".." + Y1; }
}

/// One belt connection the bands must lay. Source: a row out port (or null for a block input), Target: a row in port.
public class FpConnection
{
    public int Item;
    public double Rate;                                // items/min on this connection
    public FpRow From;                                 // null = block input (comb)
    public FpRow To;
    public Port Source;                                // From's out port (null for comb)
    public Port Target;                                // To's in port
    public FpRoute Route;
    /// Band: the horizontal lane row (y) assigned to this item; Comb: the tail x on the input edge. -1 = not assigned yet.
    public int Slot = -1;
    /// Several consumers of one producer: the connections share Source; the band puts one splitter per fork (2-way,
    /// rule 8) or feeds the next consumer from the lane tail (rule 8 / 12). Group = index of the shared source.
    public int Group = -1;
    public override string ToString() { return Route + " " + Item + " " + Rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "/min " + (From == null ? "comb" : From.Name) + " -> " + To.Name; }
}

/// The whole floorplan: columns placed, connections classified, band and comb rows reserved.
public class FpPlan
{
    public readonly List<FpColumn> Columns = new List<FpColumn>();
    public readonly List<FpRow> Rows = new List<FpRow>();
    public readonly List<FpConnection> Connections = new List<FpConnection>();
    /// North band: y of the first band lane (just above the tallest column) and the number of lanes reserved.
    public int BandY0, BandLanes;
    /// South comb: y of the tail row (block input edge) and the number of tails.
    public int CombY, CombTails;
    public readonly List<string> Notes = new List<string>();
    public readonly List<string> Errors = new List<string>();
    public bool Ok { get { return Errors.Count == 0; } }
}

}
