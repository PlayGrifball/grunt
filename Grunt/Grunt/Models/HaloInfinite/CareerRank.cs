// <copyright file="CareerRank.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class CareerRank
{
    public int? Rank { get; set; }

    public RewardContainer? FreeRewards { get; set; }

    public RewardContainer? PaidRewards { get; set; }

    public int? XpRequiredForRank { get; set; }

    public DisplayString? RankTitle { get; set; }

    public DisplayString? RankSubTitle { get; set; }

    public DisplayString? RankTier { get; set; }

    public string? RankIcon { get; set; }

    public string? RankLargeIcon { get; set; }

    public string? RankAdornmentIcon { get; set; }

    public string? TierType { get; set; }

    public int? RankGrade { get; set; }
}
