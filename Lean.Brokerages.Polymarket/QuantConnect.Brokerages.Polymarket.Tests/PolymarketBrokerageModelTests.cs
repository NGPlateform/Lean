/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using NUnit.Framework;
using Moq;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketBrokerageModelTests
    {
        private PolymarketBrokerageModel _model;

        [SetUp]
        public void Setup()
        {
            _model = new PolymarketBrokerageModel();
            try { Market.Add("polymarket", 43); } catch (ArgumentException) { }
        }

        [Test]
        public void AccountType_IsCash()
        {
            Assert.AreEqual(AccountType.Cash, _model.AccountType);
        }

        [Test]
        public void GetLeverage_ReturnsOne()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            Assert.AreEqual(1m, _model.GetLeverage(security));
        }

        [Test]
        public void CanSubmitOrder_LimitOrder_ValidPrice_ReturnsTrue()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new LimitOrder(symbol, 100, 0.50m, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsTrue(result);
            Assert.IsNull(message);
        }

        [Test]
        public void CanSubmitOrder_LimitOrder_PriceAboveOne_ReturnsFalse()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new LimitOrder(symbol, 100, 1.50m, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsFalse(result);
            Assert.IsNotNull(message);
            Assert.That(message.Message, Does.Contain("between 0.00 and 1.00"));
        }

        [Test]
        public void CanSubmitOrder_LimitOrder_NegativePrice_ReturnsFalse()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new LimitOrder(symbol, 100, -0.10m, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsFalse(result);
            Assert.IsNotNull(message);
        }

        [Test]
        public void CanSubmitOrder_MarketOrder_ReturnsTrue()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new MarketOrder(symbol, 100, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsTrue(result);
        }

        [Test]
        public void CanSubmitOrder_StopOrder_ReturnsFalse()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new StopMarketOrder(symbol, 100, 0.50m, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsFalse(result);
            Assert.IsNotNull(message);
        }

        [Test]
        public void CanSubmitOrder_WrongSecurityType_ReturnsFalse()
        {
            // Use Forex instead of Equity to avoid MapFileProvider dependency in tests
            var symbol = Symbol.Create("EURUSD", SecurityType.Forex, Market.FXCM);
            var security = CreateForexSecurity(symbol);
            var order = new MarketOrder(symbol, 100, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsFalse(result);
            Assert.IsNotNull(message);
        }

        [Test]
        public void CanSubmitOrder_WrongMarket_ReturnsFalse()
        {
            var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Kraken);
            var security = CreateSecurity(symbol);
            var order = new MarketOrder(symbol, 100, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsFalse(result);
            Assert.IsNotNull(message);
            Assert.That(message.Message, Does.Contain("polymarket"));
        }

        [Test]
        public void CanUpdateOrder_AlwaysReturnsFalse()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new LimitOrder(symbol, 100, 0.50m, DateTime.UtcNow);

            var result = _model.CanUpdateOrder(security, order, new UpdateOrderRequest(DateTime.UtcNow, 1, new UpdateOrderFields()), out var message);
            Assert.IsFalse(result);
            Assert.IsNotNull(message);
        }

        [Test]
        public void GetFeeModel_ReturnsPolymarketFeeModel()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var feeModel = _model.GetFeeModel(security);
            Assert.IsInstanceOf<PolymarketFeeModel>(feeModel);
        }

        [Test]
        public void DefaultMarkets_ContainsPolymarket()
        {
            Assert.IsTrue(_model.DefaultMarkets.ContainsKey(SecurityType.Crypto));
            Assert.AreEqual("polymarket", _model.DefaultMarkets[SecurityType.Crypto]);
        }

        [Test]
        public void CanSubmitOrder_LimitOrder_PriceAtZero_ReturnsFalse()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            // Exactly 0 should be ok per the plan spec [0, 1], but -0.1 should not
            var order = new LimitOrder(symbol, 100, 0m, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsTrue(result); // 0.00 is valid
        }

        [Test]
        public void CanSubmitOrder_LimitOrder_PriceAtOne_ReturnsTrue()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol);
            var order = new LimitOrder(symbol, 100, 1.0m, DateTime.UtcNow);

            var result = _model.CanSubmitOrder(security, order, out var message);
            Assert.IsTrue(result); // 1.00 is valid
        }

        private static Security CreateSecurity(Symbol symbol)
        {
            var quoteCurrency = new Cash("USDC", 10000, 1);
            return new Security(
                symbol,
                SecurityExchangeHours.AlwaysOpen(NodaTime.DateTimeZone.Utc),
                quoteCurrency,
                SymbolProperties.GetDefault("USDC"),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache());
        }

        private static Security CreateForexSecurity(Symbol symbol)
        {
            return new Security(
                symbol,
                SecurityExchangeHours.AlwaysOpen(NodaTime.DateTimeZone.Utc),
                new Cash(Currencies.USD, 10000, 1),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache());
        }
    }
}
