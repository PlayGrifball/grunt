// <copyright file="SearchableData.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System.Collections.Generic;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class SearchableData
{
    public string PublicName { get; set; }
    public string Description { get; set; }
    public List<string> Tags { get; set; }
    public bool HasNodeGraph { get; set; }
    public int CloneBehavior { get; set; }
    public string PublishedVersion { get; set; }
    public bool IsBanned { get; set; }
    public int FavoritesCount { get; set; }
    public double RatingScore { get; set; }
    public int RatingCount { get; set; }
    public int AssetState { get; set; }
    public APIFormattedDate AssetCreatedDate { get; set; }
    public APIFormattedDate AssetLastModified { get; set; }
}
