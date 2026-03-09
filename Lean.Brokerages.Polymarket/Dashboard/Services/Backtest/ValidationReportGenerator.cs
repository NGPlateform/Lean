using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    public class ValidationReportGenerator
    {
        private readonly ILogger _logger;

        public ValidationReportGenerator(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Runs correlation analysis and backtests for all downloaded batches, then generates a Markdown report.
        /// </summary>
        public string GenerateReport(string dataRoot = null)
        {
            // Find all downloaded batches
            var expandedService = new ExpandedDataDownloadService(_logger, dataRoot);
            var batchNames = expandedService.GetDownloadedBatchNames();
            expandedService.Dispose();

            if (batchNames.Count == 0)
            {
                _logger.LogWarning("No downloaded batches found. Run --download-expanded first.");
                return null;
            }

            _logger.LogInformation("Found {Count} downloaded batches: {Names}",
                batchNames.Count, string.Join(", ", batchNames));

            // Step 1: Run correlation analysis for each batch
            var analyzer = new CorrelationAnalyzer(_logger);
            var correlationReports = new Dictionary<string, BatchCorrelationReport>();

            foreach (var batchName in batchNames)
            {
                _logger.LogInformation("Analyzing correlations for batch '{Batch}'...", batchName);
                var report = analyzer.AnalyzeBatch(batchName, dataRoot);
                correlationReports[batchName] = report;
            }

            // Step 2: Run backtests for each batch
            var runner = new BacktestRunner(_logger);
            var backtestResults = runner.RunBatchComparison(batchNames, dataRoot: dataRoot);

            // Step 3: Generate Markdown report
            var markdown = BuildMarkdown(correlationReports, backtestResults);

            // Save to file
            var resolvedRoot = dataRoot ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
            var outputDir = Path.Combine(resolvedRoot, "Dashboard", "docs");
            if (!Directory.Exists(outputDir))
            {
                // Try relative to current directory
                outputDir = Path.Combine(Directory.GetCurrentDirectory(), "docs");
            }
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, "Validation-Report.md");

            // Preserve manual analysis sections (after "## 6.") if file already exists
            string manualSections = null;
            if (File.Exists(outputPath))
            {
                var existing = File.ReadAllText(outputPath);
                var marker = "\n## 6.";
                var idx = existing.IndexOf(marker);
                if (idx >= 0)
                {
                    manualSections = existing.Substring(idx);
                }
            }

            if (manualSections != null)
            {
                // Replace the auto-generated trailing line with manual sections
                var autoTrailer = "---\n*Report generated automatically by ValidationReportGenerator*\n";
                markdown = markdown.Replace(autoTrailer, "");
                // Also try with \r\n
                autoTrailer = "---\r\n*Report generated automatically by ValidationReportGenerator*\r\n";
                markdown = markdown.Replace(autoTrailer, "");
                markdown = markdown.TrimEnd() + "\n" + manualSections;
            }

            File.WriteAllText(outputPath, markdown);

            _logger.LogInformation("Validation report saved to {Path}", outputPath);
            return outputPath;
        }

        private string BuildMarkdown(
            Dictionary<string, BatchCorrelationReport> correlations,
            Dictionary<string, BacktestComparisonResult> backtests)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Cross-Category Validation Report");
            sb.AppendLine();
            sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine($"**Batches analyzed:** {correlations.Count}");
            sb.AppendLine();

            // Section 1: Hypothesis Review
            sb.AppendLine("## 1. Hypotheses Under Test");
            sb.AppendLine();
            sb.AppendLine("| # | Hypothesis | Validation Criterion |");
            sb.AppendLine("|---|-----------|---------------------|");
            sb.AppendLine("| H1 | BTC price leads Polymarket token probabilities by ~10 min | BTC-price batches: lag1_corr > 0.5, best_lag = 1 |");
            sb.AppendLine("| H2 | BTC correlation extends to ETH/altcoin prediction markets | ETH/altcoin batches: lag1_corr > 0.3 |");
            sb.AppendLine("| H3 | Non-price crypto markets have weak BTC correlation | crypto_events: lag1_corr < 0.3 |");
            sb.AppendLine("| H4 | Political markets have zero BTC correlation (negative control) | politics_control: |corr| < 0.15 |");
            sb.AppendLine("| H5 | Downside asymmetry is persistent across time periods | asymmetry_ratio > 1.2 across BTC batches |");
            sb.AppendLine();

            // Section 2: Correlation Comparison Table
            sb.AppendLine("## 2. Correlation Analysis by Category");
            sb.AppendLine();
            sb.AppendLine("| Batch | Type | Tokens | Lag-0 Corr | Lag-1 Corr | Best Lag | Asymmetry | % > 0.3 | Validates? |");
            sb.AppendLine("|-------|------|--------|-----------|-----------|---------|-----------|---------|-----------|");

            foreach (var kvp in correlations.OrderBy(k => k.Key))
            {
                var name = kvp.Key;
                var r = kvp.Value;
                var validates = EvaluateCorrelationHypothesis(name, r);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2}/{3} | {4:F3} | {5:F3} | {6} | {7:F2} | {8:F1}% | {9} |",
                    name,
                    r.MarketType ?? "—",
                    r.TokensWithData, r.TotalTokens,
                    r.MeanLag0Correlation,
                    r.MeanLag1Correlation,
                    r.BestLagMode,
                    r.AsymmetryRatio,
                    r.PercentAboveThreshold,
                    validates));
            }

            sb.AppendLine();

            // Section 3: Backtest Performance Table
            sb.AppendLine("## 3. Backtest Performance by Category");
            sb.AppendLine();
            sb.AppendLine("| Batch | Best Strategy | Params | PnL | Sharpe | MaxDD | WinRate | Trades |");
            sb.AppendLine("|-------|--------------|--------|-----|--------|-------|---------|--------|");

            foreach (var kvp in backtests.OrderBy(k => k.Key))
            {
                var name = kvp.Key;
                var comparison = kvp.Value;

                if (comparison.Results.Count == 0)
                {
                    sb.AppendLine($"| {name} | — | — | — | — | — | — | — |");
                    continue;
                }

                var best = comparison.Results.OrderByDescending(r => r.Metrics.SharpeRatio).First();
                var paramStr = string.Join(" ", best.Parameters.Select(p =>
                {
                    var key = p.Key.Length > 2 ? p.Key.Substring(0, 2).ToUpper() : p.Key.ToUpper();
                    return $"{key}={p.Value}";
                }));

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3} | {4:F2} | {5} | {6:F1}% | {7} |",
                    name,
                    best.StrategyName,
                    paramStr,
                    best.Metrics.TotalPnl >= 0 ? $"+${best.Metrics.TotalPnl:F2}" : $"-${Math.Abs(best.Metrics.TotalPnl):F2}",
                    best.Metrics.SharpeRatio,
                    $"-${best.Metrics.MaxDrawdown:F2}",
                    best.Metrics.WinRate,
                    best.Metrics.TradeCount));
            }

            sb.AppendLine();

            // Section 4: Per-batch detail for top tokens
            sb.AppendLine("## 4. Top Correlated Tokens per Batch");
            sb.AppendLine();

            foreach (var kvp in correlations.OrderBy(k => k.Key))
            {
                var name = kvp.Key;
                var r = kvp.Value;

                if (r.TokenResults.Count == 0) continue;

                sb.AppendLine($"### {name} ({r.MarketType})");
                sb.AppendLine();
                sb.AppendLine("| Ticker | Lag-0 | Lag-1 | Best Lag | Up Corr | Down Corr | Samples |");
                sb.AppendLine("|--------|-------|-------|---------|---------|-----------|---------|");

                var topTokens = r.TokenResults
                    .OrderByDescending(t => Math.Abs(t.Lag1Correlation))
                    .Take(10);

                foreach (var t in topTokens)
                {
                    var tickerShort = t.Ticker.Length > 35 ? t.Ticker.Substring(0, 35) + "..." : t.Ticker;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "| {0} | {1:F3} | {2:F3} | {3} | {4:F3} | {5:F3} | {6} |",
                        tickerShort,
                        t.Lag0Correlation,
                        t.Lag1Correlation,
                        t.BestLag,
                        t.UpCorrelation,
                        t.DownCorrelation,
                        t.SampleCount));
                }

                sb.AppendLine();
            }

            // Section 5: Conclusions
            sb.AppendLine("## 5. Conclusions");
            sb.AppendLine();

            var conclusions = new List<(string hypothesis, string result, string detail)>();

            // H1: BTC lead stability
            var btcBatches = correlations
                .Where(k => k.Key.StartsWith("btc_price"))
                .ToList();
            if (btcBatches.Count > 0)
            {
                var allPass = btcBatches.All(b => b.Value.MeanLag1Correlation > 0.5m && b.Value.BestLagMode == 1);
                var avgLag1 = btcBatches.Average(b => (double)b.Value.MeanLag1Correlation);
                conclusions.Add(("H1: BTC lead is stable across time",
                    allPass ? "VALIDATED" : "PARTIAL",
                    $"Mean lag-1 correlation across BTC batches: {avgLag1:F3}"));
            }

            // H2: Cross-asset correlation
            var ethBatch = correlations.GetValueOrDefault("eth_price_recent");
            var altBatch = correlations.GetValueOrDefault("altcoin_price");
            if (ethBatch != null || altBatch != null)
            {
                var ethPass = ethBatch != null && ethBatch.MeanLag1Correlation > 0.3m;
                var altPass = altBatch != null && altBatch.MeanLag1Correlation > 0.3m;
                var ethStr = ethBatch != null ? ethBatch.MeanLag1Correlation.ToString("F3") : "N/A";
                var altStr = altBatch != null ? altBatch.MeanLag1Correlation.ToString("F3") : "N/A";
                conclusions.Add(("H2: BTC correlation extends to ETH/altcoins",
                    ethPass || altPass ? "VALIDATED" : "NOT VALIDATED",
                    $"ETH lag-1: {ethStr}, Alt lag-1: {altStr}"));
            }

            // H3: Non-price crypto markets
            var eventsBatch = correlations.GetValueOrDefault("crypto_events");
            if (eventsBatch != null)
            {
                var pass = Math.Abs(eventsBatch.MeanLag1Correlation) < 0.3m;
                conclusions.Add(("H3: Non-price crypto markets have weak BTC correlation",
                    pass ? "VALIDATED" : "NOT VALIDATED",
                    $"crypto_events lag-1: {eventsBatch.MeanLag1Correlation:F3}"));
            }

            // H4: Politics negative control
            var politicsBatch = correlations.GetValueOrDefault("politics_control");
            if (politicsBatch != null)
            {
                var pass = Math.Abs(politicsBatch.MeanLag1Correlation) < 0.15m;
                conclusions.Add(("H4: Political markets have zero BTC correlation",
                    pass ? "VALIDATED" : "NOT VALIDATED",
                    $"politics_control lag-1: {politicsBatch.MeanLag1Correlation:F3}"));
            }

            // H5: Downside asymmetry
            if (btcBatches.Count > 0)
            {
                var allAsymmetric = btcBatches.All(b => b.Value.AsymmetryRatio > 1.2m);
                var avgAsymmetry = btcBatches.Average(b => (double)b.Value.AsymmetryRatio);
                conclusions.Add(("H5: Downside asymmetry is persistent",
                    allAsymmetric ? "VALIDATED" : "PARTIAL",
                    $"Mean asymmetry ratio across BTC batches: {avgAsymmetry:F2}"));
            }

            sb.AppendLine("| Hypothesis | Result | Detail |");
            sb.AppendLine("|-----------|--------|--------|");
            foreach (var (hyp, result, detail) in conclusions)
            {
                var emoji = result == "VALIDATED" ? "PASS" : result == "PARTIAL" ? "PARTIAL" : "FAIL";
                sb.AppendLine($"| {hyp} | **{emoji}** | {detail} |");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("*Report generated automatically by ValidationReportGenerator*");

            return sb.ToString();
        }

        private static string EvaluateCorrelationHypothesis(string batchName, BatchCorrelationReport report)
        {
            if (report.TokensWithData == 0) return "NO DATA";

            if (batchName.StartsWith("btc_price"))
            {
                // BTC price batches: expect lag1_corr > 0.5 and best_lag == 1
                if (report.MeanLag1Correlation > 0.5m && report.BestLagMode == 1)
                    return "PASS";
                if (report.MeanLag1Correlation > 0.3m)
                    return "PARTIAL";
                return "FAIL";
            }

            if (batchName.StartsWith("eth_") || batchName == "altcoin_price")
            {
                // ETH/altcoin: expect lag1_corr > 0.3
                return report.MeanLag1Correlation > 0.3m ? "PASS" : "FAIL";
            }

            if (batchName == "crypto_events")
            {
                // Non-price crypto: expect lag1_corr < 0.3
                return Math.Abs(report.MeanLag1Correlation) < 0.3m ? "PASS" : "FAIL";
            }

            if (batchName == "politics_control")
            {
                // Politics: expect |corr| < 0.15
                return Math.Abs(report.MeanLag1Correlation) < 0.15m ? "PASS" : "FAIL";
            }

            return "N/A";
        }
    }
}
