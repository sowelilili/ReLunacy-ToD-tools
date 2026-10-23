using System.Buffers.Binary;
using System.Numerics;

namespace ReLunacy.Engine.Assets.Collision;

/// <summary>
/// Tools of Destruction's level collision, the level's own collision.dat (a raw big-endian blob, not
/// an IGHW container). Loaded by the EBOOT at "/app_home/%s/collision.dat"; 0x004C2970 warns "Packed
/// level has no collision data" when the header's instance count is 0, and 0x004E0970 relocates it.
/// See docs/reverse/tod_collision.md for the full layout and how it was checked.
///
/// The file is a list of instances (0x80 bytes each) that place meshes stored once in a shared blob.
/// TIE collision is one or more instances per TIE placement, carrying that placement's exact matrix;
/// the rest of the level (terrain, shells) is split into world-space chunks with an identity matrix.
/// </summary>
public sealed class TodCollision
{
    public IReadOnlyList<CollisionInstance> Instances { get; }
    public IReadOnlyList<CollisionMesh> Meshes { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    /// <summary>SHA-256 of the whole collision.dat, hex. Keys anything derived from it that is cached
    /// on disk (see WallbounceAnalysis).</summary>
    public string ContentHash { get; }

    private TodCollision(IReadOnlyList<CollisionInstance> instances, IReadOnlyList<CollisionMesh> meshes, Vector3 boundsMin, Vector3 boundsMax, string contentHash)
    {
        Instances = instances;
        Meshes = meshes;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        ContentHash = contentHash;
    }

    // Header (0x80 bytes). 0x004E0970 reads +0x00..+0x58; the grid at +0x30 and the tables at
    // +0x10..+0x2E are runtime query structures the renderer does not need.
    private const int HeaderSize = 0x80;
    private const int InstanceStride = 0x80;

    /// <summary>Instance +0x70 bit 29: the instance belongs to a TIE placement (its matrix is the
    /// placement's, matched exactly on every TIE of cobalia, metropolis, fastoon and sargasso).</summary>
    private const uint TieInstanceFlag = 0x20000000;

    /// <summary>Mesh vertices are 16-bit offsets from a 32-bit origin, both in 1/1024 world units.</summary>
    private const float VertexScale = 1f / 1024f;

    public static TodCollision? Read(byte[] data)
    {
        if (data.Length < HeaderSize) return null;
        ReadOnlySpan<byte> d = data;

        uint instanceOffset = U32(d, 0x00);
        uint instanceCount = U32(d, 0x04);
        uint meshBase = U32(d, 0x08);
        uint meshBlobLength = U32(d, 0x0C);
        if (instanceCount == 0) return null;
        if (instanceOffset + (ulong)instanceCount * InstanceStride > (ulong)data.Length ||
            meshBase + (ulong)meshBlobLength > (ulong)data.Length)
        {
            Console.WriteLine($"collision.dat: header points past the end of the file (0x{data.Length:X} bytes); skipped.");
            return null;
        }

        var meshes = new List<CollisionMesh>();
        var meshAt = new Dictionary<uint, CollisionMesh>();
        var instances = new List<CollisionInstance>((int)instanceCount);
        int skipped = 0;
        for (int i = 0; i < instanceCount; i++)
        {
            int o = checked((int)(instanceOffset + i * InstanceStride));
            var transform = new Matrix4x4(
                F32(d, o + 0x00), F32(d, o + 0x04), F32(d, o + 0x08), F32(d, o + 0x0C),
                F32(d, o + 0x10), F32(d, o + 0x14), F32(d, o + 0x18), F32(d, o + 0x1C),
                F32(d, o + 0x20), F32(d, o + 0x24), F32(d, o + 0x28), F32(d, o + 0x2C),
                F32(d, o + 0x30), F32(d, o + 0x34), F32(d, o + 0x38), F32(d, o + 0x3C));
            var sphere = new Vector4(F32(d, o + 0x40), F32(d, o + 0x44), F32(d, o + 0x48), F32(d, o + 0x4C));
            var boundsMin = new Vector3(F32(d, o + 0x50), F32(d, o + 0x54), F32(d, o + 0x58));
            var boundsMax = new Vector3(F32(d, o + 0x60), F32(d, o + 0x64), F32(d, o + 0x68));
            uint flags = U32(d, o + 0x70);
            // +0x74 is relative to the mesh blob (0x004E0970 adds header +0x08 to it); the low half of
            // +0x78 is the mesh's padded byte length.
            uint meshOffset = U32(d, o + 0x74);
            uint meshLength = U32(d, o + 0x78) & 0xFFFF;

            if (!meshAt.TryGetValue(meshOffset, out var mesh))
            {
                mesh = meshOffset < meshBlobLength ? CollisionMesh.Read(d, checked((int)(meshBase + meshOffset)), meshLength) : null;
                if (mesh == null)
                {
                    skipped++;
                    continue;
                }
                meshAt[meshOffset] = mesh;
                meshes.Add(mesh);
            }

            instances.Add(new CollisionInstance(transform, sphere, boundsMin, boundsMax, (flags & TieInstanceFlag) != 0, flags & 0x0FFFFFFF, mesh));
        }

        if (skipped != 0)
            Console.WriteLine($"collision.dat: {skipped} instance(s) point at a mesh that does not parse; skipped.");

        return new TodCollision(instances, meshes,
            new Vector3(F32(d, 0x40), F32(d, 0x44), F32(d, 0x48)),
            new Vector3(F32(d, 0x50), F32(d, 0x54), F32(d, 0x58)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
    }

    internal static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d[o..]);
    internal static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d[o..]);
    internal static int S32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32BigEndian(d[o..]);
    internal static float F32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleBigEndian(d[o..]);

    /// <summary>Vertex position from the mesh's origin and one quantized component triple.</summary>
    internal static Vector3 Dequantize(int ox, int oy, int oz, ushort x, ushort y, ushort z) =>
        new((ox + x) * VertexScale, (oy + y) * VertexScale, (oz + z) * VertexScale);
}

/// <summary>One placement of a collision mesh (collision.dat instance, 0x80 bytes).</summary>
public sealed class CollisionInstance(Matrix4x4 transform, Vector4 boundingSphere, Vector3 boundsMin, Vector3 boundsMax,
    bool isTie, uint id, CollisionMesh mesh)
{
    /// <summary>Mesh to world, row-vector convention like every other placement (translation in M41..M43).
    /// Identity on world collision.</summary>
    public Matrix4x4 Transform { get; } = transform;
    /// <summary>World-space bounding sphere (+0x40).</summary>
    public Vector4 BoundingSphere { get; } = boundingSphere;
    /// <summary>World-space box (+0x50 / +0x60). The game trims a side now and then (about 2% of
    /// instances are one face short of their transformed mesh), so it is not a tight bound.</summary>
    public Vector3 BoundsMin { get; } = boundsMin;
    public Vector3 BoundsMax { get; } = boundsMax;
    /// <summary>True for a TIE placement's collision, false for world (terrain and shell) chunks.</summary>
    public bool IsTie { get; } = isTie;
    /// <summary>Low 28 bits of +0x70. Counts up across the chunks of one TIE class; meaning not closed.</summary>
    public uint Id { get; } = id;
    public CollisionMesh Mesh { get; } = mesh;
    /// <summary>Per triangle of <see cref="Mesh"/>, placed here: the tilt from vertical, in whole degrees,
    /// of a triangle that can give a glitched wallbounce (see WallbounceAnalysis); 0 for one that cannot.
    /// Null until the analysis has run, or when no triangle of this placement passes.</summary>
    public byte[]? WallbounceTilt { get; set; }
}

/// <summary>
/// One collision mesh from the blob. Header (all big-endian u16): +0x00 vertex count, +0x02 vertex
/// block offset (0x60 on every retail mesh), +0x06 triangle count, +0x08 triangle offset, +0x0C
/// material offset. +0x14..+0x50 is an oriented box (three axis + half-extent vec4s, then the centre).
/// The vertex block is an s32 x/y/z origin, 4 bytes of padding, then groups of 8 vertices stored
/// x[8], y[8], z[8] as u16, each component line in the order 0,4,1,5,2,6,3,7. A triangle is one u32: indices in bits 0-8, 9-17 and 18-26, flags in
/// 27-31. Each triangle has one material byte.
/// </summary>
public sealed class CollisionMesh
{
    public Vector3[] Positions { get; }
    /// <summary>Three vertex indices per triangle, in the file's winding.</summary>
    public int[] Indices { get; }
    /// <summary>Collision material of each triangle (what the game reads as the surface type).</summary>
    public byte[] Materials { get; }
    /// <summary>Bits 27-31 of each triangle word. Not closed; kept for inspection.</summary>
    public byte[] TriangleFlags { get; }

