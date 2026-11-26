// <copyright file="AssetSearchResult.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using Surprenant.Grunt.Models.HaloInfinite.Foundation;

using System;

namespace Surprenant.Grunt.Models.HaloInfinite;

[IsAutomaticallySerializable]
public class AssetSearchResult : Asset
{
    public string Name { get; set; }
    public string[] Tags { get; set; }
    public string ThumbnailUrl { get; set; }
    public string[] ReferencedAssets { get; set; }
    public string OriginalAuthor { get; set; }
    public int Likes { get; set; }
    public int Bookmarks { get; set; }
    public int PlaysRecent { get; set; }
    public int NumberOfObjects { get; set; }
    public APIFormattedDate DateCreatedUtc { get; set; }
    public APIFormattedDate DateModifiedUtc { get; set; }
    public APIFormattedDate DatePublishedUtc { get; set; }
    public bool HasNodeGraph { get; set; }
    public bool ReadOnlyClones { get; set; }
    public int PlaysAllTime { get; set; }
    public int ParentAssetCount { get; set; }
    public float AverageRating { get; set; }
    public int NumberOfRatings { get; set; }
    public Guid? PublishedVersion { get; set; }
    public bool? IsBanned { get; set; }
    public int FavoritesCount { get; set; }
    public float RatingScore { get; set; }
    public int RatingCount { get; set; }
    public int AssetState { get; set; }
    public APIFormattedDate AssetCreatedDate { get; set; }
    public APIFormattedDate AssetLastModified { get; set; }
}
