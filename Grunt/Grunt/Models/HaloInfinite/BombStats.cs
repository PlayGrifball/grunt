// <copyright file="BombStats.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class BombStats
{
    public int? BombCarriersKilled { get; set; }
    public int? BombDefusals { get; set; }
    public int? BombDefusersKilled { get; set; }
    public int? BombDetonations { get; set; }
    public int? BombPickUps { get; set; }
    public int? BombPlants { get; set; }
    public int? BombReturns { get; set; }
    public int? KillsAsBombCarrier { get; set; }
    public TimeSpan? TimeAsBombCarrier { get; set; }
}
