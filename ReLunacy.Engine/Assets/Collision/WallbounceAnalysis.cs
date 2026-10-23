using System.Buffers.Binary;
using System.Numerics;

namespace ReLunacy.Engine.Assets.Collision;

/// <summary>
/// Finds the collision triangles that can give a glitched wallbounce (docs/reverse/tod_wallbounce.md).
///
/// The wall-jump push-off runs along the normal of whatever surface the wall probe hit. When that normal
/// tilts upward by more than about 6 degrees, the jump's upward speed feeds back into the push-off speed
/// faster than the push-off can bleed it down to 6.0, and Ratchet is launched. So a triangle counts when
/// it is static collision with flag bit 0 clear, tilted upward past that threshold, and a Ratchet placed
/// against it would pass the entry check (Hero_TryEnterWallJumpState, 0x0020822C):
///   - the probe (from 1.0 above the feet, swept along the facing) reaches this triangle first,
///   - the column from the feet to the head line is open,
///   - the head line (from 2.75 above the feet, 1.5 along the facing) hits a surface with bit 0 clear.
/// Ratchet is tried at four points on the triangle and two stand-off distances, facing into it.
///
/// Results are cached on disk keyed by the collision file's hash, so each level is computed once.
/// </summary>
public static class WallbounceAnalysis
{
    /// <summary>Bumped whenever the checks below change, so stale caches are ignored.</summary>
    public const int Version = 1;

    private const float ProbeHeight = 1.0f;     // hero+0x1450, the probe's origin above the feet
    private const float HeadHeight = 2.75f;     // TOC 0x8AB078 + 0x5B18
    private const float HeadReach = 1.5f;       // TOC 0x8AB078 + 0x5B1C
    private const float ProbeReach = 2.0f;      // the probe sweep (~1.7) plus its 0.3 radius
    private const float JumpUpSpeed = 9.49f;    // the jump's vertical speed, measured live
    private const float CellSize = 3f;

    /// <summary>The smallest tilt from vertical that boosts: the up-speed fed back per frame
    /// (JumpUpSpeed * n.y) must beat the push-off's 1 per frame.</summary>
    public static readonly float BoostThresholdDegrees = MathF.Asin(1f / JumpUpSpeed) * 180f / MathF.PI;

    private static readonly float[] StandOffs = [0.35f, 0.55f];
    private static readonly (float U, float V)[] Samples = [(1f / 3f, 1f / 3f), (0.6f, 0.2f), (0.2f, 0.6f), (0.2f, 0.2f)];

    /// <summary>Per instance, per triangle: tilt from vertical in whole degrees for a triangle that
    /// passes, 0 for one that does not. An instance with no passing triangle gets null.</summary>
    public static byte[]?[] Run(TodCollision collision, Action<float>? progress = null, CancellationToken cancel = default)
    {
        var instances = collision.Instances;
        var start = new int[instances.Count + 1];
        for (int i = 0; i < instances.Count; i++)
            start[i + 1] = start[i] + instances[i].Mesh.TriangleCount;
        int total = start[^1];

        // Every triangle in world space. Winding is inconsistent in these files, so the normal is
        // taken on its upward side.
        var a = new Vector3[total];
        var e1 = new Vector3[total];
        var e2 = new Vector3[total];
        var normal = new Vector3[total];
        var flags = new byte[total];
        var lo = new Vector3[total];
        var hi = new Vector3[total];
        Parallel.For(0, instances.Count, i =>
        {
            var inst = instances[i];
            var mesh = inst.Mesh;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                int k = start[i] + t;
                Vector3 p0 = Vector3.Transform(mesh.Positions[mesh.Indices[t * 3]], inst.Transform);
                Vector3 p1 = Vector3.Transform(mesh.Positions[mesh.Indices[t * 3 + 1]], inst.Transform);
                Vector3 p2 = Vector3.Transform(mesh.Positions[mesh.Indices[t * 3 + 2]], inst.Transform);
                a[k] = p0; e1[k] = p1 - p0; e2[k] = p2 - p0;
                Vector3 n = Vector3.Cross(e1[k], e2[k]);
                float len = n.Length();
                n = len > 0f ? n / len : Vector3.Zero;
                normal[k] = n.Y < 0f ? -n : n;
                flags[k] = mesh.TriangleFlags[t];
                lo[k] = Vector3.Min(p0, Vector3.Min(p1, p2));
                hi[k] = Vector3.Max(p0, Vector3.Max(p1, p2));
            }
        });

