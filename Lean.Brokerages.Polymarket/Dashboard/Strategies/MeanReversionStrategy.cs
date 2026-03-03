using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Strategies
{
    /// <summary>
    /// Tracks each token's mid-price over a sliding window.
    /// When mid-price deviates from the mean beyond a threshold, trades in the opposite direction.
    /// </summary>
    public class MeanReversionStrategy : IDryRunStrategy
    {
        public string Name => "MeanReversion";
        public string Description => "Mean reversion: trades against price deviations from rolling average";

        private decimal _spreadThreshold = 0.03m;
        private decimal _orderSize = 25m;
        private decimal _maxPositionSize = 200m;
        private int _windowSize = 20;

        private readonly Dictionary<string, List<decimal>> _priceHistory = new();

        public void Initialize(Dictionary<string, string> parameters)
        {
            if (parameters.TryGetValue("SpreadThreshold", out var st) && decimal.TryParse(st, out var stv))
                _spreadThreshold = stv;
            if (parameters.TryGetValue("OrderSize", out var os) && decimal.TryParse(os, out var osv))
                _orderSize = osv;
            if (parameters.TryGetValue("MaxPositionSize", out var mps) && decimal.TryParse(mps, out var mpsv))
                _maxPositionSize = mpsv;
            if (parameters.TryGetValue("WindowSize", out var ws) && int.TryParse(ws, out var wsv))
                _windowSize = wsv;
        }

        public List<StrategyAction> Evaluate(StrategyContext context)
        {
            var actions = new List<StrategyAction>();

            foreach (var kvp in context.OrderBooks)
            {
                var tokenId = kvp.Key;
                var book = kvp.Value;

                var bestBid = GetBestPrice(book.Bids, ascending: false);
                var bestAsk = GetBestPrice(book.Asks, ascending: true);
                if (!bestBid.HasValue || !bestAsk.HasValue) continue;

                var mid = (bestBid.Value + bestAsk.Value) / 2;
                if (mid <= 0 || mid >= 1) continue;

                // Update price history
                if (!_priceHistory.TryGetValue(tokenId, out var history))
                {
                    history = new List<decimal>();
                    _priceHistory[tokenId] = history;
                }
                history.Add(mid);
                if (history.Count > _windowSize)
                    history.RemoveAt(0);

                // Need enough history
                if (history.Count < _windowSize / 2) continue;

                var mean = history.Average();
                var deviation = mid - mean;

                // Check existing position size
                context.Positions.TryGetValue(tokenId, out var pos);
                var currentSize = pos?.Size ?? 0;

                // Check if we already have an open order for this token
                if (context.OpenOrders.Any(o => o.TokenId == tokenId)) continue;

                if (deviation < -_spreadThreshold && currentSize < _maxPositionSize)
                {
                    // Price below mean → BUY
                    var size = Math.Min(_orderSize, _maxPositionSize - currentSize);
                    if (size > 0 && context.Balance >= size * bestAsk.Value)
                    {
                        actions.Add(new PlaceOrderAction
                        {
                            TokenId = tokenId,
                            Price = Math.Min(bestAsk.Value, mid + 0.005m),
                            Size = size,
                            Side = "BUY",
                            Reason = $"Mean reversion BUY: mid={mid:F4}, mean={mean:F4}, dev={deviation:F4}"
                        });
                    }
                }
                else if (deviation > _spreadThreshold && currentSize > 0)
                {
                    // Price above mean → SELL
                    var size = Math.Min(_orderSize, currentSize);
                    if (size > 0)
                    {
                        actions.Add(new PlaceOrderAction
                        {
                            TokenId = tokenId,
                            Price = Math.Max(bestBid.Value, mid - 0.005m),
                            Size = size,
                            Side = "SELL",
                            Reason = $"Mean reversion SELL: mid={mid:F4}, mean={mean:F4}, dev={deviation:F4}"
                        });
                    }
                }
            }

            return actions;
        }

        public void OnFill(SimulatedTrade trade) { }

        public Dictionary<string, string> GetParameters()
        {
            return new Dictionary<string, string>
            {
                ["SpreadThreshold"] = _spreadThreshold.ToString(),
                ["OrderSize"] = _orderSize.ToString(),
                ["MaxPositionSize"] = _maxPositionSize.ToString(),
                ["WindowSize"] = _windowSize.ToString()
            };
        }

        public List<MarketScore> GetMarketScores(StrategyContext context)
        {
            var scores = new List<MarketScore>();
            foreach (var kvp in context.OrderBooks)
            {
                var tokenId = kvp.Key;
                string question = null;
                if (context.Markets != null)
                {
                    foreach (var m in context.Markets)
                    {
                        if (m.Tokens != null)
                            foreach (var t in m.Tokens)
                                if (t.TokenId == tokenId) { question = m.Question; break; }
                        if (question != null) break;
                    }
                }
                context.Positions.TryGetValue(tokenId, out var pos);
                scores.Add(new MarketScore
                {
                    TokenId = tokenId,
                    Question = question,
                    Score = 1.0m,
                    IsSelected = true,
                    HasPosition = pos != null && pos.Size > 0
                });
            }
            return scores;
        }

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
