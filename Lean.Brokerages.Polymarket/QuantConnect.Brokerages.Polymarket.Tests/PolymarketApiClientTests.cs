/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Brokerages.Polymarket.Tests.Helpers;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketApiClientTests
    {
        // Hardhat #0 private key (well-known test key — DO NOT use in production)
        private const string TestPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
        private const string TestApiKey = "test-api-key";
        private const string TestApiSecret = "dGVzdC1hcGktc2VjcmV0"; // base64("test-api-secret")
        private const string TestPassphrase = "test-passphrase";

        private MockHttpMessageHandler _handler;
        private PolymarketApiClient _client;

        [SetUp]
        public void Setup()
        {
            _handler = new MockHttpMessageHandler();
            var credentials = new PolymarketCredentials(TestApiKey, TestApiSecret, TestPrivateKey, TestPassphrase);
            _client = new PolymarketApiClient(credentials, "https://clob.polymarket.com", _handler);
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
        }

        [Test]
        public void GetOpenOrders_EmptyList_ReturnsEmpty()
        {
            _handler.SetResponse("/orders", "[]");

            var orders = _client.GetOpenOrders();

            Assert.AreEqual(0, orders.Count);
        }

        [Test]
        public void GetOpenOrders_MultipleOrders_ParsesCorrectly()
        {
            var json = @"[
                {
                    ""id"": ""order-1"",
                    ""asset_id"": ""token-abc"",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""0"",
                    ""status"": ""live"",
                    ""type"": ""GTC""
                },
                {
                    ""id"": ""order-2"",
                    ""asset_id"": ""token-def"",
                    ""side"": ""SELL"",
                    ""price"": ""0.80"",
                    ""original_size"": ""50"",
                    ""size_matched"": ""10"",
                    ""status"": ""live"",
                    ""type"": ""GTC""
                }
            ]";
            _handler.SetResponse("/orders", json);

            var orders = _client.GetOpenOrders();

            Assert.AreEqual(2, orders.Count);
            Assert.AreEqual("order-1", orders[0].Id);
            Assert.AreEqual("token-abc", orders[0].AssetId);
            Assert.AreEqual("BUY", orders[0].Side);
            Assert.AreEqual("0.65", orders[0].Price);
            Assert.AreEqual("100", orders[0].OriginalSize);

            Assert.AreEqual("order-2", orders[1].Id);
            Assert.AreEqual("SELL", orders[1].Side);
        }

        [Test]
        public void GetPositions_ParsesCorrectly()
        {
            var json = @"[{
                ""asset_id"": ""token-xyz"",
                ""condition_id"": ""cond-1"",
                ""size"": ""200"",
                ""avg_price"": ""0.55"",
                ""cur_price"": ""0.70"",
                ""realized_pnl"": ""10.5"",
                ""unrealized_pnl"": ""30.0""
            }]";
            _handler.SetResponse("/positions", json);

            var positions = _client.GetPositions();

            Assert.AreEqual(1, positions.Count);
            Assert.AreEqual("token-xyz", positions[0].AssetId);
            Assert.AreEqual("200", positions[0].Size);
            Assert.AreEqual("0.55", positions[0].AvgPrice);
            Assert.AreEqual("0.70", positions[0].CurPrice);
        }

        [Test]
        public void GetBalance_ValidBalance_ParsesCorrectly()
        {
            _handler.SetResponse("/balance", @"{""balance"":""1500.50"",""allowance"":""10000""}");

            var balance = _client.GetBalance();

            Assert.AreEqual("1500.50", balance.Balance);
        }

        [Test]
        public void GetBalance_ZeroBalance_ReturnsZero()
        {
            _handler.SetResponse("/balance", @"{""balance"":""0""}");

            var balance = _client.GetBalance();

            Assert.AreEqual("0", balance.Balance);
        }

        [Test]
        public void GetOrderBook_ParsesBidsAndAsks()
        {
            var json = @"{
                ""market"": ""market-1"",
                ""asset_id"": ""token-abc"",
                ""bids"": [
                    {""price"": ""0.60"", ""size"": ""500""},
                    {""price"": ""0.59"", ""size"": ""300""}
                ],
                ""asks"": [
                    {""price"": ""0.62"", ""size"": ""400""},
                    {""price"": ""0.63"", ""size"": ""200""}
                ],
                ""hash"": ""abc123"",
                ""timestamp"": ""1700000000""
            }";
            _handler.SetResponse("/book", json);

            var book = _client.GetOrderBook("token-abc");

            Assert.AreEqual(2, book.Bids.Count);
            Assert.AreEqual(2, book.Asks.Count);
            Assert.AreEqual("0.60", book.Bids[0].Price);
            Assert.AreEqual("500", book.Bids[0].Size);
            Assert.AreEqual("0.62", book.Asks[0].Price);
            Assert.AreEqual("400", book.Asks[0].Size);
        }

        [Test]
        public void GetTrades_ParsesCorrectly()
        {
            var json = @"[{
                ""id"": ""trade-1"",
                ""price"": ""0.65"",
                ""size"": ""100"",
                ""side"": ""BUY"",
                ""asset_id"": ""token-abc"",
                ""status"": ""matched""
            }]";
            _handler.SetResponse("/trades", json);

            var trades = _client.GetTrades("token-abc");

            Assert.AreEqual(1, trades.Count);
            Assert.AreEqual("trade-1", trades[0].Id);
            Assert.AreEqual("0.65", trades[0].Price);
            Assert.AreEqual("100", trades[0].Size);
            Assert.AreEqual("BUY", trades[0].Side);
        }

        [Test]
        public void PlaceOrder_Success_ReturnsOrderId()
        {
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""new-order-123"",""status"":""live""}");

            var response = _client.PlaceOrder("12345", 0.65m, 100m, Orders.OrderDirection.Buy);

            Assert.IsTrue(response.Success);
            Assert.AreEqual("new-order-123", response.OrderId);
        }

        [Test]
        public void PlaceOrder_Error_ReturnsErrorMsg()
        {
            _handler.SetResponse("/order", @"{""success"":false,""errorMsg"":""Insufficient balance""}");

            var response = _client.PlaceOrder("12345", 0.65m, 100m, Orders.OrderDirection.Buy);

            Assert.IsFalse(response.Success);
            Assert.AreEqual("Insufficient balance", response.ErrorMsg);
        }

        [Test]
        public void CancelOrder_Success()
        {
            _handler.SetResponse("/order/order-1", @"{""canceled"":true,""orderID"":""order-1""}");

            var response = _client.CancelOrder("order-1");

            Assert.IsTrue(response.Canceled);
        }

        [Test]
        public void CancelOrder_Failure()
        {
            _handler.SetResponse("/order/order-1", @"{""canceled"":false,""not_canceled"":true,""orderID"":""order-1""}");

            var response = _client.CancelOrder("order-1");

            Assert.IsFalse(response.Canceled);
            Assert.IsTrue(response.NotCanceled);
        }

        [Test]
        public void AuthenticatedGet_AddsCorrectHeaders()
        {
            _handler.SetDefaultResponse("[]");

            _client.GetOpenOrders();

            Assert.AreEqual(1, _handler.SentRequests.Count);
            var request = _handler.SentRequests[0];

            Assert.IsTrue(request.Headers.Contains("POLY_API_KEY"), "Missing POLY_API_KEY header");
            Assert.IsTrue(request.Headers.Contains("POLY_SIGNATURE"), "Missing POLY_SIGNATURE header");
            Assert.IsTrue(request.Headers.Contains("POLY_TIMESTAMP"), "Missing POLY_TIMESTAMP header");
            Assert.IsTrue(request.Headers.Contains("POLY_PASSPHRASE"), "Missing POLY_PASSPHRASE header");

            Assert.AreEqual(TestApiKey, request.Headers.GetValues("POLY_API_KEY").First());
            Assert.AreEqual(TestPassphrase, request.Headers.GetValues("POLY_PASSPHRASE").First());

            // Signature should be a non-empty base64 string
            var signature = request.Headers.GetValues("POLY_SIGNATURE").First();
            Assert.IsNotEmpty(signature);

            // Timestamp should be a valid unix timestamp
            var timestamp = request.Headers.GetValues("POLY_TIMESTAMP").First();
            Assert.IsTrue(long.TryParse(timestamp, out var ts));
            Assert.That(ts, Is.GreaterThan(0));
        }
    }
}
