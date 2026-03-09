using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    public class TokenCorrelationResult
    {
        public string TokenId { get; set; }
        public string Ticker { get; set; }
        public decimal Lag0Correlation { get; set; }
        public decimal Lag1Correlation { get; set; }
        public int BestLag { get; set; }
        public decimal BestLagCorrelation { get; set; }
        public decimal UpCorrelation { get; set; }
        public decimal DownCorrelation { get; set; }
        public int SampleCount { get; set; }
    }

    public class BatchCorrelationReport
    {
        public string BatchName { get; set; }
        public string MarketType { get; set; }
        public DateTime DataStartDate { get; set; }
        public DateTime DataEndDate { get; set; }
        public int TotalTokens { get; set; }
        public int TokensWithData { get; set; }

        // Aggregate stats
        public decimal MeanLag0Correlation { get; set; }
        public decimal MeanLag1Correlation { get; set; }
        public int BestLagMode { get; set; }
        public decimal AsymmetryRatio { get; set; }
        public decimal PercentAboveThreshold { get; set; }

        public List<TokenCorrelationResult> TokenResults { get; set; } = new();
    }

    public class CorrelationAnalyzer
    {
        private readonly ILogger _logger;
        private const int MaxLag = 6; // up to 60 minutes at 10-min intervals
        private const decimal CorrelationThreshold = 0.3m;

        public CorrelationAnalyzer(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Analyzes BTC correlation for all tokens in a batch.
        /// </summary>
        public BatchCorrelationReport AnalyzeBatch(string batchName, string dataRoot = null)
        {
            var loader = new HistoricalDataLoader(dataRoot, batchName);
            var markets = loader.LoadMarketMetadata();

            if (markets.Count == 0)
            {
                _logger.LogWarning("No market data for batch '{Batch}'", batchName);
                return new BatchCorrelationReport { BatchName = batchName };
            }

            var tokenTickerMap = HistoricalDataLoader.BuildTokenTickerMap(markets);
            var marketType = markets.FirstOrDefault()?.MarketType ?? "Unknown";

            // Determine date range by scanning CSV filenames (avoids iterating millions of days)
            var allDates = new List<DateTime>();
            foreach (var kvp in tokenTickerMap)
            {
                var dates = loader.GetAvailableDates(kvp.Value);
                if (dates.Count > 0)
                {
                    allDates.Add(dates.First());
                    allDates.Add(dates.Last());
                }
            }

            if (allDates.Count == 0)
            {
                _logger.LogWarning("No price data for batch '{Batch}'", batchName);
                return new BatchCorrelationReport { BatchName = batchName, MarketType = marketType };
            }

            var dataStart = allDates.Min();
            var dataEnd = allDates.Max();

            // Load BTC reference data from shared directory
            var btcLoader = new HistoricalDataLoader(dataRoot);
            var btcBars = btcLoader.LoadBtcBars(dataStart, dataEnd);

            if (btcBars.Count < 10)
            {
                _logger.LogWarning("Insufficient BTC data for batch '{Batch}' ({Count} bars)", batchName, btcBars.Count);
                return new BatchCorrelationReport
                {
                    BatchName = batchName,
                    MarketType = marketType,
                    DataStartDate = dataStart,
                    DataEndDate = dataEnd
                };
            }

            // Build BTC returns series indexed by time
            var btcReturns = BuildReturnSeries(btcBars);

            var tokenResults = new List<TokenCorrelationResult>();

            foreach (var kvp in tokenTickerMap)
            {
                var tokenId = kvp.Key;
                var ticker = kvp.Value;

                var tokenBars = loader.LoadPriceBars(ticker, dataStart, dataEnd);
                if (tokenBars.Count < 10) continue;

                var tokenReturns = BuildReturnSeries(tokenBars);
                var result = ComputeTokenCorrelation(tokenId, ticker, tokenReturns, btcReturns);
                if (result != null)
                    tokenResults.Add(result);
            }

            _logger.LogInformation("Batch '{Batch}': analyzed {Count}/{Total} tokens",
                batchName, tokenResults.Count, tokenTickerMap.Count);

            // Compute aggregate statistics
            var report = new BatchCorrelationReport
            {
                BatchName = batchName,
                MarketType = marketType,
                DataStartDate = dataStart,
                DataEndDate = dataEnd,
                TotalTokens = tokenTickerMap.Count,
                TokensWithData = tokenResults.Count,
                TokenResults = tokenResults
            };

            if (tokenResults.Count > 0)
            {
                report.MeanLag0Correlation = tokenResults.Average(r => r.Lag0Correlation);
                report.MeanLag1Correlation = tokenResults.Average(r => r.Lag1Correlation);

                // Mode of best lag
                report.BestLagMode = tokenResults
                    .GroupBy(r => r.BestLag)
                    .OrderByDescending(g => g.Count())
                    .First().Key;

                // Asymmetry ratio: mean |down_corr| / mean |up_corr|
                var meanUpCorr = tokenResults
                    .Where(r => r.UpCorrelation != 0)
                    .Select(r => Math.Abs(r.UpCorrelation))
                    .DefaultIfEmpty(0)
                    .Average();
                var meanDownCorr = tokenResults
                    .Where(r => r.DownCorrelation != 0)
                    .Select(r => Math.Abs(r.DownCorrelation))
                    .DefaultIfEmpty(0)
                    .Average();
                report.AsymmetryRatio = meanUpCorr > 0
                    ? meanDownCorr / meanUpCorr
                    : 0;

                // Percent of tokens with |corr| > threshold
                report.PercentAboveThreshold = (decimal)tokenResults
                    .Count(r => Math.Abs(r.Lag1Correlation) > CorrelationThreshold)
                    / tokenResults.Count * 100;
            }

            return report;
        }

        private TokenCorrelationResult ComputeTokenCorrelation(
            string tokenId, string ticker,
            List<(DateTime Time, decimal Return)> tokenReturns,
            List<(DateTime Time, decimal Return)> btcReturns)
        {
            // Align series by time
            var btcDict = btcReturns.ToDictionary(r => RoundTo10Min(r.Time), r => r.Return);
            var aligned = tokenReturns
                .Select(r => (Time: RoundTo10Min(r.Time), TokenReturn: r.Return))
                .Where(r => btcDict.ContainsKey(r.Time))
                .Select(r => (r.TokenReturn, BtcReturn: btcDict[r.Time]))
                .ToList();

            if (aligned.Count < 10) return null;

            var tokenList = aligned.Select(a => a.TokenReturn).ToList();
            var btcList = aligned.Select(a => a.BtcReturn).ToList();

            // Lag-0 correlation
            var lag0 = CorrelationMonitor.PearsonCorrelation(btcList, tokenList);

            // Lag-1 correlation (BTC leads token by 1 period = 10 min)
            var lag1 = ComputeLaggedCorrelation(btcList, tokenList, 1);

            // Find best lag (0 to MaxLag)
            var bestLag = 0;
            var bestCorr = Math.Abs(lag0);
            for (int lag = 1; lag <= MaxLag && lag < btcList.Count; lag++)
            {
                var lagCorr = ComputeLaggedCorrelation(btcList, tokenList, lag);
                if (Math.Abs(lagCorr) > bestCorr)
                {
                    bestCorr = Math.Abs(lagCorr);
                    bestLag = lag;
                }
            }

            // Up/down asymmetry: compute correlation for BTC-up and BTC-down subsets
            var upIndices = Enumerable.Range(0, btcList.Count).Where(i => btcList[i] > 0).ToList();
            var downIndices = Enumerable.Range(0, btcList.Count).Where(i => btcList[i] < 0).ToList();

            decimal upCorr = 0, downCorr = 0;
            if (upIndices.Count >= 5)
            {
                var upBtc = upIndices.Select(i => btcList[i]).ToList();
                var upToken = upIndices.Select(i => tokenList[i]).ToList();
                upCorr = CorrelationMonitor.PearsonCorrelation(upBtc, upToken);
            }
            if (downIndices.Count >= 5)
            {
                var downBtc = downIndices.Select(i => btcList[i]).ToList();
                var downToken = downIndices.Select(i => tokenList[i]).ToList();
                downCorr = CorrelationMonitor.PearsonCorrelation(downBtc, downToken);
            }

            return new TokenCorrelationResult
            {
                TokenId = tokenId,
                Ticker = ticker,
                Lag0Correlation = lag0,
                Lag1Correlation = lag1,
                BestLag = bestLag,
                BestLagCorrelation = bestLag == 0 ? lag0 : ComputeLaggedCorrelation(btcList, tokenList, bestLag),
                UpCorrelation = upCorr,
                DownCorrelation = downCorr,
                SampleCount = aligned.Count
            };
        }

        /// <summary>
        /// Computes Pearson correlation with BTC leading by 'lag' periods.
        /// btc[0..N-lag-1] vs token[lag..N-1]
        /// </summary>
        private static decimal ComputeLaggedCorrelation(List<decimal> btc, List<decimal> token, int lag)
        {
            if (lag >= btc.Count || lag >= token.Count) return 0;

            var n = Math.Min(btc.Count - lag, token.Count - lag);
            if (n < 5) return 0;

            var btcSlice = btc.GetRange(0, n);
            var tokenSlice = token.GetRange(lag, n);

            return CorrelationMonitor.PearsonCorrelation(btcSlice, tokenSlice);
        }

        private static List<(DateTime Time, decimal Return)> BuildReturnSeries(List<HistoricalBar> bars)
        {
            var returns = new List<(DateTime Time, decimal Return)>();
            for (int i = 1; i < bars.Count; i++)
            {
                if (bars[i - 1].Close == 0) continue;
                var ret = (bars[i].Close - bars[i - 1].Close) / bars[i - 1].Close;
                returns.Add((bars[i].Time, ret));
            }
            return returns;
        }

        private static DateTime RoundTo10Min(DateTime dt)
        {
            var ticks = dt.Ticks;
            var interval = TimeSpan.FromMinutes(10).Ticks;
            return new DateTime(ticks - ticks % interval, dt.Kind);
        }
    }
}