    public int TriangleCount => Materials.Length;

    private CollisionMesh(Vector3[] positions, int[] indices, byte[] materials, byte[] triangleFlags)
    {
        Positions = positions;
        Indices = indices;
        Materials = materials;
        TriangleFlags = triangleFlags;
    }

    internal static CollisionMesh? Read(ReadOnlySpan<byte> d, int o, uint length)
    {
        if (o < 0 || o + 0x10 > d.Length) return null;
        int vertexCount = TodCollision.U16(d, o + 0x00);
        int vertexOffset = TodCollision.U16(d, o + 0x02);
        int triangleCount = TodCollision.U16(d, o + 0x06);
        int triangleOffset = TodCollision.U16(d, o + 0x08);
        int materialOffset = TodCollision.U16(d, o + 0x0C);
        int groups = (vertexCount + 7) / 8;
        int end = Math.Max(Math.Max(vertexOffset + 0x10 + groups * 0x30, triangleOffset + triangleCount * 4), materialOffset + triangleCount);
        if (vertexCount == 0 || triangleCount == 0 || o + end > d.Length || (length != 0 && end > length))
            return null;

        int ox = TodCollision.S32(d, o + vertexOffset);
        int oy = TodCollision.S32(d, o + vertexOffset + 4);
        int oz = TodCollision.S32(d, o + vertexOffset + 8);
        var positions = new Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            // Within a group, vertices 0-3 sit in the even u16 slots and 4-7 in the odd ones (each u32
            // pairs vertex k with k + 4). Read in plain order instead, flat collision quads come out
            // non-planar: 46% planar on metropolis against 96.5% this way.
            int k = v & 7;
            int slot = k < 4 ? k * 2 : (k - 4) * 2 + 1;
            int g = o + vertexOffset + 0x10 + (v >> 3) * 0x30 + slot * 2;
            positions[v] = TodCollision.Dequantize(ox, oy, oz,
                TodCollision.U16(d, g), TodCollision.U16(d, g + 0x10), TodCollision.U16(d, g + 0x20));
        }

        var indices = new int[triangleCount * 3];
        var flags = new byte[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            uint word = TodCollision.U32(d, o + triangleOffset + t * 4);
            int a = (int)((word >> 18) & 0x1FF), b = (int)((word >> 9) & 0x1FF), c = (int)(word & 0x1FF);
            if (a >= vertexCount || b >= vertexCount || c >= vertexCount) return null;
            indices[t * 3 + 0] = a;
            indices[t * 3 + 1] = b;
            indices[t * 3 + 2] = c;
            flags[t] = (byte)(word >> 27);
        }

        byte[] materials = d.Slice(o + materialOffset, triangleCount).ToArray();
        return new CollisionMesh(positions, indices, materials, flags);
    }
}
