#nullable enable
using System.Collections.Generic;
using T3.Core.DataTypes;
using T3.Core.Operator;

namespace T3.Core.Output;

/// <summary>
/// A graph node that supplies a laser frame to the output setup — the laser counterpart of
/// <see cref="IContentSupplier"/>. The node registers itself with <see cref="LaserContentRegistry"/>
/// so the Output Setup can list it as content; routing lives on the node's own OutputRef input
/// (it names the laser output it feeds), not on slices.
/// </summary>
public interface ILaserContentSupplier
{
    /// <summary>The current laser frame (ILDA points), or null when nothing is produced.</summary>
    StructuredList? GetLaserPoints(EvaluationContext context);
}

/// <summary>
/// The set of live <see cref="ILaserContentSupplier"/> instances. Suppliers add themselves on
/// construction and remove themselves on dispose, so the registry survives operator hot-reloads.
/// Insertion order is preserved.
/// </summary>
public static class LaserContentRegistry
{
    /// <summary>Bumped whenever membership changes, so hosts can skip work only invalidated by
    /// a laser camera appearing or disappearing.</summary>
    public static int Version { get; private set; }

    public static void Register(ILaserContentSupplier supplier)
    {
        if (_suppliers.Contains(supplier))
            return;

        _suppliers.Add(supplier);
        Version++;
    }

    public static void Unregister(ILaserContentSupplier supplier)
    {
        if (_suppliers.Remove(supplier))
            Version++;
    }

    public static IReadOnlyList<ILaserContentSupplier> Suppliers => _suppliers;

    private static readonly List<ILaserContentSupplier> _suppliers = [];
}
