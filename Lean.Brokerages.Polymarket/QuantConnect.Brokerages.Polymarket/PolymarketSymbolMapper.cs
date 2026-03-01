/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Polymarket
{
    /// <summary>
    /// Maps between LEAN Symbol objects and Polymarket token IDs.
    /// Polymarket hierarchy: Market Question -> condition_id -> token_id (YES/NO)
    /// LEAN ticker format: "{QUESTION_SLUG}{OUTCOME}" e.g. "ETH5000MAR26YES"
    /// </summary>
    public class PolymarketSymbolMapper : ISymbolMapper
    {
        /// <summary>
        /// The polymarket market identifier
        /// </summary>
        public const string PolymarketMarket = "polymarket";

        private readonly ConcurrentDictionary<string, string> _leanToTokenId = new();
        private readonly ConcurrentDictionary<string, Symbol> _tokenIdToLean = new();
        private readonly ConcurrentDictionary<string, PolymarketMarketInfo> _conditionIdToMarket = new();
        private readonly string _baseApiUrl;

        /// <summary>
        /// Creates a new instance of the <see cref="PolymarketSymbolMapper"/>
        /// </summary>
        /// <param name="baseApiUrl">The base URL for the Polymarket CLOB API</param>
        public PolymarketSymbolMapper(string baseApiUrl = "https://clob.polymarket.com")
        {
            _baseApiUrl = baseApiUrl;
        }

        /// <summary>
        /// Converts a LEAN Symbol to a Polymarket token ID
        /// </summary>
        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol == null || symbol == Symbol.Empty || string.IsNullOrWhiteSpace(symbol.Value))
            {
                throw new ArgumentException("Invalid symbol: null or empty");
            }

            if (symbol.ID.Market != PolymarketMarket)
            {
                throw new ArgumentException($"Invalid market for Polymarket symbol mapper: {symbol.ID.Market}");
            }

            if (_leanToTokenId.TryGetValue(symbol.Value, out var tokenId))
            {
                return tokenId;
            }

            throw new ArgumentException($"Unknown Polymarket symbol: {symbol.Value}. Call LoadMarket() first.");
        }

        /// <summary>
        /// Converts a Polymarket token ID to a LEAN Symbol
        /// </summary>
        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = 0)
        {
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                throw new ArgumentException("Invalid brokerage symbol: null or empty");
            }

            if (_tokenIdToLean.TryGetValue(brokerageSymbol, out var symbol))
            {
                return symbol;
            }

            throw new ArgumentException($"Unknown Polymarket token ID: {brokerageSymbol}. Call LoadMarket() first.");
        }

        /// <summary>
        /// Registers a Polymarket market (condition) with its YES and NO token IDs
        /// </summary>
        /// <param name="leanTicker">The LEAN ticker (e.g. "ETH5000MAR26YES")</param>
        /// <param name="tokenId">The Polymarket token ID</param>
        /// <param name="conditionId">The condition ID for the market</param>
        /// <param name="info">Additional market metadata</param>
        public void AddMapping(string leanTicker, string tokenId, string conditionId, PolymarketMarketInfo info = null)
        {
            if (string.IsNullOrWhiteSpace(leanTicker) || string.IsNullOrWhiteSpace(tokenId))
            {
                throw new ArgumentException("Ticker and token ID must not be empty");
            }

            var symbol = Symbol.Create(leanTicker, SecurityType.Crypto, PolymarketMarket);

            _leanToTokenId[leanTicker] = tokenId;
            _tokenIdToLean[tokenId] = symbol;

            if (info != null && !string.IsNullOrWhiteSpace(conditionId))
            {
                _conditionIdToMarket[conditionId] = info;
            }
        }

        /// <summary>
        /// Loads market mappings from the Polymarket REST API
        /// </summary>
        /// <param name="conditionId">Optional specific condition ID to load. If null, loads all active markets.</param>
        public void LoadMarkets(string conditionId = null)
        {
            try
            {
                using var httpClient = new HttpClient();
                var url = string.IsNullOrEmpty(conditionId)
                    ? $"{_baseApiUrl}/markets?active=true&limit=500"
                    : $"{_baseApiUrl}/markets/{conditionId}";

                var response = httpClient.GetStringAsync(url).Result;

                if (string.IsNullOrEmpty(conditionId))
                {
                    var markets = JsonConvert.DeserializeObject<List<PolymarketApiMarket>>(response);
                    if (markets != null)
                    {
                        foreach (var market in markets)
                        {
                            RegisterMarket(market);
                        }
                    }
                }
                else
                {
                    var market = JsonConvert.DeserializeObject<PolymarketApiMarket>(response);
                    if (market != null)
                    {
                        RegisterMarket(market);
                    }
                }

                Log.Trace($"PolymarketSymbolMapper.LoadMarkets(): Loaded {_leanToTokenId.Count} symbol mappings");
            }
            catch (Exception e)
            {
                Log.Error(e, "PolymarketSymbolMapper.LoadMarkets(): Failed to load markets");
            }
        }

        /// <summary>
        /// Gets the market info for a given condition ID
        /// </summary>
        public PolymarketMarketInfo GetMarketInfo(string conditionId)
        {
            _conditionIdToMarket.TryGetValue(conditionId, out var info);
            return info;
        }

        /// <summary>
        /// Gets all registered LEAN symbols
        /// </summary>
        public IEnumerable<Symbol> GetAllSymbols()
        {
            return _tokenIdToLean.Values;
        }

        /// <summary>
        /// Checks if a token ID is registered
        /// </summary>
        public bool IsKnownTokenId(string tokenId)
        {
            return _tokenIdToLean.ContainsKey(tokenId);
        }

        /// <summary>
        /// Checks if a LEAN ticker is registered
        /// </summary>
        public bool IsKnownLeanSymbol(string leanTicker)
        {
            return _leanToTokenId.ContainsKey(leanTicker);
        }

        private void RegisterMarket(PolymarketApiMarket market)
        {
            if (market?.Tokens == null || market.Tokens.Count < 2)
            {
                return;
            }

            var slug = GenerateSlug(market.Question);
            var info = new PolymarketMarketInfo
            {
                ConditionId = market.ConditionId,
                Question = market.Question,
                EndDate = market.EndDateIso,
                IsResolved = market.Closed
            };

            foreach (var token in market.Tokens)
            {
                var outcome = token.Outcome?.ToUpperInvariant() ?? "UNKNOWN";
                var leanTicker = $"{slug}{outcome}";
                AddMapping(leanTicker, token.TokenId, market.ConditionId, info);
            }
        }

        /// <summary>
        /// Generates a URL-safe slug from a market question
        /// </summary>
        public static string GenerateSlug(string question)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return "UNKNOWN";
            }

            // Take first few significant words, remove special chars, uppercase
            var words = question
                .Replace("?", "")
                .Replace("'", "")
                .Replace("\"", "")
                .Split(new[] { ' ', '-', '/', '\\', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 1)
                .Take(4)
                .Select(w => w.ToUpperInvariant());

            return string.Join("", words);
        }
    }

    /// <summary>
    /// Metadata about a Polymarket prediction market
    /// </summary>
    public class PolymarketMarketInfo
    {
        /// <summary>
        /// The condition ID
        /// </summary>
        public string ConditionId { get; set; }

        /// <summary>
        /// The market question
        /// </summary>
        public string Question { get; set; }

        /// <summary>
        /// When the market ends/resolves
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// Whether the market has been resolved
        /// </summary>
        public bool IsResolved { get; set; }
    }

    /// <summary>
    /// Represents a market from the Polymarket API response
    /// </summary>
    internal class PolymarketApiMarket
    {
        [JsonProperty("condition_id")]
        public string ConditionId { get; set; }

        [JsonProperty("question")]
        public string Question { get; set; }

        [JsonProperty("end_date_iso")]
        public DateTime EndDateIso { get; set; }

        [JsonProperty("closed")]
        public bool Closed { get; set; }

        [JsonProperty("tokens")]
        public List<PolymarketApiToken> Tokens { get; set; }
    }

    /// <summary>
    /// Represents a token from the Polymarket API response
    /// </summary>
    internal class PolymarketApiToken
    {
        [JsonProperty("token_id")]
        public string TokenId { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }
    }
}
