/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketWebSocketTests
    {
        private const string TestApiKey = "test-api-key";
        private const string TestApiSecret = "dGVzdC1hcGktc2VjcmV0";
        private const string TestPassphrase = "test-passphrase";

        private PolymarketWebSocketClient _wsClient;

        [SetUp]
        public void Setup()
        {
            var credentials = new PolymarketCredentials(TestApiKey, TestApiSecret, null, TestPassphrase);
            _wsClient = new PolymarketWebSocketClient(credentials);
        }

        #region Subscription Creation Tests

        [Test]
        public void CreateMarketSubscription_SingleToken()
        {
            var json = _wsClient.CreateMarketSubscription(new[] { "token-abc" });
            var sub = JsonConvert.DeserializeObject<PolymarketWsSubscription>(json);

            Assert.AreEqual("market", sub.Type);
            Assert.AreEqual(1, sub.AssetIds.Count);
            Assert.AreEqual("token-abc", sub.AssetIds[0]);
            Assert.IsNull(sub.Auth);
        }

        [Test]
        public void CreateMarketSubscription_MultipleTokens()
        {
            var tokens = new[] { "token-1", "token-2", "token-3" };
            var json = _wsClient.CreateMarketSubscription(tokens);
            var sub = JsonConvert.DeserializeObject<PolymarketWsSubscription>(json);

            Assert.AreEqual("market", sub.Type);
            Assert.AreEqual(3, sub.AssetIds.Count);
        }

        [Test]
        public void CreateUserSubscription_IncludesAuth()
        {
            var json = _wsClient.CreateUserSubscription(new[] { "token-abc" });
            var sub = JsonConvert.DeserializeObject<PolymarketWsSubscription>(json);

            Assert.AreEqual("user", sub.Type);
            Assert.IsNotNull(sub.Auth);
            Assert.AreEqual(TestApiKey, sub.Auth.ApiKey);
            Assert.AreEqual(TestApiSecret, sub.Auth.Secret);
            Assert.AreEqual(TestPassphrase, sub.Auth.Passphrase);
        }

        [Test]
        public void CreateUserSubscription_MultipleTokens()
        {
            var tokens = new[] { "token-x", "token-y" };
            var json = _wsClient.CreateUserSubscription(tokens);
            var sub = JsonConvert.DeserializeObject<PolymarketWsSubscription>(json);

            Assert.AreEqual(2, sub.AssetIds.Count);
        }

        #endregion

        #region ParseMessage Tests

        [Test]
        public void ParseMessage_BookSnapshot()
        {
            var json = @"{
                ""event_type"": ""book"",
                ""asset_id"": ""token-abc"",
                ""market"": ""market-1"",
                ""changes"": [
                    [""buy"", ""0.60"", ""500""],
                    [""sell"", ""0.65"", ""300""]
                ]
            }";

            var msg = PolymarketWebSocketClient.ParseMessage(json);

            Assert.IsNotNull(msg);
            Assert.AreEqual("book", msg.EventType);
            Assert.AreEqual("token-abc", msg.AssetId);
            Assert.AreEqual(2, msg.Changes.Count);
            Assert.AreEqual("buy", msg.Changes[0][0]);
            Assert.AreEqual("0.60", msg.Changes[0][1]);
        }

        [Test]
        public void ParseMessage_PriceChange()
        {
            var json = @"{
                ""event_type"": ""price_change"",
                ""asset_id"": ""token-abc"",
                ""changes"": [
                    [""buy"", ""0.62"", ""100""]
                ]
            }";

            var msg = PolymarketWebSocketClient.ParseMessage(json);

            Assert.IsNotNull(msg);
            Assert.AreEqual("price_change", msg.EventType);
            Assert.AreEqual(1, msg.Changes.Count);
        }

        [Test]
        public void ParseMessage_TradeUpdate()
        {
            var json = @"{
                ""event_type"": ""trade"",
                ""asset_id"": ""token-abc"",
                ""price"": ""0.65"",
                ""size"": ""50"",
                ""side"": ""BUY""
            }";

            var msg = PolymarketWebSocketClient.ParseMessage(json);

            Assert.IsNotNull(msg);
            Assert.AreEqual("trade", msg.EventType);
            Assert.AreEqual("0.65", msg.Price);
            Assert.AreEqual("50", msg.Size);
            Assert.AreEqual("BUY", msg.Side);
        }

        [Test]
        public void ParseMessage_OrderUpdate()
        {
            var json = @"{
                ""event_type"": ""order"",
                ""order"": {
                    ""id"": ""order-123"",
                    ""status"": ""live"",
                    ""asset_id"": ""token-abc"",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""0""
                }
            }";

            var msg = PolymarketWebSocketClient.ParseMessage(json);

            Assert.IsNotNull(msg);
            Assert.AreEqual("order", msg.EventType);
            Assert.IsNotNull(msg.Order);
            Assert.AreEqual("order-123", msg.Order.Id);
            Assert.AreEqual("live", msg.Order.Status);
        }

        [Test]
        public void ParseMessage_InvalidJson_ReturnsNull()
        {
            var result = PolymarketWebSocketClient.ParseMessage("{not valid json}}}");
            Assert.IsNull(result);
        }

        [Test]
        public void ParseMessage_NullInput_ReturnsNull()
        {
            var result = PolymarketWebSocketClient.ParseMessage(null);
            Assert.IsNull(result);
        }

        [Test]
        public void ParseMessage_EmptyString_ReturnsNull()
        {
            var result = PolymarketWebSocketClient.ParseMessage("");
            Assert.IsNull(result);
        }

        [Test]
        public void ParseMessage_UnknownEventType()
        {
            var json = @"{""event_type"": ""heartbeat"", ""asset_id"": ""token-abc""}";

            var msg = PolymarketWebSocketClient.ParseMessage(json);

            Assert.IsNotNull(msg);
            Assert.AreEqual("heartbeat", msg.EventType);
        }

        #endregion
    }
}
