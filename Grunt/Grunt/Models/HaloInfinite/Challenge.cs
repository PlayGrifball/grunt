// <copyright file="Challenge.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class Challenge
{
    public DisplayString Description { get; set; }
    public string Difficulty { get; set; }
    public string Category { get; set; }
    public Reward Reward { get; set; }
    public int ThresholdForSuccess { get; set; }
    public DisplayString Title { get; set; }
    public Reward SecondaryReward { get; set; }
    public string TypeIconPath { get; set; }
    public bool? IsUserEvent { get; set; }
    public string Path { get; set; }
    public int? Progress { get; set; }
    public string Id { get; set; }
    public bool? CanReroll { get; set; }
}
