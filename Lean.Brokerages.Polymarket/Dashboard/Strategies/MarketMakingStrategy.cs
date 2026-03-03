using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Strategies
{
    public class MarketMakingStrategy : IDryRunStrategy
    {
        public string Name => "MarketMaking";
        public string Description => "Professional market making with inventory skew, volatility-adaptive spread, and market selection scoring";

        // Configuration
        private decimal _orderSize = 25m;
        private decimal _halfSpread = 0.02m;
        private decimal _skewFactor = 0.005m;
        private decimal _maxPositionPerToken = 150m;
        private decimal _maxTotalExposure = 500m;
        private int _maxActiveMarkets = 5;
        private int _requoteIntervalTicks = 6;

        // Spread bounds
        private const decimal MinHalfSpread = 0.005m;
        private const decimal MaxHalfSpread = 0.10m;

        // Price filters
        private const decimal MinPrice = 0.08m;
        private const decimal MaxPrice = 0.92m;
        private const decimal MinMarketSpread = 0.002m;
        private const decimal MaxMarketSpread = 0.20m;

        // State
        private int _tickCount;
        private int _lastMarketSelectionTick;
        private readonly Dictionary<string, List<decimal>> _priceHistory = new();
        private readonly HashSet<string> _selectedTokens = new();
        private readonly Dictionary<string, int> _lastQuoteTick = new();
        private const int VolatilityWindow = 20;
        private const int MarketSelectionIntervalTicks = 12;

        public void Initialize(Dictionary<string, string> parameters)
        {
            if (parameters.TryGetValue("OrderSize", out var os) && decimal.TryParse(os, out var osv))
                _orderSize = osv;
            if (parameters.TryGetValue("HalfSpread", out var hs) && decimal.TryParse(hs, out var hsv))
                _halfSpread = hsv;
            if (parameters.TryGetValue("SkewFactor", out var sf) && decimal.TryParse(sf, out var sfv))
                _skewFactor = sfv;
            if (parameters.TryGetValue("MaxPositionPerToken", out var mp) && decimal.TryParse(mp, out var mpv))
                _maxPositionPerToken = mpv;
            if (parameters.TryGetValue("MaxTotalExposure", out var me) && decimal.TryParse(me, out var mev))
                _maxTotalExposure = mev;
            if (parameters.TryGetValue("MaxActiveMarkets", out var ma) && int.TryParse(ma, out var mav))
                _maxActiveMarkets = mav;
            if (parameters.TryGetValue("RequoteIntervalTicks", out var ri) && int.TryParse(ri, out var riv))
                _requoteIntervalTicks = riv;
        }

        public List<StrategyAction> Evaluate(StrategyContext context)
        {
            _tickCount++;
            var actions = new List<StrategyAction>();

            // Update price history for all tokens with order books
            UpdatePriceHistory(context.OrderBooks);

            // Refresh market selection periodically
            if (_tickCount - _lastMarketSelectionTick >= MarketSelectionIntervalTicks || _selectedTokens.Count == 0)
            {
                SelectMarkets(context);
                _lastMarketSelectionTick = _tickCount;
            }

            // Calculate total exposure
            var totalExposure = context.Positions.Values
                .Where(p => p.Size > 0)
                .Sum(p => p.Size * p.AvgPrice);

            // Process each selected token
            foreach (var tokenId in _selectedTokens)
            {
                if (!context.OrderBooks.TryGetValue(tokenId, out var book)) continue;

                var midPrice = GetMidPrice(book);
                if (!midPrice.HasValue) continue;

                // Filter extreme prices
                if (midPrice.Value < MinPrice || midPrice.Value > MaxPrice) continue;

                var tokenActions = ProcessToken(tokenId, book, midPrice.Value, context, totalExposure);
                actions.AddRange(tokenActions);
            }

            return actions;
        }

        public void OnFill(SimulatedTrade trade) { }

        public Dictionary<string, string> GetParameters()
        {
            return new Dictionary<string, string>
            {
                ["OrderSize"] = _orderSize.ToString(),
                ["HalfSpread"] = _halfSpread.ToString(),
                ["SkewFactor"] = _skewFactor.ToString(),
                ["MaxPositionPerToken"] = _maxPositionPerToken.ToString(),
                ["MaxTotalExposure"] = _maxTotalExposure.ToString(),
                ["MaxActiveMarkets"] = _maxActiveMarkets.ToString(),
                ["RequoteIntervalTicks"] = _requoteIntervalTicks.ToString()
            };
        }

        public List<MarketScore> GetMarketScores(StrategyContext context)
        {
            var scores = new List<MarketScore>();
            foreach (var kvp in context.OrderBooks)
            {
                var tokenId = kvp.Key;
                var book = kvp.Value;
                var score = ScoreMarket(book);

                string question = null;
                if (context.Markets != null)
                {
                    foreach (var m in context.Markets)
                    {
                        if (m.Tokens != null)
                        {
                            foreach (var t in m.Tokens)
                            {
                                if (t.TokenId == tokenId) { question = m.Question; break; }
                            }
                        }
                        if (question != null) break;
                    }
                }

                context.Positions.TryGetValue(tokenId, out var pos);

                var bestBid = GetBestPrice(book.Bids, ascending: false);
                var bestAsk = GetBestPrice(book.Asks, ascending: true);
                var components = new Dictionary<string, decimal>();
                if (bestBid.HasValue && bestAsk.HasValue)
                {
                    var mid = (bestBid.Value + bestAsk.Value) / 2m;
                    var spread = bestAsk.Value - bestBid.Value;
                    components["SpreadQuality"] = spread < MaxMarketSpread ? 1m - Math.Min(1m, spread / MaxMarketSpread) : 0m;
                    components["Liquidity"] = Math.Min(1m, (GetTotalDepth(book.Bids) + GetTotalDepth(book.Asks)) / 1000m);
                    components["Centrality"] = 1m - Math.Abs(mid - 0.5m) * 2m;
                }

                scores.Add(new MarketScore
                {
                    TokenId = tokenId,
                    Question = question,
                    Score = score,
                    IsSelected = _selectedTokens.Contains(tokenId),
                    HasPosition = pos != null && pos.Size > 0,
                    ScoreComponents = components
                });
            }
            return scores.OrderByDescending(s => s.Score).ToList();
        }

        private List<StrategyAction> ProcessToken(string tokenId, PolymarketOrderBook book,
            decimal midPrice, StrategyContext context, decimal totalExposure)
        {
            var actions = new List<StrategyAction>();

            // Check if we need to requote
            var existingOrders = context.OpenOrders
                .Where(o => o.TokenId == tokenId && o.Source == "Strategy")
                .ToList();

            _lastQuoteTick.TryGetValue(tokenId, out var lastQuote);
            var shouldRequote = _tickCount - lastQuote >= _requoteIntervalTicks;

            if (!shouldRequote && existingOrders.Count > 0)
                return actions;

            // Cancel existing orders for this token
            foreach (var order in existingOrders)
            {
                actions.Add(new CancelOrderAction { OrderId = order.Id, Reason = "Requote" });
            }

            // Get position
            context.Positions.TryGetValue(tokenId, out var position);
            var posSize = position?.Size ?? 0m;
            var posCost = posSize * (position?.AvgPrice ?? 0m);

            // Calculate inventory skew
            var skew = posSize * _skewFactor;

            // Calculate volatility-adaptive half spread
            var adjustedHalfSpread = CalculateAdaptiveSpread(tokenId);

            // Emergency mode: extreme inventory
            if (posCost >= _maxPositionPerToken * 0.9m)
            {
                // Stop bidding, aggressive sell
                var emergencySellPrice = Math.Max(0.01m, midPrice - adjustedHalfSpread * 0.5m);
                var emergencySize = Math.Min(_orderSize, posSize);
                if (emergencySize > 0)
                {
                    actions.Add(new PlaceOrderAction
                    {
                        TokenId = tokenId,
                        Side = "SELL",
                        Price = Math.Round(emergencySellPrice, 4),
                        Size = emergencySize,
                        Reason = $"EMERGENCY SELL | pos={posSize:F1} mid={midPrice:F4}"
                    });
                }
                _lastQuoteTick[tokenId] = _tickCount;
                return actions;
            }

            // Normal quoting: place bid and ask
            var bidPrice = midPrice - adjustedHalfSpread - skew;
            var askPrice = midPrice + adjustedHalfSpread - skew;

            // Clamp to valid prediction market range
            bidPrice = Math.Max(0.01m, Math.Min(0.99m, bidPrice));
            askPrice = Math.Max(0.01m, Math.Min(0.99m, askPrice));

            // Ensure bid < ask
            if (bidPrice >= askPrice)
            {
                bidPrice = midPrice - 0.01m;
                askPrice = midPrice + 0.01m;
                bidPrice = Math.Max(0.01m, bidPrice);
                askPrice = Math.Min(0.99m, askPrice);
            }

            // Size adjustments
            var bidSize = _orderSize;
            var askSize = _orderSize;

            // Reduce bid if approaching position limit
            if (posCost + bidPrice * bidSize > _maxPositionPerToken)
            {
                var remaining = _maxPositionPerToken - posCost;
                bidSize = remaining > 0 ? Math.Floor(remaining / bidPrice) : 0;
            }

            // Reduce bid if approaching total exposure limit
            if (totalExposure + bidPrice * bidSize > _maxTotalExposure)
            {
                var remaining = _maxTotalExposure - totalExposure;
                bidSize = remaining > 0 ? Math.Min(bidSize, Math.Floor(remaining / bidPrice)) : 0;
            }

            // Can't sell more than we hold
            if (posSize < askSize)
                askSize = posSize;

            // Balance check for bids
            if (bidSize * bidPrice > context.Balance * 0.9m)
            {
                bidSize = Math.Floor(context.Balance * 0.9m / bidPrice);
            }

            // Place bid
            if (bidSize >= 1m)
            {
                actions.Add(new PlaceOrderAction
                {
                    TokenId = tokenId,
                    Side = "BUY",
                    Price = Math.Round(bidPrice, 4),
                    Size = bidSize,
                    Reason = $"MM BID | mid={midPrice:F4} spread={adjustedHalfSpread:F4} skew={skew:F4}"
                });
            }

            // Place ask
            if (askSize >= 1m)
            {
                actions.Add(new PlaceOrderAction
                {
                    TokenId = tokenId,
                    Side = "SELL",
                    Price = Math.Round(askPrice, 4),
                    Size = askSize,
                    Reason = $"MM ASK | mid={midPrice:F4} spread={adjustedHalfSpread:F4} skew={skew:F4}"
                });
            }

            _lastQuoteTick[tokenId] = _tickCount;
            return actions;
        }

        private decimal CalculateAdaptiveSpread(string tokenId)
        {
            if (!_priceHistory.TryGetValue(tokenId, out var history) || history.Count < 3)
                return Math.Clamp(_halfSpread, MinHalfSpread, MaxHalfSpread);

            // Calculate standard deviation of recent mid prices
            var window = history.TakeLast(VolatilityWindow).ToList();
            var mean = window.Average();
            var variance = window.Sum(p => (p - mean) * (p - mean)) / window.Count;
            var stdDev = (decimal)Math.Sqrt((double)variance);

            var adjusted = _halfSpread + 2m * stdDev;
            return Math.Clamp(adjusted, MinHalfSpread, MaxHalfSpread);
        }

        private void UpdatePriceHistory(Dictionary<string, PolymarketOrderBook> orderBooks)
        {
            foreach (var kvp in orderBooks)
            {
                var mid = GetMidPrice(kvp.Value);
                if (!mid.HasValue) continue;

                if (!_priceHistory.TryGetValue(kvp.Key, out var history))
                {
                    history = new List<decimal>();
                    _priceHistory[kvp.Key] = history;
                }

                history.Add(mid.Value);

                // Keep only recent history
                if (history.Count > VolatilityWindow * 2)
                    history.RemoveRange(0, history.Count - VolatilityWindow * 2);
            }
        }

        private void SelectMarkets(StrategyContext context)
        {
            _selectedTokens.Clear();

            // Force-include tokens where we have positions
            foreach (var kvp in context.Positions)
            {
                if (kvp.Value.Size > 0)
                    _selectedTokens.Add(kvp.Key);
            }

            // Score all available tokens
            var candidates = new List<(string tokenId, decimal score)>();

            foreach (var kvp in context.OrderBooks)
            {
                var tokenId = kvp.Key;
                if (_selectedTokens.Contains(tokenId)) continue;

                var book = kvp.Value;
                var score = ScoreMarket(book);
                if (score > 0)
                    candidates.Add((tokenId, score));
            }

            // Select top N by score
            var remaining = _maxActiveMarkets - _selectedTokens.Count;
            if (remaining > 0)
            {
                foreach (var c in candidates.OrderByDescending(c => c.score).Take(remaining))
                {
                    _selectedTokens.Add(c.tokenId);
                }
            }
        }

        private decimal ScoreMarket(PolymarketOrderBook book)
        {
            var bestBid = GetBestPrice(book.Bids, ascending: false);
            var bestAsk = GetBestPrice(book.Asks, ascending: true);

            if (!bestBid.HasValue || !bestAsk.HasValue) return 0;

            var mid = (bestBid.Value + bestAsk.Value) / 2m;
            var spread = bestAsk.Value - bestBid.Value;

            // Filter: extreme prices
            if (mid < MinPrice || mid > MaxPrice) return 0;

            // Filter: spread too narrow or too wide
            if (spread < MinMarketSpread || spread > MaxMarketSpread) return 0;

            // Spread quality (40%): tighter is better, normalized to [0,1]
            var spreadScore = 1m - Math.Min(1m, spread / MaxMarketSpread);

            // Liquidity (40%): total depth on both sides
            var bidDepth = GetTotalDepth(book.Bids);
            var askDepth = GetTotalDepth(book.Asks);
            var totalDepth = bidDepth + askDepth;
            var liquidityScore = Math.Min(1m, totalDepth / 1000m); // normalize to ~1000 tokens

            // Price centrality (20%): closer to 0.50 is better
            var centrality = 1m - Math.Abs(mid - 0.5m) * 2m;

            return spreadScore * 0.4m + liquidityScore * 0.4m + centrality * 0.2m;
        }

        private static decimal? GetMidPrice(PolymarketOrderBook book)
        {
            var bestBid = GetBestPrice(book.Bids, ascending: false);
            var bestAsk = GetBestPrice(book.Asks, ascending: true);

            if (bestBid.HasValue && bestAsk.HasValue)
                return (bestBid.Value + bestAsk.Value) / 2m;
            if (bestBid.HasValue) return bestBid.Value;
            if (bestAsk.HasValue) return bestAsk.Value;
            return null;
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

        private static decimal GetTotalDepth(List<PolymarketOrderBookLevel> levels)
        {
            if (levels == null) return 0;
            decimal total = 0;
            foreach (var level in levels)
            {
                if (decimal.TryParse(level.Size, out var s))
                    total += s;
            }
            return total;
        }
    }
}
