using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Polymarket.Api;
using QuantConnect.Brokerages.Polymarket.Api.Models;
using QuantConnect.Brokerages.Polymarket.Auth;
using QuantConnect.Brokerages.Polymarket.Dashboard.Hubs;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    /// <summary>
    /// Background service that connects to Polymarket WebSocket and broadcasts real-time data via SignalR
    /// </summary>
    public class MarketDataService : BackgroundService
    {
        private readonly IHubContext<TradingHub> _hubContext;
        private readonly PolymarketCredentials _credentials;
        private readonly TradingService _tradingService;
        private readonly ILogger<MarketDataService> _logger;
        private readonly PolymarketWebSocketClient _wsClient;

        // In-memory order book snapshots keyed by token ID
        private readonly ConcurrentDictionary<string, PolymarketOrderBook> _orderBooks = new();

        // Token IDs currently subscribed to
        private readonly ConcurrentDictionary<string, byte> _subscribedTokens = new();

        private ClientWebSocket _marketWs;

        public MarketDataService(
            IHubContext<TradingHub> hubContext,
            PolymarketCredentials credentials,
            TradingService tradingService,
            ILogger<MarketDataService> logger)
        {
            _hubContext = hubContext;
            _credentials = credentials;
            _tradingService = tradingService;
            _logger = logger;
            _wsClient = new PolymarketWebSocketClient(credentials);
        }

        /// <summary>
        /// Gets the cached order book for a token
        /// </summary>
        public PolymarketOrderBook GetCachedOrderBook(string tokenId)
        {
            _orderBooks.TryGetValue(tokenId, out var book);
            return book;
        }

        /// <summary>
        /// Seeds the order book cache with a pre-fetched book (used by DryRunEngine to prime cache on auto-subscribe)
        /// </summary>
        public void SeedOrderBook(string tokenId, PolymarketOrderBook book)
        {
            if (book != null)
                _orderBooks[tokenId] = book;
        }

        /// <summary>
        /// Subscribes to real-time data for the given token IDs
        /// </summary>
        public async Task SubscribeAsync(IEnumerable<string> tokenIds)
        {
            var newTokens = new List<string>();
            foreach (var id in tokenIds)
            {
                if (_subscribedTokens.TryAdd(id, 1))
                {
                    newTokens.Add(id);
                }
            }

            if (newTokens.Count == 0 || _marketWs?.State != WebSocketState.Open) return;

            var subMessage = _wsClient.CreateMarketSubscription(newTokens);
            var bytes = Encoding.UTF8.GetBytes(subMessage);
            await _marketWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            _logger.LogInformation("Subscribed to {Count} new tokens", newTokens.Count);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Wait briefly for DI to fully initialize
            await Task.Delay(2000, stoppingToken);

            _logger.LogInformation("MarketDataService starting...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndListenAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WebSocket connection error, reconnecting in 5s...");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private async Task ConnectAndListenAsync(CancellationToken ct)
        {
            _marketWs = new ClientWebSocket();
            await _marketWs.ConnectAsync(new Uri(PolymarketWebSocketClient.MarketWssUrl), ct);
            _logger.LogInformation("Connected to Polymarket market WebSocket");

            // Re-subscribe if we had tokens
            if (!_subscribedTokens.IsEmpty)
            {
                var subMessage = _wsClient.CreateMarketSubscription(_subscribedTokens.Keys);
                var bytes = Encoding.UTF8.GetBytes(subMessage);
                await _marketWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }

            var buffer = new byte[8192];
            var messageBuffer = new StringBuilder();

            while (_marketWs.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _marketWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("WebSocket closed by server");
                    break;
                }

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (!result.EndOfMessage) continue;

                var raw = messageBuffer.ToString();
                messageBuffer.Clear();

                await ProcessMessageAsync(raw);
            }

            _marketWs.Dispose();
        }

        private async Task ProcessMessageAsync(string raw)
        {
            // Polymarket can send arrays of messages
            if (raw.TrimStart().StartsWith("["))
            {
                var messages = JsonConvert.DeserializeObject<List<PolymarketWsMessage>>(raw);
                if (messages != null)
                {
                    foreach (var msg in messages)
                    {
                        await HandleMessageAsync(msg);
                    }
                }
            }
            else
            {
                var msg = PolymarketWebSocketClient.ParseMessage(raw);
                if (msg != null)
                {
                    await HandleMessageAsync(msg);
                }
            }
        }

        private async Task HandleMessageAsync(PolymarketWsMessage msg)
        {
            switch (msg.EventType)
            {
                case PolymarketWsMessageType.BookSnapshot:
                case PolymarketWsMessageType.PriceChange:
                    await HandleOrderBookUpdate(msg);
                    break;

                case PolymarketWsMessageType.TradeUpdate:
                    await _hubContext.Clients.All.SendAsync("TradeUpdate", msg);
                    break;
            }
        }

        private async Task HandleOrderBookUpdate(PolymarketWsMessage msg)
        {
            var tokenId = msg.AssetId;
            if (string.IsNullOrEmpty(tokenId)) return;

            // Update cached book if changes come in
            if (msg.Changes != null)
            {
                if (!_orderBooks.TryGetValue(tokenId, out var book))
                {
                    // Fetch full snapshot on first update
                    try
                    {
                        book = _tradingService.GetOrderBook(tokenId);
                        _orderBooks[tokenId] = book;
                    }
                    catch
                    {
                        return;
                    }
                }

                // Apply incremental changes
                foreach (var change in msg.Changes)
                {
                    if (change.Count < 3) continue;
                    var side = change[0]; // "buy" or "sell"
                    var price = change[1];
                    var size = change[2];

                    var levels = side == "buy" ? book.Bids : book.Asks;
                    ApplyChange(levels, price, size);
                }

                _orderBooks[tokenId] = book;
            }

            await _hubContext.Clients.All.SendAsync("OrderBookUpdate", new
            {
                tokenId,
                book = _orderBooks.GetValueOrDefault(tokenId)
            });
        }

        private static void ApplyChange(List<PolymarketOrderBookLevel> levels, string price, string size)
        {
            if (levels == null) return;

            // Remove existing level at this price
            levels.RemoveAll(l => l.Price == price);

            // Add if size > 0
            if (decimal.TryParse(size, out var s) && s > 0)
            {
                levels.Add(new PolymarketOrderBookLevel { Price = price, Size = size });
            }
        }
    }
}
