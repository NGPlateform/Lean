/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Moq;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Brokerages.Polymarket.Tests.Helpers;
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    /// <summary>
    /// Testable subclass that skips real WebSocket/HTTP initialization and exposes OnMessage
    /// </summary>
    internal class TestablePolymarketBrokerage : PolymarketBrokerage
    {
        public TestablePolymarketBrokerage() : base()
        {
        }

        /// <summary>
        /// Simulates receiving a WebSocket message
        /// </summary>
        public void SimulateWebSocketMessage(string json)
        {
            var textMessage = new WebSocketClientWrapper.TextMessage { Message = json };
            var wsMessage = new WebSocketMessage(null, textMessage);
            // Call the protected OnMessage directly via reflection
            var method = typeof(PolymarketBrokerage).GetMethod("OnMessage",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(object), typeof(WebSocketMessage) },
                null);
            method.Invoke(this, new object[] { null, wsMessage });
        }
    }

    [TestFixture]
    public class PolymarketBrokerageIntegrationTests
    {
        private const string TestPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
        private const string TestApiKey = "test-api-key";
        private const string TestApiSecret = "dGVzdC1hcGktc2VjcmV0";
        private const string TestPassphrase = "test-passphrase";
        private const string TestTokenId = "token-test-123";
        private const string TestTicker = "TESTYES";
        private const string TestConditionId = "condition-test-1";

        private MockHttpMessageHandler _handler;
        private PolymarketApiClient _apiClient;
        private TestablePolymarketBrokerage _brokerage;
        private PolymarketSymbolMapper _symbolMapper;
        private List<OrderEvent> _orderEvents;
        private Symbol _testSymbol;

        [SetUp]
        public void Setup()
        {
            // Register the polymarket market
            try { Market.Add("polymarket", 43); } catch (ArgumentException) { }

            _testSymbol = Symbol.Create(TestTicker, SecurityType.Crypto, "polymarket");

            // Create mock-backed API client
            _handler = new MockHttpMessageHandler();
            var credentials = new PolymarketCredentials(TestApiKey, TestApiSecret, TestPrivateKey, TestPassphrase);
            _apiClient = new PolymarketApiClient(credentials, "https://clob.polymarket.com", _handler);

            // Create symbol mapper and register test mapping
            _symbolMapper = new PolymarketSymbolMapper();
            _symbolMapper.AddMapping(TestTicker, TestTokenId, TestConditionId);

            // Create testable brokerage and inject dependencies via reflection
            _brokerage = new TestablePolymarketBrokerage();
            SetPrivateField("_apiClient", _apiClient);
            SetPrivateField("_symbolMapper", _symbolMapper);

            // Collect order events
            _orderEvents = new List<OrderEvent>();
            _brokerage.OrdersStatusChanged += (_, events) => _orderEvents.AddRange(events);
        }

        [TearDown]
        public void TearDown()
        {
            _apiClient?.Dispose();
        }

        private void SetPrivateField(string fieldName, object value)
        {
            var field = typeof(PolymarketBrokerage).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_brokerage, value);
        }

        private T GetPrivateField<T>(string fieldName)
        {
            var field = typeof(PolymarketBrokerage).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return (T)field.GetValue(_brokerage);
        }

        private LimitOrder CreateTestOrder(decimal quantity = 100m, decimal limitPrice = 0.65m)
        {
            var order = new LimitOrder(_testSymbol, quantity, limitPrice, DateTime.UtcNow);
            return order;
        }

        #region PlaceOrder Tests

        [Test]
        public void PlaceOrder_Success_EmitsSubmittedEvent()
        {
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""broker-order-1"",""status"":""live""}");
            var order = CreateTestOrder();

            var result = _brokerage.PlaceOrder(order);

            Assert.IsTrue(result);
            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, _orderEvents[0].Status);

            // Verify order ID mapping
            var orderIdMap = GetPrivateField<System.Collections.Concurrent.ConcurrentDictionary<string, string>>("_orderIdMap");
            Assert.IsTrue(orderIdMap.ContainsKey("broker-order-1"));
        }

        [Test]
        public void PlaceOrder_Failure_EmitsInvalidEvent()
        {
            _handler.SetResponse("/order", @"{""success"":false,""errorMsg"":""Insufficient balance""}");
            var order = CreateTestOrder();

            var result = _brokerage.PlaceOrder(order);

            Assert.IsFalse(result);
            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
            Assert.That(_orderEvents[0].Message, Does.Contain("Insufficient balance"));
        }

        [Test]
        public void PlaceOrder_HttpError_EmitsInvalidEvent()
        {
            _handler.SetResponse("/order", @"{""error"":""server error""}", System.Net.HttpStatusCode.InternalServerError);
            var order = CreateTestOrder();

            var result = _brokerage.PlaceOrder(order);

            Assert.IsFalse(result);
            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
        }

        #endregion

        #region CancelOrder Tests

        [Test]
        public void CancelOrder_Success_EmitsCanceledEvent()
        {
            // First place an order to populate the order ID map
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""broker-order-2"",""status"":""live""}");
            var order = CreateTestOrder();
            _brokerage.PlaceOrder(order);
            _orderEvents.Clear();

            // Now cancel it
            _handler.SetResponse("/order/broker-order-2", @"{""canceled"":true,""orderID"":""broker-order-2""}");

            var result = _brokerage.CancelOrder(order);

            Assert.IsTrue(result);
            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Canceled, _orderEvents[0].Status);
        }

        [Test]
        public void CancelOrder_UnknownOrderId_ReturnsFalse()
        {
            var order = CreateTestOrder();
            // Don't place the order first — brokerage has no mapping

            var result = _brokerage.CancelOrder(order);

            Assert.IsFalse(result);
        }

        [Test]
        public void CancelOrder_ApiFailure_ReturnsFalse()
        {
            // Place order first
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""broker-order-3"",""status"":""live""}");
            var order = CreateTestOrder();
            _brokerage.PlaceOrder(order);
            _orderEvents.Clear();

            // Cancel fails
            _handler.SetResponse("/order/broker-order-3", @"{""canceled"":false,""not_canceled"":true}");

            var result = _brokerage.CancelOrder(order);

            Assert.IsFalse(result);
        }

        #endregion

        #region UpdateOrder Tests

        [Test]
        public void UpdateOrder_AlwaysReturnsFalse()
        {
            var order = CreateTestOrder();
            var result = _brokerage.UpdateOrder(order);
            Assert.IsFalse(result);
        }

        #endregion

        #region GetOpenOrders Tests

        [Test]
        public void GetOpenOrders_ConvertsToLeanOrders()
        {
            var json = $@"[
                {{
                    ""id"": ""order-a"",
                    ""asset_id"": ""{TestTokenId}"",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""0"",
                    ""status"": ""live"",
                    ""type"": ""GTC""
                }},
                {{
                    ""id"": ""order-b"",
                    ""asset_id"": ""{TestTokenId}"",
                    ""side"": ""SELL"",
                    ""price"": ""0.80"",
                    ""original_size"": ""50"",
                    ""size_matched"": ""0"",
                    ""status"": ""live"",
                    ""type"": ""GTC""
                }}
            ]";
            _handler.SetResponse("/orders", json);

            var orders = _brokerage.GetOpenOrders();

            Assert.AreEqual(2, orders.Count);

            var buyOrder = orders[0] as LimitOrder;
            Assert.IsNotNull(buyOrder);
            Assert.AreEqual(_testSymbol, buyOrder.Symbol);
            Assert.AreEqual(0.65m, buyOrder.LimitPrice);
            Assert.AreEqual(100m, buyOrder.Quantity);

            var sellOrder = orders[1] as LimitOrder;
            Assert.IsNotNull(sellOrder);
            Assert.AreEqual(0.80m, sellOrder.LimitPrice);
            Assert.AreEqual(-50m, sellOrder.Quantity); // Negative for sell
        }

        [Test]
        public void GetOpenOrders_UnknownToken_SkipsOrder()
        {
            var json = @"[{
                ""id"": ""order-unknown"",
                ""asset_id"": ""unknown-token-xyz"",
                ""side"": ""BUY"",
                ""price"": ""0.50"",
                ""original_size"": ""100"",
                ""status"": ""live""
            }]";
            _handler.SetResponse("/orders", json);

            var orders = _brokerage.GetOpenOrders();

            Assert.AreEqual(0, orders.Count);
        }

        #endregion

        #region GetAccountHoldings Tests

        [Test]
        public void GetAccountHoldings_ConvertsToHoldings()
        {
            var json = $@"[{{
                ""asset_id"": ""{TestTokenId}"",
                ""condition_id"": ""{TestConditionId}"",
                ""size"": ""200"",
                ""avg_price"": ""0.55"",
                ""cur_price"": ""0.70""
            }}]";
            _handler.SetResponse("/positions", json);

            var holdings = _brokerage.GetAccountHoldings();

            Assert.AreEqual(1, holdings.Count);
            Assert.AreEqual(_testSymbol, holdings[0].Symbol);
            Assert.AreEqual(200m, holdings[0].Quantity);
            Assert.AreEqual(0.55m, holdings[0].AveragePrice);
            Assert.AreEqual(0.70m, holdings[0].MarketPrice);
        }

        #endregion

        #region GetCashBalance Tests

        [Test]
        public void GetCashBalance_ReturnsUsdcBalance()
        {
            _handler.SetResponse("/balance", @"{""balance"":""1500.50""}");

            var balances = _brokerage.GetCashBalance();

            Assert.AreEqual(1, balances.Count);
            Assert.AreEqual(1500.50m, balances[0].Amount);
            Assert.AreEqual("USDC", balances[0].Currency);
        }

        [Test]
        public void GetCashBalance_NullBalance_ReturnsZero()
        {
            _handler.SetResponse("/balance", @"{""balance"":null}");

            var balances = _brokerage.GetCashBalance();

            Assert.AreEqual(1, balances.Count);
            Assert.AreEqual(0m, balances[0].Amount);
        }

        #endregion

        #region WebSocket Order Update Tests

        [Test]
        public void HandleOrderUpdate_Live_EmitsSubmitted()
        {
            // Place order first so the brokerage has the mapping
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""ws-order-1"",""status"":""live""}");
            var order = CreateTestOrder();
            _brokerage.PlaceOrder(order);
            _orderEvents.Clear();

            // Simulate WebSocket order update
            var wsJson = @"{
                ""event_type"": ""order"",
                ""order"": {
                    ""id"": ""ws-order-1"",
                    ""status"": ""live"",
                    ""asset_id"": """ + TestTokenId + @""",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""0""
                }
            }";
            _brokerage.SimulateWebSocketMessage(wsJson);

            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Submitted, _orderEvents[0].Status);
        }

        [Test]
        public void HandleOrderUpdate_Matched_EmitsFilled()
        {
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""ws-order-2"",""status"":""live""}");
            var order = CreateTestOrder(100m, 0.65m);
            _brokerage.PlaceOrder(order);
            _orderEvents.Clear();

            var wsJson = @"{
                ""event_type"": ""order"",
                ""order"": {
                    ""id"": ""ws-order-2"",
                    ""status"": ""matched"",
                    ""asset_id"": """ + TestTokenId + @""",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""100""
                }
            }";
            _brokerage.SimulateWebSocketMessage(wsJson);

            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Filled, _orderEvents[0].Status);
            Assert.AreEqual(100m, _orderEvents[0].FillQuantity);
            Assert.AreEqual(0.65m, _orderEvents[0].FillPrice);
        }

        [Test]
        public void HandleOrderUpdate_PartialFill()
        {
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""ws-order-3"",""status"":""live""}");
            var order = CreateTestOrder(100m, 0.65m);
            _brokerage.PlaceOrder(order);
            _orderEvents.Clear();

            var wsJson = @"{
                ""event_type"": ""order"",
                ""order"": {
                    ""id"": ""ws-order-3"",
                    ""status"": ""matched"",
                    ""asset_id"": """ + TestTokenId + @""",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""30""
                }
            }";
            _brokerage.SimulateWebSocketMessage(wsJson);

            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.PartiallyFilled, _orderEvents[0].Status);
            Assert.AreEqual(30m, _orderEvents[0].FillQuantity);
        }

        [Test]
        public void HandleOrderUpdate_Canceled()
        {
            _handler.SetResponse("/order", @"{""success"":true,""orderID"":""ws-order-4"",""status"":""live""}");
            var order = CreateTestOrder();
            _brokerage.PlaceOrder(order);
            _orderEvents.Clear();

            var wsJson = @"{
                ""event_type"": ""order"",
                ""order"": {
                    ""id"": ""ws-order-4"",
                    ""status"": ""canceled"",
                    ""asset_id"": """ + TestTokenId + @""",
                    ""side"": ""BUY"",
                    ""price"": ""0.65"",
                    ""original_size"": ""100"",
                    ""size_matched"": ""0""
                }
            }";
            _brokerage.SimulateWebSocketMessage(wsJson);

            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Canceled, _orderEvents[0].Status);
        }

        #endregion
    }
}
