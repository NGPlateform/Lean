using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Hubs
{
    /// <summary>
    /// SignalR hub for real-time trading data (order book updates, trades, order status changes)
    /// </summary>
    public class TradingHub : Hub
    {
        private readonly MarketDataService _marketDataService;
        private readonly ILogger<TradingHub> _logger;

        public TradingHub(MarketDataService marketDataService, ILogger<TradingHub> logger)
        {
            _marketDataService = marketDataService;
            _logger = logger;
        }

        /// <summary>
        /// Client calls this to subscribe to real-time data for specific tokens
        /// </summary>
        public async Task SubscribeToTokens(string[] tokenIds)
        {
            _logger.LogInformation("Client subscribing to {Count} tokens", tokenIds.Length);
            await _marketDataService.SubscribeAsync(tokenIds);
        }
    }
}
