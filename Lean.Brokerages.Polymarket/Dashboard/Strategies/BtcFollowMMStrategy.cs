using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Strategies
{
    /// <summary>
    /// Market making strategy enhanced with BTC price momentum signals.
    /// Copies all MM logic from MarketMakingStrategy and adds a BTC signal layer
    /// that adjusts spreads and sizes based on BTC momentum, strike-aware delta,
    /// and real-time BTC↔token correlation.
    /// </summary>
    public class BtcFollowMMStrategy : IDryRunStrategy
    {
        public string Name => "BtcFollowMM";
        public string Description => "BTC-aware market making: adjusts quotes based on BTC momentum, strike delta, and correlation";

        private readonly BtcPriceService _btcPriceService;
        private readonly CorrelationMonitor _correlationMonitor;

        // === MM Configuration (same as MarketMakingStrategy) ===
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

        // === BTC Signal Configuration ===
        private decimal _momentumThreshold = 0.002m;     // 0.2% BTC momentum threshold
        private decimal _momentumSpreadMultiplier = 2.0m; // Widen unfavorable side spread
        private decimal _momentumSizeReduction = 0.5m;    // Reduce unfavorable side size
        private decimal _minCorrelation = 0.3m;           // Minimum |correlation| to apply BTC signal

        // State
        private int _tickCount;
        private int _lastMarketSelectionTick;
        private readonly Dictionary<string, List<decimal>> _priceHistory = new();
        private readonly HashSet<string> _selectedTokens = new();
        private readonly Dictionary<string, int> _lastQuoteTick = new();
        private readonly Dictionary<string, decimal?> _strikeCache = new();
        private const int VolatilityWindow = 20;
        private const int MarketSelectionIntervalTicks = 12;

        // Regex to extract dollar amounts like $74k, $74,000, $150,000, $90000 from market questions
        private static readonly Regex StrikeRegex = new(@"\$([\d,]+(?:\.\d+)?)(k|K)?", RegexOptions.Compiled);

        public BtcFollowMMStrategy(BtcPriceService btcPriceService, CorrelationMonitor correlationMonitor)
        {
            _btcPriceService = btcPriceService;
            _correlationMonitor = correlationMonitor;
        }

        public void Initialize(Dictionary<string, string> parameters)
        {
            // MM parameters
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

            // BTC signal parameters
            if (parameters.TryGetValue("MomentumThreshold", out var mt) && decimal.TryParse(mt, out var mtv))
                _momentumThreshold = mtv;
            if (parameters.TryGetValue("MomentumSpreadMultiplier", out var msm) && decimal.TryParse(msm, out var msmv))
                _momentumSpreadMultiplier = msmv;
            if (parameters.TryGetValue("MomentumSizeReduction", out var msr) && decimal.TryParse(msr, out var msrv))
                _momentumSizeReduction = msrv;
            if (parameters.TryGetValue("MinCorrelation", out var mc) && decimal.TryParse(mc, out var mcv))
                _minCorrelation = mcv;
        }

        public List<StrategyAction> Evaluate(StrategyContext context)
        {
            _tickCount++;
            var actions = new List<StrategyAction>();

            // Update price history for all tokens with order books
            UpdatePriceHistory(context.OrderBooks);

            // Update correlation monitor with current token prices
            foreach (var kvp in context.OrderBooks)
            {
                var mid = GetMidPrice(kvp.Value);
                if (mid.HasValue)
                    _correlationMonitor.UpdateTokenPrice(kvp.Key, mid.Value);
            }

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

            // Get BTC momentum signal
            var btcMomentum = _btcPriceService.Momentum;
            var btcPrice = _btcPriceService.CurrentPrice;

            // Process each selected token
            foreach (var tokenId in _selectedTokens)
            {
                if (!context.OrderBooks.TryGetValue(tokenId, out var book)) continue;

                var midPrice = GetMidPrice(book);
                if (!midPrice.HasValue) continue;
                if (midPrice.Value < MinPrice || midPrice.Value > MaxPrice) continue;

                // Resolve strike for this token's market
                var strike = GetStrikeForToken(tokenId, context.Markets);

                // Calculate BTC signal adjustments
                var btcSignal = CalculateBtcSignal(tokenId, btcMomentum, btcPrice, strike);

                var tokenActions = ProcessToken(tokenId, book, midPrice.Value, context, totalExposure, btcSignal);
                actions.AddRange(tokenActions);
            }

            return actions;
        }

        public void OnFill(SimulatedTrade trade) { }

        // === BTC Signal Calculation ===

        internal BtcSignalAdjustment CalculateBtcSignal(string tokenId, decimal btcMomentum, decimal? btcPrice, decimal? strike)
        {
            var adjustment = new BtcSignalAdjustment();

            // Check correlation gate
            var correlation = _correlationMonitor.GetCorrelation(tokenId);
            if (Math.Abs(correlation) < _minCorrelation)
            {
                adjustment.Reason = $"BTC skip (|corr|={Math.Abs(correlation):F2} < {_minCorrelation})";
                return adjustment; // No adjustment — use vanilla MM
            }

            // Calculate delta multiplier based on moneyness
            var deltaMultiplier = 1.0m;
            if (btcPrice.HasValue && strike.HasValue && btcPrice.Value > 0)
            {
                var moneyness = (btcPrice.Value - strike.Value) / btcPrice.Value;
                deltaMultiplier = Math.Clamp(1.0m - Math.Abs(moneyness) * 5m, 0.1m, 1.0m);
            }

            // Apply momentum signal scaled by correlation and delta
            var signalStrength = btcMomentum * correlation * deltaMultiplier;

            if (signalStrength > _momentumThreshold)
            {
                // BTC bullish + positive correlation → YES token likely to rise
                // Widen ask (reluctant to sell), reduce ask size
                adjustment.AskSpreadMultiplier = _momentumSpreadMultiplier;
                adjustment.AskSizeMultiplier = _momentumSizeReduction;
                adjustment.Reason = $"BTC BULL | mom={btcMomentum:F4} corr={correlation:F2} delta={deltaMultiplier:F2} sig={signalStrength:F4}";
            }
            else if (signalStrength < -_momentumThreshold)
            {
                // BTC bearish + positive correlation → YES token likely to fall
                // Widen bid (reluctant to buy), reduce bid size
                adjustment.BidSpreadMultiplier = _momentumSpreadMultiplier;
                adjustment.BidSizeMultiplier = _momentumSizeReduction;
                adjustment.Reason = $"BTC BEAR | mom={btcMomentum:F4} corr={correlation:F2} delta={deltaMultiplier:F2} sig={signalStrength:F4}";
            }
            else
            {
                adjustment.Reason = $"BTC neutral | mom={btcMomentum:F4} corr={correlation:F2} delta={deltaMultiplier:F2} sig={signalStrength:F4}";
            }

            return adjustment;
        }

        internal static decimal? ExtractStrike(string question)
        {
            if (string.IsNullOrEmpty(question)) return null;

            var match = StrikeRegex.Match(question);
            if (!match.Success) return null;

            var numStr = match.Groups[1].Value.Replace(",", "");
            if (!decimal.TryParse(numStr, out var value)) return null;

            // Handle "k" suffix (e.g., "$74k" → 74000)
            if (match.Groups[2].Success)
                value *= 1000m;

            return value;
        }

        private decimal? GetStrikeForToken(string tokenId, List<DashboardMarket> markets)
        {
            if (_strikeCache.TryGetValue(tokenId, out var cached))
                return cached;

            decimal? strike = null;
            if (markets != null)
            {
                foreach (var market in markets)
                {
                    if (market.Tokens == null) continue;
                    foreach (var token in market.Tokens)
                    {
                        if (token.TokenId == tokenId)
                        {
                            strike = ExtractStrike(market.Question);
                            break;
                        }
                    }
                    if (strike.HasValue) break;
                }
            }

            _strikeCache[tokenId] = strike;
            return strike;
        }

        // === Core MM Logic (from MarketMakingStrategy) ===

        private List<StrategyAction> ProcessToken(string tokenId, PolymarketOrderBook book,
            decimal midPrice, StrategyContext context, decimal totalExposure, BtcSignalAdjustment btcSignal)
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

            // Apply BTC signal to spread
            var bidHalfSpread = adjustedHalfSpread * btcSignal.BidSpreadMultiplier;
            var askHalfSpread = adjustedHalfSpread * btcSignal.AskSpreadMultiplier;

            // Normal quoting with BTC-adjusted spread
            var bidPrice = midPrice - bidHalfSpread - skew;
            var askPrice = midPrice + askHalfSpread - skew;

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

            // Apply BTC signal to size
            var bidSize = _orderSize * btcSignal.BidSizeMultiplier;
            var askSize = _orderSize * btcSignal.AskSizeMultiplier;

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
                    Reason = $"MM BID | mid={midPrice:F4} spread={bidHalfSpread:F4} skew={skew:F4} | {btcSignal.Reason}"
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
                    Reason = $"MM ASK | mid={midPrice:F4} spread={askHalfSpread:F4} skew={skew:F4} | {btcSignal.Reason}"
                });
            }

            _lastQuoteTick[tokenId] = _tickCount;
            return actions;
        }

        private decimal CalculateAdaptiveSpread(string tokenId)
        {
            if (!_priceHistory.TryGetValue(tokenId, out var history) || history.Count < 3)
                return Math.Clamp(_halfSpread, MinHalfSpread, MaxHalfSpread);

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

            if (mid < MinPrice || mid > MaxPrice) return 0;
            if (spread < MinMarketSpread || spread > MaxMarketSpread) return 0;

            var spreadScore = 1m - Math.Min(1m, spread / MaxMarketSpread);
            var bidDepth = GetTotalDepth(book.Bids);
            var askDepth = GetTotalDepth(book.Asks);
            var totalDepth = bidDepth + askDepth;
            var liquidityScore = Math.Min(1m, totalDepth / 1000m);
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

    /// <summary>
    /// Represents the BTC signal adjustments to be applied to MM quotes.
    /// Default values (1.0) mean no adjustment.
    /// </summary>
    public class BtcSignalAdjustment
    {
        public decimal BidSpreadMultiplier { get; set; } = 1.0m;
        public decimal AskSpreadMultiplier { get; set; } = 1.0m;
        public decimal BidSizeMultiplier { get; set; } = 1.0m;
        public decimal AskSizeMultiplier { get; set; } = 1.0m;
        public string Reason { get; set; } = "";
    }
}
