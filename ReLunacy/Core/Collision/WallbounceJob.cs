using ReLunacy.Engine.Rendering;
using ReLunacy.Engine.Rendering.Vulkan;
using ReLunacy.Utility.Localization;

namespace ReLunacy.Core.Collision;

/// <summary>
/// Runs <see cref="CollisionWallbounce"/> for the collision scene on display, on a worker thread (or from its
/// disk cache), the first time <see cref="CollisionColorMode.Wallbounce"/> is on for that scene, and hands the
/// result to the renderer once it is in. View3D calls <see cref="Update"/> every frame the mode is on; the
/// collision panel shows <see cref="Status"/>.
/// </summary>
public static class WallbounceJob
{
    private static CollisionScene? _scene;
    private static Task<CollisionWallbounce.Result>? _task;
    private static CancellationTokenSource? _cancel;
    private static VulkanRenderer? _appliedTo;
    private static float _progress;

    /// <summary>One line for the collision panel: progress, the result, or why it failed. Empty when idle.</summary>
    public static string Status { get; private set; } = "";

    public static void Update(CollisionScene? scene, VulkanRenderer renderer)
    {
        if (scene == null || scene.IsEmpty) { Status = ""; return; }
        if (!ReferenceEquals(scene, _scene))
        {
            // A new level, renderer or rebuilt scene: whatever was running is for something else.
            _cancel?.Cancel();
            var cancel = _cancel = new CancellationTokenSource();
            _scene = scene;
            _appliedTo = null;
            Volatile.Write(ref _progress, 0f);
            _task = Task.Run(() => CollisionWallbounce.Get(scene, p => Volatile.Write(ref _progress, p), cancel.Token), cancel.Token);
        }

        if (_task is null || !_task.IsCompleted)
        {
            Status = LM.Get("GUI_Label_WallbounceComputing", (int)(Volatile.Read(ref _progress) * 100));
            return;
        }
        if (_task.IsFaulted || _task.IsCanceled)
        {
            Status = LM.Get("GUI_Label_WallbounceFailed", _task.Exception?.GetBaseException().Message ?? "cancelled");
            return;
        }

        var result = _task.Result;
        if (!ReferenceEquals(_appliedTo, renderer))
        {
            renderer.SetCollisionWallbounce(result.Words, result.InstanceBases);
            _appliedTo = renderer;
            Status = result.FromCache
                ? LM.Get("GUI_Label_WallbounceCached", result.PassingTriangles)
                : LM.Get("GUI_Label_WallbounceComputed", result.PassingTriangles, result.Milliseconds / 1000.0);
            Console.WriteLine($"[Collision] Wallbounce: {result.PassingTriangles} triangle(s), " +
                              (result.FromCache ? "from cache" : $"computed in {result.Milliseconds} ms") + ".");
        }
    }
}
