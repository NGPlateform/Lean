using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Controllers
{
    [ApiController]
    [Route("api")]
    public class TradingController : ControllerBase
    {
        private readonly TradingService _tradingService;
        private readonly MarketDataService _marketDataService;
        private readonly DataDownloadService _dataDownloadService;
        private readonly DryRunSettings _dryRunSettings;
        private readonly DryRunEngine _dryRunEngine;
        private readonly ILogger<TradingController> _logger;

        private static DataDownloadResult _lastDownloadResult;
        private static bool _downloadRunning;

        private bool IsDryRunMode => _dryRunSettings?.Enabled == true && _dryRunEngine != null;

        public TradingController(
            TradingService tradingService,
            MarketDataService marketDataService,
            DataDownloadService dataDownloadService,
            DryRunSettings dryRunSettings,
            ILogger<TradingController> logger,
            DryRunEngine dryRunEngine = null)
        {
            _tradingService = tradingService;
            _marketDataService = marketDataService;
            _dataDownloadService = dataDownloadService;
            _dryRunSettings = dryRunSettings;
            _dryRunEngine = dryRunEngine;
            _logger = logger;
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(new
            {
                hasCredentials = IsDryRunMode || _tradingService.HasCredentials,
                dryRunMode = IsDryRunMode,
                dryRunStrategy = IsDryRunMode ? _dryRunEngine.StrategyName : null,
                message = IsDryRunMode
                    ? $"DRY RUN mode active — Strategy: {_dryRunEngine.StrategyName}"
                    : _tradingService.HasCredentials
                        ? "Connected with API credentials"
                        : "No API credentials. Public market data available. Configure appsettings.json for trading."
            });
        }

        // === Market Data endpoints — always use real data ===

        [HttpGet("markets")]
        public IActionResult GetMarkets([FromQuery] string q = null)
        {
            try
            {
                var markets = string.IsNullOrEmpty(q)
                    ? _tradingService.GetMarkets()
                    : _tradingService.SearchMarkets(q);

                return Ok(markets);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get markets");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("events")]
        public IActionResult GetEvents()
        {
            try
            {
                return Ok(_tradingService.GetEvents());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get events");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("markets/{marketId}")]
        public IActionResult GetMarket(string marketId)
        {
            try
            {
                var market = _tradingService.GetMarket(marketId);
                if (market == null) return NotFound(new { error = "Market not found" });
                return Ok(market);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get market {MarketId}", marketId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("orderbook/{tokenId}")]
        public IActionResult GetOrderBook(string tokenId)
        {
            try
            {
                var book = _marketDataService.GetCachedOrderBook(tokenId)
                    ?? _tradingService.GetOrderBook(tokenId);
                return Ok(book);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get order book for {TokenId}", tokenId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("prices")]
        public IActionResult GetPrices([FromBody] PriceRequest request)
        {
            try
            {
                if (request?.TokenIds == null || request.TokenIds.Count == 0)
                    return BadRequest(new { error = "Provide token IDs" });

                var prices = _tradingService.GetPrices(request.TokenIds);
                return Ok(prices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get prices");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("markets/refresh")]
        public IActionResult RefreshMarkets()
        {
            try
            {
                _tradingService.RefreshMarkets();
                var markets = _tradingService.GetMarkets();
                return Ok(new { count = markets.Count, message = "Markets refreshed" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh markets");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // === Trading endpoints — conditional routing ===

        [HttpGet("positions")]
        public IActionResult GetPositions()
        {
            try
            {
                return Ok(IsDryRunMode
                    ? _dryRunEngine.GetPositions()
                    : _tradingService.GetPositions());
            }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpGet("balance")]
        public IActionResult GetBalance()
        {
            try
            {
                return Ok(IsDryRunMode
                    ? _dryRunEngine.GetBalance()
                    : _tradingService.GetBalance());
            }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpGet("orders")]
        public IActionResult GetOrders()
        {
            try
            {
                return Ok(IsDryRunMode
                    ? _dryRunEngine.GetOpenOrders()
                    : _tradingService.GetOpenOrders());
            }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        [HttpPost("orders")]
        public IActionResult PlaceOrder([FromBody] PlaceOrderRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.TokenId) || request.Price <= 0 || request.Size <= 0)
                    return BadRequest(new { error = "Invalid order: tokenId, price, size required" });

                if (request.Price < 0.01m || request.Price > 0.99m)
                    return BadRequest(new { error = "Price must be between 0.01 and 0.99" });

                if (IsDryRunMode)
                {
                    var result = _dryRunEngine.PlaceOrder(request.TokenId, request.Price, request.Size, request.Side);
                    return Ok(result);
                }

                return Ok(_tradingService.PlaceOrder(request.TokenId, request.Price, request.Size, request.Side));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to place order");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpDelete("orders/{orderId}")]
        public IActionResult CancelOrder(string orderId)
        {
            try
            {
                if (IsDryRunMode)
                    return Ok(_dryRunEngine.CancelOrder(orderId));

                return Ok(_tradingService.CancelOrder(orderId));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("trades")]
        public IActionResult GetTrades([FromQuery] int limit = 50)
        {
            try
            {
                return Ok(IsDryRunMode
                    ? _dryRunEngine.GetTrades(limit)
                    : _tradingService.GetTrades(limit));
            }
            catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
        }

        // === DryRun-specific endpoints ===

        [HttpGet("logs")]
        public IActionResult GetLogs([FromQuery] int limit = 200)
        {
            if (!IsDryRunMode)
                return Ok(new List<object>());

            return Ok(_dryRunEngine.GetLogs(limit));
        }

        // === Data Download endpoint ===

        [HttpPost("data/download")]
        public IActionResult DownloadData([FromQuery] int days = 30)
        {
            if (_downloadRunning)
                return Ok(new { status = "running", message = "Download already in progress" });

            _downloadRunning = true;
            _lastDownloadResult = null;

            // Run in background to avoid blocking the request
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    _lastDownloadResult = await _dataDownloadService.DownloadAllAsync(days);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background download failed");
                    _lastDownloadResult = new DataDownloadResult { Error = ex.Message };
                }
                finally
                {
                    _downloadRunning = false;
                }
            });

            return Ok(new { status = "started", days, message = $"Download started for {days} days of crypto market data" });
        }

        [HttpGet("data/download/status")]
        public IActionResult GetDownloadStatus()
        {
            if (_downloadRunning)
                return Ok(new { status = "running" });

            if (_lastDownloadResult != null)
                return Ok(new { status = "completed", result = _lastDownloadResult });

            return Ok(new { status = "idle" });
        }
    }

    public class PlaceOrderRequest
    {
        public string TokenId { get; set; }
        public decimal Price { get; set; }
        public decimal Size { get; set; }
        public string Side { get; set; }
    }

    public class PriceRequest
    {
        public List<string> TokenIds { get; set; }
    }
}
