using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class BtcPriceServiceTests
    {
        private BtcPriceService _service;

        [SetUp]
        public void Setup()
        {
            var logger = NullLogger<BtcPriceService>.Instance;
            _service = new BtcPriceService(logger);
        }

        #region CurrentPrice Tests

        [Test]
        public void CurrentPrice_ReturnsNull_WhenNoSamples()
        {
            Assert.IsNull(_service.CurrentPrice);
        }

        [Test]
        public void CurrentPrice_ReturnsLatest_AfterInject()
        {
            _service.InjectSample(85000m);
            _service.InjectSample(85500m);

            Assert.AreEqual(85500m, _service.CurrentPrice);
        }

        [Test]
        public void SampleCount_TracksInjectedSamples()
        {
            Assert.AreEqual(0, _service.SampleCount);

            _service.InjectSample(85000m);
            Assert.AreEqual(1, _service.SampleCount);

            _service.InjectSample(85500m);
            Assert.AreEqual(2, _service.SampleCount);
        }

        #endregion

        #region GetReturn Tests

        [Test]
        public void GetReturn_ReturnsZero_WhenNoSamples()
        {
            Assert.AreEqual(0m, _service.GetReturn(6));
        }

        [Test]
        public void GetReturn_ReturnsZero_WhenOneSample()
        {
            _service.InjectSample(85000m);
            Assert.AreEqual(0m, _service.GetReturn(6));
        }

        [Test]
        public void GetReturn_CalculatesCorrectly()
        {
            _service.InjectSample(80000m);
            _service.InjectSample(81000m);
            _service.InjectSample(82000m);

            // Return over 2 points: (82000 - 80000) / 80000 = 0.025
            var ret = _service.GetReturn(2);
            Assert.AreEqual(0.025m, ret);
        }

        [Test]
        public void GetReturn_ClampsLookback_WhenExceedsSamples()
        {
            _service.InjectSample(80000m);
            _service.InjectSample(84000m);

            // Lookback of 100 should still work — clamps to index 0
            var ret = _service.GetReturn(100);
            Assert.AreEqual(0.05m, ret); // (84000 - 80000) / 80000
        }

        [Test]
        public void GetReturn_NegativeReturn()
        {
            _service.InjectSample(100000m);
            _service.InjectSample(95000m);

            var ret = _service.GetReturn(1);
            Assert.AreEqual(-0.05m, ret);
        }

        #endregion

        #region Momentum Tests

        [Test]
        public void Momentum_ReturnsZero_WhenNoSamples()
        {
            Assert.AreEqual(0m, _service.Momentum);
        }

        [Test]
        public void Momentum_Positive_WhenPriceRising()
        {
            // Inject a rising price series
            for (int i = 0; i < 20; i++)
            {
                _service.InjectSample(80000m + i * 100m);
            }

            // Short EMA should be above long EMA → positive momentum
            Assert.Greater(_service.Momentum, 0m);
        }

        [Test]
        public void Momentum_Negative_WhenPriceFalling()
        {
            // Inject a falling price series
            for (int i = 0; i < 20; i++)
            {
                _service.InjectSample(90000m - i * 100m);
            }

            // Short EMA should be below long EMA → negative momentum
            Assert.Less(_service.Momentum, 0m);
        }

        [Test]
        public void Momentum_NearZero_WhenStable()
        {
            for (int i = 0; i < 20; i++)
            {
                _service.InjectSample(85000m);
            }

            Assert.AreEqual(0m, _service.Momentum);
        }

        #endregion

        #region Sliding Window Tests

        [Test]
        public void SlidingWindow_LimitsTo60Samples()
        {
            for (int i = 0; i < 100; i++)
            {
                _service.InjectSample(80000m + i);
            }

            Assert.AreEqual(60, _service.SampleCount);
            // Latest should be the last injected
            Assert.AreEqual(80099m, _service.CurrentPrice);
        }

        #endregion

        #region PollPriceAsync Tests

        [Test]
        public async Task PollPriceAsync_ParsesBinanceResponse()
        {
            var handler = new MockBinanceHandler("{\"symbol\":\"BTCUSDT\",\"price\":\"87654.32000000\"}");
            var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var logger = NullLogger<BtcPriceService>.Instance;
            var service = new BtcPriceService(logger, httpClient);

            await service.PollPriceAsync();

            Assert.AreEqual(87654.32m, service.CurrentPrice);
            Assert.AreEqual(1, service.SampleCount);
        }

        [Test]
        public void PollPriceAsync_HandlesInvalidJson()
        {
            var handler = new MockBinanceHandler("invalid json");
            var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var logger = NullLogger<BtcPriceService>.Instance;
            var service = new BtcPriceService(logger, httpClient);

            Assert.ThrowsAsync<Newtonsoft.Json.JsonReaderException>(() => service.PollPriceAsync());
            Assert.IsNull(service.CurrentPrice);
        }

        #endregion

        private class MockBinanceHandler : HttpMessageHandler
        {
            private readonly string _response;

            public MockBinanceHandler(string response)
            {
                _response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_response)
                });
            }
        }
    }
}
