using System.Collections;
using System.Globalization;
using System.Text;

namespace Surprenant.Grunt.Gateway.Client;

/// <summary>URI building helpers used by the generated <see cref="HaloGatewayClient"/>.</summary>
public static class HaloGatewayUri
{
    /// <summary>One escaped route segment.</summary>
    public static string Segment(object? value) => Uri.EscapeDataString(Format(value));

    /// <summary>A catch-all route value: each '/'-separated part escaped, slashes kept.</summary>
    public static string CatchAll(object? value) => string.Join("/", Format(value).Split('/').Select(Uri.EscapeDataString));

    /// <summary>Invariant, round-trippable text for route and query values (enums by name, dates ISO 8601).</summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Query string builder; <see cref="ToString"/> returns "" or "?a=1&amp;b=2".</summary>
    public sealed class QueryBuilder
    {
        private readonly StringBuilder _sb = new();

        public void Add(string name, object? value)
        {
            if (value is null)
                return;
            _sb.Append(_sb.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(Format(value)));
        }

        public void AddAll(string name, IEnumerable? values)
        {
            if (values is null)
                return;
            foreach (var value in values)
                Add(name, value);
        }

        public override string ToString() => _sb.ToString();
    }
}
