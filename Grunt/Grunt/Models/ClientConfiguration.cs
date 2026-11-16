// <copyright file="ClientConfiguration.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System.Text.Json.Serialization;

namespace Surprenant.Grunt.Models;

public class ClientConfiguration
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string ClientSecret { get; set; }

    [JsonPropertyName("redirect_url")]
    public string RedirectUrl { get; set; }

    /// <summary>
    /// Retry and backoff for transient failures (429, 5xx, network errors) in <see cref="Surprenant.Grunt.Core.HaloInfiniteClientFactory"/>.
    /// Bound from <c>ClientConfiguration:Retry</c>.
    /// </summary>
    [JsonPropertyName("retry")]
    public Surprenant.Grunt.Core.HaloRetryOptions Retry { get; set; } = new();

    /// <summary>
    /// The account's budgets with Halo, by a name of their own (e.g. <c>profile</c>): each a group of operations sharing a limit,
    /// a lockout after Halo answers 429, and how long a call waits for room (<see cref="Surprenant.Grunt.Core.HaloBudgets"/>).
    /// Operations in none are not limited. Bound from <c>ClientConfiguration:Budgets</c>; read at startup.
    /// </summary>
    [JsonPropertyName("budgets")]
    public System.Collections.Generic.Dictionary<string, Surprenant.Grunt.Core.HaloBudget> Budgets { get; set; } = new();
}
