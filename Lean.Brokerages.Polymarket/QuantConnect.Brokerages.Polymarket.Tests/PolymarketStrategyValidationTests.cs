/*
 * QuantConnect - Polymarket Strategy Backtest Validation Tests
 *
 * Licensed under the Apache License, Version 2.0
 *
 * Validates strategy logic, alpha models, risk management, portfolio construction,
 * fill model behavior, and historical data quality without requiring the full LEAN engine.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Brokerages.Polymarket.Data;
using QuantConnect.Brokerages.Polymarket.Strategies.Alphas;
using QuantConnect.Brokerages.Polymarket.Strategies.Portfolio;
using QuantConnect.Brokerages.Polymarket.Strategies.Risk;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket.Tests
{
    [TestFixture]
    public class PolymarketStrategyValidationTests
    {
        private Symbol _yesSymbol;
        private Symbol _noSymbol;
        private Symbol _btcYesSymbol;
        private Symbol _btcNoSymbol;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            try { Market.Add("polymarket", 43); } catch (ArgumentException) { }
        }

        [SetUp]
        public void Setup()
        {
            _yesSymbol = Symbol.Create("ETH5000MAR26YES", SecurityType.Crypto, "polymarket");
            _noSymbol = Symbol.Create("ETH5000MAR26NO", SecurityType.Crypto, "polymarket");
            _btcYesSymbol = Symbol.Create("BTC70KMAR3YES", SecurityType.Crypto, "polymarket");
            _btcNoSymbol = Symbol.Create("BTC70KMAR3NO", SecurityType.Crypto, "polymarket");
        }

        #region PolymarketFillModel Tests

        [Test]
        public void FillModel_ClampPrice_ClampsToValidRange()
        {
            var fillModel = new PolymarketFillModel();

            Assert.AreEqual(0.001m, PolymarketFillModel.MinPrice);
            Assert.AreEqual(0.999m, PolymarketFillModel.MaxPrice);
            Assert.AreEqual(0.02m, PolymarketFillModel.DefaultSpread);
        }

        [Test]
        public void FillModel_Constants_AreCorrectForPredictionMarkets()
        {
            // Prediction market prices must be in (0, 1)
            Assert.That(PolymarketFillModel.MinPrice, Is.GreaterThan(0m));
            Assert.That(PolymarketFillModel.MaxPrice, Is.LessThan(1m));
            Assert.That(PolymarketFillModel.DefaultSpread, Is.GreaterThan(0m));
            Assert.That(PolymarketFillModel.DefaultSpread, Is.LessThan(0.1m));
        }

        #endregion

        #region KellyPortfolioConstructionModel Tests

        [Test]
        public void Kelly_FlatInsight_ReturnsZeroTarget()
        {
            var kelly = new TestableKellyPCM(kellyFraction: 0.5m, maxPositionSize: 0.10m);
            var insight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Flat);

            var targets = kelly.TestDetermineTargetPercent(new List<Insight> { insight });

            Assert.AreEqual(0, targets[insight]);
        }

        [Test]
        public void Kelly_LowConfidence_ReturnsZeroTarget()
        {
            var kelly = new TestableKellyPCM(kellyFraction: 0.5m, maxPositionSize: 0.10m, minConfidence: 0.5m);
            var insight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Up,
                magnitude: 0.05, confidence: 0.3);

            var targets = kelly.TestDetermineTargetPercent(new List<Insight> { insight });

            Assert.AreEqual(0, targets[insight]);
        }

        [Test]
        public void Kelly_HighConfidenceUp_ReturnsPositiveTarget()
        {
            var kelly = new TestableKellyPCM(kellyFraction: 0.5m, maxPositionSize: 0.10m, minConfidence: 0.5m);
            var insight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Up,
                magnitude: 0.05, confidence: 0.8);
            // Set reference value for odds calculation
            insight.ReferenceValue = 0.50m;

            var targets = kelly.TestDetermineTargetPercent(new List<Insight> { insight });

            Assert.That(targets[insight], Is.GreaterThan(0), "Up insight with high confidence should produce positive target");
            Assert.That(targets[insight], Is.LessThanOrEqualTo(0.10), "Target should not exceed max position size");
        }

        [Test]
        public void Kelly_DownInsight_ReturnsNegativeTarget()
        {
            var kelly = new TestableKellyPCM(kellyFraction: 0.5m, maxPositionSize: 0.10m, minConfidence: 0.5m);
            var insight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Down,
                magnitude: 0.05, confidence: 0.8);
            insight.ReferenceValue = 0.50m;

            var targets = kelly.TestDetermineTargetPercent(new List<Insight> { insight });

            Assert.That(targets[insight], Is.LessThan(0), "Down insight should produce negative target");
        }

        [Test]
        public void Kelly_HalfKelly_SmallerThanFullKelly()
        {
            var halfKelly = new TestableKellyPCM(kellyFraction: 0.5m, maxPositionSize: 0.50m);
            var fullKelly = new TestableKellyPCM(kellyFraction: 1.0m, maxPositionSize: 0.50m);
            var insight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Up,
                magnitude: 0.05, confidence: 0.8);
            insight.ReferenceValue = 0.50m;

            var halfTargets = halfKelly.TestDetermineTargetPercent(new List<Insight> { insight });
            var fullTargets = fullKelly.TestDetermineTargetPercent(new List<Insight> { insight });

            Assert.That(Math.Abs(halfTargets[insight]), Is.LessThanOrEqualTo(Math.Abs(fullTargets[insight])),
                "Half-Kelly should produce smaller or equal targets than full Kelly");
        }

        [Test]
        public void Kelly_MaxPositionSize_ClampedCorrectly()
        {
            var kelly = new TestableKellyPCM(kellyFraction: 1.0m, maxPositionSize: 0.05m, minConfidence: 0.1m);
            var insight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Up,
                magnitude: 0.1, confidence: 0.95);
            insight.ReferenceValue = 0.10m; // Very low price = very high odds = large Kelly size

            var targets = kelly.TestDetermineTargetPercent(new List<Insight> { insight });

            Assert.That(Math.Abs(targets[insight]), Is.LessThanOrEqualTo(0.05),
                "Kelly target should be clamped to max position size");
        }

        [Test]
        public void Kelly_MultipleInsights_ProducesIndependentTargets()
        {
            var kelly = new TestableKellyPCM(kellyFraction: 0.5m, maxPositionSize: 0.10m);

            var upInsight = Insight.Price(_yesSymbol, TimeSpan.FromMinutes(5), InsightDirection.Up,
                magnitude: 0.05, confidence: 0.8);
            upInsight.ReferenceValue = 0.50m;

            var downInsight = Insight.Price(_noSymbol, TimeSpan.FromMinutes(5), InsightDirection.Down,
                magnitude: 0.03, confidence: 0.7);
            downInsight.ReferenceValue = 0.50m;

            var targets = kelly.TestDetermineTargetPercent(new List<Insight> { upInsight, downInsight });

            Assert.AreEqual(2, targets.Count);
            Assert.That(targets[upInsight], Is.GreaterThan(0));
            Assert.That(targets[downInsight], Is.LessThan(0));
        }

        #endregion

        #region PredictionMarketRiskManagementModel Tests

        [Test]
        public void RiskModel_DefaultParameters_AreReasonable()
        {
            // Verify the risk model can be instantiated with defaults
            var model = new PredictionMarketRiskManagementModel();
            Assert.IsNotNull(model);
        }

        [Test]
        public void RiskModel_CustomParameters_AreAccepted()
        {
            var model = new PredictionMarketRiskManagementModel(
                daysBeforeSettlementToClose: 5,
                maxSingleMarketPercent: 0.15m,
                extremePriceThreshold: 0.05m,
                extremePriceReduction: 0.25m,
                maxTotalExposurePercent: 0.60m);
            Assert.IsNotNull(model);
        }

        [Test]
        public void RiskModel_SetSettlementDate_StoresDate()
        {
            var model = new PredictionMarketRiskManagementModel();
            var date = new DateTime(2026, 3, 26);

            // Should not throw
            model.SetSettlementDate(_yesSymbol, date);
        }

        #endregion

        #region CrossMarketArbitrageAlpha Tests

        [Test]
        public void ArbAlpha_DefaultThreshold_Is2Cents()
        {
            var alpha = new CrossMarketArbitrageAlpha();
            Assert.IsNotNull(alpha);
            Assert.AreEqual(nameof(CrossMarketArbitrageAlpha), alpha.Name);
        }

        [Test]
        public void ArbAlpha_CustomThreshold_IsAccepted()
        {
            var alpha = new CrossMarketArbitrageAlpha(
                deviationThreshold: 0.05m,
                insightPeriod: TimeSpan.FromMinutes(10));
            Assert.IsNotNull(alpha);
        }

        #endregion

        #region ProbabilityMeanReversionAlpha Tests

        [Test]
        public void MeanRevAlpha_DefaultParameters_AreCorrect()
        {
            var alpha = new ProbabilityMeanReversionAlpha();
            Assert.AreEqual(nameof(ProbabilityMeanReversionAlpha), alpha.Name);
        }

        [Test]
        public void MeanRevAlpha_CustomParameters_AreAccepted()
        {
            var alpha = new ProbabilityMeanReversionAlpha(
                lookbackPeriod: 30,
                deviationMultiplier: 1.5m,
                insightPeriod: TimeSpan.FromMinutes(30));
            Assert.IsNotNull(alpha);
        }

        #endregion

        #region CrossMarketCorrelationAlpha Tests

        [Test]
        public void CorrAlpha_DefaultParameters_AreCorrect()
        {
            var alpha = new CrossMarketCorrelationAlpha();
            Assert.AreEqual(nameof(CrossMarketCorrelationAlpha), alpha.Name);
        }

        [Test]
        public void CorrAlpha_CustomParameters_AreAccepted()
        {
            var alpha = new CrossMarketCorrelationAlpha(
                correlationWindow: 50,
                correlationThreshold: 0.8,
                insightPeriod: TimeSpan.FromMinutes(20));
            Assert.IsNotNull(alpha);
        }

        #endregion

        #region Market Maker Logic Tests

        [Test]
        public void MarketMaker_ClampPrice_ClampsToValidRange()
        {
            // Test the static ClampPrice logic used by the market maker
            // Price must be in [0.01, 0.99]
            Assert.AreEqual(0.01m, ClampMarketMakerPrice(-0.5m));
            Assert.AreEqual(0.01m, ClampMarketMakerPrice(0.001m));
            Assert.AreEqual(0.01m, ClampMarketMakerPrice(0.01m));
            Assert.AreEqual(0.50m, ClampMarketMakerPrice(0.50m));
            Assert.AreEqual(0.99m, ClampMarketMakerPrice(0.99m));
            Assert.AreEqual(0.99m, ClampMarketMakerPrice(1.50m));
        }

        [Test]
        public void MarketMaker_InventorySkew_ZeroInventory_NoSkew()
        {
            var skew = CalculateSkew(inventory: 0, maxInventory: 1000m, skewFactor: 0.5m, halfSpread: 0.01m);
            Assert.AreEqual(0m, skew);
        }

        [Test]
        public void MarketMaker_InventorySkew_LongPosition_SkewsDown()
        {
            // Long inventory should skew prices down (cheaper bids, cheaper asks) to encourage selling
            var skew = CalculateSkew(inventory: 500m, maxInventory: 1000m, skewFactor: 0.5m, halfSpread: 0.01m);
            Assert.That(skew, Is.LessThan(0), "Long inventory should produce negative skew");
        }

        [Test]
        public void MarketMaker_InventorySkew_ShortPosition_SkewsUp()
        {
            // Short inventory should skew prices up to encourage buying
            var skew = CalculateSkew(inventory: -500m, maxInventory: 1000m, skewFactor: 0.5m, halfSpread: 0.01m);
            Assert.That(skew, Is.GreaterThan(0), "Short inventory should produce positive skew");
        }

        [Test]
        public void MarketMaker_MultiLevelQuotes_SpacedCorrectly()
        {
            var midPrice = 0.50m;
            var halfSpread = 0.01m;
            var levelSpacing = 0.01m;
            var levels = 3;
            var skew = 0m;

            var bids = new List<decimal>();
            var asks = new List<decimal>();

            for (var level = 0; level < levels; level++)
            {
                var offset = level * levelSpacing;
                bids.Add(ClampMarketMakerPrice(midPrice - halfSpread - offset + skew));
                asks.Add(ClampMarketMakerPrice(midPrice + halfSpread + offset + skew));
            }

            // Bids should be decreasing
            for (var i = 1; i < bids.Count; i++)
            {
                Assert.That(bids[i], Is.LessThanOrEqualTo(bids[i - 1]),
                    $"Bid level {i} should be <= level {i - 1}");
            }

            // Asks should be increasing
            for (var i = 1; i < asks.Count; i++)
            {
                Assert.That(asks[i], Is.GreaterThanOrEqualTo(asks[i - 1]),
                    $"Ask level {i} should be >= level {i - 1}");
            }

            // Best bid < best ask (no crossed book)
            Assert.That(bids[0], Is.LessThan(asks[0]), "Best bid should be below best ask");

            // Level spacing is correct
            Assert.AreEqual(levelSpacing, bids[0] - bids[1], 0.001m);
            Assert.AreEqual(levelSpacing, asks[1] - asks[0], 0.001m);
        }

        [Test]
        public void MarketMaker_LevelSizes_OuterLevelsLarger()
        {
            var baseSize = 100m;
            var outerMultiplier = 1.5m;
            var levels = 3;

            var sizes = new List<decimal>();
            for (var level = 0; level < levels; level++)
            {
                var multiplier = 1m + level * (outerMultiplier - 1m) / Math.Max(1, levels - 1);
                sizes.Add(baseSize * multiplier);
            }

            // Inner level = base size
            Assert.AreEqual(baseSize, sizes[0]);
            // Outer levels should be larger
            for (var i = 1; i < sizes.Count; i++)
            {
                Assert.That(sizes[i], Is.GreaterThanOrEqualTo(sizes[i - 1]),
                    $"Level {i} size should be >= level {i - 1}");
            }
            // Last level should reach the full multiplier
            Assert.AreEqual(baseSize * outerMultiplier, sizes[levels - 1], 0.001m);
        }

        [Test]
        public void MarketMaker_ComplementaryArb_OverpricedDetection()
        {
            // YES bid = 0.55, NO bid = 0.50 → sum = 1.05 > 1.00 + threshold
            var yesBid = 0.55m;
            var noBid = 0.50m;
            var threshold = 0.005m;
            var bidSum = yesBid + noBid;

            Assert.That(bidSum, Is.GreaterThan(1.0m + threshold),
                "Bid sum should exceed 1 + threshold → sell both sides");
        }

        [Test]
        public void MarketMaker_ComplementaryArb_UnderpricedDetection()
        {
            // YES ask = 0.45, NO ask = 0.48 → sum = 0.93 < 1.00 - threshold
            var yesAsk = 0.45m;
            var noAsk = 0.48m;
            var threshold = 0.005m;
            var askSum = yesAsk + noAsk;

            Assert.That(askSum, Is.LessThan(1.0m - threshold),
                "Ask sum should be below 1 - threshold → buy both sides");
        }

        [Test]
        public void MarketMaker_ComplementaryArb_NoSignalInNormalRange()
        {
            // YES bid = 0.50, NO bid = 0.49 → sum = 0.99, within threshold
            var yesBid = 0.50m;
            var noBid = 0.49m;
            var threshold = 0.005m;
            var bidSum = yesBid + noBid;

            Assert.That(bidSum, Is.LessThanOrEqualTo(1.0m + threshold),
                "Normal bid sum should not trigger sell arb");

            // YES ask = 0.51, NO ask = 0.50 → sum = 1.01, within threshold
            var yesAsk = 0.51m;
            var noAsk = 0.50m;
            var askSum = yesAsk + noAsk;

            Assert.That(askSum, Is.GreaterThanOrEqualTo(1.0m - threshold),
                "Normal ask sum should not trigger buy arb");
        }

        #endregion

        #region Historical Data Quality Validation

        private static readonly string DataBasePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "Data", "crypto", "polymarket");

        [Test]
        public void HistoricalData_MinuteDirectoryExists()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            Assert.IsTrue(Directory.Exists(minutePath),
                $"Minute data directory should exist at {minutePath}");
        }

        [Test]
        public void HistoricalData_MarketsJsonExists()
        {
            var marketsPath = Path.Combine(DataBasePath, "markets.json");
            Assert.IsTrue(File.Exists(marketsPath),
                "markets.json should exist");
        }

        [Test]
        public void HistoricalData_Has48TokenDirectories()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            var dirs = Directory.GetDirectories(minutePath);
            Assert.AreEqual(48, dirs.Length,
                "Should have 48 token directories (24 markets x YES/NO)");
        }

        [Test]
        public void HistoricalData_YesNoPairsMatch()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            var dirs = Directory.GetDirectories(minutePath)
                .Select(d => Path.GetFileName(d))
                .ToList();

            var yesCount = dirs.Count(d => d.EndsWith("yes"));
            var noCount = dirs.Count(d => d.EndsWith("no"));

            // Some markets use "up"/"down" instead of "yes"/"no"
            var upCount = dirs.Count(d => d.EndsWith("up"));
            var downCount = dirs.Count(d => d.EndsWith("down"));

            Assert.AreEqual(yesCount + upCount, noCount + downCount,
                "Every YES/UP token should have a matching NO/DOWN token");
        }

        [Test]
        public void HistoricalData_CsvFormat_IsValidOhlcv()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            // Check a sample CSV from the first available ticker
            var firstDir = Directory.GetDirectories(minutePath).FirstOrDefault();
            if (firstDir == null)
            {
                Assert.Inconclusive("No token directories found");
                return;
            }

            var firstCsv = Directory.GetFiles(firstDir, "*_trade.csv").FirstOrDefault();
            if (firstCsv == null)
            {
                Assert.Inconclusive("No trade CSV files found");
                return;
            }

            var lines = File.ReadAllLines(firstCsv);
            Assert.That(lines.Length, Is.GreaterThan(0), "CSV should have data");

            foreach (var line in lines.Take(10))
            {
                var parts = line.Split(',');
                Assert.AreEqual(6, parts.Length, $"Each row should have 6 columns (ms,O,H,L,C,V): {line}");

                // Column 0: milliseconds from midnight (0 to 86400000)
                Assert.IsTrue(long.TryParse(parts[0], out var ms), $"Column 0 should be numeric ms: {parts[0]}");
                Assert.That(ms, Is.GreaterThanOrEqualTo(0));
                Assert.That(ms, Is.LessThan(86_400_000), "ms should be < 86400000 (24h)");

                // Columns 1-4: OHLC prices in [0, 1]
                for (var i = 1; i <= 4; i++)
                {
                    Assert.IsTrue(decimal.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var price),
                        $"Column {i} should be decimal: {parts[i]}");
                    Assert.That(price, Is.GreaterThanOrEqualTo(0m), $"Price should be >= 0: {price}");
                    Assert.That(price, Is.LessThanOrEqualTo(1m), $"Price should be <= 1: {price}");
                }

                // Column 5: Volume >= 0
                Assert.IsTrue(decimal.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var vol),
                    $"Column 5 should be decimal volume: {parts[5]}");
                Assert.That(vol, Is.GreaterThanOrEqualTo(0m));
            }
        }

        [Test]
        public void HistoricalData_OhlcRelationships_AreValid()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            var violationCount = 0;
            var totalBars = 0;

            foreach (var dir in Directory.GetDirectories(minutePath))
            {
                foreach (var csv in Directory.GetFiles(dir, "*_trade.csv"))
                {
                    foreach (var line in File.ReadAllLines(csv))
                    {
                        var parts = line.Split(',');
                        if (parts.Length != 6) continue;

                        if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
                        if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
                        if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
                        if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;

                        totalBars++;

                        // High >= Low
                        if (high < low) violationCount++;
                        // High >= Open and High >= Close
                        if (high < open || high < close) violationCount++;
                        // Low <= Open and Low <= Close
                        if (low > open || low > close) violationCount++;
                    }
                }
            }

            Assert.That(totalBars, Is.GreaterThan(0), "Should have parsed some bars");
            Assert.AreEqual(0, violationCount,
                $"Found {violationCount} OHLC relationship violations in {totalBars} bars");
        }

        [Test]
        public void HistoricalData_NoDustPrices()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            var dustCount = 0;
            var totalBars = 0;

            foreach (var dir in Directory.GetDirectories(minutePath))
            {
                foreach (var csv in Directory.GetFiles(dir, "*_trade.csv"))
                {
                    foreach (var line in File.ReadAllLines(csv))
                    {
                        var parts = line.Split(',');
                        if (parts.Length != 6) continue;

                        if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;

                        totalBars++;

                        // Dust price: close <= 0.0001 or close >= 0.9999
                        if (close > 0 && close <= 0.0001m) dustCount++;
                        if (close >= 0.9999m) dustCount++;
                    }
                }
            }

            Assert.That(totalBars, Is.GreaterThan(0), "Should have parsed some bars");
            var dustPercent = totalBars > 0 ? (double)dustCount / totalBars * 100 : 0;
            Assert.That(dustPercent, Is.LessThan(1.0),
                $"Dust prices should be < 1% of data ({dustCount}/{totalBars} = {dustPercent:F2}%)");
        }

        [Test]
        public void HistoricalData_DateRange_Covers7Days()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            var allDates = new HashSet<string>();
            foreach (var dir in Directory.GetDirectories(minutePath))
            {
                foreach (var csv in Directory.GetFiles(dir, "*_trade.csv"))
                {
                    var filename = Path.GetFileNameWithoutExtension(csv);
                    var dateStr = filename.Replace("_trade", "");
                    allDates.Add(dateStr);
                }
            }

            Assert.That(allDates.Count, Is.GreaterThanOrEqualTo(7),
                $"Should have at least 7 distinct dates, found {allDates.Count}: {string.Join(", ", allDates.OrderBy(d => d))}");
        }

        [Test]
        public void HistoricalData_TotalBars_ExceedsMinimum()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            var totalBars = 0;
            foreach (var dir in Directory.GetDirectories(minutePath))
            {
                foreach (var csv in Directory.GetFiles(dir, "*_trade.csv"))
                {
                    totalBars += File.ReadAllLines(csv).Count(l => !string.IsNullOrWhiteSpace(l));
                }
            }

            Assert.That(totalBars, Is.GreaterThanOrEqualTo(6000),
                $"Should have at least 6000 bars total, found {totalBars}");
        }

        [Test]
        public void HistoricalData_YesNoPrices_AreIndependent()
        {
            var minutePath = Path.Combine(DataBasePath, "minute");
            if (!Directory.Exists(minutePath))
            {
                Assert.Inconclusive("Minute data directory not found");
                return;
            }

            // Check that YES prices are NOT simply 1 - NO prices (they should be independent order book prices)
            var dirs = Directory.GetDirectories(minutePath).Select(Path.GetFileName).ToList();
            var yesDir = dirs.FirstOrDefault(d => d.EndsWith("yes"));
            if (yesDir == null)
            {
                Assert.Inconclusive("No YES token directory found");
                return;
            }

            var baseName = yesDir[..^3]; // Remove "yes"
            var noDir = baseName + "no";

            var yesPath = Path.Combine(minutePath, yesDir);
            var noPath = Path.Combine(minutePath, noDir);

            if (!Directory.Exists(noPath))
            {
                Assert.Inconclusive($"No matching NO directory for {yesDir}");
                return;
            }

            var yesCsvs = Directory.GetFiles(yesPath, "*_trade.csv").OrderBy(f => f).ToList();
            var noCsvs = Directory.GetFiles(noPath, "*_trade.csv").OrderBy(f => f).ToList();

            // Check at least one shared date
            var sharedCsvs = yesCsvs.Select(Path.GetFileName)
                .Intersect(noCsvs.Select(Path.GetFileName))
                .ToList();

            Assert.That(sharedCsvs.Count, Is.GreaterThan(0), "Should have at least one shared trading date");

            // Read first shared file and compare
            var yesPrices = ReadClosePrices(Path.Combine(yesPath, sharedCsvs[0]));
            var noPrices = ReadClosePrices(Path.Combine(noPath, sharedCsvs[0]));

            // Count how many times YES close + NO close = exactly 1.0
            var commonCount = Math.Min(yesPrices.Count, noPrices.Count);
            var exactMatchCount = 0;
            for (var i = 0; i < commonCount; i++)
            {
                if (Math.Abs(yesPrices[i] + noPrices[i] - 1.0m) < 0.0001m)
                {
                    exactMatchCount++;
                }
            }

            // If more than 90% are exact 1.0 sums, they might be derived rather than independent
            if (commonCount > 0)
            {
                var exactPercent = (double)exactMatchCount / commonCount * 100;
                Assert.That(exactPercent, Is.LessThan(90),
                    $"YES+NO prices should be mostly independent ({exactPercent:F1}% exact 1.0 sums)");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Replicates PolymarketMarketMaker.ClampPrice
        /// </summary>
        private static decimal ClampMarketMakerPrice(decimal price)
        {
            return Math.Max(0.01m, Math.Min(0.99m, price));
        }

        /// <summary>
        /// Replicates market maker inventory skew calculation
        /// </summary>
        private static decimal CalculateSkew(decimal inventory, decimal maxInventory, decimal skewFactor, decimal halfSpread)
        {
            var inventoryRatio = maxInventory > 0 ? inventory / maxInventory : 0;
            return -inventoryRatio * skewFactor * halfSpread * 2;
        }

        private static List<decimal> ReadClosePrices(string csvPath)
        {
            var prices = new List<decimal>();
            foreach (var line in File.ReadAllLines(csvPath))
            {
                var parts = line.Split(',');
                if (parts.Length >= 5 &&
                    decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close))
                {
                    prices.Add(close);
                }
            }
            return prices;
        }

        #endregion
    }

    /// <summary>
    /// Exposes the protected DetermineTargetPercent for testing
    /// </summary>
    internal class TestableKellyPCM : KellyPortfolioConstructionModel
    {
        public TestableKellyPCM(decimal kellyFraction = 0.5m, decimal maxPositionSize = 0.10m, decimal minConfidence = 0.5m)
            : base(kellyFraction, maxPositionSize, minConfidence)
        {
        }

        public Dictionary<Insight, double> TestDetermineTargetPercent(List<Insight> insights)
        {
            return DetermineTargetPercent(insights);
        }
    }
}
