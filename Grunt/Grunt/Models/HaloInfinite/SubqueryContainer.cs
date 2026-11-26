// <copyright file="SubqueryContainer.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;
using System.Collections.Generic;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class SubqueryContainer
{
    public List<string>? SeasonIds { get; set; }

    public List<GameVariantCategory>? GameVariantCategories { get; set; }

    public List<bool>? IsRanked { get; set; }

    public List<Guid>? PlaylistAssetIds { get; set; }
}
