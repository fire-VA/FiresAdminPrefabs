using UnityEngine;

namespace VerdantsAscent.Managers.Snapping
{
    // The snap layouts for vanilla props FAP puts in the hammer. Most of them were never meant to be built with, so
    // they ship with no snap points (dvergr poles and beams, goblin walls, the demisters) or with too few to build
    // with (blackmarble_floor_large has its 8 corners and nothing between them). Each entry below is one prop's
    // layout: points on its edges and corners, at the spacing of the piece it is sized like.
    //
    // Having an entry is the opt-in. A prop with no entry keeps whatever it shipped with; nothing is invented for
    // it. Points are added AFTER the ones a prop already has, so its own first point stays the ghost's default.
    internal static class VASnapLayouts
    {
        private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        // Wall-like props, x along the face: the four corners of a face w wide and h tall, bottom edge at y0.
        private static Vector3[] FaceCorners(float halfWidth, float y0, float y1) => new[]
        {
            V(-halfWidth, y0, 0f), V(halfWidth, y0, 0f), V(-halfWidth, y1, 0f), V(halfWidth, y1, 0f),
        };

        // The four corners of a horizontal x-by-z rectangle at height y, diagonal pairs first.
        private static Vector3[] Ring(float x, float y, float z) => new[] { V(-x, y, -z), V(x, y, z), V(-x, y, z), V(x, y, -z) };

        // A box's eight corners, top face first.
        private static Vector3[] BoxCorners(float x, float yTop, float yBottom, float z)
        {
            var top = Ring(x, yTop, z);
            var bottom = Ring(x, yBottom, z);
            return new[] { top[0], top[1], top[2], top[3], bottom[0], bottom[1], bottom[2], bottom[3] };
        }

        private static readonly Vector3[] DvergrChestPoints =
        {
            Vector3.zero,
            V(0.66f, 0f, 0.33f), V(0.66f, 0f, -0.33f), V(-0.66f, 0f, 0.33f), V(-0.66f, 0f, -0.33f),
            V(0.6f, 0.7f, 0.33f), V(0.6f, 0.7f, -0.33f), V(-0.6f, 0.7f, 0.33f), V(-0.6f, 0.7f, -0.33f),
        };

        private static readonly Vector3[] StoneChestPoints =
        {
            V(0f, -0.01f, 0f),
            V(1f, -0.01f, 0.37f), V(1f, -0.01f, -0.37f), V(-1f, -0.01f, 0.37f), V(-1f, -0.01f, -0.37f),
            V(0.65f, 0.8f, 0.35f), V(0.65f, 0.8f, -0.35f), V(-0.65f, 0.8f, 0.35f), V(-0.65f, 0.8f, -0.35f),
        };

        // Ashlands 2x2 walls. Both faces get labelled points ("inner" is the face the wall's -z side looks at on the
        // straight pieces); the corner pieces also get their corner column.
        private const string In = "$hud_snappoint_inner ", Out = "Outer ";
        private const string Bottom = "$hud_snappoint_bottom", Top = "$hud_snappoint_top", Corner = "$hud_snappoint_corner";

        private static (Vector3, string)[] AshWallFaces(float innerZ, float outerZ, float xFirst) => new[]
        {
            (V(xFirst, 0f, innerZ), In + Bottom + " 1"), (V(-xFirst, 0f, innerZ), In + Bottom + " 2"),
            (V(xFirst, 2f, innerZ), In + Top + " 1"), (V(-xFirst, 2f, innerZ), In + Top + " 2"),
            (V(xFirst, 0f, outerZ), Out + Bottom + " 1"), (V(-xFirst, 0f, outerZ), Out + Bottom + " 2"),
            (V(xFirst, 2f, outerZ), Out + Top + " 1"), (V(-xFirst, 2f, outerZ), Out + Top + " 2"),
        };

