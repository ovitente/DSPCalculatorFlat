// Collider.cs - Miniature Particle Collider (2310) rows: cell table and off-centre footprint. Row layout: SlotRows.Row.
// The collider is not square and its sorter slots are all on the west half (local x < 0): N face 0..2, W face 3..5,
// S face 6..8 (analysis\buildings\slots.md section 2). Rows put the long axis along the row (canonical yaw 90 in the
// Flow-N frame): side A lane on the local N face, side B lane on the local S face, as the community blueprints do.
//
// Geometry source (no number is guessed; sorter numbers relative to the collider, at the source collider yaw):
//  * gsp = x-imp\Old Science Designs (still work)\Science - Gravity Matrix\Green Science + Proliferator.txt (community
//    blueprint, imported by the user, not built by the user):
//      - Strange Matter collider #1017 (91,8) yaw 0: in #976 slot 1 L1 (N), in #974 slot 2 L2 (N, behind),
//        in #1037 slot 6 L1 (S), out #1028 slot 7 L2 (S, behind)  = the whole 3-input cell on one collider.
//      - Strange Matter collider #981 (91,15) yaw 0: in #970 slot 2 L1 (N), in #978 slot 7 L1 (S), in #979 slot 8 L2 (S).
//      - Deuterium collider #903 (77,4) yaw 180 (recipe 40, prototype record, parameters [0]): out #908 slot 6 L1.
//  * am6 = x-imp\Anti Matter\Antimatter.txt (community): collider #119 (19,3) yaw 0: out #188 slot 7 L1 (S).
//  * Footprint: colliders.json build boxes (union x -5.85..4.45 m, z -3.016..2.904 m at yaw 0).
using System;
using System.Collections.Generic;
using System.Linq;

namespace DspFlat.Engine
{

public static class Collider
{
    public const string SourceKey = "gsp", SourceFile = @"x-imp\Old Science Designs (still work)\Science - Gravity Matrix\Green Science + Proliferator.txt";
    public const string SourceKey2 = "am6", SourceFile2 = @"x-imp\Anti Matter\Antimatter.txt";
    /// Footprint union at yaw 0 in metres (analysis\buildings\colliders.json, 2310 derived.footprintBoundsXZ_m): used when
    /// the collider boxes are not loaded.
    public const double FootX0 = -5.85, FootX1 = 4.45, FootZ0 = -3.0157, FootZ1 = 2.9039;
    /// A tile counts as occupied when the collider overlaps it by more than this (tiles): conservative, so the grid
    /// never lets a belt or machine into the collider box.
    public const double TileEps = 0.02;

    public static bool IsCollider(int item) { return item == Ids.Collider; }

    static SlotRowDef def;
    /// Cell table, canonical collider yaw 90 in the Flow-N frame (row along y, lanes at x = +-3 (L1) / +-4 (L2)).
    public static SlotRowDef Def
    {
        get
        {
            if (def != null) return def;
            var d = new SlotRowDef { MachineItem = Ids.Collider, Short = "collider", MachineYaw = 90 };
            d.SlotFace = new[] { Dir.N, Dir.N, Dir.N, Dir.W, Dir.W, Dir.W, Dir.S, Dir.S, Dir.S };
            d.SlotPos = new[] { new[] { -0.796, 1.83 }, new[] { -1.592, 1.83 }, new[] { -2.387, 1.83 }, new[] { -3.82, 0.796 }, new[] { -3.82, 0.0 }, new[] { -3.82, -0.796 },
                                new[] { -2.387, -1.83 }, new[] { -1.592, -1.83 }, new[] { -0.796, -1.83 } };
            // side A (local N face) inputs, preference order: L1 slot 1, L1 slot 2 (extra for throughput), L2 slot 2
            d.Add(SourceKey, SourceFile, 1017, 976, 0, true, -2, 3, -1.544, 3, -1.546, 1.834, 1, 1, null);
            d.Add(SourceKey, SourceFile, 981, 970, 0, true, -2, 3, -2.251, 3, -2.252, 1.84, 2, 1, null);
            d.Add(SourceKey, SourceFile, 1017, 974, 0, true, -2, 4, -2.315, 4, -2.318, 1.841, 2, 2, null);
            // side B (local S face) inputs: L1 slot 6, L1 slot 7, L2 slot 8
            d.Add(SourceKey, SourceFile, 1017, 1037, 0, true, -2, -3, -2.359, -3, -2.356, -1.816, 6, 1, null);
            d.Add(SourceKey, SourceFile, 981, 978, 0, true, -2, -3, -1.525, -3, -1.524, -1.823, 7, 1, null);
            d.Add(SourceKey, SourceFile, 981, 979, 0, true, -1, -4, -0.763, -4, -0.762, -1.827, 8, 2, null);
            // side B outputs: L1 slot 7 (am6 #188), L1 slot 6 (gsp #908, collider yaw 180), L2 slot 7 (gsp #1028)
            d.Add(SourceKey2, SourceFile2, 119, 188, 0, false, -2, -3, -1.711, -1.832, -1.706, -3, 7, 1, null);
            d.Add(SourceKey, SourceFile, 903, 908, 180, false, 2, 3, 2.36, 1.842, 2.358, 3, 6, 1, null);
            d.Add(SourceKey, SourceFile, 1017, 1028, 0, false, -2, -4, -1.571, -1.823, -1.574, -4, 7, 2, null);
            def = d;
            return d;
        }
    }

    /// Occupied tiles: every tile the collider's build boxes overlap by more than TileEps (exact, off-centre, rotated).
    public static Rect TileRect(GameData gd, Bld b)
    {
        double x0, x1, z0, z1;
        var boxes = gd != null ? gd.BoxesAt(b.ItemId, b.Yaw) : new List<GameData.ColliderBox>();
        if (boxes.Count > 0)
        {
            x0 = boxes.Min(o => o.Cx - o.Hx); x1 = boxes.Max(o => o.Cx + o.Hx);
            z0 = boxes.Min(o => o.Cz - o.Hz); z1 = boxes.Max(o => o.Cz + o.Hz);
        }
        else
        {
            // same rotation as GameData.BoxesAt (clockwise yaw: world = (x cos a + z sin a, -x sin a + z cos a))
            double cx = (FootX0 + FootX1) / 2, cz = (FootZ0 + FootZ1) / 2, hx = (FootX1 - FootX0) / 2, hz = (FootZ1 - FootZ0) / 2;
            int q = (int)Math.Round(Dirs.Norm(b.Yaw) / 90.0) % 4;
            for (int i = 0; i < q; i++) { double t = cx; cx = cz; cz = -t; t = hx; hx = hz; hz = t; }
            x0 = cx - hx; x1 = cx + hx; z0 = cz - hz; z1 = cz + hz;
        }
        double m = GameData.MetersPerTile;
        return new Rect(Lo(b.X + x0 / m), Lo(b.Y + z0 / m), Hi(b.X + x1 / m), Hi(b.Y + z1 / m));
    }

    static int Lo(double v) { return (int)Math.Floor(v + TileEps - 0.5) + 1; }
    static int Hi(double v) { return (int)Math.Ceiling(v - TileEps + 0.5) - 1; }
}
}