        var grid = new Grid(lo, hi);
        cancel.ThrowIfCancellationRequested();

        float nyMin = 1f / JumpUpSpeed;
        var candidates = new List<int>();
        for (int k = 0; k < total; k++)
            if ((flags[k] & 1) == 0 && normal[k].Y > nyMin && normal[k].Y < 0.995f)
                candidates.Add(k);

        var result = new byte[total];
        int done = 0;
        // All cores but one, so the editor stays responsive while this runs.
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.For(0, candidates.Count, options,
            () => new RayQuery(grid, a, e1, e2, total),
            (ci, _, ray) =>
            {
                int k = candidates[ci];
                if (Passes(k, ray, a, e1, e2, normal, flags))
                    result[k] = (byte)Math.Clamp((int)MathF.Round(MathF.Asin(normal[k].Y) * 180f / MathF.PI), 1, 90);
                int d = Interlocked.Increment(ref done);
                if ((d & 1023) == 0) progress?.Invoke((float)d / candidates.Count);
                return ray;
            },
            _ => { });

        var perInstance = new byte[]?[instances.Count];
        for (int i = 0; i < instances.Count; i++)
        {
            int count = start[i + 1] - start[i];
            if (Array.FindIndex(result, start[i], count, b => b != 0) < 0)
                continue;
            perInstance[i] = result.AsSpan(start[i], count).ToArray();
        }
        progress?.Invoke(1f);
        return perInstance;
    }

    private static bool Passes(int k, RayQuery ray, Vector3[] a, Vector3[] e1, Vector3[] e2, Vector3[] normal, byte[] flags)
    {
        Vector3 n = normal[k];
        var facing = Vector3.Normalize(new Vector3(-n.X, 0f, -n.Z));
        foreach (var (u, v) in Samples)
        {
            Vector3 hit = a[k] + e1[k] * u + e2[k] * v;
            foreach (float standOff in StandOffs)
            {
                Vector3 probe = hit + n * standOff;
                Vector3 feet = probe - Vector3.UnitY * ProbeHeight;
                Vector3 head = feet + Vector3.UnitY * HeadHeight;

                // The probe, swept along the facing, must reach this surface (or one in its plane) first.
                int first = ray.Nearest(probe - facing * 0.2f, probe + facing * ProbeReach);
                if (first < 0 || Vector3.Dot(normal[first], n) < 0.999f) continue;
                // Ratchet's body must be in open air.
                if (ray.Nearest(feet, head) >= 0) continue;
                int wall = ray.Nearest(head, head + facing * HeadReach);
                if (wall >= 0 && (flags[wall] & 1) == 0) return true;
            }
        }
        return false;
    }

    /// <summary>Uniform grid over triangle bounding boxes, stored as sorted cell keys.</summary>
    private sealed class Grid
    {
        public readonly long[] Keys;
        public readonly int[] Triangles;

        public Grid(Vector3[] lo, Vector3[] hi)
        {
            int n = lo.Length;
            var counts = new long[n + 1];
            for (int k = 0; k < n; k++)
            {
                var (c0, c1) = (Cell(lo[k]), Cell(hi[k]));
                counts[k + 1] = counts[k] + (long)(c1.X - c0.X + 1) * (c1.Y - c0.Y + 1) * (c1.Z - c0.Z + 1);
            }
            Keys = new long[counts[n]];
            Triangles = new int[counts[n]];
            Parallel.For(0, n, k =>
            {
                var (c0, c1) = (Cell(lo[k]), Cell(hi[k]));
                long o = counts[k];
                for (int x = c0.X; x <= c1.X; x++)
                    for (int y = c0.Y; y <= c1.Y; y++)
                        for (int z = c0.Z; z <= c1.Z; z++)
                        {
                            Keys[o] = Key(x, y, z);
                            Triangles[o++] = k;
                        }
            });
            Array.Sort(Keys, Triangles);
        }

        public static (int X, int Y, int Z) Cell(Vector3 p) =>
            ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize), (int)MathF.Floor(p.Z / CellSize));

        public static long Key(int x, int y, int z) =>
            ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));

        public int LowerBound(long key)
        {
            int l = 0, r = Keys.Length;
            while (l < r)
            {
                int m = (l + r) >>> 1;
                if (Keys[m] < key) l = m + 1; else r = m;
            }
            return l;
        }
    }

    /// <summary>Segment queries against the grid. One per worker thread: the visit stamps are scratch.</summary>
    private sealed class RayQuery(Grid grid, Vector3[] a, Vector3[] e1, Vector3[] e2, int triangleCount)
    {
        private readonly int[] _stamp = new int[triangleCount];
        private int _visit;

        /// <summary>The nearest triangle the segment p0-p1 crosses, or -1.</summary>
        public int Nearest(Vector3 p0, Vector3 p1)
        {
            if (++_visit == int.MaxValue) { Array.Clear(_stamp); _visit = 1; }
            Vector3 d = p1 - p0;
            var (c0, c1) = (Grid.Cell(Vector3.Min(p0, p1)), Grid.Cell(Vector3.Max(p0, p1)));
            int best = -1;
            float bestT = float.MaxValue;
            for (int x = c0.X; x <= c1.X; x++)
                for (int y = c0.Y; y <= c1.Y; y++)
                    for (int z = c0.Z; z <= c1.Z; z++)
                    {
                        long key = Grid.Key(x, y, z);
                        for (int i = grid.LowerBound(key); i < grid.Keys.Length && grid.Keys[i] == key; i++)
                        {
                            int k = grid.Triangles[i];
                            if (_stamp[k] == _visit) continue;
                            _stamp[k] = _visit;
                            // Moller-Trumbore, limited to the segment.
                            Vector3 h = Vector3.Cross(d, e2[k]);
                            float det = Vector3.Dot(e1[k], h);
                            if (MathF.Abs(det) < 1e-12f) continue;
                            float inv = 1f / det;
                            Vector3 s = p0 - a[k];
                            float u = inv * Vector3.Dot(s, h);
                            if (u < 0f || u > 1f) continue;
                            Vector3 q = Vector3.Cross(s, e1[k]);
                            float v = inv * Vector3.Dot(d, q);
                            if (v < 0f || u + v > 1f) continue;
                            float t = inv * Vector3.Dot(e2[k], q);
                            if (t < 0f || t > 1f || t >= bestT) continue;
                            bestT = t; best = k;
                        }
                    }
            return best;
        }
    }

    // ---- Disk cache ----

    private static ReadOnlySpan<byte> Magic => "RLWB"u8;

    public static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReLunacy", "Cache");

    public static string CachePath(TodCollision collision) =>
        Path.Combine(CacheDirectory, $"wallbounce-v{Version}-{collision.ContentHash[..32]}.bin");

    /// <summary>Layout: "RLWB", u32 version, u32 instance count, then per instance a u32 triangle count
    /// (0 when none passes) followed by that many tilt bytes. Little-endian.</summary>
    public static void Save(TodCollision collision, byte[]?[] perInstance)
    {
        Directory.CreateDirectory(CacheDirectory);
        using var ms = new MemoryStream();
        ms.Write(Magic);
        var u32 = new byte[4];
        void W(int v) { BinaryPrimitives.WriteInt32LittleEndian(u32, v); ms.Write(u32); }
        W(Version);
        W(perInstance.Length);
        foreach (var tilts in perInstance)
        {
            W(tilts?.Length ?? 0);
            if (tilts != null) ms.Write(tilts);
        }
        string path = CachePath(collision);
        File.WriteAllBytes(path + ".tmp", ms.ToArray());
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static byte[]?[]? TryLoad(TodCollision collision)
    {
        string path = CachePath(collision);
        if (!File.Exists(path)) return null;
        try
        {
            ReadOnlySpan<byte> d = File.ReadAllBytes(path);
            if (d.Length < 12 || !d[..4].SequenceEqual(Magic)) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(d[4..]) != Version) return null;
            int count = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
            if (count != collision.Instances.Count) return null;
            var perInstance = new byte[]?[count];
            int o = 12;
            for (int i = 0; i < count; i++)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d[o..]); o += 4;
                if (n == 0) continue;
                if (n != collision.Instances[i].Mesh.TriangleCount) return null;
                perInstance[i] = d.Slice(o, n).ToArray(); o += n;
            }
            return perInstance;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Wallbounce cache {path} could not be read ({e.Message}); recomputing.");
            return null;
        }
    }

    /// <summary>Hands each instance its results (see <see cref="CollisionInstance.WallbounceTilt"/>).</summary>
    public static void Apply(TodCollision collision, byte[]?[] perInstance)
    {
        for (int i = 0; i < collision.Instances.Count; i++)
            collision.Instances[i].WallbounceTilt = perInstance[i];
    }
}
