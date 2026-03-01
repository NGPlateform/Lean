/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Orders.Fills;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket.Data
{
    /// <summary>
    /// Custom fill model for Polymarket prediction markets.
    /// Accounts for:
    /// - Price bounded in [0, 1] range
    /// - Typically wider bid-ask spreads
    /// - Liquidity changes near settlement
    /// - Binary outcome characteristics
    /// </summary>
    public class PolymarketFillModel : FillModel
    {
        /// <summary>
        /// Minimum valid price for prediction market tokens
        /// </summary>
        public const decimal MinPrice = 0.001m;

        /// <summary>
        /// Maximum valid price for prediction market tokens
        /// </summary>
        public const decimal MaxPrice = 0.999m;

        /// <summary>
        /// Default spread assumption when no quote data is available
        /// </summary>
        public const decimal DefaultSpread = 0.02m;

        /// <summary>
        /// Fills a market order for Polymarket prediction market tokens
        /// </summary>
        public override OrderEvent MarketFill(Security asset, MarketOrder order)
        {
            var fill = base.MarketFill(asset, order);

            // Clamp fill price to valid prediction market range
            fill.FillPrice = ClampPrice(fill.FillPrice);

            // Apply additional spread for prediction markets (wider than traditional)
            if (fill.FillPrice > 0)
            {
                var spreadAdjustment = DefaultSpread / 2m;
                if (order.Direction == OrderDirection.Buy)
                {
                    fill.FillPrice = Math.Min(MaxPrice, fill.FillPrice + spreadAdjustment);
                }
                else
                {
                    fill.FillPrice = Math.Max(MinPrice, fill.FillPrice - spreadAdjustment);
                }
            }

            return fill;
        }

        /// <summary>
        /// Fills a limit order for Polymarket prediction market tokens
        /// </summary>
        public override OrderEvent LimitFill(Security asset, LimitOrder order)
        {
            var fill = base.LimitFill(asset, order);

            // Clamp fill price
            if (fill.Status == OrderStatus.Filled || fill.Status == OrderStatus.PartiallyFilled)
            {
                fill.FillPrice = ClampPrice(fill.FillPrice);
            }

            return fill;
        }

        /// <summary>
        /// Gets prices for fill model simulation, with prediction market adjustments
        /// </summary>
        protected override Prices GetPrices(Security asset, OrderDirection direction)
        {
            var prices = base.GetPrices(asset, direction);

            // Clamp all prices to valid range
            return new Prices(
                prices.EndTime,
                ClampPrice(prices.Current),
                ClampPrice(prices.Open),
                ClampPrice(prices.High),
                ClampPrice(prices.Low),
                ClampPrice(prices.Close));
        }

        /// <summary>
        /// Clamps a price to the valid prediction market range [0.001, 0.999]
        /// </summary>
        private static decimal ClampPrice(decimal price)
        {
            if (price <= 0) return 0;
            return Math.Max(MinPrice, Math.Min(MaxPrice, price));
        }
    }
}
