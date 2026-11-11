namespace FreePartition.Core.Updates;

/// <summary>Reads and compares version strings such as <c>v0.1.0</c>, <c>0.2.0-beta.1</c> or <c>0.1.0+a1b2c3d</c>.</summary>
public static class AppVersion
{
    /// <summary>Parses a tag or version. The leading <c>v</c> and the <c>+build</c> suffix are ignored; <c>-text</c> is the pre-release label.</summary>
    public static bool TryParse(string? text, out Version version, out string? preRelease)
    {
        version = new Version(0, 0, 0);
        preRelease = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];

        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = s[(dash + 1)..];
            s = s[..dash];
        }

        string[] parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return false;

        var numbers = new int[3];
        for (int i = 0; i < parts.Length && i < 3; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out numbers[i])) return false;

        // A fourth number is accepted but ignored: releases use MAJOR.MINOR.PATCH.
        if (parts.Length == 4 && !int.TryParse(parts[3], System.Globalization.NumberStyles.None, null, out _)) return false;

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>. Unreadable input is never newer.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var cv, out var cp)) return false;
        if (!TryParse(current, out var rv, out var rp)) return false;

        int byNumber = cv.CompareTo(rv);
        if (byNumber != 0) return byNumber > 0;

        // Same number: a final release is newer than its pre-release; two pre-releases compare as text.
        if (cp is null) return rp is not null;
        if (rp is null) return false;
        return string.CompareOrdinal(cp, rp) > 0;
    }
}
