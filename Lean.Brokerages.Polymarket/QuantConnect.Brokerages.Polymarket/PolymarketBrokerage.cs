/*
 * QuantConnect - Polymarket Brokerage Integration
 *
 * Licensed under the Apache License, Version 2.0
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Polymarket
{
    /// <summary>
    /// Polymarket prediction market brokerage implementation.
    /// Connects to the Polymarket CLOB (Central Limit Order Book) via REST and WebSocket APIs.
    /// Supports real-time market data and order management for binary outcome tokens.
    /// </summary>
    public class PolymarketBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
    {
        private readonly PolymarketApiClient _apiClient;
        private readonly PolymarketWebSocketClient _wsClient;
        private readonly PolymarketSymbolMapper _symbolMapper;
        private readonly PolymarketCredentials _credentials;
        private readonly ConcurrentDictionary<string, string> _orderIdMap = new();
        private readonly ConcurrentDictionary<Symbol, DefaultOrderBook> _orderBooks = new();
        private readonly ConcurrentQueue<Tick> _ticks = new();
        private readonly IAlgorithm _algorithm;
        private bool _isConnected;

        /// <summary>
        /// Returns true if the brokerage is connected
        /// </summary>
        public override bool IsConnected => _isConnected;

        /// <summary>
        /// Creates a new instance of the <see cref="PolymarketBrokerage"/>
        /// </summary>
        public PolymarketBrokerage(
            string apiKey,
            string apiSecret,
            string privateKey,
            string passphrase,
            IAlgorithm algorithm,
            string baseApiUrl = "https://clob.polymarket.com")
            : base("Polymarket")
        {
            _algorithm = algorithm;
            _credentials = new PolymarketCredentials(apiKey, apiSecret, privateKey, passphrase);
            _apiClient = new PolymarketApiClient(_credentials, baseApiUrl);
            _wsClient = new PolymarketWebSocketClient(_credentials);
            _symbolMapper = new PolymarketSymbolMapper(baseApiUrl);

            // Register the polymarket market
            RegisterMarket();

            // Initialize the base brokerage with WebSocket
            var webSocket = new WebSocketClientWrapper();
            Initialize(
                PolymarketWebSocketClient.MarketWssUrl,
                webSocket,
                new HttpClient(),
                apiKey,
                apiSecret);

            // Sync order state after WebSocket reconnects
            webSocket.Open += (_, _) => OnWebSocketReconnected();

            var subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            subscriptionManager.SubscribeImpl += (symbols, _) => Subscribe(symbols);
            subscriptionManager.UnsubscribeImpl += (symbols, _) => Unsubscribe(symbols);
            SubscriptionManager = subscriptionManager;
        }

        /// <summary>
        /// Parameterless constructor for factory creation
        /// </summary>
        public PolymarketBrokerage() : base("Polymarket")
        {
            RegisterMarket();
        }

        private static void RegisterMarket()
        {
            try
            {
                Market.Add("polymarket", 43);
            }
            catch (ArgumentException)
            {
                // Already registered
            }
        }

        #region IBrokerage Implementation

        /// <summary>
        /// Places an order on Polymarket
        /// </summary>
        public override bool PlaceOrder(Order order)
        {
            try
            {
                var tokenId = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                var price = order.Type == OrderType.Limit ? ((LimitOrder)order).LimitPrice : GetMarketPrice(order.Symbol, order.Direction);
                var size = Math.Abs(order.Quantity);

                Log.Trace($"PolymarketBrokerage.PlaceOrder(): {order.Direction} {size} @ {price} for {order.Symbol}");

                var response = _apiClient.PlaceOrder(tokenId, price, size, order.Direction);

                if (response?.Success == true)
                {
                    _orderIdMap[response.OrderId] = order.Id.ToStringInvariant();
                    CachedOrderIDs[order.Id] = order;

                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                    {
                        Status = OrderStatus.Submitted
                    });

                    return true;
                }

                var errorMessage = response?.ErrorMsg ?? "Unknown error";
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, errorMessage)
                {
                    Status = OrderStatus.Invalid
                });

                Log.Error($"PolymarketBrokerage.PlaceOrder(): Failed - {errorMessage}");
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "PolymarketBrokerage.PlaceOrder()");
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, e.Message)
                {
                    Status = OrderStatus.Invalid
                });
                return false;
            }
        }

        /// <summary>
        /// Polymarket does not support order updates. Returns false.
        /// </summary>
        public override bool UpdateOrder(Order order)
        {
            Log.Error("PolymarketBrokerage.UpdateOrder(): Order updates are not supported. Cancel and resubmit.");
            return false;
        }

        /// <summary>
        /// Cancels an order on Polymarket
        /// </summary>
        public override bool CancelOrder(Order order)
        {
            try
            {
                var brokerageOrderId = _orderIdMap.FirstOrDefault(x => x.Value == order.Id.ToStringInvariant()).Key;
                if (string.IsNullOrEmpty(brokerageOrderId))
                {
                    Log.Error($"PolymarketBrokerage.CancelOrder(): Cannot find brokerage order ID for order {order.Id}");
                    return false;
                }

                var response = _apiClient.CancelOrder(brokerageOrderId);

                if (response?.Canceled == true)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                    {
                        Status = OrderStatus.Canceled
                    });
                    return true;
                }

                Log.Error($"PolymarketBrokerage.CancelOrder(): Failed to cancel order {order.Id}");
                return false;
            }
            catch (Exception e)
            {
                Log.Error(e, "PolymarketBrokerage.CancelOrder()");
                return false;
            }
        }

        /// <summary>
        /// Connects to the Polymarket WebSocket
        /// </summary>
        public override void Connect()
        {
            base.Connect();
            _isConnected = true;
            Log.Trace("PolymarketBrokerage.Connect(): Connected to Polymarket");
        }

        /// <summary>
        /// Disconnects from Polymarket
        /// </summary>
        public override void Disconnect()
        {
            if (WebSocket?.IsOpen == true)
            {
                WebSocket.Close();
            }
            _isConnected = false;
            Log.Trace("PolymarketBrokerage.Disconnect(): Disconnected from Polymarket");
        }

        /// <summary>
        /// Gets all open orders
        /// </summary>
        public override List<Order> GetOpenOrders()
        {
            var polyOrders = _apiClient.GetOpenOrders();
            return polyOrders.Select(ConvertToLeanOrder).Where(o => o != null).ToList();
        }

        /// <summary>
        /// Gets all current holdings
        /// </summary>
        public override List<Holding> GetAccountHoldings()
        {
            var positions = _apiClient.GetPositions();
            return positions.Select(ConvertToHolding).Where(h => h != null).ToList();
        }

        /// <summary>
        /// Gets the current cash balance (USDC)
        /// </summary>
        public override List<CashAmount> GetCashBalance()
        {
            var balance = _apiClient.GetBalance();
            var amount = decimal.TryParse(balance?.Balance, NumberStyles.Any, CultureInfo.InvariantCulture, out var bal)
                ? bal
                : 0m;

            return new List<CashAmount>
            {
                new CashAmount(amount, "USDC")
            };
        }

        /// <summary>
        /// Gets historical data for the specified request
        /// </summary>
        public override IEnumerable<BaseData> GetHistory(QuantConnect.Data.HistoryRequest request)
        {
            // Polymarket history can be retrieved from the CLOB API
            // For now, returns empty; override with PolymarketDataDownloader for backtest data
            return Enumerable.Empty<BaseData>();
        }

        #endregion

        #region WebSocket Handling

        /// <summary>
        /// Called when the WebSocket reconnects. Syncs order state with the exchange
        /// and clears stale order book data so fresh snapshots are received.
        /// </summary>
        private void OnWebSocketReconnected()
        {
            try
            {
                // Clear order books to force fresh snapshots from WebSocket
                _orderBooks.Clear();

                if (_orderIdMap.IsEmpty) return;

                // Check which orders still exist on the exchange
                var liveOrders = _apiClient.GetOpenOrders();
                var liveIds = new HashSet<string>(liveOrders.Select(o => o.Id));

                foreach (var kvp in _orderIdMap)
                {
                    if (liveIds.Contains(kvp.Key)) continue;

                    // Order no longer exists on exchange — emit canceled
                    if (int.TryParse(kvp.Value, out var leanOrderId) &&
                        CachedOrderIDs.TryGetValue(leanOrderId, out var order))
                    {
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                        {
                            Status = OrderStatus.Canceled
                        });
                        Log.Trace($"PolymarketBrokerage.OnWebSocketReconnected(): Order {kvp.Key} no longer live, emitting Canceled");
                    }

                    _orderIdMap.TryRemove(kvp.Key, out _);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "PolymarketBrokerage.OnWebSocketReconnected()");
            }
        }

        /// <summary>
        /// Handles incoming WebSocket messages
        /// </summary>
        protected override void OnMessage(object sender, WebSocketMessage e)
        {
            try
            {
                var raw = e.Data as WebSocketClientWrapper.TextMessage;
                if (raw == null) return;

                var message = PolymarketWebSocketClient.ParseMessage(raw.Message);
                if (message == null) return;

                switch (message.EventType)
                {
                    case PolymarketWsMessageType.BookSnapshot:
                    case PolymarketWsMessageType.PriceChange:
                        HandleOrderBookUpdate(message);
                        break;

                    case PolymarketWsMessageType.TradeUpdate:
                        HandleTradeUpdate(message);
                        break;

                    case PolymarketWsMessageType.OrderUpdate:
                        HandleOrderUpdate(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PolymarketBrokerage.OnMessage()");
            }
        }

        /// <summary>
        /// Subscribes to market data for the given symbols
        /// </summary>
        protected override bool Subscribe(IEnumerable<Symbol> symbols)
        {
            var symbolList = symbols.ToList();
            if (!symbolList.Any()) return true;

            try
            {
                var tokenIds = symbolList
                    .Select(s => _symbolMapper.GetBrokerageSymbol(s))
                    .ToList();

                var subscriptionMsg = _wsClient.CreateMarketSubscription(tokenIds);
                WebSocket.Send(subscriptionMsg);

                // Also subscribe to user channel for order updates
                var userMsg = _wsClient.CreateUserSubscription(tokenIds);
                WebSocket.Send(userMsg);

                foreach (var symbol in symbolList)
                {
                    if (!_orderBooks.ContainsKey(symbol))
                    {
                        _orderBooks[symbol] = new DefaultOrderBook(symbol);
                    }
                }

                Log.Trace($"PolymarketBrokerage.Subscribe(): Subscribed to {symbolList.Count} symbols");
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, "PolymarketBrokerage.Subscribe()");
                return false;
            }
        }

        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            foreach (var symbol in symbols)
            {
                _orderBooks.TryRemove(symbol, out _);
            }
            return true;
        }

        private void HandleOrderBookUpdate(PolymarketWsMessage message)
        {
            if (string.IsNullOrEmpty(message.AssetId) || !_symbolMapper.IsKnownTokenId(message.AssetId))
            {
                return;
            }

            var symbol = _symbolMapper.GetLeanSymbol(message.AssetId, SecurityType.Crypto, "polymarket");

            if (!_orderBooks.TryGetValue(symbol, out var orderBook))
            {
                return;
            }

            if (message.Changes != null)
            {
                foreach (var change in message.Changes)
                {
                    if (change.Count < 3) continue;

                    var side = change[0]; // "buy" or "sell"
                    var price = decimal.Parse(change[1], CultureInfo.InvariantCulture);
                    var size = decimal.Parse(change[2], CultureInfo.InvariantCulture);

                    if (side == "buy")
                    {
                        if (size == 0) orderBook.RemoveBidRow(price);
                        else orderBook.UpdateBidRow(price, size);
                    }
                    else
                    {
                        if (size == 0) orderBook.RemoveAskRow(price);
                        else orderBook.UpdateAskRow(price, size);
                    }
                }
            }

            EmitQuoteTick(symbol, orderBook);
        }

        private void HandleTradeUpdate(PolymarketWsMessage message)
        {
            if (string.IsNullOrEmpty(message.AssetId) || !_symbolMapper.IsKnownTokenId(message.AssetId))
            {
                return;
            }

            var symbol = _symbolMapper.GetLeanSymbol(message.AssetId, SecurityType.Crypto, "polymarket");

            if (decimal.TryParse(message.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) &&
                decimal.TryParse(message.Size, NumberStyles.Any, CultureInfo.InvariantCulture, out var size))
            {
                var tick = new Tick
                {
                    Symbol = symbol,
                    TickType = TickType.Trade,
                    Time = DateTime.UtcNow,
                    Value = price,
                    Quantity = size
                };

                _ticks.Enqueue(tick);
            }
        }

        private void HandleOrderUpdate(PolymarketWsMessage message)
        {
            if (message.Order == null) return;

            var polyOrder = message.Order;
            if (!_orderIdMap.TryGetValue(polyOrder.Id, out var leanOrderIdStr))
            {
                return;
            }

            if (!int.TryParse(leanOrderIdStr, out var leanOrderId))
            {
                return;
            }

            if (!CachedOrderIDs.TryGetValue(leanOrderId, out var order))
            {
                return;
            }

            var status = polyOrder.Status?.ToLowerInvariant() switch
            {
                "live" => OrderStatus.Submitted,
                "matched" => OrderStatus.Filled,
                "canceled" => OrderStatus.Canceled,
                "delayed" => OrderStatus.Submitted,
                _ => OrderStatus.None
            };

            if (status == OrderStatus.None) return;

            decimal fillQuantity = 0;
            decimal fillPrice = 0;

            if (status == OrderStatus.Filled &&
                decimal.TryParse(polyOrder.SizeMatched, NumberStyles.Any, CultureInfo.InvariantCulture, out var matched) &&
                decimal.TryParse(polyOrder.Price, NumberStyles.Any, CultureInfo.InvariantCulture, out fillPrice))
            {
                fillQuantity = matched;

                var originalSize = decimal.TryParse(polyOrder.OriginalSize, NumberStyles.Any, CultureInfo.InvariantCulture, out var orig) ? orig : 0;
                if (matched < originalSize && matched > 0)
                {
                    status = OrderStatus.PartiallyFilled;
                }
            }

            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = status,
                FillQuantity = fillQuantity,
                FillPrice = fillPrice
            });
        }

        private void EmitQuoteTick(Symbol symbol, IOrderBookUpdater<decimal, decimal> orderBook)
        {
            if (orderBook is DefaultOrderBook book && book.BestBidPrice > 0 && book.BestAskPrice > 0)
            {
                var tick = new Tick
                {
                    Symbol = symbol,
                    TickType = TickType.Quote,
                    Time = DateTime.UtcNow,
                    BidPrice = book.BestBidPrice,
                    BidSize = book.BestBidSize,
                    AskPrice = book.BestAskPrice,
                    AskSize = book.BestAskSize
                };

                _ticks.Enqueue(tick);
            }
        }

        #endregion

        #region IDataQueueHandler Implementation

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            SubscriptionManager.Subscribe(dataConfig);

            var enumerator = new PolymarketDataEnumerator(_ticks, dataConfig.Symbol);
            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            SubscriptionManager.Unsubscribe(dataConfig);
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        public void SetJob(LiveNodePacket job)
        {
        }

        private bool CanSubscribe(Symbol symbol)
        {
            return symbol.ID.SecurityType == SecurityType.Crypto &&
                   symbol.ID.Market == PolymarketSymbolMapper.PolymarketMarket;
        }

        #endregion

        #region Conversion Helpers

        private Order ConvertToLeanOrder(PolymarketOrder polyOrder)
        {
            try
            {
                if (!_symbolMapper.IsKnownTokenId(polyOrder.AssetId))
                {
                    return null;
                }

                var symbol = _symbolMapper.GetLeanSymbol(polyOrder.AssetId, SecurityType.Crypto, "polymarket");
                var price = decimal.Parse(polyOrder.Price, CultureInfo.InvariantCulture);
                var quantity = decimal.Parse(polyOrder.OriginalSize, CultureInfo.InvariantCulture);

                if (polyOrder.Side?.ToLowerInvariant() == "sell")
                {
                    quantity = -quantity;
                }

                var order = new LimitOrder(symbol, quantity, price, polyOrder.CreatedAt);
                _orderIdMap[polyOrder.Id] = order.Id.ToStringInvariant();
                return order;
            }
            catch (Exception e)
            {
                Log.Error(e, $"PolymarketBrokerage.ConvertToLeanOrder(): Failed for order {polyOrder?.Id}");
                return null;
            }
        }

        private Holding ConvertToHolding(PolymarketPosition position)
        {
            try
            {
                if (!_symbolMapper.IsKnownTokenId(position.AssetId))
                {
                    return null;
                }

                var symbol = _symbolMapper.GetLeanSymbol(position.AssetId, SecurityType.Crypto, "polymarket");
                var quantity = decimal.Parse(position.Size, CultureInfo.InvariantCulture);
                var avgPrice = decimal.Parse(position.AvgPrice, CultureInfo.InvariantCulture);
                var curPrice = decimal.TryParse(position.CurPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var cp) ? cp : avgPrice;

                return new Holding
                {
                    Symbol = symbol,
                    Quantity = quantity,
                    AveragePrice = avgPrice,
                    MarketPrice = curPrice,
                    CurrencySymbol = "$"
                };
            }
            catch (Exception e)
            {
                Log.Error(e, $"PolymarketBrokerage.ConvertToHolding(): Failed for position {position?.AssetId}");
                return null;
            }
        }

        private decimal GetMarketPrice(Symbol symbol, OrderDirection direction)
        {
            if (_orderBooks.TryGetValue(symbol, out var book))
            {
                if (direction == OrderDirection.Buy && book.BestAskPrice > 0)
                {
                    return book.BestAskPrice;
                }
                if (direction == OrderDirection.Sell && book.BestBidPrice > 0)
                {
                    return book.BestBidPrice;
                }
            }

            // Fallback: get from API
            try
            {
                var tokenId = _symbolMapper.GetBrokerageSymbol(symbol);
                var orderBookData = _apiClient.GetOrderBook(tokenId);
                if (orderBookData?.Bids?.Count > 0 && direction == OrderDirection.Sell)
                {
                    return decimal.Parse(orderBookData.Bids[0].Price, CultureInfo.InvariantCulture);
                }
                if (orderBookData?.Asks?.Count > 0 && direction == OrderDirection.Buy)
                {
                    return decimal.Parse(orderBookData.Asks[0].Price, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "PolymarketBrokerage.GetMarketPrice()");
            }

            return 0.5m; // Default mid-price for prediction markets
        }

        #endregion

        /// <summary>
        /// Gets the symbol mapper for this brokerage
        /// </summary>
        public PolymarketSymbolMapper SymbolMapper => _symbolMapper;

        /// <summary>
        /// Disposes the brokerage
        /// </summary>
        public override void Dispose()
        {
            _apiClient?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Enumerator that yields ticks from a concurrent queue, filtered by symbol
    /// </summary>
    internal class PolymarketDataEnumerator : IEnumerator<BaseData>
    {
        private readonly ConcurrentQueue<Tick> _tickQueue;
        private readonly Symbol _symbol;
        private Tick _current;

        public PolymarketDataEnumerator(ConcurrentQueue<Tick> tickQueue, Symbol symbol)
        {
            _tickQueue = tickQueue;
            _symbol = symbol;
        }

        public BaseData Current => _current;
        object System.Collections.IEnumerator.Current => _current;

        public bool MoveNext()
        {
            while (_tickQueue.TryDequeue(out var tick))
            {
                if (tick.Symbol == _symbol)
                {
                    _current = tick;
                    return true;
                }
            }
            _current = null;
            return false;
        }

        public void Reset() { }
        public void Dispose() { }
    }
}
