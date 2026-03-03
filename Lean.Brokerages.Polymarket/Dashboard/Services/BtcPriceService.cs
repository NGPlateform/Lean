using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    /// <summary>
    /// Background service that polls Binance for BTC/USDT price every 10 seconds.
    /// Maintains a sliding window and exposes momentum signals for strategy use.
    /// </summary>
    public class BtcPriceService : BackgroundService
    {
        private readonly HttpClient _http;
        private readonly ILogger<BtcPriceService> _logger;
        private readonly List<PriceSample> _samples = new();
        private readonly object _lock = new();

        private const string BinanceTickerUrl = "https://api.binance.com/api/v3/ticker/price?symbol=BTCUSDT";
        private const int PollIntervalMs = 10_000; // 10 seconds
        private const int MaxSamples = 60; // 60 samples × 10s = 10 minutes window
        private const int ShortEmaPoints = 6;  // 6 × 10s = 1 minute
        private const int LongEmaPoints = 60;  // 60 × 10s = 10 minutes

        /// <summary>Latest BTC/USDT price, null if not yet fetched.</summary>
        public decimal? CurrentPrice
        {
            get { lock (_lock) return _samples.Count > 0 ? _samples[^1].Price : null; }
        }

        /// <summary>
        /// Returns the percentage return over the last N sample points.
        /// E.g., GetReturn(6) gives the ~1 minute return.
        /// </summary>
        public decimal GetReturn(int lookbackPoints)
        {
            lock (_lock)
            {
                if (_samples.Count < 2 || lookbackPoints < 1) return 0m;
                var idx = Math.Max(0, _samples.Count - 1 - lookbackPoints);
                var oldPrice = _samples[idx].Price;
                var newPrice = _samples[^1].Price;
                return oldPrice == 0m ? 0m : (newPrice - oldPrice) / oldPrice;
            }
        }

        /// <summary>
        /// Short-term vs long-term EMA difference, normalized by price.
        /// Positive = bullish momentum, Negative = bearish.
        /// </summary>
        public decimal Momentum
        {
            get
            {
                lock (_lock)
                {
                    if (_samples.Count < 2) return 0m;
                    var shortEma = CalculateEma(ShortEmaPoints);
                    var longEma = CalculateEma(LongEmaPoints);
                    var price = _samples[^1].Price;
                    return price == 0m ? 0m : (shortEma - longEma) / price;
                }
            }
        }

        /// <summary>Number of samples currently in the window.</summary>
        public int SampleCount
        {
            get { lock (_lock) return _samples.Count; }
        }

        public BtcPriceService(ILogger<BtcPriceService> logger, HttpClient httpClient = null)
        {
            _logger = logger;
            _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BtcPriceService starting (poll every {Interval}s)...", PollIntervalMs / 1000);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollPriceAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to poll BTC price");
                }

                await Task.Delay(PollIntervalMs, stoppingToken);
            }
        }

        internal async Task PollPriceAsync()
        {
            var json = await _http.GetStringAsync(BinanceTickerUrl);
            var obj = JObject.Parse(json);
            var priceStr = obj["price"]?.ToString();

            if (string.IsNullOrEmpty(priceStr) ||
                !decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
            {
                return;
            }

            lock (_lock)
            {
                _samples.Add(new PriceSample { Time = DateTime.UtcNow, Price = price });

                if (_samples.Count > MaxSamples)
                    _samples.RemoveAt(0);
            }
        }

        /// <summary>Injects a price sample directly (for testing).</summary>
        internal void InjectSample(decimal price, DateTime? time = null)
        {
            lock (_lock)
            {
                _samples.Add(new PriceSample
                {
                    Time = time ?? DateTime.UtcNow,
                    Price = price
                });

                if (_samples.Count > MaxSamples)
                    _samples.RemoveAt(0);
            }
        }

        private decimal CalculateEma(int period)
        {
            // Must be called under _lock
            if (_samples.Count == 0) return 0m;

            var multiplier = 2.0m / (period + 1);
            var ema = _samples[0].Price;

            for (int i = 1; i < _samples.Count; i++)
            {
                ema = (_samples[i].Price - ema) * multiplier + ema;
            }

            return ema;
        }

        private class PriceSample
        {
            public DateTime Time { get; set; }
            public decimal Price { get; set; }
        }
    }
}
