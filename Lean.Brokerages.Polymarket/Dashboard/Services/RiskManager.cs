using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Brokerages.Polymarket.Dashboard.Models;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class RiskManager
    {
        private readonly RiskSettings _settings;
        private readonly ConcurrentDictionary<string, decimal> _marketExposure = new();
        private decimal _dailySpending;
        private decimal _dailyPnl;
        private decimal _peakEquity;
        private decimal _currentEquity;
        private DateTime _lastResetDate = DateTime.UtcNow.Date;

        public RiskManager(RiskSettings settings)
        {
            _settings = settings;
        }

        public RiskCheckResult ValidateOrder(string tokenId, decimal price, decimal size, string side)
        {
            ResetDailyIfNeeded();

            var orderCost = price * size;

            // Per-trade limit
            if (orderCost > _settings.PerTradePositionLimit)
                return RiskCheckResult.Deny(
                    $"Per-trade limit exceeded: ${orderCost:F2} > ${_settings.PerTradePositionLimit:F2}");

            // Per-market limit
            var currentMarketExposure = _marketExposure.GetValueOrDefault(tokenId, 0m);
            if (currentMarketExposure + orderCost > _settings.PerMarketPositionLimit)
                return RiskCheckResult.Deny(
                    $"Per-market limit exceeded: ${currentMarketExposure + orderCost:F2} > ${_settings.PerMarketPositionLimit:F2} for {tokenId.Substring(0, Math.Min(8, tokenId.Length))}...");

            // Daily spending limit
            if (_dailySpending + orderCost > _settings.DailySpendingLimit)
                return RiskCheckResult.Deny(
                    $"Daily spending limit exceeded: ${_dailySpending + orderCost:F2} > ${_settings.DailySpendingLimit:F2}");

            // Total exposure limit
            var totalExposure = _marketExposure.Values.Sum() + orderCost;
            if (totalExposure > _settings.TotalExposureLimit)
                return RiskCheckResult.Deny(
                    $"Total exposure limit exceeded: ${totalExposure:F2} > ${_settings.TotalExposureLimit:F2}");

            // Daily loss limit
            if (_dailyPnl < -_settings.DailyLossLimit)
                return RiskCheckResult.Deny(
                    $"Daily loss limit reached: ${_dailyPnl:F2} < -${_settings.DailyLossLimit:F2}");

            // Max drawdown
            var drawdown = _peakEquity - _currentEquity;
            if (drawdown > _settings.MaxDrawdownLimit)
                return RiskCheckResult.Deny(
                    $"Max drawdown exceeded: ${drawdown:F2} > ${_settings.MaxDrawdownLimit:F2}");

            return RiskCheckResult.Allow();
        }

        public void RecordOrderPlaced(string tokenId, decimal cost)
        {
            ResetDailyIfNeeded();
            _dailySpending += cost;
            _marketExposure.AddOrUpdate(tokenId, cost, (_, existing) => existing + cost);
        }

        public void UpdatePnl(decimal dailyPnl, decimal currentEquity)
        {
            _dailyPnl = dailyPnl;
            _currentEquity = currentEquity;
            if (currentEquity > _peakEquity)
                _peakEquity = currentEquity;
        }

        public void UpdateSettings(RiskSettings newSettings)
        {
            _settings.DailySpendingLimit = newSettings.DailySpendingLimit;
            _settings.TotalExposureLimit = newSettings.TotalExposureLimit;
            _settings.PerMarketPositionLimit = newSettings.PerMarketPositionLimit;
            _settings.PerTradePositionLimit = newSettings.PerTradePositionLimit;
            _settings.DailyLossLimit = newSettings.DailyLossLimit;
            _settings.MaxDrawdownLimit = newSettings.MaxDrawdownLimit;
            _settings.PerStrategyCapitalLimits = newSettings.PerStrategyCapitalLimits
                ?? new Dictionary<string, decimal>();
        }

        public RiskStatus GetStatus()
        {
            ResetDailyIfNeeded();
            var status = new RiskStatus
            {
                DailySpending = _dailySpending,
                DailySpendingLimit = _settings.DailySpendingLimit,
                TotalExposure = _marketExposure.Values.Sum(),
                TotalExposureLimit = _settings.TotalExposureLimit,
                DailyPnl = _dailyPnl,
                DailyLossLimit = _settings.DailyLossLimit,
                MaxDrawdown = _peakEquity - _currentEquity,
                MaxDrawdownLimit = _settings.MaxDrawdownLimit,
                PerMarketPositionLimit = _settings.PerMarketPositionLimit,
                PerTradePositionLimit = _settings.PerTradePositionLimit
            };

            // Generate alerts
            CheckAlert(status, "DailySpending", _dailySpending, _settings.DailySpendingLimit);
            CheckAlert(status, "TotalExposure", status.TotalExposure, _settings.TotalExposureLimit);
            CheckAlert(status, "DailyLoss", -_dailyPnl, _settings.DailyLossLimit);
            CheckAlert(status, "Drawdown", status.MaxDrawdown, _settings.MaxDrawdownLimit);

            return status;
        }

        public List<RiskAlert> GetAlerts()
        {
            return GetStatus().Alerts;
        }

        private void CheckAlert(RiskStatus status, string metric, decimal current, decimal limit)
        {
            if (limit <= 0) return;
            var ratio = current / limit;
            if (ratio >= 0.9m)
            {
                status.Alerts.Add(new RiskAlert
                {
                    Level = ratio >= 1.0m ? "critical" : "warning",
                    Message = $"{metric}: ${current:F2} / ${limit:F2} ({ratio:P0})",
                    Metric = metric,
                    Current = current,
                    Limit = limit
                });
            }
        }

        private void ResetDailyIfNeeded()
        {
            var today = DateTime.UtcNow.Date;
            if (_lastResetDate < today)
            {
                _dailySpending = 0;
                _dailyPnl = 0;
                _lastResetDate = today;
            }
        }
    }
}
