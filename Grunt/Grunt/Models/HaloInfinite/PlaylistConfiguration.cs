// <copyright file="PlaylistConfiguration.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class PlaylistConfiguration
{
    public string? NameHint { get; set; }

    public Guid UgcPlaylistVersion { get; set; }

    public string? PlatformMatchmakingHopperId { get; set; }

    public string? GameStartRulesId { get; set; }

    public string? ThunderheadContentConfiguration { get; set; }

    public string? ThunderheadVmSize { get; set; }

    public string? TrueMatchSettings { get; set; }

    public bool? HasCsr { get; set; }

    public string? PlaylistExperience { get; set; }

    public int MatchmakingDelaySec { get; set; }
}
