// <copyright file="ArmorConfigurationOptionsBase.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System.Collections.Generic;

namespace Surprenant.Grunt.Models.HaloInfinite.Foundation;

[IsAutomaticallySerializable]
public abstract class ArmorConfigurationOptionsBase
{
    public bool? IsRequired { get; set; }
    public string DefaultOptionPath { get; set; }
    public List<string> OptionPaths { get; set; }
    public List<RegionMetadata> OffRegionInfo { get; set; }
}
