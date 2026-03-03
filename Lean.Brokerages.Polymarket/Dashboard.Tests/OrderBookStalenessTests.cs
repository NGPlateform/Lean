using System;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class OrderBookStalenessTests
    {
        [Test]
        public void SeedOrderBook_SetsLastUpdatedTime()
        {
            var service = CreateMarketDataService();
            var book = new PolymarketOrderBook();

            var before = DateTime.UtcNow;
            service.SeedOrderBook("token-1", book);
            var after = DateTime.UtcNow;

            var lastUpdated = service.GetOrderBookLastUpdated("token-1");
            Assert.IsNotNull(lastUpdated);
            Assert.That(lastUpdated.Value, Is.GreaterThanOrEqualTo(before));
            Assert.That(lastUpdated.Value, Is.LessThanOrEqualTo(after));
        }

        [Test]
        public void GetOrderBookLastUpdated_ReturnsNullForUnknownToken()
        {
            var service = CreateMarketDataService();

            var lastUpdated = service.GetOrderBookLastUpdated("unknown-token");
            Assert.IsNull(lastUpdated);
        }

        [Test]
        public void DryRunSettings_StalenessThreshold_DefaultIs60()
        {
            var settings = new DryRunSettings();
            Assert.AreEqual(60, settings.OrderBookStaleThresholdSeconds);
        }

        private static MarketDataService CreateMarketDataService()
        {
            // MarketDataService constructor requires IHubContext, credentials, TradingService, ILogger
            // For these unit tests we only exercise SeedOrderBook/GetOrderBookLastUpdated which don't
            // touch those dependencies, so we pass nulls
            return new MarketDataService(null, null, null, null);
        }
    }
}
