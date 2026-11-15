using System.Globalization;
using System.Linq;

namespace Surprenant.Grunt.Util;

/// <summary>Xbox's gamertag rules, as far as they tell a name that can't be one.</summary>
public static class Gamertag
{
    /// <summary>
    /// Whether <paramref name="name"/> could be a gamertag, so worth asking Xbox about. Players can't choose the basic Latin
    /// symbols ("!" to "/", ":" to "@", "[" to "`", "{" to "~", and NBSP to "¿"; Xbox reserves them), except the apostrophe
    /// and the comma (classic gamertags allow it) and "#" before a modern gamertag's numeric suffix. A gamertag is at most 15
    /// characters (classic) or 12 then "#" and 1 to 14 digits (modern), at most 16 in all. Halo answers 400 to looking up a
    /// name that breaks any of that ("Bowdery(1)(G)", "Dr:Who", "a_b"). Not a full check: a name that passes may still not exist.
    /// See Microsoft's "UTF-8 character ranges for modern gamertags" and <c>XUserGamertagComponent</c>.
    /// </summary>
    public static bool IsPossible(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var hash = name.IndexOf('#');
        if (hash >= 0)
        {
            var suffix = name[(hash + 1)..];
            if (hash == 0 || suffix.Length is 0 or > 14 || !suffix.All(char.IsAsciiDigit)
                || new StringInfo(name).LengthInTextElements > 16)
                return false;
            name = name[..hash];
        }
        return new StringInfo(name).LengthInTextElements <= 15 && !name.Any(IsReserved);
    }

    private static bool IsReserved(char c)
        => char.IsControl(c)
           || c is (>= '!' and <= '/' and not '\'' and not ',') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~')
               or (>= ' ' and <= '¿');
}
