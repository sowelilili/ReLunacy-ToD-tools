using System.Numerics;
using ReLunacy.Engine.Assets.Collision;
using ReLunacy.Engine.Rendering.Resources;
using ReLunacy.Engine.Rendering.Vulkan;

namespace ReLunacy.Engine.Rendering;

/// <summary>What the collision colour shows (Render menu). Reaches the shader as LightData.PostParams.z.</summary>
public enum CollisionColourMode
{
    /// <summary>One hue per material byte.</summary>
    Material = 0,
    /// <summary>The triangle's flag bits: see <see cref="CollisionGeometry.FlagColour"/>.</summary>
    Flags = 1,
    /// <summary>Flag bit 0 on a face within 1 degree of level in world space (Y up) in orange, the rest
    /// dim grey. Decided per pixel in the lit shader, so rotated TIE placements are judged correctly.</summary>
    WrenchBoost = 2,
    /// <summary>Triangles that can give a glitched wallbounce (Assets.Collision.WallbounceAnalysis), yellow to
    /// red by tilt from vertical, at or above a minimum tilt; the rest dim grey. Needs the analysis results
    /// (AssetManager.EnsureWallbounceAnalysis); until then everything is grey.</summary>
    WallbounceSpots = 3,
}

/// <summary>
/// Turns <see cref="TodCollision"/> meshes into scene geometry for the Render menu's collision toggles.
/// Each mesh is registered once in <see cref="VulkanSceneCapture"/> and drawn per instance with one
/// shared, always-unlit material (<see cref="VkMaterialDesc.IsCollision"/>). Triangles are unshared so
/// each face keeps its own flat shade, coloured by its collision material.
///
/// For the Render menu's triangle outlines, each corner carries its barycentric coordinate in the
/// lightmap UV (collision has no lightmap) and the vertex alpha is <see cref="OutlineMarkerAlpha"/>,
/// which the lit shader reads as "this is collision". Game vertex colours never go above 1. The
/// texture UV (collision is untextured) carries the triangle's flag bits and face shade, so the
/// shader can recolour by flags (<see cref="CollisionColourMode"/>) without rebuilding anything.
/// </summary>
public static class CollisionGeometry
{
    /// <summary>Vertex alpha that marks collision vertices for the shader's outline pass (LitShader,
    /// "Collision outlines"). Keep the two in step.</summary>
    public const float OutlineMarkerAlpha = 2f;

    private static readonly Vector2[] CornerBarycentrics = [new(1f, 0f), new(0f, 1f), new(0f, 0f)];

    // Mostly overhead, so floors read brightest and walls darken by the way they face. The shade uses
    // |N.L| so a face looks the same from either side: collision is often seen from behind (TIE shells,
    // the camera inside a mesh), and the outward side is not the only one worth reading.
    private static readonly Vector3 ShadeDirection = Vector3.Normalize(new Vector3(0.35f, 1f, 0.55f));

    public static void Register(TodCollision collision)
    {
        foreach (var mesh in collision.Meshes)
        {
            if (VulkanSceneCapture.TryGet(mesh, out _)) continue;
            var vertices = new Vertex3D[mesh.Indices.Length];
            var indices = new uint[mesh.Indices.Length];
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                Vector3 a = mesh.Positions[mesh.Indices[t * 3]];
                Vector3 b = mesh.Positions[mesh.Indices[t * 3 + 1]];
                Vector3 c = mesh.Positions[mesh.Indices[t * 3 + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitY;
                float shade = 0.45f + 0.55f * MathF.Abs(Vector3.Dot(n, ShadeDirection));
                Vector3 rgb = MaterialColour(mesh.Materials[t]) * shade;
                var colour = new Vector4(rgb, OutlineMarkerAlpha);
                // The shader's other colour modes rebuild the colour from these (CollisionColourMode).
                var flagsAndShade = new Vector2(mesh.TriangleFlags[t], shade);
                for (int k = 0; k < 3; k++)
                {
                    int v = t * 3 + k;
                    vertices[v] = new Vertex3D(k == 0 ? a : k == 1 ? b : c, flagsAndShade, CornerBarycentrics[k], n, new Vector4(1f, 0f, 0f, 1f), colour);
                    indices[v] = (uint)v;
                }
            }
            VulkanSceneCapture.Register(mesh, VulkanSceneCapture.Interleave(vertices), indices);
        }
    }

    /// <summary>The one material every collision draw uses: opaque, untextured, double-sided.</summary>
    public static VkMaterialDesc Material => new()
    {
        IsCollision = true,
        GameRenderMode = (float)GameRenderMode.Opaque,
        MaterialStrengths = Vector2.One,
    };

    /// <summary>A stable colour per collision material: hues spread by the golden ratio, so material ids
    /// that differ by one still look different.</summary>
    public static Vector3 MaterialColour(byte material)
    {
        float hue = (material * 0.618034f) % 1f;
        return HsvToRgb(hue, 0.6f, 0.95f);
    }

    /// <summary>Colour of a triangle's flag bits (bits 27-31 of its word) in <see cref="CollisionColourMode.Flags"/>:
    /// bit 0 red, bit 1 green, bit 2 blue, mixed when several are set; no flags dark grey. Mirrors the
    /// lit shader's collisionFlagColour.</summary>
    public static Vector3 FlagColour(int flags) => flags == 0
        ? new Vector3(0.3f)
        : new Vector3((flags & 1) != 0 ? 1f : 0.15f, (flags & 2) != 0 ? 1f : 0.15f, (flags & 4) != 0 ? 1f : 0.15f);

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        float r = Math.Clamp(MathF.Abs(h * 6f - 3f) - 1f, 0f, 1f);
        float g = Math.Clamp(2f - MathF.Abs(h * 6f - 2f), 0f, 1f);
        float b = Math.Clamp(2f - MathF.Abs(h * 6f - 4f), 0f, 1f);
        return v * Vector3.Lerp(Vector3.One, new Vector3(r, g, b), s);
    }
}
