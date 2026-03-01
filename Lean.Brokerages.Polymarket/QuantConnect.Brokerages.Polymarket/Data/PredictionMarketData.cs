/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NodaTime;
using QuantConnect.Data;

namespace QuantConnect.Brokerages.Polymarket.Data
{
    /// <summary>
    /// Custom data type for Polymarket prediction market data.
    /// Contains market-specific metadata alongside standard price data.
    /// </summary>
    public class PredictionMarketData : BaseData
    {
        /// <summary>
        /// The Polymarket condition ID for this market
        /// </summary>
        public string ConditionId { get; set; }

        /// <summary>
        /// The human-readable market question
        /// </summary>
        public string Question { get; set; }

        /// <summary>
        /// The settlement/end date of the market
        /// </summary>
        public DateTime EndDate { get; set; }

        /// <summary>
        /// Current YES token probability/price (0-1)
        /// </summary>
        public decimal YesProbability { get; set; }

        /// <summary>
        /// Current NO token probability/price (0-1)
        /// </summary>
        public decimal NoProbability { get; set; }

        /// <summary>
        /// 24-hour trading volume in USDC
        /// </summary>
        public decimal Volume24h { get; set; }

        /// <summary>
        /// Current liquidity depth in USDC
        /// </summary>
        public decimal Liquidity { get; set; }

        /// <summary>
        /// Whether the market has been resolved
        /// </summary>
        public bool IsResolved { get; set; }

        /// <summary>
        /// The winning outcome ("YES" or "NO") if resolved
        /// </summary>
        public string WinningOutcome { get; set; }

        /// <summary>
        /// Best bid price
        /// </summary>
        public decimal BestBid { get; set; }

        /// <summary>
        /// Best ask price
        /// </summary>
        public decimal BestAsk { get; set; }

        /// <summary>
        /// The data time zone for Polymarket (UTC)
        /// </summary>
        public override DateTimeZone DataTimeZone()
        {
            return DateTimeZone.Utc;
        }

        /// <summary>
        /// Return the URL source for this custom data
        /// </summary>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            if (isLiveMode)
            {
                return new SubscriptionDataSource(
                    $"https://clob.polymarket.com/markets/{ConditionId}",
                    SubscriptionTransportMedium.Rest,
                    FileFormat.UnfoldingCollection);
            }

            var source = Path.Combine(
                Globals.DataFolder,
                "crypto",
                "polymarket",
                "minute",
                config.Symbol.Value.ToLowerInvariant(),
                $"{date:yyyyMMdd}_prediction.csv");

            return new SubscriptionDataSource(source, SubscriptionTransportMedium.LocalFile);
        }

        /// <summary>
        /// Reader converts each line of the data source into BaseData objects
        /// </summary>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            try
            {
                var csv = line.Split(',');
                if (csv.Length < 8)
                {
                    return null;
                }

                var data = new PredictionMarketData
                {
                    Symbol = config.Symbol,
                    Time = DateTime.ParseExact(csv[0], "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture),
                    YesProbability = decimal.Parse(csv[1], NumberStyles.Any, CultureInfo.InvariantCulture),
                    NoProbability = decimal.Parse(csv[2], NumberStyles.Any, CultureInfo.InvariantCulture),
                    Volume24h = decimal.Parse(csv[3], NumberStyles.Any, CultureInfo.InvariantCulture),
                    Liquidity = decimal.Parse(csv[4], NumberStyles.Any, CultureInfo.InvariantCulture),
                    BestBid = decimal.Parse(csv[5], NumberStyles.Any, CultureInfo.InvariantCulture),
                    BestAsk = decimal.Parse(csv[6], NumberStyles.Any, CultureInfo.InvariantCulture),
                    IsResolved = csv[7] == "1"
                };

                data.Value = data.YesProbability;

                if (csv.Length > 8)
                {
                    data.WinningOutcome = csv[8];
                }

                return data;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Clones this data instance
        /// </summary>
        public override BaseData Clone()
        {
            return new PredictionMarketData
            {
                Symbol = Symbol,
                Time = Time,
                Value = Value,
                ConditionId = ConditionId,
                Question = Question,
                EndDate = EndDate,
                YesProbability = YesProbability,
                NoProbability = NoProbability,
                Volume24h = Volume24h,
                Liquidity = Liquidity,
                IsResolved = IsResolved,
                WinningOutcome = WinningOutcome,
                BestBid = BestBid,
                BestAsk = BestAsk
            };
        }
    }
}
