#nullable enable
using System;

namespace T3.Core.Output;

/// <summary>
/// Kill-switch for the app-level laser output driver (profiling / emergencies): when off, laser
/// ops are not pumped per frame and lasers go dark unless a view drives them.
/// Toggled from the debug protocol via setInput-free static access (debug builds only).
/// </summary>
public static class LaserDriverEnabled
{
    public static bool Enabled = true;
}
