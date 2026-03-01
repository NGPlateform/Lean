/*
 * QuantConnect - Polymarket Alpha Models
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket.Strategies.Alphas
{
    /// <summary>
    /// Alpha model that detects cross-market arbitrage opportunities in Polymarket.
    /// When YES + NO token prices deviate significantly from 1.00, generates signals
    /// to capture the mispricing.
    /// </summary>
    public class CrossMarketArbitrageAlpha : AlphaModel
    {
        private readonly decimal _deviationThreshold;
        private readonly TimeSpan _insightPeriod;
        private readonly Dictionary<string, SymbolPair> _symbolPairs = new();

        /// <summary>
        /// Creates a new cross-market arbitrage alpha model
        /// </summary>
        /// <param name="deviationThreshold">Minimum price sum deviation from 1.00 to generate signal (default 0.02 = 2 cents)</param>
        /// <param name="insightPeriod">Duration of generated insights</param>
        public CrossMarketArbitrageAlpha(decimal deviationThreshold = 0.02m, TimeSpan? insightPeriod = null)
        {
            _deviationThreshold = deviationThreshold;
            _insightPeriod = insightPeriod ?? TimeSpan.FromMinutes(5);
            Name = nameof(CrossMarketArbitrageAlpha);
        }

        /// <summary>
        /// Generates arbitrage insights based on YES+NO price deviations from 1.00
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            var insights = new List<Insight>();

            foreach (var pair in _symbolPairs.Values)
            {
                var yesSec = algorithm.Securities[pair.YesSymbol];
                var noSec = algorithm.Securities[pair.NoSymbol];

                var yesBid = yesSec.BidPrice;
                var yesAsk = yesSec.AskPrice;
                var noBid = noSec.BidPrice;
                var noAsk = noSec.AskPrice;

                if (yesBid <= 0 || yesAsk <= 0 || noBid <= 0 || noAsk <= 0)
                {
                    continue;
                }

                // Check overpriced condition: sell both when bid sum > 1 + threshold
                var bidSum = yesBid + noBid;
                if (bidSum > 1.0m + _deviationThreshold)
                {
                    var magnitude = (double)(bidSum - 1.0m);
                    insights.Add(Insight.Price(pair.YesSymbol, _insightPeriod, InsightDirection.Down,
                        magnitude, confidence: 0.9, weight: 1.0, sourceModel: Name));
                    insights.Add(Insight.Price(pair.NoSymbol, _insightPeriod, InsightDirection.Down,
                        magnitude, confidence: 0.9, weight: 1.0, sourceModel: Name));
                }

                // Check underpriced condition: buy both when ask sum < 1 - threshold
                var askSum = yesAsk + noAsk;
                if (askSum < 1.0m - _deviationThreshold)
                {
                    var magnitude = (double)(1.0m - askSum);
                    insights.Add(Insight.Price(pair.YesSymbol, _insightPeriod, InsightDirection.Up,
                        magnitude, confidence: 0.9, weight: 1.0, sourceModel: Name));
                    insights.Add(Insight.Price(pair.NoSymbol, _insightPeriod, InsightDirection.Up,
                        magnitude, confidence: 0.9, weight: 1.0, sourceModel: Name));
                }
            }

            return insights;
        }

        /// <summary>
        /// Registers YES/NO symbol pairs when securities change
        /// </summary>
        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                var ticker = security.Symbol.Value.ToUpperInvariant();
                string baseName;
                bool isYes;

                if (ticker.EndsWith("YES"))
                {
                    baseName = ticker[..^3];
                    isYes = true;
                }
                else if (ticker.EndsWith("NO"))
                {
                    baseName = ticker[..^2];
                    isYes = false;
                }
                else
                {
                    continue;
                }

                if (!_symbolPairs.TryGetValue(baseName, out var pair))
                {
                    pair = new SymbolPair { BaseName = baseName };
                    _symbolPairs[baseName] = pair;
                }

                if (isYes) pair.YesSymbol = security.Symbol;
                else pair.NoSymbol = security.Symbol;
            }
        }

        private class SymbolPair
        {
            public string BaseName { get; set; }
            public Symbol YesSymbol { get; set; }
            public Symbol NoSymbol { get; set; }
        }
    }

    /// <summary>
    /// Alpha model that generates mean reversion signals when prediction market prices
    /// move sharply away from recent averages.
    /// </summary>
    public class ProbabilityMeanReversionAlpha : AlphaModel
    {
        private readonly int _lookbackPeriod;
        private readonly decimal _deviationMultiplier;
        private readonly TimeSpan _insightPeriod;
        private readonly Dictionary<Symbol, List<decimal>> _priceHistory = new();

        /// <summary>
        /// Creates a new mean reversion alpha model
        /// </summary>
        /// <param name="lookbackPeriod">Number of observations for mean calculation</param>
        /// <param name="deviationMultiplier">Standard deviation multiplier for signal threshold</param>
        /// <param name="insightPeriod">Duration of generated insights</param>
        public ProbabilityMeanReversionAlpha(int lookbackPeriod = 60, decimal deviationMultiplier = 2.0m,
            TimeSpan? insightPeriod = null)
        {
            _lookbackPeriod = lookbackPeriod;
            _deviationMultiplier = deviationMultiplier;
            _insightPeriod = insightPeriod ?? TimeSpan.FromMinutes(15);
            Name = nameof(ProbabilityMeanReversionAlpha);
        }

        /// <summary>
        /// Generates mean reversion insights
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            var insights = new List<Insight>();

            foreach (var kvp in _priceHistory)
            {
                var symbol = kvp.Key;
                var history = kvp.Value;

                if (!algorithm.Securities.TryGetValue(symbol, out var security) || security.Price <= 0)
                {
                    continue;
                }

                var currentPrice = security.Price;

                // Add current price to history
                history.Add(currentPrice);
                if (history.Count > _lookbackPeriod)
                {
                    history.RemoveAt(0);
                }

                if (history.Count < _lookbackPeriod)
                {
                    continue;
                }

                var mean = history.Average();
                var variance = history.Average(p => (p - mean) * (p - mean));
                var stdDev = (decimal)Math.Sqrt((double)variance);

                if (stdDev <= 0)
                {
                    continue;
                }

                var zScore = (currentPrice - mean) / stdDev;

                // Price significantly above mean → expect reversion down
                if (zScore > _deviationMultiplier)
                {
                    insights.Add(Insight.Price(symbol, _insightPeriod, InsightDirection.Down,
                        (double)Math.Abs(zScore - _deviationMultiplier) * 0.01,
                        confidence: Math.Min(0.9, (double)Math.Abs(zScore) / 5.0),
                        sourceModel: Name));
                }
                // Price significantly below mean → expect reversion up
                else if (zScore < -_deviationMultiplier)
                {
                    insights.Add(Insight.Price(symbol, _insightPeriod, InsightDirection.Up,
                        (double)Math.Abs(zScore + _deviationMultiplier) * 0.01,
                        confidence: Math.Min(0.9, (double)Math.Abs(zScore) / 5.0),
                        sourceModel: Name));
                }
            }

            return insights;
        }

        /// <summary>
        /// Tracks securities for mean reversion analysis
        /// </summary>
        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                _priceHistory[security.Symbol] = new List<decimal>();
            }

            foreach (var security in changes.RemovedSecurities)
            {
                _priceHistory.Remove(security.Symbol);
            }
        }
    }

    /// <summary>
    /// Alpha model that detects correlation between related prediction markets
    /// and generates lead-lag signals.
    /// </summary>
    public class CrossMarketCorrelationAlpha : AlphaModel
    {
        private readonly int _correlationWindow;
        private readonly double _correlationThreshold;
        private readonly TimeSpan _insightPeriod;
        private readonly Dictionary<Symbol, List<decimal>> _returnHistory = new();

        /// <summary>
        /// Creates a new cross-market correlation alpha model
        /// </summary>
        public CrossMarketCorrelationAlpha(int correlationWindow = 30, double correlationThreshold = 0.7,
            TimeSpan? insightPeriod = null)
        {
            _correlationWindow = correlationWindow;
            _correlationThreshold = correlationThreshold;
            _insightPeriod = insightPeriod ?? TimeSpan.FromMinutes(10);
            Name = nameof(CrossMarketCorrelationAlpha);
        }

        /// <summary>
        /// Generates correlation-based insights
        /// </summary>
        public override IEnumerable<Insight> Update(QCAlgorithm algorithm, Slice data)
        {
            var insights = new List<Insight>();
            var symbols = _returnHistory.Keys.ToList();

            // Update return histories
            foreach (var symbol in symbols)
            {
                if (!algorithm.Securities.TryGetValue(symbol, out var sec) || sec.Price <= 0)
                {
                    continue;
                }

                var returns = _returnHistory[symbol];
                if (returns.Count > 0)
                {
                    var lastPrice = returns.Last();
                    if (lastPrice > 0)
                    {
                        returns.Add((sec.Price - lastPrice) / lastPrice);
                    }
                    else
                    {
                        returns.Add(0);
                    }
                }
                else
                {
                    returns.Add(sec.Price);
                }

                if (returns.Count > _correlationWindow + 1)
                {
                    returns.RemoveAt(0);
                }
            }

            // Check pairwise correlations for lead-lag
            for (var i = 0; i < symbols.Count; i++)
            {
                for (var j = i + 1; j < symbols.Count; j++)
                {
                    var histA = _returnHistory[symbols[i]];
                    var histB = _returnHistory[symbols[j]];

                    if (histA.Count < _correlationWindow || histB.Count < _correlationWindow)
                    {
                        continue;
                    }

                    // Check if A leads B (lagged correlation)
                    var recentA = histA.Skip(histA.Count - _correlationWindow).Take(_correlationWindow - 1).ToList();
                    var recentB = histB.Skip(histB.Count - _correlationWindow + 1).ToList();

                    if (recentA.Count == recentB.Count && recentA.Count > 5)
                    {
                        var corr = ComputeCorrelation(recentA, recentB);

                        if (Math.Abs(corr) > _correlationThreshold)
                        {
                            // A's latest move predicts B's next move
                            var latestReturnA = histA.Last();
                            if (latestReturnA > 0.001m)
                            {
                                var direction = corr > 0 ? InsightDirection.Up : InsightDirection.Down;
                                insights.Add(Insight.Price(symbols[j], _insightPeriod, direction,
                                    (double)Math.Abs(latestReturnA) * Math.Abs(corr),
                                    confidence: Math.Abs(corr),
                                    sourceModel: Name));
                            }
                            else if (latestReturnA < -0.001m)
                            {
                                var direction = corr > 0 ? InsightDirection.Down : InsightDirection.Up;
                                insights.Add(Insight.Price(symbols[j], _insightPeriod, direction,
                                    (double)Math.Abs(latestReturnA) * Math.Abs(corr),
                                    confidence: Math.Abs(corr),
                                    sourceModel: Name));
                            }
                        }
                    }
                }
            }

            return insights;
        }

        /// <summary>
        /// Tracks securities for correlation analysis
        /// </summary>
        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var sec in changes.AddedSecurities)
            {
                _returnHistory[sec.Symbol] = new List<decimal>();
            }

            foreach (var sec in changes.RemovedSecurities)
            {
                _returnHistory.Remove(sec.Symbol);
            }
        }

        private static double ComputeCorrelation(List<decimal> a, List<decimal> b)
        {
            var n = Math.Min(a.Count, b.Count);
            if (n < 3) return 0;

            var meanA = a.Take(n).Average();
            var meanB = b.Take(n).Average();

            double cov = 0, varA = 0, varB = 0;
            for (var i = 0; i < n; i++)
            {
                var da = (double)(a[i] - meanA);
                var db = (double)(b[i] - meanB);
                cov += da * db;
                varA += da * da;
                varB += db * db;
            }

            var denom = Math.Sqrt(varA * varB);
            return denom > 0 ? cov / denom : 0;
        }
    }
}
