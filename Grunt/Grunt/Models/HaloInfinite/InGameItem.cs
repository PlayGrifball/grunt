// <copyright file="InGameItem.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System.Collections.Generic;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class InGameItem
{
    public int TagId { get; set; }
    public IdentifierName ThemeName { get; set; }
    public IdentifierName EmblemShaderName { get; set; }
    public CommonItemData CommonData { get; set; }
    public Configuration[] AvailableConfigurations { get; set; }
    public IdentifierName StyleId { get; set; }
    public IdentifierName RegionPartId { get; set; }
    public DisplayPath ImagePath { get; set; }
    public int? LightingScalarOverride { get; set; }
    public List<RegionMetadata> RegionData { get; set; }
    public MarkerLocation MarkerLocation { get; set; }
    public int? MeshIndex { get; set; }
    public bool? IsItemOwnershipRequired { get; set; }
    public StandardConfigurationOptions Coatings { get; set; }
    public HelmetConfigurationOptions Helmets { get; set; }
    public StandardConfigurationOptions Visors { get; set; }
    public StandardConfigurationOptions LeftShoulderPads { get; set; }
    public StandardConfigurationOptions RightShoulderPads { get; set; }
    public StandardConfigurationOptions Gloves { get; set; }
    public StandardConfigurationOptions KneePads { get; set; }
    public StandardConfigurationOptions ChestAttachments { get; set; }
    public StandardConfigurationOptions WristAttachments { get; set; }
    public StandardConfigurationOptions HipAttachments { get; set; }
    public EmblemConfigurationOptions Emblems { get; set; }
    public StandardConfigurationOptions ArmorFx { get; set; }
    public StandardConfigurationOptions MythicFx { get; set; }
    public CoreRegionData CoreRegionData { get; set; }
    public IdentifierName VariantId { get; set; }
    public bool? IsKit { get; set; }
    public string KitBaseThemePath { get; set; }
    public IdentifierName StanceId { get; set; }
    public int? WeaponTagId { get; set; }
    public List<int> FxIds { get; set; }
    public string FxContentType { get; set; }
    public IdentifierName ComposerSceneName { get; set; }
}
