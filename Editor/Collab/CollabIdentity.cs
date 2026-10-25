#nullable enable

namespace T3.Editor.Collab;

/// <summary>
/// Identity of a collaboration participant. Users are either locally nicknamed or logged in via
/// Discord. The persistent user id doubles as the seed for the per-user accent color so the same
/// person renders in the same color in every session.
/// </summary>
internal sealed class CollabUserIdentity
{
    public Guid UserId { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = "";
    public string Source { get; init; } = "local";
    public string AvatarUrl { get; init; } = "";

    public Vector4 AccentColor => CollabColors.FromId(UserId);
}

internal static class CollabColors
{
    /// <summary>
    /// Deterministic accent color for a user id. Hashes the GUID bytes, spreads the hue over the full
    /// circle and keeps saturation/value fixed so every color stays readable on dark node bodies and
    /// the graph background alike.
    /// </summary>
    public static Vector4 FromId(Guid id)
    {
        var hash = StableHash(id.ToByteArray());
        var hue = (hash & 0xffff) / 65535f;
        return HsvToRgb(hue, 0.72f, 0.98f);
    }

    public static uint ToUint(Vector4 color)
    {
        var r = (uint)(Math.Clamp(color.X, 0f, 1f) * 255f);
        var g = (uint)(Math.Clamp(color.Y, 0f, 1f) * 255f);
        var b = (uint)(Math.Clamp(color.Z, 0f, 1f) * 255f);
        var a = (uint)(Math.Clamp(color.W, 0f, 1f) * 255f);
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    private static int StableHash(byte[] bytes)
    {
        unchecked
        {
            var hash = 17;
            foreach (var b in bytes)
                hash = hash * 31 + b;

            return hash;
        }
    }

    private static Vector4 HsvToRgb(float hue, float saturation, float value)
    {
        hue = hue % 1f;
        if (hue < 0)
            hue += 1f;

        var hue6 = hue * 6f;
        var sector = (int)hue6 % 6;
        var fraction = hue6 - (int)hue6;
        var minValue = value * (1f - saturation);
        var descending = value * (1f - fraction * saturation);
        var ascending = value * (1f - (1f - fraction) * saturation);

        return sector switch
                   {
                       0 => new Vector4(value, ascending, minValue, 1f),
                       1 => new Vector4(descending, value, minValue, 1f),
                       2 => new Vector4(minValue, value, ascending, 1f),
                       3 => new Vector4(minValue, descending, value, 1f),
                       4 => new Vector4(ascending, minValue, value, 1f),
                       _ => new Vector4(value, minValue, descending, 1f),
                   };
    }
}
