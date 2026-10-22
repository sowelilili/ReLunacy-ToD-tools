using ReLunacy.Engine.Assets.Collision;
using ReLunacy.Engine.Loading.IO;

namespace ReLunacy.Engine.Loading.Readers;

/// <summary>Reads Tools of Destruction's collision.dat (see <see cref="TodCollision"/>). Old engine only;
/// a level without the file has no collision to show.</summary>
public sealed class CollisionReader
{
    public const string FileName = "collision.dat";

    private readonly FileManager _fileManager;

    public CollisionReader(FileManager fileManager)
    {
        _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
    }

    public TodCollision? Read()
    {
        if (!_fileManager.isOld) return null;
        if (!_fileManager.rawfiles.TryGetValue(FileName, out Stream? stream) || stream is null) return null;

        try
        {
            stream.Position = 0;
            using var buffer = new MemoryStream(checked((int)stream.Length));
            stream.CopyTo(buffer);
            var collision = TodCollision.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length).ToArray());
            if (collision != null)
            {
                int tieInstances = collision.Instances.Count(i => i.IsTie);
                Console.WriteLine(
                    $"Collision: {collision.Instances.Count} instance(s) ({tieInstances} TIE, {collision.Instances.Count - tieInstances} world), " +
                    $"{collision.Meshes.Count} mesh(es), {collision.Meshes.Sum(m => m.TriangleCount)} triangle(s).");
            }
            return collision;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Collision: {FileName} could not be read ({e.Message}); skipped.");
            return null;
        }
    }
}
