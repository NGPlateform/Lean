/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Polymarket
{
    /// <summary>
    /// Custom order properties for Polymarket orders
    /// </summary>
    public class PolymarketOrderProperties : OrderProperties
    {
        /// <summary>
        /// If true, the limit order will only be posted as a maker order (Good-Til-Cancelled, post-only).
        /// If the order would immediately match, it will be rejected.
        /// </summary>
        public bool PostOnly { get; set; }

        /// <summary>
        /// Time-to-live in seconds. 0 means Good-Til-Cancelled.
        /// </summary>
        public int TimeToLiveSecs { get; set; }

        /// <summary>
        /// The nonce for EIP-712 signing (auto-generated if not set)
        /// </summary>
        public long Nonce { get; set; }

        /// <summary>
        /// Fill-or-Kill: the order must be filled entirely or cancelled
        /// </summary>
        public bool FillOrKill { get; set; }
    }
}
