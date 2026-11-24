using System;
using System.Text.RegularExpressions;

namespace Surprenant.Grunt.Util;

/// <summary>
/// The shapes of the identifiers callers put into Halo URLs. A value that can't be one is answered 400 without a call: Halo
/// would refuse it anyway, every call counts against its rate limits, and an unchecked value could change the request.
/// </summary>
public static partial class HaloInput
{
    /// <summary>Most players one customization call may ask about.</summary>
    public const int MaxPlayersPerCall = 20;

    [GeneratedRegex(@"^xuid\(\d{1,20}\)$")]
    private static partial Regex XuidPattern();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,100}$")]
    private static partial Regex CoreIdPattern();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex ItemPathPattern();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+/)*[A-Za-z0-9_-]+\.(png|jpg|jpeg|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePathPattern();

    /// <summary>Whether <paramref name="player"/> is <c>xuid(DIGITS)</c>.</summary>
    public static bool IsPlayer(string? player) => player is not null && XuidPattern().IsMatch(player);

    /// <summary>Whether <paramref name="coreId"/> is letters, digits and dashes, like <c>017-001-eag-c13d0b38</c>.</summary>
    public static bool IsCoreId(string? coreId) => coreId is not null && CoreIdPattern().IsMatch(coreId);

    /// <summary>Whether <paramref name="itemPath"/> is a GameCms item document: folders and a name of letters, digits, dashes and underscores, ending <c>.json</c>.</summary>
    public static bool IsItemPath(string? itemPath) => itemPath is not null && itemPath.Length <= 200 && ItemPathPattern().IsMatch(itemPath);

    /// <summary>Whether <paramref name="imagePath"/> is a Waypoint image file: folders and a name of letters, digits, dashes and underscores, ending in an image extension.</summary>
    public static bool IsImagePath(string? imagePath) => imagePath is not null && imagePath.Length <= 200 && ImagePathPattern().IsMatch(imagePath);

    /// <summary>Whether <paramref name="view"/> is the customization view <c>public</c> or <c>private</c>.</summary>
    public static bool IsCustomizationView(string? view)
        => string.Equals(view, "public", StringComparison.OrdinalIgnoreCase) || string.Equals(view, "private", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="flightId"/> is a GUID, or empty (no flight).</summary>
    public static bool IsFlightId(string? flightId)
        => flightId is not null && (flightId.Length == 0 || Guid.TryParseExact(flightId, "D", out _));
}
