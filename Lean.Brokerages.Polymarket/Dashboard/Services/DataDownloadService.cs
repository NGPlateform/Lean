using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class DataDownloadService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ILogger<DataDownloadService> _logger;
        private readonly string _dataRoot;

        private const string GammaApi = "https://gamma-api.polymarket.com";
        private const string ClobApi = "https://clob.polymarket.com";

        // Crypto keywords for market filtering.
        // "token" and "avalanche" excluded — too many false positives (sports teams, generic usage).
        private static readonly string[] CryptoKeywords = new[]
        {
            "bitcoin", "btc", "ethereum", "eth", "solana", "sol",
            "crypto", "defi", "nft", "altcoin", "blockchain",
            "dogecoin", "doge", "xrp", "ripple", "cardano", "ada",
            "polygon", "matic", "avax", "chainlink", "link"
        };

        // Exclude markets whose question matches these patterns (sports false positives)
        private static readonly string[] ExcludePatterns = new[]
        {
            "stanley cup", "nhl", "nba", "nfl", "mlb", "fifa", "world cup",
            "masters tournament", "grand slam", "super bowl"
        };

        public DataDownloadService(ILogger<DataDownloadService> logger, string dataRoot = null)
        {
            _logger = logger;
            _dataRoot = dataRoot ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data");
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ====== Market Discovery ======

        public async Task<List<CryptoMarketInfo>> DiscoverCryptoMarketsAsync()
        {
            _logger.LogInformation("Discovering crypto markets from Gamma API...");

            var url = $"{GammaApi}/markets?closed=false&active=true&limit=200&order=volume24hr&ascending=false";
            var json = await _http.GetStringAsync(url);
            var arr = JArray.Parse(json);

            var cryptoMarkets = new List<CryptoMarketInfo>();

            foreach (var item in arr)
            {
                var question = item["question"]?.ToString() ?? "";
                var category = item["category"]?.ToString() ?? "";

                if (!IsCryptoMarket(question, category))
                    continue;

                var conditionId = item["conditionId"]?.ToString() ?? "";
                var slug = item["slug"]?.ToString() ?? "";
                var volume = ParseDecimal(item["volumeNum"] ?? item["volume"]);
                var volume24h = ParseDecimal(item["volume24hrNum"] ?? item["volume24hr"]);

                List<string> clobTokenIds;
                List<string> outcomes;
                try
                {
                    clobTokenIds = JsonConvert.DeserializeObject<List<string>>(
                        item["clobTokenIds"]?.ToString() ?? "[]") ?? new List<string>();
                    outcomes = JsonConvert.DeserializeObject<List<string>>(
                        item["outcomes"]?.ToString() ?? "[]") ?? new List<string>();
                }
                catch
                {
                    continue;
                }

                if (clobTokenIds.Count == 0 || outcomes.Count == 0)
                    continue;

                var tokens = new List<CryptoTokenInfo>();
                for (int i = 0; i < outcomes.Count && i < clobTokenIds.Count; i++)
                {
                    var outcome = outcomes[i];
                    var tokenId = clobTokenIds[i];
                    if (string.IsNullOrEmpty(tokenId)) continue;

                    // Build ticker: slug + outcome (e.g. "will-bitcoin-hit-100kYES")
                    var ticker = (slug + outcome).ToLowerInvariant().Replace(" ", "");
                    tokens.Add(new CryptoTokenInfo
                    {
                        TokenId = tokenId,
                        Outcome = outcome,
                        Ticker = ticker
                    });
                }

                if (tokens.Count == 0) continue;

                cryptoMarkets.Add(new CryptoMarketInfo
                {
                    Question = question,
                    ConditionId = conditionId,
                    Slug = slug,
                    Volume = volume,
                    Volume24h = volume24h,
                    Category = category,
                    Tokens = tokens
                });
            }

            _logger.LogInformation("Found {Count} crypto markets ({Tokens} tokens)",
                cryptoMarkets.Count, cryptoMarkets.Sum(m => m.Tokens.Count));

            return cryptoMarkets;
        }

        private static bool IsCryptoMarket(string question, string category)
        {
            var qLower = question.ToLowerInvariant();
            var cLower = category.ToLowerInvariant();

            // Reject if it matches a sports exclusion pattern
            foreach (var pattern in ExcludePatterns)
            {
                if (qLower.Contains(pattern))
                    return false;
            }

            foreach (var keyword in CryptoKeywords)
            {
                if (qLower.Contains(keyword) || cLower.Contains(keyword))
                    return true;
            }
            return false;
        }

        // ====== Price History Download ======
        //
        // Uses CLOB API prices-history endpoint which returns per-token price
        // snapshots (~10min intervals at fidelity=1). This is the only public
        // endpoint that returns per-token data — data-api.polymarket.com/trades
        // ignores the asset_id parameter and returns a global trade feed.

        public async Task<int> DownloadTradeHistoryAsync(string tokenId, string ticker, DateTime start, DateTime end)
        {
            _logger.LogInformation("Downloading prices for {Ticker} ({Start:yyyy-MM-dd} to {End:yyyy-MM-dd})",
                ticker, start, end);

            var points = await FetchPriceHistoryAsync(tokenId, start, end);
            if (points.Count == 0)
            {
                _logger.LogInformation("  No price data for {Ticker}", ticker);
                return 0;
            }

            // Group price points into 10-minute OHLCV bars
            var bars = AggregateToBars(points);
            var totalBars = 0;

            foreach (var dateGroup in bars.GroupBy(b => b.Time.Date))
            {
                var date = dateGroup.Key;
                var outputDir = Path.Combine(_dataRoot, "crypto", "polymarket", "minute", ticker);
                Directory.CreateDirectory(outputDir);

                var outputFile = Path.Combine(outputDir, $"{date:yyyyMMdd}_trade.csv");
                using var writer = new StreamWriter(outputFile);

                foreach (var bar in dateGroup.OrderBy(b => b.Time))
                {
                    var msFromMidnight = (long)(bar.Time - bar.Time.Date).TotalMilliseconds;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5}",
                        msFromMidnight,
                        bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
                    totalBars++;
                }
            }

            _logger.LogInformation("  Wrote {Bars} bars for {Ticker}", totalBars, ticker);
            return totalBars;
        }

        /// <summary>
        /// Fetches price history from CLOB API prices-history endpoint.
        /// Returns per-token price snapshots at ~10 minute intervals.
        /// </summary>
        private async Task<List<PricePoint>> FetchPriceHistoryAsync(string tokenId, DateTime start, DateTime end)
        {
            var url = $"{ClobApi}/prices-history?market={tokenId}&interval=max&fidelity=1";
            var startEpoch = new DateTimeOffset(start).ToUnixTimeSeconds();
            var endEpoch = new DateTimeOffset(end).ToUnixTimeSeconds();

            try
            {
                var response = await _http.GetStringAsync(url);
                var json = JObject.Parse(response);
                var history = json["history"] as JArray;

                if (history == null || history.Count == 0)
                    return new List<PricePoint>();

                var points = new List<PricePoint>();
                foreach (var item in history)
                {
                    var t = item["t"]?.Value<long>() ?? 0;
                    var p = item["p"]?.Value<decimal>() ?? 0;

                    if (t < startEpoch || t > endEpoch) continue;
                    if (p <= 0) continue;

                    points.Add(new PricePoint
                    {
                        Time = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime,
                        Price = p
                    });
                }

                return points;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching price history for {TokenId}", tokenId);
                return new List<PricePoint>();
            }
        }

        /// <summary>
        /// Aggregates price snapshots into 10-minute OHLCV bars.
        /// prices-history returns ~1 point per 10 minutes, so we group by 10-minute windows.
        /// </summary>
        private static List<OhlcvBar> AggregateToBars(List<PricePoint> points)
        {
            var period = TimeSpan.FromMinutes(10);

            return points
                .GroupBy(p => new DateTime(p.Time.Ticks / period.Ticks * period.Ticks, DateTimeKind.Utc))
                .OrderBy(g => g.Key)
                .Select(group =>
                {
                    var sorted = group.OrderBy(p => p.Time).ToList();
                    return new OhlcvBar
                    {
                        Time = group.Key,
                        Open = sorted.First().Price,
                        High = sorted.Max(p => p.Price),
                        Low = sorted.Min(p => p.Price),
                        Close = sorted.Last().Price,
                        Volume = 0  // prices-history does not provide volume
                    };
                })
                .ToList();
        }

        // ====== Order Book Snapshot ======

        public async Task CaptureOrderBookSnapshotAsync(string tokenId, string ticker)
        {
            try
            {
                var url = $"{ClobApi}/book?token_id={tokenId}";
                var response = await _http.GetStringAsync(url);
                var json = JObject.Parse(response);

                var bids = ParseBookSide(json["bids"] as JArray);
                var asks = ParseBookSide(json["asks"] as JArray);

                var now = DateTime.UtcNow;
                var timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds();

                var outputDir = Path.Combine(_dataRoot, "crypto", "polymarket", "orderbook", ticker);
                Directory.CreateDirectory(outputDir);

                var outputFile = Path.Combine(outputDir, $"{now:yyyyMMdd}_book.csv");
                var isNew = !File.Exists(outputFile);

                using var writer = new StreamWriter(outputFile, append: true);

                // Build row: timestamp, then top-5 bids (price,size), then top-5 asks (price,size)
                var parts = new List<string> { timestamp.ToString() };

                // Top 5 bids (sorted by price descending)
                var topBids = bids.OrderByDescending(b => b.Price).Take(5).ToList();
                for (int i = 0; i < 5; i++)
                {
                    if (i < topBids.Count)
                    {
                        parts.Add(topBids[i].Price.ToString(CultureInfo.InvariantCulture));
                        parts.Add(topBids[i].Size.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        parts.Add("0");
                        parts.Add("0");
                    }
                }

                // Top 5 asks (sorted by price ascending)
                var topAsks = asks.OrderBy(a => a.Price).Take(5).ToList();
                for (int i = 0; i < 5; i++)
                {
                    if (i < topAsks.Count)
                    {
                        parts.Add(topAsks[i].Price.ToString(CultureInfo.InvariantCulture));
                        parts.Add(topAsks[i].Size.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        parts.Add("0");
                        parts.Add("0");
                    }
                }

                writer.WriteLine(string.Join(",", parts));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture order book for {Ticker}", ticker);
            }
        }

        private static List<BookLevel> ParseBookSide(JArray arr)
        {
            var levels = new List<BookLevel>();
            if (arr == null) return levels;

            foreach (var item in arr)
            {
                var price = decimal.TryParse(item["price"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
                var size = decimal.TryParse(item["size"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0;
                if (price > 0)
                    levels.Add(new BookLevel { Price = price, Size = size });
            }

            return levels;
        }

        // ====== Main Entry Point ======

        public async Task<DataDownloadResult> DownloadAllAsync(int days = 30)
        {
            var result = new DataDownloadResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Step 1: Discover crypto markets
                var markets = await DiscoverCryptoMarketsAsync();
                result.MarketsFound = markets.Count;

                // Step 2: Save market metadata
                SaveMarketMetadata(markets);

                var end = DateTime.UtcNow;
                var start = end.AddDays(-days);

                // Step 3: Download trade history + order book for each token
                foreach (var market in markets)
                {
                    _logger.LogInformation("Processing market: {Question}", market.Question);

                    foreach (var token in market.Tokens)
                    {
                        // Download trade history
                        try
                        {
                            var bars = await DownloadTradeHistoryAsync(token.TokenId, token.Ticker, start, end);
                            result.TotalBars += bars;
                            if (bars > 0) result.TokensWithData++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to download trades for {Ticker}", token.Ticker);
                            result.Errors++;
                        }

                        // Rate limit between tokens
                        await Task.Delay(200);

                        // Capture order book snapshot
                        try
                        {
                            await CaptureOrderBookSnapshotAsync(token.TokenId, token.Ticker);
                            result.OrderBookSnapshots++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to capture order book for {Ticker}", token.Ticker);
                            result.Errors++;
                        }

                        // Rate limit between API calls
                        await Task.Delay(200);

                        result.TokensProcessed++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download failed");
                result.Error = ex.Message;
            }

            sw.Stop();
            result.ElapsedSeconds = (int)sw.Elapsed.TotalSeconds;

            _logger.LogInformation(
                "Download complete: {Markets} markets, {Tokens} tokens, {Bars} bars, {Books} book snapshots, {Errors} errors in {Elapsed}s",
                result.MarketsFound, result.TokensProcessed, result.TotalBars,
                result.OrderBookSnapshots, result.Errors, result.ElapsedSeconds);

            return result;
        }

        private void SaveMarketMetadata(List<CryptoMarketInfo> markets)
        {
            var outputDir = Path.Combine(_dataRoot, "crypto", "polymarket");
            Directory.CreateDirectory(outputDir);

            var outputFile = Path.Combine(outputDir, "markets.json");
            var json = JsonConvert.SerializeObject(markets, Formatting.Indented);
            File.WriteAllText(outputFile, json);

            _logger.LogInformation("Saved market metadata to {File}", outputFile);
        }

        private static decimal ParseDecimal(JToken token)
        {
            if (token == null) return 0;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<decimal>();
            decimal.TryParse(token.ToString(), out var v);
            return v;
        }

        public void Dispose()
        {
            _http?.Dispose();
        }

        // ====== Models ======

        private class PricePoint
        {
            public DateTime Time { get; set; }
            public decimal Price { get; set; }
        }

        private class OhlcvBar
        {
            public DateTime Time { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
        }

        private class BookLevel
        {
            public decimal Price { get; set; }
            public decimal Size { get; set; }
        }
    }

    public class CryptoMarketInfo
    {
        [JsonProperty("question")]
        public string Question { get; set; }

        [JsonProperty("conditionId")]
        public string ConditionId { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }

        [JsonProperty("volume")]
        public decimal Volume { get; set; }

        [JsonProperty("volume24h")]
        public decimal Volume24h { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("tokens")]
        public List<CryptoTokenInfo> Tokens { get; set; }
    }

    public class CryptoTokenInfo
    {
        [JsonProperty("tokenId")]
        public string TokenId { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("ticker")]
        public string Ticker { get; set; }
    }

    public class DataDownloadResult
    {
        public int MarketsFound { get; set; }
        public int TokensProcessed { get; set; }
        public int TokensWithData { get; set; }
        public int TotalBars { get; set; }
        public int OrderBookSnapshots { get; set; }
        public int Errors { get; set; }
        public int ElapsedSeconds { get; set; }
        public string Error { get; set; }
    }
}
