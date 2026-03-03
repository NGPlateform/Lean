using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Hubs;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class DryRunEngine : BackgroundService
    {
        private readonly DryRunSettings _settings;
        private IDryRunStrategy _strategy;
        private readonly TradingService _tradingService;
        private readonly MarketDataService _marketDataService;
        private readonly IHubContext<TradingHub> _hubContext;
        private readonly ILogger<DryRunEngine> _logger;

        private decimal _balance;
        private decimal _realizedPnl;
        private int _tickCount;
        private int _orderCounter;

        private readonly ConcurrentDictionary<string, SimulatedPosition> _positions = new();
        private readonly ConcurrentDictionary<string, SimulatedOrder> _openOrders = new();
        private readonly List<SimulatedTrade> _trades = new();
        private readonly List<DryRunLogEntry> _logs = new();
        private readonly List<EquityPoint> _equityCurve = new();
        private readonly object _lock = new();

        public static readonly string[] AvailableStrategies = { "MarketMaking", "MeanReversion", "SpreadCapture", "BtcFollowMM" };

        private int _lastAutoSubscribeTick;
        private const int AutoSubscribeRefreshIntervalTicks = 60;

        public DryRunEngine(
            DryRunSettings settings,
            IDryRunStrategy strategy,
            TradingService tradingService,
            MarketDataService marketDataService,
            IHubContext<TradingHub> hubContext,
            ILogger<DryRunEngine> logger)
        {
            _settings = settings;
            _strategy = strategy;
            _tradingService = tradingService;
            _marketDataService = marketDataService;
            _hubContext = hubContext;
            _logger = logger;
            _balance = settings.InitialBalance;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled) return;

            await Task.Delay(3000, stoppingToken);

            _strategy.Initialize(_settings.StrategyParameters);
            Log("Engine", "Info", $"DryRun engine started. Strategy: {_strategy.Name}, Balance: ${_balance:F2}");
            _logger.LogInformation("DryRun engine started with strategy {Strategy}, balance {Balance}",
                _strategy.Name, _balance);

            // Auto-subscribe to top markets on startup
            if (_settings.AutoSubscribeTopN > 0)
            {
                await AutoSubscribeTopMarkets();
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DryRun tick error");
                    Log("Engine", "Error", $"Tick error: {ex.Message}");
                }

                await Task.Delay(_settings.TickIntervalMs, stoppingToken);
            }
        }

        private async Task Tick()
        {
            _tickCount++;

            // 0. Periodically refresh auto-subscriptions
            if (_settings.AutoSubscribeTopN > 0 &&
                _tickCount - _lastAutoSubscribeTick >= AutoSubscribeRefreshIntervalTicks)
            {
                await AutoSubscribeTopMarkets();
            }

            // 1. Gather real order books
            var orderBooks = GetAllCachedOrderBooks();

            // 2. Match existing open orders against real books
            MatchOrders(orderBooks);

            // 3. Update position current prices from order books
            UpdatePositionPrices(orderBooks);

            // 4. Build strategy context and evaluate
            var context = BuildContext(orderBooks);
            List<StrategyAction> actions;
            try
            {
                actions = _strategy.Evaluate(context);
            }
            catch (Exception ex)
            {
                Log("Strategy", "Error", $"Evaluate error: {ex.Message}");
                actions = new List<StrategyAction>();
            }

            // 5. Execute actions
            foreach (var action in actions)
            {
                ExecuteAction(action);
            }

            // 6. Broadcast state via SignalR
            await BroadcastState();
        }

        private readonly ConcurrentDictionary<string, DateTime> _staleWarningTimes = new();

        private Dictionary<string, PolymarketOrderBook> GetAllCachedOrderBooks()
        {
            var books = new Dictionary<string, PolymarketOrderBook>();
            var staleThreshold = TimeSpan.FromSeconds(_settings.OrderBookStaleThresholdSeconds);
            _tradingService.EnsureMarketsLoaded();
            var markets = _tradingService.GetMarkets();
            foreach (var market in markets)
            {
                foreach (var token in market.Tokens)
                {
                    if (string.IsNullOrEmpty(token.TokenId)) continue;
                    var book = _marketDataService.GetCachedOrderBook(token.TokenId);
                    if (book == null) continue;

                    var lastUpdated = _marketDataService.GetOrderBookLastUpdated(token.TokenId);
                    if (lastUpdated.HasValue && DateTime.UtcNow - lastUpdated.Value > staleThreshold)
                    {
                        // Throttle warnings to once per ~60s per token
                        var now = DateTime.UtcNow;
                        if (!_staleWarningTimes.TryGetValue(token.TokenId, out var lastWarning) ||
                            now - lastWarning > TimeSpan.FromSeconds(60))
                        {
                            _staleWarningTimes[token.TokenId] = now;
                            var age = (int)(now - lastUpdated.Value).TotalSeconds;
                            _logger.LogWarning("Skipping stale order book for {TokenId} (age: {Age}s, threshold: {Threshold}s)",
                                ShortId(token.TokenId), age, _settings.OrderBookStaleThresholdSeconds);
                        }
                        continue;
                    }

                    books[token.TokenId] = book;
                }
            }
            return books;
        }

        private static readonly Random _random = new();

        private void MatchOrders(Dictionary<string, PolymarketOrderBook> orderBooks)
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

                // Aggressive (taker) fill: order crosses the spread
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

                // Passive (maker) fill: limit order resting in/near the spread
                // Simulates incoming market orders hitting our resting limit
                if (!shouldFill && bestBid.HasValue && bestAsk.HasValue)
                {
                    var mid = (bestBid.Value + bestAsk.Value) / 2m;
                    var spread = bestAsk.Value - bestBid.Value;
                    var maxDistance = Math.Max(spread * 5m, 0.03m); // consider orders within 5x spread or 3 cents

                    if (order.Side == "BUY" && order.Price >= bestBid.Value * 0.95m)
                    {
                        // How close is our bid to the best ask? Closer = higher fill probability
                        var distance = bestAsk.Value - order.Price;
                        if (distance > 0 && distance < maxDistance)
                        {
                            var fillProb = (double)(1m - distance / maxDistance) * 0.12; // up to 12% per tick
                            if (_random.NextDouble() < fillProb)
                            {
                                shouldFill = true;
                                fillPrice = order.Price; // maker fills at own price
                            }
                        }
                    }
                    else if (order.Side == "SELL" && order.Price <= bestAsk.Value * 1.05m)
                    {
                        var distance = order.Price - bestBid.Value;
                        if (distance > 0 && distance < maxDistance)
                        {
                            var fillProb = (double)(1m - distance / maxDistance) * 0.12;
                            if (_random.NextDouble() < fillProb)
                            {
                                shouldFill = true;
                                fillPrice = order.Price;
                            }
                        }
                    }
                }

                if (!shouldFill) continue;

                // Calculate fill size with randomness
                var availableDepth = GetAvailableDepth(book, order.Side, order.Price);
                var depthFraction = 0.3 + _random.NextDouble() * 0.7;
                var maxFillFromDepth = availableDepth * (decimal)depthFraction;
                var fillSize = Math.Min(order.RemainingSize, Math.Max(1, maxFillFromDepth));
                if (fillSize <= 0) continue;

                ExecuteFill(order, fillPrice, fillSize);

                if (order.RemainingSize <= 0)
                {
                    order.Status = "MATCHED";
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
                _openOrders.TryRemove(id, out _);
        }

        private void ExecuteFill(SimulatedOrder order, decimal fillPrice, decimal fillSize)
        {
            lock (_lock)
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
                    MatchTime = DateTime.UtcNow
                };
                _trades.Add(trade);

                // Update position
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

                Log("Engine", "Info",
                    $"FILL: {order.Side} {fillSize:F2} of {ShortId(order.TokenId)} @ {fillPrice:F4}" +
                    $" | Balance: ${_balance:F2}");

                try { _strategy.OnFill(trade); }
                catch (Exception ex) { _logger.LogDebug(ex, "Strategy.OnFill error"); }
            }
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

        private StrategyContext BuildContext(Dictionary<string, PolymarketOrderBook> orderBooks)
        {
            var unrealizedPnl = _positions.Values.Where(p => p.Size > 0).Sum(p => p.UnrealizedPnl);

            return new StrategyContext
            {
                CurrentTime = DateTime.UtcNow,
                Markets = _tradingService.GetMarkets(),
                OrderBooks = orderBooks,
                Balance = _balance,
                Positions = new Dictionary<string, SimulatedPosition>(_positions),
                OpenOrders = _openOrders.Values.ToList(),
                RecentTrades = _trades.TakeLast(50).ToList(),
                RealizedPnl = _realizedPnl,
                UnrealizedPnl = unrealizedPnl
            };
        }

        private void ExecuteAction(StrategyAction action)
        {
            switch (action)
            {
                case PlaceOrderAction place:
                    PlaceOrderInternal(place.TokenId, place.Price, place.Size, place.Side, "Strategy", place.Reason);
                    break;
                case CancelOrderAction cancel:
                    CancelOrderInternal(cancel.OrderId, cancel.Reason);
                    break;
            }
        }

        private SimulatedOrder PlaceOrderInternal(string tokenId, decimal price, decimal size, string side, string source, string reason = null)
        {
            lock (_lock)
            {
                var orderId = $"DRY-{Interlocked.Increment(ref _orderCounter):D6}";
                var order = new SimulatedOrder
                {
                    Id = orderId,
                    TokenId = tokenId,
                    Side = side.ToUpper(),
                    Price = price,
                    OriginalSize = size,
                    FilledSize = 0,
                    Status = "LIVE",
                    CreatedAt = DateTime.UtcNow,
                    Source = source
                };
                _openOrders[orderId] = order;

                var msg = $"ORDER: {side} {size:F2} of {ShortId(tokenId)} @ {price:F4}";
                if (!string.IsNullOrEmpty(reason)) msg += $" | {reason}";
                Log(source, "Info", msg);

                return order;
            }
        }

        private bool CancelOrderInternal(string orderId, string reason = null)
        {
            if (_openOrders.TryRemove(orderId, out var order))
            {
                order.Status = "CANCELLED";
                var msg = $"CANCEL: {orderId}";
                if (!string.IsNullOrEmpty(reason)) msg += $" | {reason}";
                Log("Engine", "Info", msg);
                return true;
            }
            return false;
        }

        private async Task BroadcastState()
        {
            var unrealizedPnl = _positions.Values.Where(p => p.Size > 0).Sum(p => p.UnrealizedPnl);
            var totalEquity = _balance + unrealizedPnl;

            // Track equity curve
            lock (_lock)
            {
                _equityCurve.Add(new EquityPoint { Time = DateTime.UtcNow, Equity = totalEquity });
                if (_equityCurve.Count > 2000)
                    _equityCurve.RemoveRange(0, _equityCurve.Count - 2000);
            }

            await _hubContext.Clients.All.SendAsync("DryRunUpdate", new
            {
                balance = _balance,
                realizedPnl = _realizedPnl,
                unrealizedPnl,
                totalEquity,
                tickCount = _tickCount,
                openOrders = _openOrders.Count,
                positions = _positions.Values.Count(p => p.Size > 0),
                trades = _trades.Count
            });
        }

        private void Log(string source, string level, string message)
        {
            var entry = new DryRunLogEntry
            {
                Timestamp = DateTime.UtcNow,
                Source = source,
                Level = level,
                Message = message
            };

            lock (_lock)
            {
                _logs.Add(entry);
                if (_logs.Count > 1000)
                    _logs.RemoveRange(0, _logs.Count - 1000);
            }

            _ = _hubContext.Clients.All.SendAsync("DryRunLog", entry);
        }

        // === Public API (returns Polymarket model types for frontend compatibility) ===

        public List<PolymarketPosition> GetPositions()
        {
            return _positions.Values
                .Where(p => p.Size > 0)
                .Select(p => new PolymarketPosition
                {
                    AssetId = p.TokenId,
                    Size = p.Size.ToString("F4"),
                    AvgPrice = p.AvgPrice.ToString("F4"),
                    CurPrice = p.CurrentPrice.ToString("F4"),
                    RealizedPnl = p.RealizedPnl.ToString("F4"),
                    UnrealizedPnl = p.UnrealizedPnl.ToString("F4")
                }).ToList();
        }

        public PolymarketBalance GetBalance()
        {
            return new PolymarketBalance
            {
                Balance = _balance.ToString("F2"),
                Allowance = _balance.ToString("F2")
            };
        }

        public List<PolymarketOrder> GetOpenOrders()
        {
            return _openOrders.Values.Select(o => new PolymarketOrder
            {
                Id = o.Id,
                AssetId = o.TokenId,
                Side = o.Side,
                Price = o.Price.ToString("F4"),
                OriginalSize = o.OriginalSize.ToString("F4"),
                SizeMatched = o.FilledSize.ToString("F4"),
                Status = o.Status,
                CreatedAt = o.CreatedAt,
                Type = "GTC"
            }).ToList();
        }

        public List<PolymarketTrade> GetTrades(int limit = 50)
        {
            lock (_lock)
            {
                return _trades.AsEnumerable().Reverse().Take(limit).Select(t => new PolymarketTrade
                {
                    Id = t.Id,
                    TakerOrderId = t.OrderId,
                    AssetId = t.TokenId,
                    Side = t.Side,
                    Price = t.Price.ToString("F4"),
                    Size = t.Size.ToString("F4"),
                    Status = "MATCHED",
                    MatchTime = t.MatchTime
                }).ToList();
            }
        }

        public PolymarketOrderResponse PlaceOrder(string tokenId, decimal price, decimal size, string side)
        {
            var order = PlaceOrderInternal(tokenId, price, size, side, "Manual");
            return new PolymarketOrderResponse
            {
                Success = true,
                OrderId = order.Id,
                Status = "LIVE"
            };
        }

        public PolymarketCancelResponse CancelOrder(string orderId)
        {
            var canceled = CancelOrderInternal(orderId, "Manual cancel");
            return new PolymarketCancelResponse
            {
                Canceled = canceled,
                OrderId = orderId
            };
        }

        public List<DryRunLogEntry> GetLogs(int limit = 200)
        {
            lock (_lock)
            {
                return _logs.AsEnumerable().Reverse().Take(limit).ToList();
            }
        }

        public string StrategyName => _strategy.Name;

        public List<EquityPoint> GetEquityCurve()
        {
            lock (_lock)
            {
                return new List<EquityPoint>(_equityCurve);
            }
        }

        public void SwitchStrategy(IDryRunStrategy newStrategy, bool resetState)
        {
            lock (_lock)
            {
                if (resetState)
                {
                    _balance = _settings.InitialBalance;
                    _realizedPnl = 0;
                    _positions.Clear();
                    _openOrders.Clear();
                    _trades.Clear();
                    _equityCurve.Clear();
                    _tickCount = 0;
                    _orderCounter = 0;
                }

                _strategy = newStrategy;
                _strategy.Initialize(_settings.StrategyParameters);
                Log("Engine", "Info", $"Strategy switched to {newStrategy.Name}" + (resetState ? " (state reset)" : ""));
            }
        }

        public Dictionary<string, string> GetStrategyParameters()
        {
            return _strategy.GetParameters();
        }

        public void UpdateStrategyParameters(Dictionary<string, string> parameters)
        {
            lock (_lock)
            {
                var current = _strategy.GetParameters();
                foreach (var kvp in parameters)
                {
                    current[kvp.Key] = kvp.Value;
                }
                _strategy.Initialize(current);
                Log("Engine", "Info", $"Strategy parameters updated: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
            }
        }

        public List<MarketScore> GetMarketScores()
        {
            var orderBooks = GetAllCachedOrderBooks();
            var context = BuildContext(orderBooks);
            return _strategy.GetMarketScores(context);
        }

        // === Auto-Subscribe ===

        private async Task AutoSubscribeTopMarkets()
        {
            try
            {
                _tradingService.EnsureMarketsLoaded();
                var markets = _tradingService.GetMarkets();

                // Sort by 24h volume descending, take top N
                var topMarkets = markets
                    .Where(m => m.Active && !m.Closed && !m.Resolved)
                    .OrderByDescending(m => m.Volume24h)
                    .Take(_settings.AutoSubscribeTopN)
                    .ToList();

                var tokenIds = topMarkets
                    .SelectMany(m => m.Tokens)
                    .Where(t => !string.IsNullOrEmpty(t.TokenId))
                    .Select(t => t.TokenId)
                    .Distinct()
                    .ToList();

                if (tokenIds.Count > 0)
                {
                    await _marketDataService.SubscribeAsync(tokenIds);

                    // Pre-fetch order books via REST to prime the cache
                    // (WebSocket cache only populates on incoming changes)
                    var seeded = 0;
                    foreach (var tokenId in tokenIds)
                    {
                        if (_marketDataService.GetCachedOrderBook(tokenId) != null) continue;
                        try
                        {
                            var book = _tradingService.GetOrderBook(tokenId);
                            if (book != null)
                            {
                                _marketDataService.SeedOrderBook(tokenId, book);
                                seeded++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to seed order book for {TokenId}", ShortId(tokenId));
                        }
                    }

                    Log("Engine", "Info", $"Auto-subscribed to {tokenIds.Count} tokens from top {topMarkets.Count} markets (seeded {seeded} order books)");
                    _logger.LogInformation("Auto-subscribed to {Count} tokens from top {Markets} markets, seeded {Seeded} books",
                        tokenIds.Count, topMarkets.Count, seeded);
                }

                _lastAutoSubscribeTick = _tickCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-subscribe to top markets");
                Log("Engine", "Warning", $"Auto-subscribe failed: {ex.Message}");
            }
        }

        // === Helpers ===

        private static decimal? GetBestPrice(List<PolymarketOrderBookLevel> levels, bool ascending)
        {
            if (levels == null || levels.Count == 0) return null;
            decimal? best = null;
            foreach (var level in levels)
            {
                if (!decimal.TryParse(level.Price, out var p)) continue;
                if (!decimal.TryParse(level.Size, out var s) || s <= 0) continue;
                if (!best.HasValue)
                    best = p;
                else if (ascending && p < best.Value)
                    best = p;
                else if (!ascending && p > best.Value)
                    best = p;
            }
            return best;
        }

        private static decimal GetAvailableDepth(PolymarketOrderBook book, string orderSide, decimal orderPrice)
        {
            var levels = orderSide == "BUY" ? book.Asks : book.Bids;
            if (levels == null) return 0;

            decimal total = 0;
            foreach (var level in levels)
            {
                if (!decimal.TryParse(level.Price, out var p)) continue;
                if (!decimal.TryParse(level.Size, out var s)) continue;

                if (orderSide == "BUY" && p <= orderPrice)
                    total += s;
                else if (orderSide == "SELL" && p >= orderPrice)
                    total += s;
            }
            return total;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 12) return id ?? "";
            return id.Substring(0, 6) + "..." + id.Substring(id.Length - 4);
        }
    }

    public class EquityPoint
    {
        public DateTime Time { get; set; }
        public decimal Equity { get; set; }
    }
}
