using System;
using System.Globalization;

namespace Earworm.Domain.Tenants;

/// <summary>
/// Parses Discord guild snowflakes and returns the one canonical string form
/// used by persistence and per-guild registries.
/// </summary>
public static class DiscordGuildId
{
    /// <summary>
    /// Accepts numeric aliases supported by <see cref="ulong.TryParse(string?, out ulong)"/>
    /// (for example, leading zeroes, a leading plus sign, or surrounding
    /// whitespace) and emits the invariant, unsigned decimal representation.
    /// </summary>
    public static bool TryNormalize(string? value, out string canonicalGuildId)
    {
        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed != 0)
        {
            canonicalGuildId = parsed.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        canonicalGuildId = string.Empty;
        return false;
    }

    public static string Normalize(string value, string? parameterName = null)
    {
        if (TryNormalize(value, out var canonicalGuildId))
        {
            return canonicalGuildId;
        }

        throw new ArgumentException(
            $"Guild ID '{value}' must be a numeric Discord snowflake.",
            parameterName ?? nameof(value));
    }

    public static bool IsCanonical(string? value) =>
        TryNormalize(value, out var canonicalGuildId) &&
        string.Equals(value, canonicalGuildId, StringComparison.Ordinal);
}
