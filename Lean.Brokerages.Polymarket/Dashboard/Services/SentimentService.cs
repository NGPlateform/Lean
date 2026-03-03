using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    /// <summary>
    /// Background service that polls Fear &amp; Greed Index and Binance funding rate.
    /// Provides sentiment-based spread multipliers and directional bias for strategy use.
    /// </summary>
    public class SentimentService : BackgroundService
    {
        private readonly HttpClient _http;
        private readonly ILogger<SentimentService> _logger;
        private readonly object _lock = new();

        private const string FearGreedUrl = "https://api.alternative.me/fng/?limit=1";
        private const string FundingRateUrl = "https://fapi.binance.com/fapi/v1/premiumIndex?symbol=BTCUSDT";
        private const int FearGreedIntervalMs = 30 * 60 * 1000; // 30 minutes
        private const int FundingRateIntervalMs = 60 * 1000;     // 60 seconds

        // Funding rate normalization: 0.01% (0.0001) is the baseline 8-hour rate
        private const decimal FundingRateBaseline = 0.0001m;
        private const decimal FundingRateMaxSignal = 0.001m; // ±0.1% clamp for signal

        // Sentiment spread multiplier bounds
        private const decimal MinSpreadMultiplier = 0.8m;
        private const decimal MaxSpreadMultiplier = 1.5m;

        // FGI classification thresholds
        private const int ExtremeFearThreshold = 25;
        private const int FearThreshold = 45;
        private const int NeutralUpperThreshold = 55;
        private const int GreedThreshold = 75;

        // State
        private int _fearGreedIndex = -1;
        private string _fearGreedClassification = "Unknown";
        private decimal _fundingRate;
        private decimal _fundingRateSignal;
        private bool _hasFearGreed;
        private bool _hasFundingRate;

        /// <summary>Fear &amp; Greed Index (0-100, -1 if not yet fetched).</summary>
        public int FearGreedIndex
        {
            get { lock (_lock) return _fearGreedIndex; }
        }

        /// <summary>Fear &amp; Greed classification string.</summary>
        public string FearGreedClassification
        {
            get { lock (_lock) return _fearGreedClassification; }
        }

        /// <summary>Raw Binance BTCUSDT funding rate (e.g. 0.0001).</summary>
        public decimal FundingRate
        {
            get { lock (_lock) return _fundingRate; }
        }

        /// <summary>Normalized funding rate signal [-1, 1]. 0.01% baseline → 0.0.</summary>
        public decimal FundingRateSignal
        {
            get { lock (_lock) return _fundingRateSignal; }
        }

        /// <summary>True when both data sources have been fetched at least once.</summary>
        public bool IsReady
        {
            get { lock (_lock) return _hasFearGreed && _hasFundingRate; }
        }

        public SentimentService(ILogger<SentimentService> logger, HttpClient httpClient = null)
        {
            _logger = logger;
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SentimentService starting (FGI every {FgiMin}min, FundingRate every {FrSec}s)...",
                FearGreedIntervalMs / 60000, FundingRateIntervalMs / 1000);

            var nextFgi = Task.CompletedTask;
            var nextFr = Task.CompletedTask;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Poll FGI
                try
                {
                    await PollFearGreedAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to poll Fear & Greed Index");
                }

                // Poll funding rate
                try
                {
                    await PollFundingRateAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to poll Binance funding rate");
                }

                // Use Task.WhenAny for dual-timer pattern
                nextFgi = Task.Delay(FearGreedIntervalMs, stoppingToken);
                nextFr = Task.Delay(FundingRateIntervalMs, stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(nextFgi, nextFr);

                    if (stoppingToken.IsCancellationRequested) break;

                    if (completed == nextFgi)
                    {
                        try { await PollFearGreedAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to poll Fear & Greed Index"); }
                        nextFgi = Task.Delay(FearGreedIntervalMs, stoppingToken);
                    }
                    else
                    {
                        try { await PollFundingRateAsync(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to poll Binance funding rate"); }
                        nextFr = Task.Delay(FundingRateIntervalMs, stoppingToken);
                    }
                }
            }
        }

        internal async Task PollFearGreedAsync()
        {
            var json = await _http.GetStringAsync(FearGreedUrl);
            var obj = JObject.Parse(json);
            var dataArr = obj["data"] as JArray;
            if (dataArr == null || dataArr.Count == 0) return;

            var entry = dataArr[0];
            var valueStr = entry["value"]?.ToString();
            var classification = entry["value_classification"]?.ToString();

            if (string.IsNullOrEmpty(valueStr) ||
                !int.TryParse(valueStr, out var value))
                return;

            lock (_lock)
            {
                _fearGreedIndex = value;
                _fearGreedClassification = ClassifyFearGreed(value);
                _hasFearGreed = true;
            }
        }

        internal async Task PollFundingRateAsync()
        {
            var json = await _http.GetStringAsync(FundingRateUrl);
            var obj = JObject.Parse(json);
            var rateStr = obj["lastFundingRate"]?.ToString();

            if (string.IsNullOrEmpty(rateStr) ||
                !decimal.TryParse(rateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                return;

            lock (_lock)
            {
                _fundingRate = rate;
                _fundingRateSignal = NormalizeFundingRate(rate);
                _hasFundingRate = true;
            }
        }

        /// <summary>Injects a Fear &amp; Greed value directly (for testing).</summary>
        internal void InjectFearGreed(int value)
        {
            lock (_lock)
            {
                _fearGreedIndex = Math.Clamp(value, 0, 100);
                _fearGreedClassification = ClassifyFearGreed(_fearGreedIndex);
                _hasFearGreed = true;
            }
        }

        /// <summary>Injects a funding rate directly (for testing).</summary>
        internal void InjectFundingRate(decimal rate)
        {
            lock (_lock)
            {
                _fundingRate = rate;
                _fundingRateSignal = NormalizeFundingRate(rate);
                _hasFundingRate = true;
            }
        }

        /// <summary>
        /// Returns a spread multiplier in [0.8, 1.5] based on composite sentiment.
        /// Values &gt; 1.0 mean widen spread (defensive), &lt; 1.0 mean tighten (confident).
        /// </summary>
        public decimal GetSentimentSpreadMultiplier()
        {
            lock (_lock)
            {
                if (!_hasFearGreed && !_hasFundingRate)
                    return 1.0m;

                var fgiComponent = 0.0m;
                if (_hasFearGreed)
                {
                    // Distance from neutral (50) → 0 at center, 1 at extremes
                    var extremity = Math.Abs(_fearGreedIndex - 50) / 50.0m;
                    // Quadratic scaling: mild extremes have small effect, extreme extremes have large effect
                    fgiComponent = extremity * extremity * 0.5m; // max contribution: 0.5
                }

                var frComponent = 0.0m;
                if (_hasFundingRate)
                {
                    // High absolute funding rate → widen spread
                    frComponent = Math.Abs(_fundingRateSignal) * 0.3m; // max contribution: 0.3
                }

                var multiplier = 1.0m + fgiComponent + frComponent;
                return Math.Clamp(multiplier, MinSpreadMultiplier, MaxSpreadMultiplier);
            }
        }

        /// <summary>
        /// Returns a directional bias in [-1.0, 1.0] based on contrarian sentiment.
        /// Positive = bullish bias (Extreme Fear → contrarian buy signal).
        /// Negative = bearish bias (Extreme Greed → contrarian sell signal).
        /// </summary>
        public decimal GetSentimentDirectionalBias()
        {
            lock (_lock)
            {
                if (!_hasFearGreed && !_hasFundingRate)
                    return 0.0m;

                var fgiBias = 0.0m;
                if (_hasFearGreed)
                {
                    // Contrarian: fear → bullish, greed → bearish
                    // Map 0-100 to +1.0 to -1.0
                    fgiBias = (50 - _fearGreedIndex) / 50.0m;
                    // Scale down for mild readings
                    fgiBias *= Math.Abs(fgiBias); // quadratic with sign preservation
                }

                var frBias = 0.0m;
                if (_hasFundingRate)
                {
                    // High positive funding → too many longs → bearish contrarian
                    // High negative funding → too many shorts → bullish contrarian
                    frBias = -_fundingRateSignal * 0.5m;
                }

                // Weighted combination: FGI 70%, FR 30%
                var bias = fgiBias * 0.7m + frBias * 0.3m;
                return Math.Clamp(bias, -1.0m, 1.0m);
            }
        }

        internal static string ClassifyFearGreed(int value)
        {
            if (value < ExtremeFearThreshold) return "Extreme Fear";
            if (value < FearThreshold) return "Fear";
            if (value <= NeutralUpperThreshold) return "Neutral";
            if (value <= GreedThreshold) return "Greed";
            return "Extreme Greed";
        }

        internal static decimal NormalizeFundingRate(decimal rate)
        {
            // Normalize relative to baseline: (rate - baseline) / maxSignal, clamped to [-1, 1]
            var signal = (rate - FundingRateBaseline) / FundingRateMaxSignal;
            return Math.Clamp(signal, -1.0m, 1.0m);
        }
    }
}
