using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ConvertApi.Core.Formats;

/// <summary>
/// The canonical spelling of a format: 1 to 16 lower-case letters or digits, no dot.
/// ".MP4" and " mp4 " both become "mp4".
/// </summary>
public static partial class FormatId
{
    public const int MaxLength = 16;

    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? format)
    {
        format = null;
        if (value is null)
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith('.'))
        {
            normalized = normalized[1..];
        }

        if (!Pattern().IsMatch(normalized))
        {
            return false;
        }

        format = normalized;
        return true;
    }

    [GeneratedRegex(@"^[a-z0-9]{1,16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
