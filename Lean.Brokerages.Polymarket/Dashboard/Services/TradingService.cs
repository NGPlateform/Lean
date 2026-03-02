using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    /// <summary>
    /// Trading service using Gamma API + CLOB API patterns from polyclaw
    /// (https://github.com/chainstacklabs/polyclaw)
    ///
    /// Key improvement: uses clobTokenIds from Gamma API directly,
    /// eliminating per-market CLOB API lookups.
    /// </summary>
    public class TradingService : IDisposable
    {
        private readonly PolymarketApiClient _apiClient;
        private readonly PolymarketSymbolMapper _symbolMapper;
        private readonly ILogger<TradingService> _logger;
        private readonly bool _hasCredentials;
        private readonly HttpClient _http;
        private bool _marketsLoaded;

        private readonly List<DashboardMarket> _markets = new();
        private readonly List<DashboardEvent> _events = new();

        // Token ID → Market lookup for fast access
        private readonly ConcurrentDictionary<string, DashboardMarket> _tokenToMarket = new();

        private const string GammaApi = "https://gamma-api.polymarket.com";
        private const string ClobApi = "https://clob.polymarket.com";

        public TradingService(
            PolymarketCredentials credentials,
            PolymarketSymbolMapper symbolMapper,
            ILogger<TradingService> logger)
        {
            _apiClient = new PolymarketApiClient(credentials);
            _symbolMapper = symbolMapper;
            _logger = logger;
            _hasCredentials = !string.IsNullOrWhiteSpace(credentials.ApiKey);
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public bool HasCredentials => _hasCredentials;

        // ====== Market Discovery (Gamma API pattern from polyclaw) ======

        public void EnsureMarketsLoaded()
        {
            if (_marketsLoaded) return;
            try
            {
                LoadTrendingMarkets();
                LoadEvents();
                _marketsLoaded = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load markets");
            }
        }

        /// <summary>
        /// Loads trending markets from Gamma API sorted by 24h volume.
        /// Uses clobTokenIds directly (polyclaw pattern) — no per-market CLOB lookups needed.
        /// </summary>
        private void LoadTrendingMarkets()
        {
            var url = $"{GammaApi}/markets?closed=false&active=true&limit=200&order=volume24hr&ascending=false";
            var json = _http.GetStringAsync(url).Result;
            var arr = JArray.Parse(json);

            _markets.Clear();
            _tokenToMarket.Clear();

            foreach (var item in arr)
            {
                var market = ParseGammaMarket(item);
                if (market == null) continue;

                _markets.Add(market);
                foreach (var token in market.Tokens)
                {
                    if (!string.IsNullOrEmpty(token.TokenId))
                        _tokenToMarket[token.TokenId] = market;
                }
            }

            _logger.LogInformation("Loaded {Count} markets from Gamma API ({Tokens} tokens)",
                _markets.Count, _tokenToMarket.Count);
        }

        /// <summary>
        /// Loads events (grouped markets) from Gamma API
        /// </summary>
        private void LoadEvents()
        {
            try
            {
                var url = $"{GammaApi}/events?closed=false&limit=50&order=volume24hr&ascending=false";
                var json = _http.GetStringAsync(url).Result;
                var arr = JArray.Parse(json);

                _events.Clear();

                foreach (var item in arr)
                {
                    var evt = new DashboardEvent
                    {
                        Id = item["id"]?.ToString() ?? "",
                        Title = item["title"]?.ToString() ?? "",
                        Slug = item["slug"]?.ToString() ?? "",
                        Markets = new List<DashboardMarket>()
                    };

                    var eventMarkets = item["markets"] as JArray;
                    if (eventMarkets != null)
                    {
                        foreach (var em in eventMarkets)
                        {
                            var m = ParseGammaMarket(em);
                            if (m != null) evt.Markets.Add(m);
                        }
                    }

                    if (evt.Markets.Count > 0)
                        _events.Add(evt);
                }

                _logger.LogInformation("Loaded {Count} events from Gamma API", _events.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load events");
            }
        }

        /// <summary>
        /// Parses a single market from Gamma API JSON response.
        /// Extracts clobTokenIds directly (polyclaw pattern).
        /// </summary>
        private DashboardMarket ParseGammaMarket(JToken item)
        {
            var question = item["question"]?.ToString();
            if (string.IsNullOrEmpty(question)) return null;

            var conditionId = item["conditionId"]?.ToString() ?? "";

            // Parse clobTokenIds (polyclaw key insight: these come directly from Gamma API)
            // Note: clobTokenIds is a JSON-encoded string, not a raw JArray
            var clobTokenIds = new List<string>();
            try
            {
                clobTokenIds = JsonConvert.DeserializeObject<List<string>>(
                    item["clobTokenIds"]?.ToString() ?? "[]") ?? new List<string>();
            }
            catch { }

            // Parse outcomes and prices
            List<string> outcomes;
            List<decimal> prices;
            try
            {
                outcomes = JsonConvert.DeserializeObject<List<string>>(
                    item["outcomes"]?.ToString() ?? "[]") ?? new List<string>();
                prices = JsonConvert.DeserializeObject<List<decimal>>(
                    item["outcomePrices"]?.ToString() ?? "[]") ?? new List<decimal>();
            }
            catch
            {
                outcomes = new List<string>();
                prices = new List<decimal>();
            }

            // Build token list: pair outcomes with clobTokenIds
            var tokens = new List<DashboardToken>();
            for (int i = 0; i < outcomes.Count; i++)
            {
                tokens.Add(new DashboardToken
                {
                    TokenId = i < clobTokenIds.Count ? clobTokenIds[i] : "",
                    Outcome = outcomes[i],
                    Price = i < prices.Count ? prices[i] : 0
                });
            }

            if (tokens.Count == 0) return null;

            // Register with symbol mapper for orderbook lookups
            foreach (var token in tokens.Where(t => !string.IsNullOrEmpty(t.TokenId)))
            {
                var slug = PolymarketSymbolMapper.GenerateSlug(question);
                var ticker = slug + token.Outcome.ToUpperInvariant();
                try
                {
                    if (!_symbolMapper.IsKnownLeanSymbol(ticker))
                        _symbolMapper.AddMapping(ticker, token.TokenId, conditionId);
                }
                catch { }
            }

            return new DashboardMarket
            {
                Question = question,
                Slug = item["slug"]?.ToString() ?? "",
                ConditionId = conditionId,
                Volume = ParseDecimal(item["volumeNum"] ?? item["volume"]),
                Volume24h = ParseDecimal(item["volume24hrNum"] ?? item["volume24hr"]),
                Liquidity = ParseDecimal(item["liquidityNum"] ?? item["liquidity"]),
                EndDate = item["endDate"]?.ToString() ?? "",
                Category = item["category"]?.ToString() ?? "",
                Active = item["active"]?.Value<bool>() ?? false,
                Closed = item["closed"]?.Value<bool>() ?? false,
                Resolved = item["resolved"]?.Value<bool>() ?? false,
                Outcome = item["outcome"]?.ToString(),
                Tokens = tokens
            };
        }

        private static decimal ParseDecimal(JToken token)
        {
            if (token == null) return 0;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<decimal>();
            decimal.TryParse(token.ToString(), out var v);
            return v;
        }

        // ====== Data Retrieval ======

        public List<DashboardMarket> GetMarkets()
        {
            EnsureMarketsLoaded();
            return _markets;
        }

        public List<DashboardEvent> GetEvents()
        {
            EnsureMarketsLoaded();
            return _events;
        }

        /// <summary>
        /// Search markets by keyword (polyclaw pattern: client-side filter on Gamma data)
        /// </summary>
        public List<DashboardMarket> SearchMarkets(string query)
        {
            EnsureMarketsLoaded();
            if (string.IsNullOrWhiteSpace(query)) return _markets;

            var q = query.ToLowerInvariant();
            return _markets.Where(m =>
                m.Question.ToLowerInvariant().Contains(q) ||
                m.Category.ToLowerInvariant().Contains(q)).ToList();
        }

        /// <summary>
        /// Gets a single market by its Gamma market ID or condition ID
        /// </summary>
        public DashboardMarket GetMarket(string marketId)
        {
            EnsureMarketsLoaded();

            // Try local cache first
            var found = _markets.FirstOrDefault(m =>
                m.ConditionId == marketId || m.Slug == marketId);
            if (found != null) return found;

            // Fetch from Gamma API
            try
            {
                var url = $"{GammaApi}/markets/{marketId}";
                var json = _http.GetStringAsync(url).Result;

                // Could be single object or array
                if (json.TrimStart().StartsWith("["))
                {
                    var arr = JArray.Parse(json);
                    return arr.Count > 0 ? ParseGammaMarket(arr[0]) : null;
                }
                return ParseGammaMarket(JObject.Parse(json));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets market info for a given token ID
        /// </summary>
        public DashboardMarket GetMarketByToken(string tokenId)
        {
            EnsureMarketsLoaded();
            _tokenToMarket.TryGetValue(tokenId, out var market);
            return market;
        }

        /// <summary>
        /// Gets orderbook from CLOB API
        /// </summary>
        public PolymarketOrderBook GetOrderBook(string tokenId)
        {
            return _apiClient.GetOrderBook(tokenId);
        }

        /// <summary>
        /// Gets live prices from CLOB API for given token IDs (polyclaw pattern)
        /// </summary>
        public Dictionary<string, decimal> GetPrices(List<string> tokenIds)
        {
            var result = new Dictionary<string, decimal>();
            if (tokenIds == null || tokenIds.Count == 0) return result;

            try
            {
                // CLOB price endpoint accepts comma-separated token IDs
                var idsParam = string.Join(",", tokenIds);
                var url = $"{ClobApi}/prices?token_ids={Uri.EscapeDataString(idsParam)}";
                var json = _http.GetStringAsync(url).Result;
                var obj = JObject.Parse(json);

                foreach (var prop in obj.Properties())
                {
                    if (decimal.TryParse(prop.Value?.ToString(), out var price))
                        result[prop.Name] = price;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch CLOB prices");
            }

            return result;
        }

        // ====== Authenticated Operations ======

        public List<PolymarketPosition> GetPositions()
        {
            if (!_hasCredentials) return new List<PolymarketPosition>();
            return _apiClient.GetPositions();
        }

        public PolymarketBalance GetBalance()
        {
            if (!_hasCredentials) return new PolymarketBalance { Balance = "0", Allowance = "0" };
            return _apiClient.GetBalance();
        }

        public List<PolymarketOrder> GetOpenOrders()
        {
            if (!_hasCredentials) return new List<PolymarketOrder>();
            return _apiClient.GetOpenOrders();
        }

        public List<PolymarketTrade> GetTrades(int limit = 50)
        {
            if (!_hasCredentials) return new List<PolymarketTrade>();
            return _apiClient.GetTrades(limit: limit);
        }

        public PolymarketOrderResponse PlaceOrder(string tokenId, decimal price, decimal size, string side)
        {
            if (!_hasCredentials)
                throw new InvalidOperationException("API credentials required. Configure in appsettings.json.");

            var direction = side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? OrderDirection.Buy
                : OrderDirection.Sell;

            return _apiClient.PlaceOrder(tokenId, price, size, direction);
        }

        public PolymarketCancelResponse CancelOrder(string orderId)
        {
            if (!_hasCredentials)
                throw new InvalidOperationException("API credentials required. Configure in appsettings.json.");

            return _apiClient.CancelOrder(orderId);
        }

        /// <summary>
        /// Forces reload of market data from Gamma API
        /// </summary>
        public void RefreshMarkets()
        {
            _marketsLoaded = false;
            EnsureMarketsLoaded();
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
            _http?.Dispose();
        }
    }

    // ====== Data Models ======

    public class DashboardMarket
    {
        public string Question { get; set; }
        public string Slug { get; set; }
        public string ConditionId { get; set; }
        public decimal Volume { get; set; }
        public decimal Volume24h { get; set; }
        public decimal Liquidity { get; set; }
        public string EndDate { get; set; }
        public string Category { get; set; }
        public bool Active { get; set; }
        public bool Closed { get; set; }
        public bool Resolved { get; set; }
        public string Outcome { get; set; }
        public List<DashboardToken> Tokens { get; set; }
    }

    public class DashboardToken
    {
        public string TokenId { get; set; }
        public string Outcome { get; set; }
        public decimal Price { get; set; }
    }

    public class DashboardEvent
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Slug { get; set; }
        public List<DashboardMarket> Markets { get; set; }
    }
}
