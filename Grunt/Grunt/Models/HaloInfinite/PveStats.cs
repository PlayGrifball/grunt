// <copyright file="PveStats.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class PveStats
{
    public int? Kills { get; set; }
    public int? Deaths { get; set; }
    public int? Assists { get; set; }
    public float? KDA { get; set; }
    public int? MarineKills { get; set; }
    public int? GruntKills { get; set; }
    public int? JackalKills { get; set; }
    public int? EliteKills { get; set; }
    public int? BruteKills { get; set; }
    public int? HunterKills { get; set; }
    public int? SkimmerKills { get; set; }
    public int? SentinelKills { get; set; }
    public int? BossKills { get; set; }
}
