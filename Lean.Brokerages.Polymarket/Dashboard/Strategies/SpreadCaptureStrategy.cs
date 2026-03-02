using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Strategies
{
    /// <summary>
    /// Places limit orders on both sides of the spread to capture the bid-ask spread.
    /// </summary>
    public class SpreadCaptureStrategy : IDryRunStrategy
    {
        public string Name => "SpreadCapture";
        public string Description => "Spread capture: places orders inside the spread on both sides";

        private decimal _orderSize = 25m;
        private decimal _edgeOffset = 0.005m;
        private decimal _maxExposure = 200m;
        private decimal _minSpread = 0.02m;

        public void Initialize(Dictionary<string, string> parameters)
        {
            if (parameters.TryGetValue("OrderSize", out var os) && decimal.TryParse(os, out var osv))
                _orderSize = osv;
            if (parameters.TryGetValue("EdgeOffset", out var eo) && decimal.TryParse(eo, out var eov))
                _edgeOffset = eov;
            if (parameters.TryGetValue("MaxExposure", out var me) && decimal.TryParse(me, out var mev))
                _maxExposure = mev;
            if (parameters.TryGetValue("MinSpread", out var ms) && decimal.TryParse(ms, out var msv))
                _minSpread = msv;
        }

        public List<StrategyAction> Evaluate(StrategyContext context)
        {
            var actions = new List<StrategyAction>();

            // Calculate total exposure
            var totalExposure = context.Positions.Values
                .Where(p => p.Size > 0)
                .Sum(p => p.Size * p.AvgPrice);

            foreach (var kvp in context.OrderBooks)
            {
                var tokenId = kvp.Key;
                var book = kvp.Value;

                var bestBid = GetBestPrice(book.Bids, ascending: false);
                var bestAsk = GetBestPrice(book.Asks, ascending: true);
                if (!bestBid.HasValue || !bestAsk.HasValue) continue;

                var spread = bestAsk.Value - bestBid.Value;
                if (spread < _minSpread) continue;

                // Skip if we already have open orders for this token
                if (context.OpenOrders.Any(o => o.TokenId == tokenId)) continue;

                // Check exposure limits
                if (totalExposure >= _maxExposure) continue;

                context.Positions.TryGetValue(tokenId, out var pos);
                var currentSize = pos?.Size ?? 0;

                // Place BUY inside the spread
                var buyPrice = bestBid.Value + _edgeOffset;
                if (buyPrice < bestAsk.Value && buyPrice > 0.01m && buyPrice < 0.99m
                    && context.Balance >= _orderSize * buyPrice)
                {
                    actions.Add(new PlaceOrderAction
                    {
                        TokenId = tokenId,
                        Price = buyPrice,
                        Size = _orderSize,
                        Side = "BUY",
                        Reason = $"Spread capture BUY: bid={bestBid.Value:F4}, ask={bestAsk.Value:F4}, spread={spread:F4}"
                    });
                }

                // Place SELL inside the spread if we have position
                if (currentSize > 0)
                {
                    var sellPrice = bestAsk.Value - _edgeOffset;
                    var sellSize = Math.Min(_orderSize, currentSize);
                    if (sellPrice > bestBid.Value && sellPrice > 0.01m && sellPrice < 0.99m)
                    {
                        actions.Add(new PlaceOrderAction
                        {
                            TokenId = tokenId,
                            Price = sellPrice,
                            Size = sellSize,
                            Side = "SELL",
                            Reason = $"Spread capture SELL: bid={bestBid.Value:F4}, ask={bestAsk.Value:F4}, spread={spread:F4}"
                        });
                    }
                }
            }

            return actions;
        }

        public void OnFill(SimulatedTrade trade) { }

        private static decimal? GetBestPrice(List<PolymarketOrderBookLevel> levels, bool ascending)
        {
            if (levels == null || levels.Count == 0) return null;
            decimal? best = null;
            foreach (var level in levels)
            {
                if (!decimal.TryParse(level.Price, out var p)) continue;
                if (!decimal.TryParse(level.Size, out var s) || s <= 0) continue;
                if (!best.HasValue)
                    best = p;
                else if (ascending && p < best.Value)
                    best = p;
                else if (!ascending && p > best.Value)
                    best = p;
            }
            return best;
        }
    }
}
