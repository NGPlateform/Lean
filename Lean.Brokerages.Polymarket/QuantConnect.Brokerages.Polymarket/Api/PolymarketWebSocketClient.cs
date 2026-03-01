/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.Polymarket.Api
{
    /// <summary>
    /// WebSocket client for Polymarket real-time data streams.
    /// Handles market data subscriptions, order book updates, and user order status changes.
    /// </summary>
    public class PolymarketWebSocketClient
    {
        private readonly PolymarketCredentials _credentials;

        /// <summary>
        /// The public WebSocket URL for market data
        /// </summary>
        public const string MarketWssUrl = "wss://ws-subscriptions-clob.polymarket.com/ws/market";

        /// <summary>
        /// The authenticated WebSocket URL for user data
        /// </summary>
        public const string UserWssUrl = "wss://ws-subscriptions-clob.polymarket.com/ws/user";

        /// <summary>
        /// Creates a new WebSocket client
        /// </summary>
        public PolymarketWebSocketClient(PolymarketCredentials credentials)
        {
            _credentials = credentials;
        }

        /// <summary>
        /// Creates a subscription message for market data (order book + trades)
        /// </summary>
        /// <param name="tokenIds">The token IDs to subscribe to</param>
        public string CreateMarketSubscription(IEnumerable<string> tokenIds)
        {
            var subscription = new PolymarketWsSubscription
            {
                Type = "market",
                AssetIds = new List<string>(tokenIds)
            };

            return JsonConvert.SerializeObject(subscription);
        }

        /// <summary>
        /// Creates a subscription message for user order/trade updates (requires authentication)
        /// </summary>
        /// <param name="tokenIds">The token IDs to subscribe to</param>
        public string CreateUserSubscription(IEnumerable<string> tokenIds)
        {
            var subscription = new PolymarketWsSubscription
            {
                Auth = new PolymarketWsAuth
                {
                    ApiKey = _credentials.ApiKey,
                    Secret = _credentials.ApiSecret,
                    Passphrase = _credentials.Passphrase
                },
                Type = "user",
                AssetIds = new List<string>(tokenIds)
            };

            return JsonConvert.SerializeObject(subscription);
        }

        /// <summary>
        /// Parses a WebSocket message into a typed object
        /// </summary>
        public static PolymarketWsMessage ParseMessage(string rawMessage)
        {
            try
            {
                return JsonConvert.DeserializeObject<PolymarketWsMessage>(rawMessage);
            }
            catch (Exception e)
            {
                Log.Error(e, $"PolymarketWebSocketClient.ParseMessage(): Failed to parse: {rawMessage}");
                return null;
            }
        }
    }
}
