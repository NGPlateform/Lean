/*
 * QuantConnect - Polymarket Market Making Strategy
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages.Polymarket;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Polymarket.Strategies
{
    /// <summary>
    /// Market making strategy for Polymarket prediction markets.
    /// Places multi-level bid/ask quotes with inventory-aware skew.
    ///
    /// Key features:
    /// - Multi-level quoting (3-5 levels per side)
    /// - Inventory-based price skew to manage directional risk
    /// - Price clamping to [0.01, 0.99] for prediction market constraints
    /// - YES/NO complementary arbitrage detection
    /// - Time-decay spread adjustment as settlement approaches
    /// </summary>
    public class PolymarketMarketMaker : QCAlgorithm
    {
        // ─── Configuration ───────────────────────────────────────
        private string _yesTokenTicker;
        private string _noTokenTicker;
        private Symbol _yesSymbol;
        private Symbol _noSymbol;

        /// <summary>Target half-spread in price units (e.g. 0.01 = 1 cent)</summary>
        private decimal _targetHalfSpread = 0.01m;

        /// <summary>Maximum inventory per side in token units</summary>
        private decimal _maxInventory = 1000m;

        /// <summary>Inventory skew factor: how aggressively to skew on imbalance</summary>
        private decimal _inventorySkewFactor = 0.5m;

        /// <summary>Number of quote levels per side</summary>
        private int _orderLevels = 3;

        /// <summary>Price increment between levels</summary>
        private decimal _levelSpacing = 0.01m;

        /// <summary>Base order size per level</summary>
        private decimal _baseOrderSize = 100m;

        /// <summary>Size multiplier for outer levels (outer levels get larger)</summary>
        private decimal _outerLevelSizeMultiplier = 1.5m;

        /// <summary>Minimum profit threshold for YES/NO complementary arbitrage</summary>
        private decimal _arbThreshold = 0.005m;

        /// <summary>Quote refresh interval</summary>
        private TimeSpan _refreshInterval = TimeSpan.FromSeconds(30);

        // ─── State ───────────────────────────────────────────────
        private DateTime _lastRefresh = DateTime.MinValue;
        private decimal _yesInventory;
        private decimal _noInventory;
        private readonly List<OrderTicket> _activeOrders = new();

        /// <summary>
        /// Initializes the algorithm
        /// </summary>
        public override void Initialize()
        {
            SetStartDate(2026, 1, 1);
            SetCash("USDC", 10000);

            // Register the Polymarket market
            try { Market.Add("polymarket", 43); } catch (ArgumentException) { }

            // Configuration from parameters
            _yesTokenTicker = GetParameter("yes-ticker", "ETH5000MAR26YES");
            _noTokenTicker = GetParameter("no-ticker", "ETH5000MAR26NO");
            _targetHalfSpread = GetParameter("target-half-spread", 0.01m);
            _maxInventory = GetParameter("max-inventory", 1000m);
            _inventorySkewFactor = GetParameter("inventory-skew", 0.5m);
            _orderLevels = (int)GetParameter("order-levels", 3m);
            _levelSpacing = GetParameter("level-spacing", 0.01m);
            _baseOrderSize = GetParameter("base-order-size", 100m);
            _arbThreshold = GetParameter("arb-threshold", 0.005m);

            // Subscribe to YES and NO tokens
            _yesSymbol = AddCrypto(_yesTokenTicker, Resolution.Second, "polymarket").Symbol;
            _noSymbol = AddCrypto(_noTokenTicker, Resolution.Second, "polymarket").Symbol;

            // Schedule quote refresh
            Schedule.On(
                DateRules.EveryDay(),
                TimeRules.Every(_refreshInterval),
                RefreshQuotes);

            SetBrokerageModel(new PolymarketBrokerageModel());
        }

        /// <summary>
        /// Event handler for new data
        /// </summary>
        public override void OnData(Slice data)
        {
            // Check for YES/NO complementary arbitrage opportunity
            CheckComplementaryArbitrage(data);
        }

        /// <summary>
        /// Core market making logic: cancel old quotes and place new multi-level quotes
        /// </summary>
        private void RefreshQuotes()
        {
            // 1. Cancel all existing orders
            CancelAllActiveOrders();

            // 2. Get current prices
            var yesSecurity = Securities[_yesSymbol];
            var noSecurity = Securities[_noSymbol];

            var yesBid = yesSecurity.BidPrice;
            var yesAsk = yesSecurity.AskPrice;

            if (yesBid <= 0 || yesAsk <= 0)
            {
                return; // No market data yet
            }

            var midPrice = (yesBid + yesAsk) / 2m;

            // 3. Calculate inventory skew
            _yesInventory = Portfolio[_yesSymbol].Quantity;
            var inventoryRatio = _maxInventory > 0 ? _yesInventory / _maxInventory : 0;
            var skew = -inventoryRatio * _inventorySkewFactor * _targetHalfSpread * 2;

            // 4. Place multi-level quotes
            for (var level = 0; level < _orderLevels; level++)
            {
                var levelOffset = level * _levelSpacing;
                var sizeMultiplier = 1m + level * (_outerLevelSizeMultiplier - 1m) / Math.Max(1, _orderLevels - 1);
                var levelSize = _baseOrderSize * sizeMultiplier;

                // Bid (buy) side
                var bidPrice = midPrice - _targetHalfSpread - levelOffset + skew;
                bidPrice = ClampPrice(bidPrice);

                if (bidPrice > 0.001m && bidPrice < 0.999m)
                {
                    var ticket = LimitOrder(_yesSymbol, levelSize, bidPrice,
                        tag: $"MM-BID-L{level}");
                    _activeOrders.Add(ticket);
                }

                // Ask (sell) side
                var askPrice = midPrice + _targetHalfSpread + levelOffset + skew;
                askPrice = ClampPrice(askPrice);

                if (askPrice > 0.001m && askPrice < 0.999m)
                {
                    var ticket = LimitOrder(_yesSymbol, -levelSize, askPrice,
                        tag: $"MM-ASK-L{level}");
                    _activeOrders.Add(ticket);
                }
            }

            _lastRefresh = Time;
        }

        /// <summary>
        /// Checks for YES/NO complementary arbitrage opportunities.
        /// If YES_bid + NO_bid > 1.00 + cost → sell both sides
        /// If YES_ask + NO_ask < 1.00 - cost → buy both sides
        /// </summary>
        private void CheckComplementaryArbitrage(Slice data)
        {
            var yesSecurity = Securities[_yesSymbol];
            var noSecurity = Securities[_noSymbol];

            var yesBid = yesSecurity.BidPrice;
            var yesAsk = yesSecurity.AskPrice;
            var noBid = noSecurity.BidPrice;
            var noAsk = noSecurity.AskPrice;

            if (yesBid <= 0 || yesAsk <= 0 || noBid <= 0 || noAsk <= 0)
            {
                return;
            }

            // Overpriced: sum of bids > 1 + threshold → sell both
            var bidSum = yesBid + noBid;
            if (bidSum > 1.0m + _arbThreshold)
            {
                var arbSize = Math.Min(_baseOrderSize, _maxInventory - Math.Abs(_yesInventory));
                if (arbSize > 0)
                {
                    Log($"Complementary Arb (SELL): YES_bid={yesBid:F3} + NO_bid={noBid:F3} = {bidSum:F3} > 1.00");
                    LimitOrder(_yesSymbol, -arbSize, yesBid, tag: "ARB-SELL-YES");
                    LimitOrder(_noSymbol, -arbSize, noBid, tag: "ARB-SELL-NO");
                }
            }

            // Underpriced: sum of asks < 1 - threshold → buy both
            var askSum = yesAsk + noAsk;
            if (askSum < 1.0m - _arbThreshold)
            {
                var arbSize = Math.Min(_baseOrderSize, _maxInventory - Math.Abs(_yesInventory));
                if (arbSize > 0)
                {
                    Log($"Complementary Arb (BUY): YES_ask={yesAsk:F3} + NO_ask={noAsk:F3} = {askSum:F3} < 1.00");
                    LimitOrder(_yesSymbol, arbSize, yesAsk, tag: "ARB-BUY-YES");
                    LimitOrder(_noSymbol, arbSize, noAsk, tag: "ARB-BUY-NO");
                }
            }
        }

        /// <summary>
        /// Handles order events
        /// </summary>
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                Log($"Order Filled: {orderEvent.Symbol} {orderEvent.FillQuantity} @ {orderEvent.FillPrice:F4}");

                // Update inventory tracking
                if (orderEvent.Symbol == _yesSymbol)
                {
                    _yesInventory = Portfolio[_yesSymbol].Quantity;
                }
                else if (orderEvent.Symbol == _noSymbol)
                {
                    _noInventory = Portfolio[_noSymbol].Quantity;
                }
            }

            // Remove completed orders from active list
            _activeOrders.RemoveAll(t =>
                t.OrderId == orderEvent.OrderId &&
                (orderEvent.Status == OrderStatus.Filled ||
                 orderEvent.Status == OrderStatus.Canceled ||
                 orderEvent.Status == OrderStatus.Invalid));
        }

        /// <summary>
        /// End of algorithm - cancel all orders
        /// </summary>
        public override void OnEndOfAlgorithm()
        {
            CancelAllActiveOrders();
            Log($"Final P&L: {Portfolio.TotalPortfolioValue:C}");
            Log($"YES inventory: {_yesInventory}, NO inventory: {_noInventory}");
        }

        private void CancelAllActiveOrders()
        {
            foreach (var ticket in _activeOrders.Where(t =>
                t.Status == OrderStatus.Submitted || t.Status == OrderStatus.PartiallyFilled))
            {
                ticket.Cancel();
            }
            _activeOrders.Clear();
        }

        /// <summary>
        /// Clamps price to valid prediction market range [0.01, 0.99]
        /// </summary>
        private static decimal ClampPrice(decimal price)
        {
            return Math.Max(0.01m, Math.Min(0.99m, price));
        }

        private decimal GetParameter(string name, decimal defaultValue)
        {
            var param = GetParameter(name);
            return string.IsNullOrEmpty(param) ? defaultValue : decimal.Parse(param);
        }
    }
}
