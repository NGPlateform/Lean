/*
 * QuantConnect - Polymarket Risk Management
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket.Strategies.Risk
{
    /// <summary>
    /// Risk management model designed for prediction markets.
    /// Handles:
    /// - Settlement risk: force close positions N days before market resolution
    /// - Position limits: cap single-market exposure at configurable percentage
    /// - Extreme probability protection: reduce positions when prices near 0 or 1
    /// - Total exposure limit: cap total investment at configurable percentage
    /// </summary>
    public class PredictionMarketRiskManagementModel : RiskManagementModel
    {
        private readonly int _daysBeforeSettlementToClose;
        private readonly decimal _maxSingleMarketPercent;
        private readonly decimal _extremePriceThreshold;
        private readonly decimal _extremePriceReduction;
        private readonly decimal _maxTotalExposurePercent;
        private readonly Dictionary<Symbol, DateTime> _settlementDates = new();

        /// <summary>
        /// Creates a new prediction market risk management model
        /// </summary>
        /// <param name="daysBeforeSettlementToClose">Days before settlement to force-close positions (default 3)</param>
        /// <param name="maxSingleMarketPercent">Maximum position as % of portfolio per market (default 0.10)</param>
        /// <param name="extremePriceThreshold">Price threshold for extreme probability protection (default 0.03)</param>
        /// <param name="extremePriceReduction">Position reduction factor when in extreme zone (default 0.5)</param>
        /// <param name="maxTotalExposurePercent">Maximum total exposure as % of portfolio (default 0.50)</param>
        public PredictionMarketRiskManagementModel(
            int daysBeforeSettlementToClose = 3,
            decimal maxSingleMarketPercent = 0.10m,
            decimal extremePriceThreshold = 0.03m,
            decimal extremePriceReduction = 0.50m,
            decimal maxTotalExposurePercent = 0.50m)
        {
            _daysBeforeSettlementToClose = daysBeforeSettlementToClose;
            _maxSingleMarketPercent = maxSingleMarketPercent;
            _extremePriceThreshold = extremePriceThreshold;
            _extremePriceReduction = extremePriceReduction;
            _maxTotalExposurePercent = maxTotalExposurePercent;
        }

        /// <summary>
        /// Manages risk by applying prediction market-specific rules
        /// </summary>
        public override IEnumerable<IPortfolioTarget> ManageRisk(QCAlgorithm algorithm, IPortfolioTarget[] targets)
        {
            var riskTargets = new List<IPortfolioTarget>();
            var portfolioValue = algorithm.Portfolio.TotalPortfolioValue;

            if (portfolioValue <= 0)
            {
                return riskTargets;
            }

            var totalExposure = 0m;

            foreach (var kvp in algorithm.Securities)
            {
                var symbol = kvp.Key;
                var security = kvp.Value;

                if (!security.Invested)
                {
                    continue;
                }

                var holdingsValue = Math.Abs(security.Holdings.HoldingsValue);
                var positionPercent = holdingsValue / portfolioValue;
                totalExposure += positionPercent;

                // Rule 1: Settlement risk - force close near settlement date
                if (_settlementDates.TryGetValue(symbol, out var settlementDate))
                {
                    var daysToSettlement = (settlementDate - algorithm.Time).TotalDays;
                    if (daysToSettlement <= _daysBeforeSettlementToClose && daysToSettlement > 0)
                    {
                        algorithm.Log($"Risk: Closing {symbol} - {daysToSettlement:F1} days to settlement");
                        riskTargets.Add(new PortfolioTarget(symbol, 0));
                        continue;
                    }
                }

                // Rule 2: Single market position limit
                if (positionPercent > _maxSingleMarketPercent)
                {
                    var targetQuantity = security.Holdings.Quantity *
                        (_maxSingleMarketPercent / positionPercent);
                    algorithm.Log($"Risk: Reducing {symbol} from {positionPercent:P1} to {_maxSingleMarketPercent:P1}");
                    riskTargets.Add(new PortfolioTarget(symbol, targetQuantity));
                    continue;
                }

                // Rule 3: Extreme probability protection
                var price = security.Price;
                if (price < _extremePriceThreshold || price > (1m - _extremePriceThreshold))
                {
                    var reducedQuantity = security.Holdings.Quantity * _extremePriceReduction;
                    algorithm.Log($"Risk: Reducing {symbol} by {1 - _extremePriceReduction:P0} - extreme price {price:F3}");
                    riskTargets.Add(new PortfolioTarget(symbol, reducedQuantity));
                }
            }

            // Rule 4: Total exposure limit
            if (totalExposure > _maxTotalExposurePercent)
            {
                var scaleFactor = _maxTotalExposurePercent / totalExposure;
                algorithm.Log($"Risk: Total exposure {totalExposure:P1} exceeds {_maxTotalExposurePercent:P1}, scaling down by {scaleFactor:F2}");

                foreach (var kvp in algorithm.Securities.Where(s => s.Value.Invested))
                {
                    var symbol = kvp.Key;
                    var security = kvp.Value;

                    // Don't override specific risk targets already set
                    if (riskTargets.Any(t => t.Symbol == symbol))
                    {
                        continue;
                    }

                    var scaledQuantity = security.Holdings.Quantity * scaleFactor;
                    riskTargets.Add(new PortfolioTarget(symbol, scaledQuantity));
                }
            }

            return riskTargets;
        }

        /// <summary>
        /// Registers settlement dates for newly added securities
        /// </summary>
        public override void OnSecuritiesChanged(QCAlgorithm algorithm, SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                // Try to find settlement date from custom data or symbol properties
                // For now, use a default of 30 days from addition
                if (!_settlementDates.ContainsKey(security.Symbol))
                {
                    _settlementDates[security.Symbol] = algorithm.Time.AddDays(30);
                }
            }

            foreach (var security in changes.RemovedSecurities)
            {
                _settlementDates.Remove(security.Symbol);
            }
        }

        /// <summary>
        /// Sets the settlement date for a specific symbol
        /// </summary>
        public void SetSettlementDate(Symbol symbol, DateTime settlementDate)
        {
            _settlementDates[symbol] = settlementDate;
        }
    }
}
