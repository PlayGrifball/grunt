// <copyright file="InfectionStats.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class InfectionStats
{
    public int? HumansInfected { get; set; }
    public int? HumansInfectedAsAlpha { get; set; }
    public int? LastHumansStandingInfected { get; set; }
    public int? ZombiesKilled { get; set; }
    public int? AlphaZombiesKilled { get; set; }
    public int? KillsAsLastHumanStanding { get; set; }
    public int? RoundsSurvivedAsHuman { get; set; }
    public int? RoundsSurvivedAsLastHumanStanding { get; set; }
    public int? RoundsAsLastHumanStanding { get; set; }
    public TimeSpan? TimeAsLastHumanStanding { get; set; }
    public int? RoundsAsAlphaZombie { get; set; }
    public int? RoundsFinishedAsZombie { get; set; }
}
