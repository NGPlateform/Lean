using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;
using QuantConnect.Brokerages.Polymarket.Tests.Helpers;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Tests
{
    [TestFixture]
    public class SentimentServiceTests
    {
        private SentimentService _service;

        [SetUp]
        public void Setup()
        {
            _service = new SentimentService(NullLogger<SentimentService>.Instance);
        }

        #region FGI Injection & Classification Tests

        [Test]
        public void InjectFearGreed_SetsValue()
        {
            _service.InjectFearGreed(25);
            Assert.AreEqual(25, _service.FearGreedIndex);
        }

        [Test]
        public void InjectFearGreed_ClampsTo0()
        {
            _service.InjectFearGreed(-10);
            Assert.AreEqual(0, _service.FearGreedIndex);
        }

        [Test]
        public void InjectFearGreed_ClampsTo100()
        {
            _service.InjectFearGreed(150);
            Assert.AreEqual(100, _service.FearGreedIndex);
        }

        [TestCase(10, "Extreme Fear")]
        [TestCase(24, "Extreme Fear")]
        [TestCase(25, "Fear")]
        [TestCase(35, "Fear")]
        [TestCase(44, "Fear")]
        [TestCase(45, "Neutral")]
        [TestCase(50, "Neutral")]
        [TestCase(55, "Neutral")]
        [TestCase(56, "Greed")]
        [TestCase(75, "Greed")]
        [TestCase(76, "Extreme Greed")]
        [TestCase(95, "Extreme Greed")]
        public void ClassifyFearGreed_ReturnsCorrectClassification(int value, string expected)
        {
            Assert.AreEqual(expected, SentimentService.ClassifyFearGreed(value));
        }

        [Test]
        public void InjectFearGreed_SetsClassification()
        {
            _service.InjectFearGreed(10);
            Assert.AreEqual("Extreme Fear", _service.FearGreedClassification);

            _service.InjectFearGreed(50);
            Assert.AreEqual("Neutral", _service.FearGreedClassification);

            _service.InjectFearGreed(90);
            Assert.AreEqual("Extreme Greed", _service.FearGreedClassification);
        }

        [Test]
        public void FearGreedIndex_DefaultIsNegativeOne()
        {
            Assert.AreEqual(-1, _service.FearGreedIndex);
        }

        [Test]
        public void FearGreedClassification_DefaultIsUnknown()
        {
            Assert.AreEqual("Unknown", _service.FearGreedClassification);
        }

        #endregion

        #region Funding Rate Injection & Signal Tests

        [Test]
        public void InjectFundingRate_SetsRawValue()
        {
            _service.InjectFundingRate(0.0003m);
            Assert.AreEqual(0.0003m, _service.FundingRate);
        }

        [Test]
        public void NormalizeFundingRate_BaselineIsZero()
        {
            // 0.0001 (baseline) → signal = 0
            var signal = SentimentService.NormalizeFundingRate(0.0001m);
            Assert.AreEqual(0.0m, signal);
        }

        [Test]
        public void NormalizeFundingRate_HighPositiveRate()
        {
            // 0.0011 → (0.0011 - 0.0001) / 0.001 = 1.0
            var signal = SentimentService.NormalizeFundingRate(0.0011m);
            Assert.AreEqual(1.0m, signal);
        }

        [Test]
        public void NormalizeFundingRate_NegativeRate()
        {
            // -0.0009 → (-0.0009 - 0.0001) / 0.001 = -1.0
            var signal = SentimentService.NormalizeFundingRate(-0.0009m);
            Assert.AreEqual(-1.0m, signal);
        }

        [Test]
        public void NormalizeFundingRate_ClampsPositive()
        {
            // Very high rate: 0.005 → clamped to 1.0
            var signal = SentimentService.NormalizeFundingRate(0.005m);
            Assert.AreEqual(1.0m, signal);
        }

        [Test]
        public void NormalizeFundingRate_ClampsNegative()
        {
            // Very negative rate: -0.005 → clamped to -1.0
            var signal = SentimentService.NormalizeFundingRate(-0.005m);
            Assert.AreEqual(-1.0m, signal);
        }

        [Test]
        public void InjectFundingRate_SetsSignal()
        {
            _service.InjectFundingRate(0.0001m); // baseline
            Assert.AreEqual(0.0m, _service.FundingRateSignal);
        }

        #endregion

        #region IsReady State Tests

        [Test]
        public void IsReady_FalseByDefault()
        {
            Assert.IsFalse(_service.IsReady);
        }

        [Test]
        public void IsReady_FalseWithOnlyFearGreed()
        {
            _service.InjectFearGreed(50);
            Assert.IsFalse(_service.IsReady);
        }

        [Test]
        public void IsReady_FalseWithOnlyFundingRate()
        {
            _service.InjectFundingRate(0.0001m);
            Assert.IsFalse(_service.IsReady);
        }

        [Test]
        public void IsReady_TrueWhenBothSet()
        {
            _service.InjectFearGreed(50);
            _service.InjectFundingRate(0.0001m);
            Assert.IsTrue(_service.IsReady);
        }

        #endregion

        #region Spread Multiplier Tests

        [Test]
        public void GetSentimentSpreadMultiplier_ReturnsOne_WhenNoData()
        {
            Assert.AreEqual(1.0m, _service.GetSentimentSpreadMultiplier());
        }

        [Test]
        public void GetSentimentSpreadMultiplier_NearOne_WhenNeutral()
        {
            _service.InjectFearGreed(50);
            _service.InjectFundingRate(0.0001m); // baseline
            var mult = _service.GetSentimentSpreadMultiplier();
            // Neutral FGI (50) → extremity=0, FR baseline → signal=0
            Assert.AreEqual(1.0m, mult);
        }

        [Test]
        public void GetSentimentSpreadMultiplier_GreaterThanOne_WhenExtremeFear()
        {
            _service.InjectFearGreed(5);
            _service.InjectFundingRate(0.0001m);
            var mult = _service.GetSentimentSpreadMultiplier();
            Assert.Greater(mult, 1.0m, "Extreme fear should widen spread");
        }

        [Test]
        public void GetSentimentSpreadMultiplier_GreaterThanOne_WhenExtremeGreed()
        {
            _service.InjectFearGreed(95);
            _service.InjectFundingRate(0.0001m);
            var mult = _service.GetSentimentSpreadMultiplier();
            Assert.Greater(mult, 1.0m, "Extreme greed should widen spread");
        }

        [Test]
        public void GetSentimentSpreadMultiplier_GreaterThanOne_WhenHighFundingRate()
        {
            _service.InjectFearGreed(50);
            _service.InjectFundingRate(0.001m); // very high positive
            var mult = _service.GetSentimentSpreadMultiplier();
            Assert.Greater(mult, 1.0m, "High funding rate should widen spread");
        }

        [Test]
        public void GetSentimentSpreadMultiplier_ClampedToMax()
        {
            // Maximum extremes
            _service.InjectFearGreed(0);
            _service.InjectFundingRate(0.01m); // very extreme
            var mult = _service.GetSentimentSpreadMultiplier();
            Assert.LessOrEqual(mult, 1.5m, "Should not exceed max multiplier");
        }

        #endregion

        #region Directional Bias Tests

        [Test]
        public void GetSentimentDirectionalBias_ReturnsZero_WhenNoData()
        {
            Assert.AreEqual(0.0m, _service.GetSentimentDirectionalBias());
        }

        [Test]
        public void GetSentimentDirectionalBias_Positive_WhenExtremeFear()
        {
            _service.InjectFearGreed(5);
            _service.InjectFundingRate(0.0001m);
            var bias = _service.GetSentimentDirectionalBias();
            Assert.Greater(bias, 0.0m, "Extreme fear should produce bullish (positive) contrarian bias");
        }

        [Test]
        public void GetSentimentDirectionalBias_Negative_WhenExtremeGreed()
        {
            _service.InjectFearGreed(95);
            _service.InjectFundingRate(0.0001m);
            var bias = _service.GetSentimentDirectionalBias();
            Assert.Less(bias, 0.0m, "Extreme greed should produce bearish (negative) contrarian bias");
        }

        [Test]
        public void GetSentimentDirectionalBias_NearZero_WhenNeutral()
        {
            _service.InjectFearGreed(50);
            _service.InjectFundingRate(0.0001m);
            var bias = _service.GetSentimentDirectionalBias();
            Assert.AreEqual(0.0m, bias, "Neutral sentiment should produce zero bias");
        }

        [Test]
        public void GetSentimentDirectionalBias_ClampedToRange()
        {
            _service.InjectFearGreed(0);
            _service.InjectFundingRate(-0.01m); // both push bullish
            var bias = _service.GetSentimentDirectionalBias();
            Assert.LessOrEqual(bias, 1.0m);
            Assert.GreaterOrEqual(bias, -1.0m);
        }

        #endregion

        #region Mock HTTP Parsing Tests

        [Test]
        public async Task PollFearGreedAsync_ParsesResponse()
        {
            var handler = new MockHttpMessageHandler();
            handler.SetResponse("alternative.me", "{\"data\":[{\"value\":\"25\",\"value_classification\":\"Extreme Fear\"}]}");
            var http = new System.Net.Http.HttpClient(handler);

            var service = new SentimentService(NullLogger<SentimentService>.Instance, http);
            await service.PollFearGreedAsync();

            Assert.AreEqual(25, service.FearGreedIndex);
            Assert.AreEqual("Fear", service.FearGreedClassification); // Our classification, not API's
        }

        [Test]
        public async Task PollFundingRateAsync_ParsesResponse()
        {
            var handler = new MockHttpMessageHandler();
            handler.SetResponse("binance.com", "{\"symbol\":\"BTCUSDT\",\"lastFundingRate\":\"0.00032100\"}");
            var http = new System.Net.Http.HttpClient(handler);

            var service = new SentimentService(NullLogger<SentimentService>.Instance, http);
            await service.PollFundingRateAsync();

            Assert.AreEqual(0.00032100m, service.FundingRate);
            Assert.AreNotEqual(0.0m, service.FundingRateSignal);
        }

        [Test]
        public async Task PollFearGreedAsync_HandlesEmptyData()
        {
            var handler = new MockHttpMessageHandler();
            handler.SetResponse("alternative.me", "{\"data\":[]}");
            var http = new System.Net.Http.HttpClient(handler);

            var service = new SentimentService(NullLogger<SentimentService>.Instance, http);
            await service.PollFearGreedAsync();

            Assert.AreEqual(-1, service.FearGreedIndex); // unchanged default
        }

        [Test]
        public async Task PollFundingRateAsync_HandlesInvalidRate()
        {
            var handler = new MockHttpMessageHandler();
            handler.SetResponse("binance.com", "{\"symbol\":\"BTCUSDT\"}");
            var http = new System.Net.Http.HttpClient(handler);

            var service = new SentimentService(NullLogger<SentimentService>.Instance, http);
            await service.PollFundingRateAsync();

            Assert.AreEqual(0.0m, service.FundingRate); // unchanged default
        }

        #endregion
    }
}
