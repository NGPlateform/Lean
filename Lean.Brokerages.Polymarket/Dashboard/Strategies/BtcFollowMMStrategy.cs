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
    /// real-time BTC↔token correlation, TTE-aware scaling, and up/down asymmetry.
    /// </summary>
    public class BtcFollowMMStrategy : IDryRunStrategy
    {
        public string Name => "BtcFollowMM";
        public string Description => "BTC-aware market making: adjusts quotes based on BTC momentum, strike delta, correlation, TTE, and asymmetry";

        private readonly BtcPriceService _btcPriceService;
        private readonly CorrelationMonitor _correlationMonitor;
        private readonly SentimentService _sentimentService;

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
        private decimal _baseMomentumSpreadMultiplier = 2.0m; // Base spread multiplier (scaled by TTE)
        private decimal _momentumSizeReduction = 0.5m;    // Reduce unfavorable side size
        private decimal _minCorrelation = 0.3m;           // Minimum |correlation| to apply BTC signal
        private decimal _downMoveMultiplierScale = 0.5m;  // BTC down-moves get reduced multiplier (asymmetry)
        private bool _enableSentiment = true;               // Enable sentiment overlay when service available

        // State
        private int _tickCount;
        private int _lastMarketSelectionTick;
        private readonly Dictionary<string, List<decimal>> _priceHistory = new();
        private readonly HashSet<string> _selectedTokens = new();
        private readonly Dictionary<string, int> _lastQuoteTick = new();
        private readonly Dictionary<string, decimal?> _strikeCache = new();
        private readonly Dictionary<string, DateTime?> _expiryCache = new();
        private const int VolatilityWindow = 20;
        private const int MarketSelectionIntervalTicks = 12;

        // Regex to extract dollar amounts like $74k, $74,000, $150,000, $90000 from market questions
        private static readonly Regex StrikeRegex = new(@"\$([\d,]+(?:\.\d+)?)(k|K)?", RegexOptions.Compiled);

        public BtcFollowMMStrategy(BtcPriceService btcPriceService, CorrelationMonitor correlationMonitor)
            : this(btcPriceService, correlationMonitor, sentimentService: null)
        {
        }

        public BtcFollowMMStrategy(BtcPriceService btcPriceService, CorrelationMonitor correlationMonitor, SentimentService sentimentService)
        {
            _btcPriceService = btcPriceService;
            _correlationMonitor = correlationMonitor;
            _sentimentService = sentimentService;
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
                _baseMomentumSpreadMultiplier = msmv;
            if (parameters.TryGetValue("MomentumSizeReduction", out var msr) && decimal.TryParse(msr, out var msrv))
                _momentumSizeReduction = msrv;
            if (parameters.TryGetValue("MinCorrelation", out var mc) && decimal.TryParse(mc, out var mcv))
                _minCorrelation = mcv;
            if (parameters.TryGetValue("DownMoveMultiplierScale", out var dm) && decimal.TryParse(dm, out var dmv))
                _downMoveMultiplierScale = dmv;
            if (parameters.TryGetValue("EnableSentiment", out var es) && bool.TryParse(es, out var esv))
                _enableSentiment = esv;
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

                // Resolve strike and expiry for this token's market
                var strike = GetStrikeForToken(tokenId, context.Markets);
                var expiry = GetExpiryForToken(tokenId, context.Markets);

                // Calculate BTC signal adjustments (with TTE and asymmetry)
                var btcSignal = CalculateBtcSignal(tokenId, btcMomentum, btcPrice, strike, expiry, context.CurrentTime);

                var tokenActions = ProcessToken(tokenId, book, midPrice.Value, context, totalExposure, btcSignal);
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
                ["RequoteIntervalTicks"] = _requoteIntervalTicks.ToString(),
                ["MomentumThreshold"] = _momentumThreshold.ToString(),
                ["MomentumSpreadMultiplier"] = _baseMomentumSpreadMultiplier.ToString(),
                ["MomentumSizeReduction"] = _momentumSizeReduction.ToString(),
                ["MinCorrelation"] = _minCorrelation.ToString(),
                ["DownMoveMultiplierScale"] = _downMoveMultiplierScale.ToString(),
                ["EnableSentiment"] = _enableSentiment.ToString()
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

                    var correlation = _correlationMonitor.GetCorrelation(tokenId);
                    components["BtcCorrelation"] = correlation;
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

        // === BTC Signal Calculation ===

        internal BtcSignalAdjustment CalculateBtcSignal(string tokenId, decimal btcMomentum, decimal? btcPrice,
            decimal? strike, DateTime? expiry = null, DateTime? currentTime = null)
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

            // Calculate TTE-aware spread multiplier
            var tteMultiplier = CalculateTteMultiplier(expiry, currentTime);

            // Calculate asymmetry-adjusted spread multiplier
            // BTC up-moves have ~4.8x stronger impact than down-moves (from analysis)
            var effectiveSpreadMultiplier = _baseMomentumSpreadMultiplier * tteMultiplier;

            // Apply momentum signal scaled by correlation and delta
            var signalStrength = btcMomentum * correlation * deltaMultiplier;

            if (signalStrength > _momentumThreshold)
            {
                // BTC bullish + positive correlation → YES token likely to rise
                // Widen ask (reluctant to sell), reduce ask size
                // Up-moves: use full multiplier (stronger impact per analysis)
                adjustment.AskSpreadMultiplier = effectiveSpreadMultiplier;
                adjustment.AskSizeMultiplier = _momentumSizeReduction;
                adjustment.Reason = $"BTC BULL | mom={btcMomentum:F4} corr={correlation:F2} delta={deltaMultiplier:F2} tte={tteMultiplier:F2} sig={signalStrength:F4}";
            }
            else if (signalStrength < -_momentumThreshold)
            {
                // BTC bearish + positive correlation → YES token likely to fall
                // Widen bid (reluctant to buy), reduce bid size
                // Down-moves: scale only the excess above 1.0 (asymmetry analysis shows weaker impact)
                // e.g., base=2.0, tte=1.25, scale=0.5 → 1 + (2.0*1.25 - 1)*0.5 = 1.75 vs up's 2.5
                var bearMultiplier = 1.0m + (effectiveSpreadMultiplier - 1.0m) * _downMoveMultiplierScale;
                adjustment.BidSpreadMultiplier = Math.Max(1.0m, bearMultiplier);
                adjustment.BidSizeMultiplier = _momentumSizeReduction;
                adjustment.Reason = $"BTC BEAR | mom={btcMomentum:F4} corr={correlation:F2} delta={deltaMultiplier:F2} tte={tteMultiplier:F2} sig={signalStrength:F4}";
            }
            else
            {
                adjustment.Reason = $"BTC neutral | mom={btcMomentum:F4} corr={correlation:F2} delta={deltaMultiplier:F2} tte={tteMultiplier:F2} sig={signalStrength:F4}";
            }

            // Sentiment overlay: multiplicative combination
            if (_enableSentiment && _sentimentService != null && _sentimentService.IsReady)
            {
                var sentimentMultiplier = _sentimentService.GetSentimentSpreadMultiplier();
                var sentimentBias = _sentimentService.GetSentimentDirectionalBias();

                adjustment.BidSpreadMultiplier *= sentimentMultiplier;
                adjustment.AskSpreadMultiplier *= sentimentMultiplier;

                // Directional bias: positive = bullish contrarian
                if (sentimentBias > 0)
                {
                    // Bullish bias: widen ask more (reluctant to sell), increase bid size
                    adjustment.AskSpreadMultiplier *= 1.0m + sentimentBias * 0.3m;
                    adjustment.BidSizeMultiplier *= 1.0m + sentimentBias * 0.2m;
                }
                else if (sentimentBias < 0)
                {
                    // Bearish bias: widen bid more (reluctant to buy), increase ask size
                    adjustment.BidSpreadMultiplier *= 1.0m + Math.Abs(sentimentBias) * 0.3m;
                    adjustment.AskSizeMultiplier *= 1.0m + Math.Abs(sentimentBias) * 0.2m;
                }

                adjustment.Reason += $" | sent={sentimentMultiplier:F2} bias={sentimentBias:F2} fgi={_sentimentService.FearGreedIndex} fr={_sentimentService.FundingRate:F6}";
            }

            return adjustment;
        }

        /// <summary>
        /// Calculates TTE-aware spread multiplier scaling.
        /// Closer to expiry → stronger BTC signal response (higher gamma).
        /// Based on TTE analysis: corr 0.73 at 3-7d → 0.83 at 1-3d → 0.80 at &lt;1d.
        /// </summary>
        internal static decimal CalculateTteMultiplier(DateTime? expiry, DateTime? currentTime)
        {
            if (!expiry.HasValue || !currentTime.HasValue)
                return 1.0m; // Default: no TTE adjustment

            var tte = expiry.Value - currentTime.Value;
            var tteDays = (decimal)tte.TotalDays;

            if (tteDays <= 0)
                return 1.5m; // Expired/at expiry: max response
            if (tteDays < 1)
                return 1.5m; // <1 day: max response
            if (tteDays < 3)
                return 1.25m; // 1-3 days: strong response
            if (tteDays < 7)
                return 1.0m; // 3-7 days: default
            return 0.75m; // >7 days: weaker signal
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

        private DateTime? GetExpiryForToken(string tokenId, List<DashboardMarket> markets)
        {
            if (_expiryCache.TryGetValue(tokenId, out var cached))
                return cached;

            DateTime? expiry = null;
            if (markets != null)
            {
                foreach (var market in markets)
                {
                    if (market.Tokens == null) continue;
                    foreach (var token in market.Tokens)
                    {
                        if (token.TokenId == tokenId)
                        {
                            if (!string.IsNullOrEmpty(market.EndDate) &&
                                DateTime.TryParse(market.EndDate, out var parsed))
                            {
                                expiry = parsed;
                            }
                            break;
                        }
                    }
                    if (expiry.HasValue) break;
                }
            }

            _expiryCache[tokenId] = expiry;
            return expiry;
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
