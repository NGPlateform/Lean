/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Polymarket.Data
{
    /// <summary>
    /// Downloads historical trade data from the Polymarket CLOB API and converts it
    /// to LEAN TradeBar format for backtesting.
    /// </summary>
    public class PolymarketDataDownloader : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _dataDirectory;

        /// <summary>
        /// Creates a new data downloader instance
        /// </summary>
        /// <param name="baseUrl">Polymarket CLOB API base URL</param>
        /// <param name="dataDirectory">Root data directory (e.g. Globals.DataFolder)</param>
        public PolymarketDataDownloader(
            string baseUrl = "https://clob.polymarket.com",
            string dataDirectory = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _dataDirectory = dataDirectory ?? Globals.DataFolder;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Downloads historical trade data for a specific token and date range,
        /// converting to LEAN minute-resolution TradeBar format.
        /// </summary>
        /// <param name="tokenId">The Polymarket token ID</param>
        /// <param name="leanTicker">The LEAN ticker name</param>
        /// <param name="startDate">Start date for download</param>
        /// <param name="endDate">End date for download</param>
        /// <returns>Number of bars written</returns>
        public int DownloadTradeData(string tokenId, string leanTicker, DateTime startDate, DateTime endDate)
        {
            Log.Trace($"PolymarketDataDownloader.DownloadTradeData(): Downloading {leanTicker} from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

            var allTrades = FetchTrades(tokenId, startDate, endDate);
            if (allTrades.Count == 0)
            {
                Log.Trace("PolymarketDataDownloader.DownloadTradeData(): No trades found");
                return 0;
            }

            var bars = AggregateToBars(allTrades, Resolution.Minute);
            var totalBars = 0;

            foreach (var dateGroup in bars.GroupBy(b => b.Time.Date))
            {
                var date = dateGroup.Key;
                var outputDir = Path.Combine(_dataDirectory, "crypto", "polymarket", "minute", leanTicker.ToLowerInvariant());
                Directory.CreateDirectory(outputDir);

                var outputFile = Path.Combine(outputDir, $"{date:yyyyMMdd}_trade.csv");
                using var writer = new StreamWriter(outputFile);

                foreach (var bar in dateGroup.OrderBy(b => b.Time))
                {
                    // LEAN CSV format: time(ms from midnight), open, high, low, close, volume
                    var msFromMidnight = (long)(bar.Time - bar.Time.Date).TotalMilliseconds;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5}",
                        msFromMidnight,
                        bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
                    totalBars++;
                }
            }

            Log.Trace($"PolymarketDataDownloader.DownloadTradeData(): Wrote {totalBars} bars for {leanTicker}");
            return totalBars;
        }

        /// <summary>
        /// Fetches raw trades from the Polymarket API with pagination
        /// </summary>
        private List<RawTrade> FetchTrades(string tokenId, DateTime startDate, DateTime endDate)
        {
            var trades = new List<RawTrade>();
            var cursor = "";
            var maxPages = 100;

            for (var page = 0; page < maxPages; page++)
            {
                var url = $"{_baseUrl}/trades?asset_id={tokenId}&limit=500";
                if (!string.IsNullOrEmpty(cursor))
                {
                    url += $"&cursor={cursor}";
                }

                try
                {
                    var response = _httpClient.GetStringAsync(url).Result;
                    var json = JObject.Parse(response);
                    var tradeBatch = json["data"]?.ToObject<List<RawTrade>>();

                    if (tradeBatch == null || tradeBatch.Count == 0)
                    {
                        break;
                    }

                    var filtered = tradeBatch
                        .Where(t => t.Timestamp >= startDate && t.Timestamp <= endDate)
                        .ToList();

                    trades.AddRange(filtered);

                    // Check if we've gone past our date range
                    if (tradeBatch.Any(t => t.Timestamp < startDate))
                    {
                        break;
                    }

                    cursor = json["next_cursor"]?.ToString();
                    if (string.IsNullOrEmpty(cursor))
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, $"PolymarketDataDownloader.FetchTrades(): Error on page {page}");
                    break;
                }
            }

            return trades;
        }

        /// <summary>
        /// Aggregates raw trades into OHLCV bars at the given resolution
        /// </summary>
        private static List<TradeBar> AggregateToBars(List<RawTrade> trades, Resolution resolution)
        {
            var bars = new List<TradeBar>();
            var period = resolution.ToTimeSpan();

            var groups = trades
                .GroupBy(t => RoundDown(t.Timestamp, period))
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                var tradelist = group.OrderBy(t => t.Timestamp).ToList();
                var bar = new TradeBar
                {
                    Time = group.Key,
                    Open = tradelist.First().Price,
                    High = tradelist.Max(t => t.Price),
                    Low = tradelist.Min(t => t.Price),
                    Close = tradelist.Last().Price,
                    Volume = tradelist.Sum(t => t.Size),
                    Period = period
                };
                bars.Add(bar);
            }

            return bars;
        }

        private static DateTime RoundDown(DateTime dt, TimeSpan period)
        {
            var ticks = dt.Ticks / period.Ticks * period.Ticks;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>
        /// Disposes the HTTP client
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private class RawTrade
        {
            [JsonProperty("price")]
            public decimal Price { get; set; }

            [JsonProperty("size")]
            public decimal Size { get; set; }

            [JsonProperty("match_time")]
            public DateTime Timestamp { get; set; }

            [JsonProperty("side")]
            public string Side { get; set; }
        }
    }
}
