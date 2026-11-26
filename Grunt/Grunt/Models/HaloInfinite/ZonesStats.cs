// <copyright file="ZonesStats.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class ZonesStats
{
    public int? ZoneCaptures { get; set; }
    public int? ZoneDefensiveKills { get; set; }
    public int? ZoneOffensiveKills { get; set; }
    public int? ZoneSecures { get; set; }
    public TimeSpan? TotalZoneOccupationTime { get; set; }
    public int? ZoneScoringTicks { get; set; }
}
