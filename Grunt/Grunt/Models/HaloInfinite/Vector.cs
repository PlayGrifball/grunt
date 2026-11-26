// <copyright file="Vector.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System.Text.Json.Serialization;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class Vector
{
    [JsonPropertyName("x")]
    public double? X { get; set; }
    [JsonPropertyName("y")]
    public double? Y { get; set; }
    [JsonPropertyName("z")]
    public double? Z { get; set; }
}