        // A corner piece's inner L and outer L, each as end 1, the corner, end 2 on the bottom and then the top.
        private static (Vector3, string)[] AshWallCorner(Vector3 innerEnd1, Vector3 innerCorner, Vector3 innerEnd2,
                                                         Vector3 outerEnd1, Vector3 outerCorner, Vector3 outerEnd2)
        {
            Vector3 up = V(0f, 2f, 0f);
            return new[]
            {
                (innerEnd1, In + Bottom + " 1"), (innerCorner, In + Bottom + " " + Corner), (innerEnd2, In + Bottom + " 2"),
                (innerEnd1 + up, In + Top + " 1"), (innerCorner + up, In + Top + " " + Corner), (innerEnd2 + up, In + Top + " 2"),
                (outerEnd1, Out + Bottom + " 1"), (outerCorner, Out + Bottom + " " + Corner), (outerEnd2, Out + Bottom + " 2"),
                (outerEnd1 + up, Out + Top + " 1"), (outerCorner + up, Out + Top + " " + Corner), (outerEnd2 + up, Out + Top + " 2"),
            };
        }

        internal static readonly VASnapRule[] Rules =
        {
            // ── small props ──
            new VASnapRule("barrell").At(V(0f, -1f, 0f)),
            new VASnapRule("ArmorStand_Male", "ArmorStand_Female", "dvergrtown_wood_stake").At(Vector3.zero),
            new VASnapRule("dverger_demister")
                .Boxes((V(0f, -0.2f, 0f), V(0.2f, 1.1f, 0.2f)), (Vector3.zero, V(1f, 0.1f, 1f)))
                .At(Vector3.zero, V(0f, -0.65f, 0f)),
            new VASnapRule("dverger_demister_large")
                .Boxes((V(0f, -0.25f, 0f), V(0.2f, 1.3f, 0.2f)), (V(0f, -0.25f, 0f), V(1f, 0.1f, 1f)))
                .At(Vector3.zero, V(0f, -0.9f, 0f)),
            new VASnapRule("dvergrprops_hooknchain").Labelled((V(0f, 2.5f, 0f), Top), (V(0f, -2f, 0f), "Hook")),

            // ── floors, ledges, roofs ──
            new VASnapRule("Ice_floor").At(
                V(2f, 1f, 2f), V(-2f, 1f, -2f), V(2f, 1f, -2f), V(-2f, 1f, 2f),
                V(2f, -1f, 2f), V(-2f, -1f, -2f), V(2f, -1f, -2f), V(-2f, -1f, 2f)),
            new VASnapRule("wood_ledge").At(
                V(0.5f, 0f, 0.25f), V(0f, 0f, 0.25f), V(-0.5f, 0f, 0.25f), V(-0.5f, 0f, -0.25f), V(0.5f, 0f, -0.25f)),
            new VASnapRule("turf_roof_top").At(V(1f, 0.5f, 0f), V(-1f, 0.5f, 0f)),
            new VASnapRule("goblin_roof_45d").At(V(1f, 0f, 1f), V(-1f, 0f, 1f), V(1f, 2f, -1f), V(-1f, 2f, -1f)),
            new VASnapRule("goblin_roof_45d_corner").At(V(-1f, 0f, -1f), V(1f, 0f, 1f)),
            new VASnapRule("stone_floor").Grid(new[] { -0.5f, 0.5f }, -2, 2, -2, 2, skipOuterCorners: true),
            new VASnapRule("stoneblock_fracture").OnPieceLayer().Grid(new[] { -0.5f, 0.5f }, -1, 1, -1, 1),
            new VASnapRule("blackmarble_floor_large").Grid(new[] { -1f, 1f }, -4, 4, -4, 4, step: 2),
            new VASnapRule("Ashlands_Fortress_Floor").Labelled(
                (V(1f, -0.5f, -1f), Bottom + " 1"), (V(-1f, -0.5f, -1f), Bottom + " 2"),
                (V(-1f, -0.5f, 1f), Bottom + " 3"), (V(1f, -0.5f, 1f), Bottom + " 4"),
                (V(1f, 0.5f, -1f), Top + " 1"), (V(-1f, 0.5f, -1f), Top + " 2"),
                (V(-1f, 0.5f, 1f), Top + " 3"), (V(1f, 0.5f, 1f), Top + " 4"),
                (Vector3.zero, "$hud_snappoint_center")),
            new VASnapRule("Ashland_Stair").Labelled(
                (V(2f, 0f, -2f), Bottom + " 1"), (V(-2f, 0f, -2f), Bottom + " 2"),
                (V(-2f, 0f, 2f), Bottom + " 3"), (V(2f, 0f, 2f), Bottom + " 4"),
                (V(2f, 2f, -2f), Top + " 1"), (V(-2f, 2f, -2f), Top + " 2")),

            // ── walls, doors, gates ──
            new VASnapRule("goblin_woodwall_1m").At(FaceCorners(0.5f, 0f, 2f)),
            new VASnapRule("goblin_woodwall_2m").At(FaceCorners(1f, 0f, 2f)),
            new VASnapRule("dvergrtown_secretdoor", "dvergrtown_slidingdoor").At(
                V(2f, 0f, 0f), V(-2f, 0f, 0f), V(2f, 4f, 0f), V(-2f, 4f, 0f)),
            new VASnapRule("dvergrprops_wood_wall").At(
                V(2.2f, 2f, 0f), V(-2.2f, 2f, 0f), V(2.2f, -2f, 0f), V(-2.2f, -2f, 0f)),
            new VASnapRule("dvergrtown_wood_wall01").Recentre("wallcollider").At(FaceCorners(3f, -2.7f, 2.7f)),
            new VASnapRule("dvergrtown_wood_wall02").At(FaceCorners(3f, -0.5f, 4.5f)),
            new VASnapRule("dvergrtown_wood_wall03").At(
                V(1.1f, 0f, 0f), V(-1.1f, 0f, 0f), V(2f, 2f, 0f), V(-2f, 2f, 0f), V(1.1f, 4f, 0f), V(-1.1f, 4f, 0f)),
            new VASnapRule("dvergrtown_arch").At(V(1f, 0.5f, 0f)),
            new VASnapRule("sunken_crypt_gate").At(V(1f, 0f, 0f), V(-1f, 0f, 0f)),
            new VASnapRule("dungeon_queen_door").At(V(2.5f, 0f, 0f), V(-2.5f, 0f, 0f)),
            new VASnapRule("dungeon_sunkencrypt_irongate").At(V(1f, -0.4f, 0f), V(-1f, -0.4f, 0f)),
            new VASnapRule("Ashlands_Fortress_Wall_Spikes").Labelled(
                (V(0f, 0f, -2f), "$hud_snappoint_edge 1"), (Vector3.zero, "$hud_snappoint_center"),
                (V(0f, 0f, 2f), "$hud_snappoint_edge 2")),
            new VASnapRule("Ashlands_Wall_2x2", "Ashlands_Wall_2x2_top", "Ashlands_Wall_2x2_edge2", "Ashlands_Wall_2x2_edge2_top")
                .Labelled(AshWallFaces(innerZ: -0.5f, outerZ: 0.5f, xFirst: -1f)),
            new VASnapRule("Ashlands_Wall_2x2_edge", "Ashlands_Wall_2x2_edge_top")
                .Labelled(AshWallFaces(innerZ: 0.5f, outerZ: -0.5f, xFirst: 1f)),
            new VASnapRule("Ashlands_Wall_2x2_cornerR", "Ashlands_Wall_2x2_cornerR_top").Labelled(AshWallCorner(
                V(-0.25f, 0f, -1f), V(-0.25f, 0f, 0f), V(0.75f, 0f, 0f),
                V(-1.25f, 0f, -1f), V(-1.25f, 0f, 1f), V(0.75f, 0f, 1f))),
            new VASnapRule("Ashlands_Wall_2x2_cornerL", "Ashlands_Wall_2x2_cornerL_top").Labelled(AshWallCorner(
                V(0f, 0f, -1.25f), V(0f, 0f, -0.5f), V(1.25f, 0f, -0.5f),
                V(-1f, 0f, -1.25f), V(-1f, 0f, 0.5f), V(1.25f, 0f, 0.5f))),

            // ── beams, poles, supports ──
            new VASnapRule("metalbar_1x2").At(V(0f, 0f, 0f), V(1f, -1f, 0f), V(1f, 0f, 0f), V(-1f, -1f, 0f), V(-1f, 0f, 0f)),
            new VASnapRule("dvergrtown_wood_beam", "dvergrprops_wood_beam").At(V(3f, 0f, 0f), V(-3f, 0f, 0f)),
            new VASnapRule("dvergrtown_wood_pole").At(V(0f, -2f, 0f), V(0f, 2f, 0f)),
            new VASnapRule("dvergrprops_wood_pole").At(V(0f, 2f, 0f), V(0f, -2f, 0f)),
            new VASnapRule("dvergrtown_wood_crane").At(V(0f, -3f, 0f)),
            new VASnapRule("dvergrtown_wood_support").At(V(-2f, 0f, 0f), V(2f, 0f, 0f)),
            new VASnapRule("dvergrtown_stair_corner_wood_left").At(
                V(0.25f, 0f, -0.25f), V(0.25f, 0f, 0.25f), V(0.25f, 1.1f, -0.25f), V(0.25f, 1.1f, 0.25f),
                V(0.25f, 0f, 2f), V(-0.25f, 1.1f, -0.25f), V(-2f, 1.1f, -0.25f)),

            // ── black marble ──
            new VASnapRule("blackmarble_head_big01", "blackmarble_head_big02").At(
                V(-1f, 1f, -1f), V(1f, 1f, 1f), V(-1f, 1f, 1f), V(1f, 1f, -1f), V(1f, -1f, -1f), V(-1f, -1f, -1f)),
            // A 2x2 column 8 m tall: its corners every 2 m, top to bottom.
            new VASnapRule("blackmarble_column_3")
                .At(Ring(1f, 4f, 1f)).At(Ring(1f, 2f, 1f)).At(Ring(1f, 0f, 1f)).At(Ring(1f, -2f, 1f)).At(Ring(1f, -4f, 1f)),
            new VASnapRule("blackmarble_creep_stair").At(
                V(-1f, 1f, -1f), V(1f, 1f, -1f), V(-1f, 0f, -1f), V(1f, 0f, -1f), V(-1f, 0f, 1f), V(1f, 0f, 1f)),
            new VASnapRule("blackmarble_creep_4x1x1").NoRandomRotation("new")
                .At(BoxCorners(0.5f, 2f, -2f, 0.5f)).At(V(0f, -2f, 0f), V(0f, 2f, 0f)),
            new VASnapRule("blackmarble_creep_4x2x1").NoRandomRotation("new").At(
                V(1f, 2f, -0.5f), V(1f, 2f, 0.5f), V(-1f, 2f, -0.5f), V(-1f, 2f, 0.5f),
                V(1f, -2f, -0.5f), V(1f, -2f, 0.5f), V(-1f, -2f, -0.5f), V(-1f, -2f, 0.5f),
                V(0f, -2f, 0f), V(0f, 2f, 0f)),
            new VASnapRule("blackmarble_creep_slope_inverted_1x1x2").At(
                V(-0.5f, 1f, -0.5f), V(0.5f, 1f, 0.5f), V(-0.5f, 1f, 0.5f), V(0.5f, 1f, -0.5f),
                V(0.5f, -1f, -0.5f), V(-0.5f, -1f, -0.5f)),
            new VASnapRule("blackmarble_creep_slope_inverted_2x2x1").At(
                V(-1f, 0.5f, -1f), V(1f, 0.5f, 1f), V(-1f, 0.5f, 1f), V(1f, 0.5f, -1f),
                V(-1f, -0.5f, -1f), V(1f, -0.5f, -1f)),

            // The tile pieces carry eight (walls) or four (floors) snap points already, in the wrong places: move them.
            new VASnapRule("blackmarble_tile_wall_1x1")
                .Move("_snappoint", V(0.5f, -0.05f, 0.1f)).Move("_snappoint (1)", V(0.5f, 0.95f, 0.1f))
                .Move("_snappoint (2)", V(-0.5f, -0.05f, 0.1f)).Move("_snappoint (3)", V(-0.5f, 0.95f, 0.1f))
                .Move("_snappoint (4)", V(0f, -0.05f, 0.1f)).Move("_snappoint (5)", V(0.5f, 0.45f, 0.1f))
                .Move("_snappoint (6)", V(0f, 0.95f, 0.1f)).Move("_snappoint (7)", V(-0.5f, 0.45f, 0.1f)),
            new VASnapRule("blackmarble_tile_wall_2x2")
                .Move("_snappoint", V(1f, -0.05f, 0.1f)).Move("_snappoint (1)", V(1f, 1.95f, 0.1f))
                .Move("_snappoint (2)", V(-1f, -0.05f, 0.1f)).Move("_snappoint (3)", V(-1f, 1.95f, 0.1f))
                .Move("_snappoint (4)", V(0.5f, -0.05f, 0.1f)).Move("_snappoint (5)", V(0.5f, 0.95f, 0.1f))
                .Move("_snappoint (6)", V(-0.5f, -0.05f, 0.1f)).Move("_snappoint (7)", V(-0.5f, 0.95f, 0.1f)),
            new VASnapRule("blackmarble_tile_wall_2x4")
                .Move("_snappoint", V(1f, -0.05f, 0.1f)).Move("_snappoint (1)", V(1f, 3.95f, 0.1f))
                .Move("_snappoint (2)", V(-1f, -0.05f, 0.1f)).Move("_snappoint (3)", V(-1f, 3.95f, 0.1f))
                .Move("_snappoint (4)", V(0.5f, -0.05f, 0.1f)).Move("_snappoint (5)", V(0.5f, 1.95f, 0.1f))
                .Move("_snappoint (6)", V(-0.5f, -0.05f, 0.1f)).Move("_snappoint (7)", V(-0.5f, 1.95f, 0.1f)),
            new VASnapRule("blackmarble_tile_floor_1x1")
                .Move("_snappoint", V(0.5f, 0.1f, 0.5f)).Move("_snappoint (1)", V(0.5f, 0.1f, -0.5f))
                .Move("_snappoint (2)", V(-0.5f, 0.1f, 0.5f)).Move("_snappoint (3)", V(-0.5f, 0.1f, -0.5f)),
            new VASnapRule("blackmarble_tile_floor_2x2")
                .Move("_snappoint", V(1f, 0.1f, 1f)).Move("_snappoint (1)", V(1f, 0.1f, -1f))
                .Move("_snappoint (2)", V(-1f, 0.1f, 1f)).Move("_snappoint (3)", V(-1f, 0.1f, -1f)),

            // ── treasure chests: the lid needs clearance, and the colliders must be on the piece layer ──
            new VASnapRule("TreasureChest_dvergrtown", "TreasureChest_dvergrtower").OnPieceLayer().At(DvergrChestPoints),
            new VASnapRule("TreasureChest_fCrypt", "TreasureChest_sunkencrypt", "TreasureChest_plains_stone")
                .OnPieceLayer().At(StoneChestPoints),
            new VASnapRule("TreasureChest_mountaincave", "TreasureChest_trollcave").OnPieceLayer().MeshCorners("stonechest"),
        };
    }
}
