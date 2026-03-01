/*
 * QuantConnect - Polymarket Portfolio Construction
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Data.UniverseSelection;

namespace QuantConnect.Brokerages.Polymarket.Strategies.Portfolio
{
    /// <summary>
    /// Portfolio construction model using the Kelly Criterion adapted for binary outcome markets.
    ///
    /// Kelly fraction for binary outcomes:
    ///   f* = (p × b - q) / b
    /// where p = estimated probability, q = 1 - p, b = odds = (1/price - 1)
    ///
    /// Uses Half-Kelly (f*/2) for risk reduction.
    /// </summary>
    public class KellyPortfolioConstructionModel : PortfolioConstructionModel
    {
        private readonly decimal _kellyFraction;
        private readonly decimal _maxPositionSize;
        private readonly decimal _minConfidence;

        /// <summary>
        /// Creates a new Kelly criterion portfolio construction model
        /// </summary>
        /// <param name="kellyFraction">Fraction of full Kelly to use (default 0.5 = Half-Kelly)</param>
        /// <param name="maxPositionSize">Maximum position as fraction of portfolio (default 0.10 = 10%)</param>
        /// <param name="minConfidence">Minimum insight confidence to act on (default 0.5)</param>
        public KellyPortfolioConstructionModel(
            decimal kellyFraction = 0.5m,
            decimal maxPositionSize = 0.10m,
            decimal minConfidence = 0.5m)
        {
            _kellyFraction = kellyFraction;
            _maxPositionSize = maxPositionSize;
            _minConfidence = minConfidence;
        }

        /// <summary>
        /// Determines the target percent for each active insight using Kelly criterion
        /// </summary>
        protected override Dictionary<Insight, double> DetermineTargetPercent(List<Insight> activeInsights)
        {
            var targets = new Dictionary<Insight, double>();

            foreach (var insight in activeInsights)
            {
                if (insight.Direction == InsightDirection.Flat)
                {
                    targets[insight] = 0;
                    continue;
                }

                var confidence = insight.Confidence ?? 0.5;
                if (confidence < (double)_minConfidence)
                {
                    targets[insight] = 0;
                    continue;
                }

                // Estimate the probability of profit based on insight direction and confidence
                var probability = insight.Direction == InsightDirection.Up ? confidence : 1.0 - confidence;

                // Get current market price (reference value from the insight)
                var marketPrice = insight.ReferenceValue > 0 ? (double)insight.ReferenceValue : 0.5;

                // Clamp market price to valid prediction market range
                marketPrice = Math.Max(0.01, Math.Min(0.99, marketPrice));

                // Calculate odds for binary outcome
                // b = (1/price - 1) for buying YES token
                double odds;
                if (insight.Direction == InsightDirection.Up)
                {
                    odds = (1.0 / marketPrice) - 1.0;
                }
                else
                {
                    // For selling/shorting, odds are inverse
                    odds = marketPrice / (1.0 - marketPrice);
                }

                if (odds <= 0)
                {
                    targets[insight] = 0;
                    continue;
                }

                // Kelly criterion: f* = (p * b - q) / b
                var q = 1.0 - probability;
                var kelly = (probability * odds - q) / odds;

                // Apply fractional Kelly
                kelly *= (double)_kellyFraction;

                // Clamp to max position size
                kelly = Math.Max(0, Math.Min((double)_maxPositionSize, kelly));

                // Apply direction
                if (insight.Direction == InsightDirection.Down)
                {
                    kelly = -kelly;
                }

                targets[insight] = kelly;
            }

            return targets;
        }
    }
}
