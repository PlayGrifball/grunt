// <copyright file="CaptureTheFlagStats.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class CaptureTheFlagStats
{
    public int? FlagCaptureAssists { get; set; }
    public int? FlagCaptures { get; set; }
    public int? FlagCarriersKilled { get; set; }
    public int? FlagGrabs { get; set; }
    public int? FlagReturnersKilled { get; set; }
    public int? FlagReturns { get; set; }
    public int? FlagSecures { get; set; }
    public int? FlagSteals { get; set; }
    public int? KillsAsFlagCarrier { get; set; }
    public int? KillsAsFlagReturner { get; set; }
    public TimeSpan? TimeAsFlagCarrier { get; set; }
}
