using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Surprenant.Grunt.Converters;

/// <summary>
/// Reads an int that Halo sometimes sends as a number and sometimes as a name (a string); a name that isn't a number reads as 0.
/// </summary>
internal class LenientIntJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return int.TryParse(reader.GetString(), out var parsed) ? parsed : 0;
        return reader.GetInt32();
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
