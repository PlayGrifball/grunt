// <copyright file="CoreRegionData.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System.Collections.Generic;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class CoreRegionData
{
    public List<RegionMetadata> BaseRegionData { get; set; }
    public List<RegionMetadata> BodyTypeSmallOverrides { get; set; }
    public List<RegionMetadata> BodyTypeLargeOverrides { get; set; }
    public RegionOverrides ProstheticLeftArmOverrides { get; set; }
    public RegionOverrides ProstheticRightArmOverrides { get; set; }
    public RegionOverrides ProstheticLeftLegOverrides { get; set; }
    public RegionOverrides ProstheticRightLegOverrides { get; set; }
}
