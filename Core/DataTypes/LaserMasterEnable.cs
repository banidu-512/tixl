using System.Threading;

namespace T3.Core.DataTypes;

/// <summary>
/// Global master enable for all laser output operators, driven by the menu-bar
/// emergency button. When disabled, laser outputs must blank their frames
/// (positions keep moving so scanners stay settled, beam intensity zeroed).
/// Defaults to OFF: laser output only flows after the user explicitly arms it.
/// Volatile: the editor UI thread writes it while output operators read it
/// from their send threads.
/// </summary>
public static class LaserMasterEnable
{
    private static volatile bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
