using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using QuantConnect.Brokerages.Polymarket.Api.Models;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services.Backtest
{
    /// <summary>
    /// Single-strategy historical replay engine with deterministic fill model.
    /// </summary>
    public class BacktestEngine
    {
        private readonly IDryRunStrategy _strategy;
        private readonly BtcPriceService _btcPriceService;
        private readonly decimal _initialBalance;
        private const int OrderAgingMaxTicks = 6; // 60 minutes at 10-min intervals

        // State
        private decimal _balance;
        private decimal _realizedPnl;
        private int _orderCounter;
        private int _orderCount;
        private int _currentTick;
        private readonly Dictionary<string, SimulatedPosition> _positions = new();
        private readonly Dictionary<string, SimulatedOrder> _openOrders = new();
        private readonly Dictionary<string, int> _orderCreatedTick = new();
        private readonly List<SimulatedTrade> _trades = new();
        private readonly List<(DateTime Time, decimal Equity)> _equityCurve = new();

        public BacktestEngine(IDryRunStrategy strategy, decimal initialBalance = 10000m,
            BtcPriceService btcPriceService = null)
        {
            _strategy = strategy;
            _initialBalance = initialBalance;
            _balance = initialBalance;
            _btcPriceService = btcPriceService;
        }

        /// <summary>
        /// Runs the strategy over a prebuilt timeline.
        /// </summary>
        public BacktestResult Run(
            List<(DateTime Time, Dictionary<string, HistoricalBar> TokenBars)> timeline,
            List<DashboardMarket> markets,
            List<HistoricalBar> btcBars = null)
        {
            var sw = Stopwatch.StartNew();

            // Index BTC bars by time for quick lookup
            var btcByTime = btcBars?.ToDictionary(b => RoundTo10Min(b.Time), b => b)
                ?? new Dictionary<DateTime, HistoricalBar>();

            var allTokenIds = new HashSet<string>();
            int ticksProcessed = 0;

            foreach (var tick in timeline)
            {
                ticksProcessed++;
                _currentTick = ticksProcessed;

                // 1. Build order books from bars
                var orderBooks = new Dictionary<string, PolymarketOrderBook>();
                foreach (var kvp in tick.TokenBars)
                {
                    allTokenIds.Add(kvp.Key);
                    orderBooks[kvp.Key] = HistoricalDataLoader.SynthesizeOrderBook(kvp.Value, kvp.Key);
                }

                // 2. Inject BTC price if available
                var tickTime = RoundTo10Min(tick.Time);
                if (_btcPriceService != null && btcByTime.TryGetValue(tickTime, out var btcBar))
                {
                    _btcPriceService.InjectSample(btcBar.Close, tick.Time);
                }

                // 3. Match existing orders (deterministic model)
                MatchOrders(orderBooks, tick.TokenBars);

                // 4. Age out old orders
                AgeOrders(ticksProcessed);

                // 5. Update position prices
                UpdatePositionPrices(orderBooks);

                // 6. Update market token prices from bars
                UpdateMarketPrices(markets, tick.TokenBars);

                // 7. Build context and evaluate strategy
                var context = BuildContext(tick.Time, markets, orderBooks);
                List<StrategyAction> actions;
                try
                {
                    actions = _strategy.Evaluate(context);
                }
                catch
                {
                    actions = new List<StrategyAction>();
                }

                // 8. Execute actions
                foreach (var action in actions)
                {
                    ExecuteAction(action, tick.Time);
                }

                // 9. Record equity point
                var unrealizedPnl = _positions.Values
                    .Where(p => p.Size > 0)
                    .Sum(p => p.UnrealizedPnl);
                _equityCurve.Add((tick.Time, _balance + unrealizedPnl));
            }

            sw.Stop();

            return new BacktestResult
            {
                StrategyName = _strategy.Name,
                Trades = new List<SimulatedTrade>(_trades),
                EquityCurve = new List<(DateTime, decimal)>(_equityCurve),
                InitialBalance = _initialBalance,
                FinalBalance = _balance + _positions.Values.Where(p => p.Size > 0).Sum(p => p.UnrealizedPnl),
                TicksProcessed = ticksProcessed,
                TokensProcessed = allTokenIds.Count,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Metrics = BacktestMetrics.Calculate(_equityCurve, _trades, _initialBalance, _orderCount)
            };
        }

        /// <summary>
        /// Deterministic order matching model.
        /// Taker: BUY price >= bestAsk fills at bestAsk; SELL price <= bestBid fills at bestBid.
        /// Maker: BUY limit near bestBid, if bar.Low <= order.Price fills at order.Price; SELL vice versa.
        /// Volume limit: min(remainingSize, depth * 0.1)
        /// </summary>
        private void MatchOrders(Dictionary<string, PolymarketOrderBook> orderBooks,
            Dictionary<string, HistoricalBar> tokenBars)
        {
            var toRemove = new List<string>();

            foreach (var kvp in _openOrders)
            {
                var order = kvp.Value;
                if (!orderBooks.TryGetValue(order.TokenId, out var book)) continue;

                var bestAsk = GetBestPrice(book.Asks, ascending: true);
                var bestBid = GetBestPrice(book.Bids, ascending: false);

                bool shouldFill = false;
                decimal fillPrice = order.Price;

                // Taker fill
                if (order.Side == "BUY" && bestAsk.HasValue && order.Price >= bestAsk.Value)
                {
                    shouldFill = true;
                    fillPrice = bestAsk.Value;
                }
                else if (order.Side == "SELL" && bestBid.HasValue && order.Price <= bestBid.Value)
                {
                    shouldFill = true;
                    fillPrice = bestBid.Value;
                }

                // Maker fill: check if bar's price range would hit our limit order
                if (!shouldFill && tokenBars.TryGetValue(order.TokenId, out var bar))
                {
                    if (order.Side == "BUY" && bar.Low <= order.Price)
                    {
                        shouldFill = true;
                        fillPrice = order.Price;
                    }
                    else if (order.Side == "SELL" && bar.High >= order.Price)
                    {
                        shouldFill = true;
                        fillPrice = order.Price;
                    }
                }

                if (!shouldFill) continue;

                // Volume limit: max fill = depth * 0.1
                var depth = GetAvailableDepth(book, order.Side);
                var maxFill = Math.Max(1m, depth * 0.1m);
                var fillSize = Math.Min(order.RemainingSize, maxFill);
                if (fillSize <= 0) continue;

                ExecuteFill(order, fillPrice, fillSize);

                if (order.RemainingSize <= 0)
                {
                    order.Status = "MATCHED";
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                _openOrders.Remove(id);
                _orderCreatedTick.Remove(id);
            }
        }

        private void AgeOrders(int currentTick)
        {
            var toRemove = new List<string>();
            foreach (var kvp in _openOrders)
            {
                if (_orderCreatedTick.TryGetValue(kvp.Key, out var createdTick) &&
                    currentTick - createdTick > OrderAgingMaxTicks)
                {
                    kvp.Value.Status = "CANCELLED";
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (var id in toRemove)
            {
                _openOrders.Remove(id);
                _orderCreatedTick.Remove(id);
            }
        }

        private void ExecuteFill(SimulatedOrder order, decimal fillPrice, decimal fillSize)
        {
            order.FilledSize += fillSize;

            var trade = new SimulatedTrade
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 16),
                OrderId = order.Id,
                TokenId = order.TokenId,
                Side = order.Side,
                Price = fillPrice,
                Size = fillSize,
                MatchTime = order.CreatedAt
            };
            _trades.Add(trade);

            // Update position (same logic as DryRunEngine)
            if (!_positions.TryGetValue(order.TokenId, out var pos))
            {
                pos = new SimulatedPosition { TokenId = order.TokenId };
                _positions[order.TokenId] = pos;
            }

            if (order.Side == "BUY")
            {
                var cost = fillPrice * fillSize;
                _balance -= cost;
                var newTotalCost = pos.TotalCost + cost;
                var newSize = pos.Size + fillSize;
                pos.AvgPrice = newSize > 0 ? newTotalCost / newSize : 0;
                pos.Size = newSize;
                pos.TotalCost = newTotalCost;
            }
            else // SELL
            {
                var proceeds = fillPrice * fillSize;
                _balance += proceeds;
                var pnl = (fillPrice - pos.AvgPrice) * fillSize;
                pos.RealizedPnl += pnl;
                _realizedPnl += pnl;
                pos.Size -= fillSize;
                pos.TotalCost = pos.Size > 0 ? pos.AvgPrice * pos.Size : 0;

                if (pos.Size <= 0)
                {
                    pos.Size = 0;
                    pos.AvgPrice = 0;
                    pos.TotalCost = 0;
                }
            }

            try { _strategy.OnFill(trade); }
            catch { /* ignore */ }
        }

        private void UpdatePositionPrices(Dictionary<string, PolymarketOrderBook> orderBooks)
        {
            foreach (var kvp in _positions)
            {
                if (kvp.Value.Size <= 0) continue;
                if (!orderBooks.TryGetValue(kvp.Key, out var book)) continue;

                var bestBid = GetBestPrice(book.Bids, ascending: false);
                var bestAsk = GetBestPrice(book.Asks, ascending: true);
                if (bestBid.HasValue && bestAsk.HasValue)
                    kvp.Value.CurrentPrice = (bestBid.Value + bestAsk.Value) / 2;
                else if (bestBid.HasValue)
                    kvp.Value.CurrentPrice = bestBid.Value;
                else if (bestAsk.HasValue)
                    kvp.Value.CurrentPrice = bestAsk.Value;
            }
        }

        private static void UpdateMarketPrices(List<DashboardMarket> markets, Dictionary<string, HistoricalBar> tokenBars)
        {
            if (markets == null) return;
            foreach (var market in markets)
            {
                if (market.Tokens == null) continue;
                foreach (var token in market.Tokens)
                {
                    if (tokenBars.TryGetValue(token.TokenId, out var bar))
                        token.Price = bar.Close;
                }
            }
        }

        private StrategyContext BuildContext(DateTime time, List<DashboardMarket> markets,
            Dictionary<string, PolymarketOrderBook> orderBooks)
        {
            var unrealizedPnl = _positions.Values.Where(p => p.Size > 0).Sum(p => p.UnrealizedPnl);

            return new StrategyContext
            {
                CurrentTime = time,
                Markets = markets,
                OrderBooks = orderBooks,
                Balance = _balance,
                Positions = new Dictionary<string, SimulatedPosition>(_positions),
                OpenOrders = _openOrders.Values.ToList(),
                RecentTrades = _trades.TakeLast(50).ToList(),
                RealizedPnl = _realizedPnl,
                UnrealizedPnl = unrealizedPnl
            };
        }

        private void ExecuteAction(StrategyAction action, DateTime time)
        {
            switch (action)
            {
                case PlaceOrderAction place:
                    _orderCount++;
                    var orderId = $"BT-{++_orderCounter:D6}";
                    var order = new SimulatedOrder
                    {
                        Id = orderId,
                        TokenId = place.TokenId,
                        Side = place.Side.ToUpper(),
                        Price = place.Price,
                        OriginalSize = place.Size,
                        FilledSize = 0,
                        Status = "LIVE",
                        CreatedAt = time,
                        Source = "Backtest"
                    };
                    _openOrders[orderId] = order;
                    _orderCreatedTick[orderId] = _currentTick;
                    break;

                case CancelOrderAction cancel:
                    if (_openOrders.TryGetValue(cancel.OrderId, out var toCancel))
                    {
                        toCancel.Status = "CANCELLED";
                        _openOrders.Remove(cancel.OrderId);
                        _orderCreatedTick.Remove(cancel.OrderId);
                    }
                    break;
            }
        }

        private static decimal? GetBestPrice(List<PolymarketOrderBookLevel> levels, bool ascending)
        {
            if (levels == null || levels.Count == 0) return null;
            decimal? best = null;
            foreach (var level in levels)
            {
                if (!decimal.TryParse(level.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) continue;
                if (!decimal.TryParse(level.Size, NumberStyles.Any, CultureInfo.InvariantCulture, out var s) || s <= 0) continue;
                if (!best.HasValue)
                    best = p;
                else if (ascending && p < best.Value)
                    best = p;
                else if (!ascending && p > best.Value)
                    best = p;
            }
            return best;
        }

        private static decimal GetAvailableDepth(PolymarketOrderBook book, string orderSide)
        {
            var levels = orderSide == "BUY" ? book.Asks : book.Bids;
            if (levels == null) return 0;
            decimal total = 0;
            foreach (var level in levels)
            {
                if (decimal.TryParse(level.Size, NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                    total += s;
            }
            return total;
        }

        private static DateTime RoundTo10Min(DateTime dt)
        {
            var ticks = dt.Ticks;
            var interval = TimeSpan.FromMinutes(10).Ticks;
            return new DateTime(ticks - ticks % interval, dt.Kind);
        }
    }
}
