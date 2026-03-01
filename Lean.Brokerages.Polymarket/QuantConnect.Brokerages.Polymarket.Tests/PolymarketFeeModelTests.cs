/*
 * QuantConnect - Polymarket Brokerage Tests
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using NUnit.Framework;
using Moq;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Crypto;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketFeeModelTests
    {
        private PolymarketFeeModel _feeModel;

        [SetUp]
        public void Setup()
        {
            _feeModel = new PolymarketFeeModel();
            try { Market.Add("polymarket", 43); } catch (ArgumentException) { }
        }

        [Test]
        public void MakerFee_IsZero()
        {
            Assert.AreEqual(0.0m, PolymarketFeeModel.MakerFee);
        }

        [Test]
        public void TakerFee_IsZero()
        {
            Assert.AreEqual(0.0m, PolymarketFeeModel.TakerFee);
        }

        [Test]
        public void SettlementFee_IsTwoPercent()
        {
            Assert.AreEqual(0.02m, PolymarketFeeModel.SettlementFee);
        }

        [Test]
        public void GetOrderFee_ZeroFee_ReturnsZeroFee()
        {
            var symbol = Symbol.Create("TESTYES", SecurityType.Crypto, "polymarket");
            var security = CreateSecurity(symbol, 0.50m, 0.51m);
            var order = new MarketOrder(symbol, 100, DateTime.UtcNow);

            var parameters = new OrderFeeParameters(security, order);
            var fee = _feeModel.GetOrderFee(parameters);

            Assert.AreEqual(OrderFee.Zero, fee);
        }

        private static Security CreateSecurity(Symbol symbol, decimal bidPrice, decimal askPrice)
        {
            var security = new Mock<Security>(
                symbol,
                SecurityExchangeHours.AlwaysOpen(NodaTime.DateTimeZone.Utc),
                new Cash("USDC", 0, 1),
                SymbolProperties.GetDefault("USDC"),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache());

            security.Setup(s => s.BidPrice).Returns(bidPrice);
            security.Setup(s => s.AskPrice).Returns(askPrice);
            security.Setup(s => s.Price).Returns((bidPrice + askPrice) / 2);

            return security.Object;
        }
    }
}
