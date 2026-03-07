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
    public class DownloadBatch
    {
        public string Name { get; set; }
        public string[] Keywords { get; set; }
        public string[] ExcludeKeywords { get; set; }
        public string MarketType { get; set; }
        public bool IncludeClosed { get; set; }
        public DateTime? EndDateMin { get; set; }
        public DateTime? EndDateMax { get; set; }
        public DateTime DataStartDate { get; set; }
        public DateTime DataEndDate { get; set; }
        public int MaxMarkets { get; set; } = 50;
    }

    public class ExpandedDataDownloadService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly string _dataRoot;
        private readonly DataDownloadService _baseService;

        private const string GammaApi = "https://gamma-api.polymarket.com";
        private const string ClobApi = "https://clob.polymarket.com";

        // Exclude sports false positives
        private static readonly string[] ExcludePatterns = new[]
        {
            "stanley cup", "nhl", "nba", "nfl", "mlb", "fifa", "world cup",
            "masters tournament", "grand slam", "super bowl"
        };

        public static readonly List<DownloadBatch> PredefinedBatches = new()
        {
            new DownloadBatch
            {
                Name = "btc_price_sep2025",
                Keywords = new[] { "bitcoin", "btc" },
                ExcludeKeywords = new[] { "dominance", "etf" },
                MarketType = "BTC Price",
                IncludeClosed = true,
                EndDateMin = new DateTime(2025, 8, 15, 0, 0, 0, DateTimeKind.Utc),
                EndDateMax = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                DataStartDate = new DateTime(2025, 8, 15, 0, 0, 0, DateTimeKind.Utc),
                DataEndDate = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                MaxMarkets = 50
            },
            new DownloadBatch
            {
                Name = "btc_price_dec2025",
                Keywords = new[] { "bitcoin", "btc" },
                ExcludeKeywords = new[] { "dominance", "etf" },
                MarketType = "BTC Price",
                IncludeClosed = true,
                EndDateMin = new DateTime(2025, 11, 15, 0, 0, 0, DateTimeKind.Utc),
                EndDateMax = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DataStartDate = new DateTime(2025, 11, 15, 0, 0, 0, DateTimeKind.Utc),
                DataEndDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                MaxMarkets = 50
            },
            new DownloadBatch
            {
                Name = "eth_price_recent",
                Keywords = new[] { "ethereum", "eth" },
                ExcludeKeywords = new[] { "dominance", "etf", "merge" },
                MarketType = "ETH Price",
                IncludeClosed = true,
                DataStartDate = new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc),
                DataEndDate = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),
                MaxMarkets = 50
            },
            new DownloadBatch
            {
                Name = "altcoin_price",
                Keywords = new[] { "solana", "sol", "xrp", "dogecoin", "doge" },
                ExcludeKeywords = new[] { "stanley cup", "nba" },
                MarketType = "Altcoin Price",
                IncludeClosed = true,
                DataStartDate = new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc),
                DataEndDate = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),
                MaxMarkets = 50
            },
            new DownloadBatch
            {
                Name = "crypto_events",
                Keywords = new[] { "crypto", "defi", "etf", "blockchain" },
                ExcludeKeywords = new[] { "bitcoin above", "btc above", "ethereum above", "eth above",
                    "solana above", "sol above", "price" },
                MarketType = "Crypto Events",
                IncludeClosed = true,
                DataStartDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                DataEndDate = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),
                MaxMarkets = 50
            },
            new DownloadBatch
            {
                Name = "politics_control",
                Keywords = new[] { "president", "election", "trump" },
                ExcludeKeywords = new[] { "bitcoin", "btc", "crypto", "ethereum", "defi" },
                MarketType = "Politics (Control)",
                IncludeClosed = true,
                DataStartDate = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                DataEndDate = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Utc),
                MaxMarkets = 50
            }
        };

        public ExpandedDataDownloadService(ILogger logger, string dataRoot = null)
        {
            _logger = logger;
            _dataRoot = dataRoot ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data");
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _baseService = new DataDownloadService(
                (logger as ILogger<DataDownloadService>) ??
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<DataDownloadService>(),
                _dataRoot);
        }

        /// <summary>
        /// Discovers markets matching the batch criteria from Gamma API.
        /// Supports closed markets, date filtering, and configurable keywords.
        /// </summary>
        public async Task<List<CryptoMarketInfo>> DiscoverMarketsForBatchAsync(DownloadBatch batch)
        {
            _logger.LogInformation("Discovering markets for batch '{Name}' (keywords: {Keywords})...",
                batch.Name, string.Join(", ", batch.Keywords));

            var allMarkets = new List<CryptoMarketInfo>();
            var seen = new HashSet<string>();
            var offset = 0;
            const int limit = 100;

            while (allMarkets.Count < batch.MaxMarkets)
            {
                var closedParam = batch.IncludeClosed ? "true" : "false";
                var url = $"{GammaApi}/markets?closed={closedParam}&limit={limit}&offset={offset}&order=volume&ascending=false";

                // Add end_date_min/max if specified
                if (batch.EndDateMin.HasValue)
                    url += $"&end_date_min={batch.EndDateMin.Value:yyyy-MM-ddTHH:mm:ssZ}";
                if (batch.EndDateMax.HasValue)
                    url += $"&end_date_max={batch.EndDateMax.Value:yyyy-MM-ddTHH:mm:ssZ}";

                string json;
                try
                {
                    json = await _http.GetStringAsync(url);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching markets at offset={Offset}", offset);
                    break;
                }

                var arr = JArray.Parse(json);
                if (arr.Count == 0) break;

                foreach (var item in arr)
                {
                    if (allMarkets.Count >= batch.MaxMarkets) break;

                    var question = item["question"]?.ToString() ?? "";
                    var category = item["category"]?.ToString() ?? "";
                    var conditionId = item["conditionId"]?.ToString() ?? "";

                    if (seen.Contains(conditionId)) continue;
                    seen.Add(conditionId);

                    if (!MatchesBatchKeywords(question, category, batch))
                        continue;

                    var slug = item["slug"]?.ToString() ?? "";
                    var volume = ParseDecimal(item["volumeNum"] ?? item["volume"]);
                    var endDateStr = item["endDate"]?.ToString();

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

                        var ticker = (slug + outcome).ToLowerInvariant().Replace(" ", "");
                        tokens.Add(new CryptoTokenInfo
                        {
                            TokenId = tokenId,
                            Outcome = outcome,
                            Ticker = ticker
                        });
                    }

                    if (tokens.Count == 0) continue;

                    allMarkets.Add(new CryptoMarketInfo
                    {
                        Question = question,
                        ConditionId = conditionId,
                        Slug = slug,
                        Volume = volume,
                        Volume24h = ParseDecimal(item["volume24hrNum"] ?? item["volume24hr"]),
                        Category = category,
                        EndDate = endDateStr,
                        MarketType = batch.MarketType,
                        Tokens = tokens
                    });
                }

                offset += limit;
                await Task.Delay(200); // rate limit

                // Stop if we got fewer than limit (last page)
                if (arr.Count < limit) break;
            }

            _logger.LogInformation("Batch '{Name}': found {Count} markets ({Tokens} tokens)",
                batch.Name, allMarkets.Count, allMarkets.Sum(m => m.Tokens.Count));

            return allMarkets;
        }

        /// <summary>
        /// Downloads all data for a single batch: discover markets, download price history, download BTC reference.
        /// </summary>
        public async Task<DataDownloadResult> DownloadBatchAsync(DownloadBatch batch)
        {
            var result = new DataDownloadResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation("=== Downloading batch: {Name} ({Type}) ===", batch.Name, batch.MarketType);

            try
            {
                // Step 1: Discover markets
                var markets = await DiscoverMarketsForBatchAsync(batch);
                result.MarketsFound = markets.Count;

                if (markets.Count == 0)
                {
                    _logger.LogWarning("No markets found for batch '{Name}'", batch.Name);
                    sw.Stop();
                    result.ElapsedSeconds = (int)sw.Elapsed.TotalSeconds;
                    return result;
                }

                // Step 2: Save market metadata to batch directory
                SaveBatchMetadata(batch.Name, markets);

                // Step 3: Download price history for each token
                foreach (var market in markets)
                {
                    foreach (var token in market.Tokens)
                    {
                        try
                        {
                            var bars = await DownloadBatchTradeHistoryAsync(
                                token.TokenId, token.Ticker, batch.Name,
                                batch.DataStartDate, batch.DataEndDate);
                            result.TotalBars += bars;
                            if (bars > 0) result.TokensWithData++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to download trades for {Ticker}", token.Ticker);
                            result.Errors++;
                        }

                        await Task.Delay(200);
                        result.TokensProcessed++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch '{Name}' download failed", batch.Name);
                result.Error = ex.Message;
            }

            // Step 4: Ensure BTC reference data covers this batch's date range
            try
            {
                var btcBars = await EnsureBtcDataAsync(batch.DataStartDate, batch.DataEndDate);
                result.BtcBars = btcBars;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download BTC reference data for batch '{Name}'", batch.Name);
                result.Errors++;
            }

            sw.Stop();
            result.ElapsedSeconds = (int)sw.Elapsed.TotalSeconds;

            _logger.LogInformation(
                "Batch '{Name}' complete: {Markets} markets, {Tokens} tokens ({WithData} with data), " +
                "{Bars} bars, {Errors} errors in {Elapsed}s",
                batch.Name, result.MarketsFound, result.TokensProcessed, result.TokensWithData,
                result.TotalBars, result.Errors, result.ElapsedSeconds);

            return result;
        }

        /// <summary>
        /// Downloads all 6 predefined batches sequentially.
        /// </summary>
        public async Task<Dictionary<string, DataDownloadResult>> DownloadAllBatchesAsync()
        {
            var results = new Dictionary<string, DataDownloadResult>();
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation("=== Starting expanded download: {Count} batches ===", PredefinedBatches.Count);

            foreach (var batch in PredefinedBatches)
            {
                var result = await DownloadBatchAsync(batch);
                results[batch.Name] = result;
            }

            totalSw.Stop();

            _logger.LogInformation("=== All batches complete in {Elapsed}s ===",
                (int)totalSw.Elapsed.TotalSeconds);

            // Print summary
            foreach (var kvp in results)
            {
                _logger.LogInformation("  {Batch}: {Markets} markets, {Bars} bars, {Errors} errors",
                    kvp.Key, kvp.Value.MarketsFound, kvp.Value.TotalBars, kvp.Value.Errors);
            }

            return results;
        }

        /// <summary>
        /// Returns the list of batch names that have been downloaded (have markets.json).
        /// </summary>
        public List<string> GetDownloadedBatchNames()
        {
            var batchesDir = Path.Combine(_dataRoot, "crypto", "polymarket", "batches");
            if (!Directory.Exists(batchesDir))
                return new List<string>();

            return Directory.GetDirectories(batchesDir)
                .Where(d => File.Exists(Path.Combine(d, "markets.json")))
                .Select(d => Path.GetFileName(d))
                .ToList();
        }

        private async Task<int> DownloadBatchTradeHistoryAsync(
            string tokenId, string ticker, string batchName,
            DateTime start, DateTime end)
        {
            var points = await _baseService.FetchPriceHistoryAsync(tokenId, start, end);
            if (points.Count == 0) return 0;

            var bars = DataDownloadService.AggregateToBars(points);
            var totalBars = 0;

            foreach (var dateGroup in bars.GroupBy(b => b.Time.Date))
            {
                var date = dateGroup.Key;
                var outputDir = Path.Combine(_dataRoot, "crypto", "polymarket", "batches",
                    batchName, "minute", ticker);
                Directory.CreateDirectory(outputDir);

                var outputFile = Path.Combine(outputDir, $"{date:yyyyMMdd}_trade.csv");
                using var writer = new StreamWriter(outputFile);

                foreach (var bar in dateGroup.OrderBy(b => b.Time))
                {
                    var msFromMidnight = (long)(bar.Time - bar.Time.Date).TotalMilliseconds;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5}",
                        msFromMidnight, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
                    totalBars++;
                }
            }

            return totalBars;
        }

        private void SaveBatchMetadata(string batchName, List<CryptoMarketInfo> markets)
        {
            var outputDir = Path.Combine(_dataRoot, "crypto", "polymarket", "batches", batchName);
            Directory.CreateDirectory(outputDir);

            var outputFile = Path.Combine(outputDir, "markets.json");
            var json = JsonConvert.SerializeObject(markets, Formatting.Indented);
            File.WriteAllText(outputFile, json);

            _logger.LogInformation("Saved batch '{Name}' metadata ({Count} markets) to {File}",
                batchName, markets.Count, outputFile);
        }

        /// <summary>
        /// Ensures BTC reference data covers the specified date range.
        /// Checks for missing dates and downloads only what's needed.
        /// </summary>
        private async Task<int> EnsureBtcDataAsync(DateTime startDate, DateTime endDate)
        {
            var btcDir = Path.Combine(_dataRoot, "reference", "btc-usd");
            var needsDownload = false;

            if (!Directory.Exists(btcDir))
            {
                needsDownload = true;
            }
            else
            {
                // Check if we have data for start and end dates
                for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(7))
                {
                    var fileName = $"{date:yyyyMMdd}_trade.csv";
                    if (!File.Exists(Path.Combine(btcDir, fileName)))
                    {
                        needsDownload = true;
                        break;
                    }
                }
            }

            if (!needsDownload)
            {
                _logger.LogInformation("BTC reference data already exists for {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}",
                    startDate, endDate);
                return 0;
            }

            return await _baseService.DownloadBtcKlinesAsync(startDate, endDate);
        }

        private static bool MatchesBatchKeywords(string question, string category, DownloadBatch batch)
        {
            var qLower = question.ToLowerInvariant();
            var cLower = category.ToLowerInvariant();

            // Check sports exclusions
            foreach (var pattern in ExcludePatterns)
            {
                if (qLower.Contains(pattern))
                    return false;
            }

            // Check batch-specific exclusions
            if (batch.ExcludeKeywords != null)
            {
                foreach (var exclude in batch.ExcludeKeywords)
                {
                    if (qLower.Contains(exclude.ToLowerInvariant()))
                        return false;
                }
            }

            // Must match at least one keyword
            foreach (var keyword in batch.Keywords)
            {
                if (qLower.Contains(keyword.ToLowerInvariant()) || cLower.Contains(keyword.ToLowerInvariant()))
                    return true;
            }

            return false;
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
            _baseService?.Dispose();
        }
    }
}
