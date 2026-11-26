// <copyright file="Transaction.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

using System;

using System.Collections.Generic;

namespace Surprenant.Grunt.Models.HaloInfinite;

// TODO: Figure out what the record looks like here.
[IsAutomaticallySerializable]
public class Transaction
{
    public string AdjustmentSource { get; set; }
    public int? BalanceAdjustment { get; set; }
    public int? ResultingBalance { get; set; }
    public bool? Finalized { get; set; }
    public Guid? TransactionId { get; set; }
    public APIFormattedDate TransactionDate { get; set; }
    public string ProductReference { get; set; }
    public int? UnitsConsumed { get; set; }
    public List<string> AuthenticatedIdentities { get; set; }
}
