using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    /// <summary>
    /// Monitors the real-time correlation between BTC price changes and Polymarket token probability changes.
    /// Maintains a sliding window of change rates and computes Pearson correlation coefficient.
    /// </summary>
    public class CorrelationMonitor
    {
        private readonly BtcPriceService _btcPriceService;
        private readonly Dictionary<string, TokenPriceTracker> _trackers = new();
        private readonly object _lock = new();

        private const int WindowSize = 20;

        public CorrelationMonitor(BtcPriceService btcPriceService)
        {
            _btcPriceService = btcPriceService;
        }

        /// <summary>
        /// Updates the token's current probability. Should be called each tick with the latest mid-price.
        /// </summary>
        public void UpdateTokenPrice(string tokenId, decimal currentPrice)
        {
            lock (_lock)
            {
                if (!_trackers.TryGetValue(tokenId, out var tracker))
                {
                    tracker = new TokenPriceTracker();
                    _trackers[tokenId] = tracker;
                }

                var btcPrice = _btcPriceService.CurrentPrice;
                tracker.AddSample(currentPrice, btcPrice);
            }
        }

        /// <summary>
        /// Returns the Pearson correlation coefficient between BTC returns and token probability changes.
        /// Returns 0 if insufficient data or BTC price unavailable.
        /// </summary>
        public decimal GetCorrelation(string tokenId)
        {
            lock (_lock)
            {
                if (!_trackers.TryGetValue(tokenId, out var tracker))
                    return 0m;

                return tracker.GetCorrelation();
            }
        }

        /// <summary>
        /// Returns correlation data for all tracked tokens.
        /// </summary>
        public Dictionary<string, decimal> GetAllCorrelations()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, decimal>();
                foreach (var kvp in _trackers)
                {
                    result[kvp.Key] = kvp.Value.GetCorrelation();
                }
                return result;
            }
        }

        private class TokenPriceTracker
        {
            private readonly List<decimal> _tokenChanges = new();
            private readonly List<decimal> _btcChanges = new();
            private decimal? _lastTokenPrice;
            private decimal? _lastBtcPrice;

            public void AddSample(decimal tokenPrice, decimal? btcPrice)
            {
                if (_lastTokenPrice.HasValue && _lastBtcPrice.HasValue && btcPrice.HasValue)
                {
                    var tokenChange = _lastTokenPrice.Value != 0m
                        ? (tokenPrice - _lastTokenPrice.Value) / _lastTokenPrice.Value
                        : 0m;
                    var btcChange = _lastBtcPrice.Value != 0m
                        ? (btcPrice.Value - _lastBtcPrice.Value) / _lastBtcPrice.Value
                        : 0m;

                    _tokenChanges.Add(tokenChange);
                    _btcChanges.Add(btcChange);

                    if (_tokenChanges.Count > WindowSize)
                    {
                        _tokenChanges.RemoveAt(0);
                        _btcChanges.RemoveAt(0);
                    }
                }

                _lastTokenPrice = tokenPrice;
                if (btcPrice.HasValue)
                    _lastBtcPrice = btcPrice;
            }

            public decimal GetCorrelation()
            {
                if (_tokenChanges.Count < 5) return 0m; // need minimum data

                return PearsonCorrelation(_btcChanges, _tokenChanges);
            }
        }

        internal static decimal PearsonCorrelation(List<decimal> x, List<decimal> y)
        {
            if (x.Count != y.Count || x.Count < 2) return 0m;

            var n = x.Count;
            var meanX = x.Average();
            var meanY = y.Average();

            decimal sumXY = 0, sumX2 = 0, sumY2 = 0;
            for (int i = 0; i < n; i++)
            {
                var dx = x[i] - meanX;
                var dy = y[i] - meanY;
                sumXY += dx * dy;
                sumX2 += dx * dx;
                sumY2 += dy * dy;
            }

            if (sumX2 == 0 || sumY2 == 0) return 0m;

            var denominator = (double)(sumX2 * sumY2);
            var correlation = (double)sumXY / Math.Sqrt(denominator);

            return (decimal)Math.Clamp(correlation, -1.0, 1.0);
        }
    }
}
