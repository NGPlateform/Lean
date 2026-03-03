using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Polymarket.Api.Models;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    public class HistoricalDataLoader
    {
        private readonly string _dataRoot;

        public HistoricalDataLoader(string dataRoot = null)
        {
            if (dataRoot != null)
            {
                _dataRoot = dataRoot;
                return;
            }

            _dataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Data");
            // If running from project root, try relative path
            if (!Directory.Exists(Path.Combine(_dataRoot, "crypto")))
            {
                _dataRoot = Path.Combine(Directory.GetCurrentDirectory(), "Data");
            }
        }

        /// <summary>
        /// Loads market metadata from markets.json.
        /// </summary>
        public List<CryptoMarketInfo> LoadMarketMetadata()
        {
            var path = Path.Combine(_dataRoot, "crypto", "polymarket", "markets.json");
            if (!File.Exists(path)) return new List<CryptoMarketInfo>();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<CryptoMarketInfo>>(json) ?? new List<CryptoMarketInfo>();
        }

        /// <summary>
        /// Loads price bars for a given ticker between start and end dates.
        /// CSV format (no header): TimeMs,Open,High,Low,Close,Volume
        /// </summary>
        public List<HistoricalBar> LoadPriceBars(string ticker, DateTime start, DateTime end)
        {
            var bars = new List<HistoricalBar>();
            var dir = Path.Combine(_dataRoot, "crypto", "polymarket", "minute", ticker);
            if (!Directory.Exists(dir)) return bars;

            for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            {
                var fileName = $"{date:yyyyMMdd}_trade.csv";
                var filePath = Path.Combine(dir, fileName);
                if (!File.Exists(filePath)) continue;

                foreach (var line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length < 6) continue;

                    if (!long.TryParse(parts[0], out var msFromMidnight)) continue;
                    if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
                    if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
                    if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
                    if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
                    if (!decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var volume)) continue;

                    var time = date.AddMilliseconds(msFromMidnight);
                    if (time < start || time > end) continue;

                    bars.Add(new HistoricalBar
                    {
                        Time = time,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume
                    });
                }
            }

            return bars;
        }

        /// <summary>
        /// Loads BTC reference bars from Data/reference/btc-usd/.
        /// </summary>
        public List<HistoricalBar> LoadBtcBars(DateTime start, DateTime end)
        {
            var bars = new List<HistoricalBar>();
            var dir = Path.Combine(_dataRoot, "reference", "btc-usd");
            if (!Directory.Exists(dir)) return bars;

            for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            {
                var fileName = $"{date:yyyyMMdd}_trade.csv";
                var filePath = Path.Combine(dir, fileName);
                if (!File.Exists(filePath)) continue;

                foreach (var line in File.ReadLines(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',');
                    if (parts.Length < 6) continue;

                    if (!long.TryParse(parts[0], out var msFromMidnight)) continue;
                    if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
                    if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
                    if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
                    if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
                    if (!decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var volume)) continue;

                    var time = date.AddMilliseconds(msFromMidnight);
                    if (time < start || time > end) continue;

                    bars.Add(new HistoricalBar
                    {
                        Time = time,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Volume = volume
                    });
                }
            }

            return bars;
        }

        /// <summary>
        /// Synthesizes a 2-level order book from a single OHLCV bar.
        /// </summary>
        public static PolymarketOrderBook SynthesizeOrderBook(HistoricalBar bar, string tokenId = null)
        {
            var halfSpread = Math.Max(0.005m, (bar.High - bar.Low) * 0.1m);
            var depth = Math.Max(100m, bar.Volume > 0 ? bar.Volume * 0.3m : 200m);

            return new PolymarketOrderBook
            {
                AssetId = tokenId ?? "",
                Bids = new List<PolymarketOrderBookLevel>
                {
                    new PolymarketOrderBookLevel
                    {
                        Price = (bar.Close - halfSpread).ToString(CultureInfo.InvariantCulture),
                        Size = (depth * 0.6m).ToString(CultureInfo.InvariantCulture)
                    },
                    new PolymarketOrderBookLevel
                    {
                        Price = bar.Low.ToString(CultureInfo.InvariantCulture),
                        Size = (depth * 0.4m).ToString(CultureInfo.InvariantCulture)
                    }
                },
                Asks = new List<PolymarketOrderBookLevel>
                {
                    new PolymarketOrderBookLevel
                    {
                        Price = (bar.Close + halfSpread).ToString(CultureInfo.InvariantCulture),
                        Size = (depth * 0.6m).ToString(CultureInfo.InvariantCulture)
                    },
                    new PolymarketOrderBookLevel
                    {
                        Price = bar.High.ToString(CultureInfo.InvariantCulture),
                        Size = (depth * 0.4m).ToString(CultureInfo.InvariantCulture)
                    }
                }
            };
        }

        /// <summary>
        /// Builds a time-sorted tick timeline from all tokens' bars.
        /// Returns ticks grouped by 10-minute window: each tick has a time and token→bar mapping.
        /// </summary>
        public List<(DateTime Time, Dictionary<string, HistoricalBar> TokenBars)> BuildTimeline(
            Dictionary<string, string> tokenIdToTicker, DateTime start, DateTime end)
        {
            // Load all bars for all tokens
            var allBars = new List<(DateTime Time, string TokenId, HistoricalBar Bar)>();

            foreach (var kvp in tokenIdToTicker)
            {
                var tokenId = kvp.Key;
                var ticker = kvp.Value;
                var bars = LoadPriceBars(ticker, start, end);
                foreach (var bar in bars)
                {
                    allBars.Add((bar.Time, tokenId, bar));
                }
            }

            // Group by 10-minute window (bars are already on 10-min intervals)
            var grouped = allBars
                .GroupBy(b => RoundTo10Min(b.Time))
                .OrderBy(g => g.Key)
                .Select(g => (
                    Time: g.Key,
                    TokenBars: g.ToDictionary(x => x.TokenId, x => x.Bar)
                ))
                .ToList();

            return grouped;
        }

        /// <summary>
        /// Converts CryptoMarketInfo list to DashboardMarket list for strategy context.
        /// </summary>
        public static List<DashboardMarket> ConvertToDashboardMarkets(List<CryptoMarketInfo> markets)
        {
            return markets.Select(m => new DashboardMarket
            {
                Question = m.Question,
                Slug = m.Slug,
                ConditionId = m.ConditionId,
                Volume = m.Volume,
                Volume24h = m.Volume24h,
                Category = m.Category ?? "",
                EndDate = null, // Not available in CryptoMarketInfo; BtcFollowMM defaults to TTE=1.0
                Active = true,
                Closed = false,
                Resolved = false,
                Tokens = m.Tokens?.Select(t => new DashboardToken
                {
                    TokenId = t.TokenId,
                    Outcome = t.Outcome,
                    Price = 0.5m // Will be updated from bar data
                }).ToList() ?? new List<DashboardToken>()
            }).ToList();
        }

        /// <summary>
        /// Builds tokenId→ticker mapping from market metadata.
        /// </summary>
        public static Dictionary<string, string> BuildTokenTickerMap(List<CryptoMarketInfo> markets)
        {
            var map = new Dictionary<string, string>();
            foreach (var market in markets)
            {
                if (market.Tokens == null) continue;
                foreach (var token in market.Tokens)
                {
                    if (!string.IsNullOrEmpty(token.TokenId) && !string.IsNullOrEmpty(token.Ticker))
                        map[token.TokenId] = token.Ticker;
                }
            }
            return map;
        }

        private static DateTime RoundTo10Min(DateTime dt)
        {
            var ticks = dt.Ticks;
            var interval = TimeSpan.FromMinutes(10).Ticks;
            return new DateTime(ticks - ticks % interval, dt.Kind);
        }
    }
}
