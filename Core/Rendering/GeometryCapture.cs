using System;
using System.Collections.Generic;
using System.Numerics;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace T3.Core.Rendering;

/// <summary>
/// Neutral classification of captured geometry - capture sources never need to know about lasers.
/// <see cref="Auto"/> entries are classified by the consumer from the buffer layout.
/// </summary>
public enum GeometryPrimitiveKind
{
    Auto = 0,
    PointCloud = 1,
    Lines = 2,
    Mesh = 3,
}

/// <summary>
/// Collects the geometry drawn inside a subtree. A consumer (e.g. Lib.laser.camera.LaserCamera)
/// installs an instance on <see cref="T3.Core.Operator.EvaluationContext.GeometryCapture"/> around
/// evaluating its scene; the ops that actually issue GPU draws register what they draw while it is
/// active (the <code>Draw</code> ops from their capture inputs, <code>_DispatchSceneDraws</code>
/// directly per dispatch). This works even for cached subtrees, because command slots re-run every
/// frame while data ops stay frozen by the dirty-flag system.
/// Entries hold non-owning buffer references that are only valid until the buffers are disposed.
/// </summary>
public sealed class GeometryCapture
{
    /// <summary>Bounded scratch - a scene with more unique draws per frame is truncated.</summary>
    public const int MaxEntries = 256;

    public bool IsActive { get; private set; }

    public IReadOnlyList<Entry> Entries => _entries;

    public readonly record struct Entry(Buffer VertexBuffer, Buffer IndexBuffer, GeometryPrimitiveKind Kind, Matrix4x4 ObjectToWorld);

    public void BeginPass()
    {
        _entries.Clear();
        _seen.Clear();
        IsActive = true;
    }

    public void EndPass()
    {
        IsActive = false;
    }

    /// <summary>Registers a draw. The same buffer drawn repeatedly under the same transform is
    /// registered once; under a different transform it is registered again.</summary>
    public void Register(Buffer vertexBuffer, Buffer indexBuffer, GeometryPrimitiveKind kind, Matrix4x4 objectToWorld)
    {
        if (!IsActive || vertexBuffer == null || _entries.Count >= MaxEntries)
            return;

        var entry = new Entry(vertexBuffer, indexBuffer, kind, objectToWorld);
        if (!_seen.Add(entry))
            return;

        _entries.Add(entry);
    }

    private readonly List<Entry> _entries = new();
    private readonly HashSet<Entry> _seen = new();
}
