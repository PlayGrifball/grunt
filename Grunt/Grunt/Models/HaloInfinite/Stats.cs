// <copyright file="Stats.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class Stats
{
    public CoreStats CoreStats { get; set; }
    public BombStats? BombStats { get; set; }
    public CaptureTheFlagStats? CaptureTheFlagStats { get; set; }
    public EliminationStats? EliminationStats { get; set; }
    public ExtractionStats? ExtractionStats { get; set; }
    public InfectionStats? InfectionStats { get; set; }
    public OddballStats? OddballStats { get; set; }
    public ZonesStats? ZonesStats { get; set; }
    public StockpileStats? StockpileStats { get; set; }
    public VIPStats? VipStats { get; set; }
    public PveStats? PveStats { get; set; }
    public PvpStats? PvpStats { get; set; }
}
