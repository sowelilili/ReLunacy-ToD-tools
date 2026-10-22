using System.Numerics;
using ReLunacy.Engine.Assets.Collision;
using ReLunacy.Engine.Rendering.Resources;
using ReLunacy.Engine.Rendering.Vulkan;

namespace ReLunacy.Engine.Rendering;

/// <summary>
/// Turns <see cref="TodCollision"/> meshes into scene geometry for the Render menu's collision toggles.
/// Each mesh is registered once in <see cref="VulkanSceneCapture"/> and drawn per instance with one
/// shared, always-unlit material (<see cref="VkMaterialDesc.IsCollision"/>). Triangles are unshared so
/// each face keeps its own flat shade, coloured by its collision material.
/// </summary>
public static class CollisionGeometry
{
    // Mostly overhead, so floors read brightest and walls darken by the way they face. The file's
    // winding is not consistent (cobalia's world floors face up and down about equally), so the
    // shade uses |N.L| and both sides of a face look the same.
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
                var colour = new Vector4(rgb, 1f);
                for (int k = 0; k < 3; k++)
                {
                    int v = t * 3 + k;
                    vertices[v] = new Vertex3D(k == 0 ? a : k == 1 ? b : c, Vector2.Zero, Vector2.Zero, n, new Vector4(1f, 0f, 0f, 1f), colour);
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

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        float r = Math.Clamp(MathF.Abs(h * 6f - 3f) - 1f, 0f, 1f);
        float g = Math.Clamp(2f - MathF.Abs(h * 6f - 2f), 0f, 1f);
        float b = Math.Clamp(2f - MathF.Abs(h * 6f - 4f), 0f, 1f);
        return v * Vector3.Lerp(Vector3.One, new Vector3(r, g, b), s);
    }
}
