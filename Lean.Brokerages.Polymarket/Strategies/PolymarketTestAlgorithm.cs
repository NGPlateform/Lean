/*
 * QuantConnect - Polymarket Test Algorithm
 *
 * A simple test algorithm to verify the Polymarket brokerage integration.
 * Run this via LEAN Launcher to see console output of market data and trades.
 */

using System;
using System.Collections.Generic;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.Polymarket;
using QuantConnect.Data;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Polymarket.Strategies
{
    /// <summary>
    /// Simple test algorithm that subscribes to Polymarket tokens
    /// and logs price data. Use this to verify connectivity and data flow.
    /// </summary>
    public class PolymarketTestAlgorithm : QCAlgorithm
    {
        private Symbol _yesSymbol;
        private Symbol _noSymbol;
        private bool _hasLoggedData;

        public override void Initialize()
        {
            // === Backtest Settings ===
            SetStartDate(2026, 1, 1);
            SetEndDate(2026, 3, 1);
            SetCash("USDC", 10000);

            // Register Polymarket market
            Market.Add("polymarket", 43);

            // Set brokerage model
            SetBrokerageModel(new PolymarketBrokerageModel());

            // === Subscribe to tokens ===
            // These are example tickers - replace with actual mapped tickers
            _yesSymbol = AddCrypto("ETH5000MAR26YES", market: "polymarket").Symbol;
            _noSymbol = AddCrypto("ETH5000MAR26NO", market: "polymarket").Symbol;

            // Log initialization
            Log("=== Polymarket Test Algorithm Initialized ===");
            Log($"  YES Symbol: {_yesSymbol}");
            Log($"  NO Symbol:  {_noSymbol}");
            Log($"  Cash:       {Portfolio.CashBook["USDC"].Amount} USDC");
            Log("=============================================");

            // Schedule a periodic status report
            Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromHours(1)), () =>
            {
                LogPortfolioStatus();
            });
        }

        public override void OnData(Slice data)
        {
            // Log first data received
            if (!_hasLoggedData)
            {
                Log($"[{Time}] First data received!");
                _hasLoggedData = true;
            }

            // Get current prices
            var yesPrice = Securities[_yesSymbol].Price;
            var noPrice = Securities[_noSymbol].Price;

            if (yesPrice <= 0 || noPrice <= 0) return;

            var priceSum = yesPrice + noPrice;

            // Log price data every 100 slices
            if (Time.Minute == 0 && Time.Hour % 4 == 0)
            {
                Log($"[{Time}] YES={yesPrice:F4} NO={noPrice:F4} SUM={priceSum:F4}");
            }

            // === Simple test trade logic ===
            // If not invested and prices look reasonable, buy a small position
            if (!Portfolio.Invested && yesPrice > 0.1m && yesPrice < 0.9m)
            {
                var quantity = 100; // Buy 100 YES tokens
                var ticket = LimitOrder(_yesSymbol, quantity, yesPrice);
                Log($"[{Time}] Placed BUY order: {quantity} YES @ {yesPrice:F4} | OrderId={ticket.OrderId}");
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            Log($"[{Time}] ORDER EVENT: {orderEvent}");

            if (orderEvent.Status == OrderStatus.Filled)
            {
                Log($"  Filled {orderEvent.FillQuantity} @ {orderEvent.FillPrice:F4}");
                Log($"  Fee: {orderEvent.OrderFee}");
                LogPortfolioStatus();
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log("=== Algorithm Complete ===");
            LogPortfolioStatus();
        }

        private void LogPortfolioStatus()
        {
            Log($"--- Portfolio Status [{Time}] ---");
            Log($"  Total Value:  {Portfolio.TotalPortfolioValue:F2}");
            Log($"  USDC Cash:    {Portfolio.CashBook["USDC"].Amount:F2}");

            foreach (var kvp in Portfolio)
            {
                if (kvp.Value.Invested)
                {
                    var holding = kvp.Value;
                    Log($"  {kvp.Key}: Qty={holding.Quantity} AvgPrice={holding.AveragePrice:F4} Value={holding.HoldingsValue:F2}");
                }
            }
            Log("----------------------------");
        }
    }
}
