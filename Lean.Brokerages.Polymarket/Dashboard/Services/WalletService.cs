using System;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Polymarket.Dashboard.Models;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class WalletService
    {
        private readonly HttpClient _http;
        private readonly ILogger<WalletService> _logger;
        private readonly string _rpcUrl;
        private readonly string _usdcAddress;
        private readonly string _ctfExchangeAddress;

        // USDC on Polygon has 6 decimals
        private static readonly BigInteger UsdcDecimals = BigInteger.Pow(10, 6);

        public WalletService(IConfiguration config, ILogger<WalletService> logger)
        {
            _http = new HttpClient();
            _logger = logger;
            _rpcUrl = config["Polygon:RpcUrl"] ?? "https://polygon-rpc.com";
            _usdcAddress = config["Polygon:UsdcContractAddress"] ?? "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174";
            _ctfExchangeAddress = config["Polygon:CtfExchangeAddress"] ?? "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";
        }

        public async Task<WalletBalanceResponse> GetBalanceAsync(string address)
        {
            var maticBalance = await RpcCallAsync("eth_getBalance", new object[] { address, "latest" });
            var usdcBalance = await GetErc20BalanceAsync(address);
            var usdcAllowance = await GetErc20AllowanceAsync(address, _ctfExchangeAddress);

            return new WalletBalanceResponse
            {
                Address = address,
                MaticBalance = FormatWei(maticBalance, 18),
                UsdcBalance = FormatUsdc(usdcBalance),
                UsdcAllowance = FormatUsdc(usdcAllowance)
            };
        }

        public TransactionParams BuildDepositTxParams(string fromAddress, string amount)
        {
            // ERC20 transfer to CTF Exchange: transfer(address,uint256)
            var amountWei = ParseUsdc(amount);
            var data = "0xa9059cbb" + // transfer(address,uint256)
                       PadAddress(_ctfExchangeAddress) +
                       PadUint256(amountWei);

            return new TransactionParams
            {
                To = _usdcAddress,
                Data = data,
                Value = "0x0",
                ChainId = "0x89" // Polygon mainnet
            };
        }

        public TransactionParams BuildWithdrawTxParams(string fromAddress, string toAddress, string amount)
        {
            var amountWei = ParseUsdc(amount);
            var data = "0xa9059cbb" + // transfer(address,uint256)
                       PadAddress(toAddress) +
                       PadUint256(amountWei);

            return new TransactionParams
            {
                To = _usdcAddress,
                Data = data,
                Value = "0x0",
                ChainId = "0x89"
            };
        }

        public TransactionParams BuildApproveTxParams(string amount)
        {
            var amountWei = string.Equals(amount, "max", StringComparison.OrdinalIgnoreCase)
                ? BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935")
                : ParseUsdc(amount);

            // approve(address,uint256)
            var data = "0x095ea7b3" +
                       PadAddress(_ctfExchangeAddress) +
                       PadUint256(amountWei);

            return new TransactionParams
            {
                To = _usdcAddress,
                Data = data,
                Value = "0x0",
                ChainId = "0x89"
            };
        }

        public async Task<TransactionStatusResponse> GetTransactionStatusAsync(string txHash)
        {
            try
            {
                var result = await RpcCallAsync("eth_getTransactionReceipt", new object[] { txHash });
                if (string.IsNullOrEmpty(result) || result == "null")
                {
                    return new TransactionStatusResponse { TxHash = txHash, Status = "pending" };
                }

                var receipt = JObject.Parse(result);
                var status = receipt["status"]?.ToString();
                var blockNum = receipt["blockNumber"]?.ToString();

                return new TransactionStatusResponse
                {
                    TxHash = txHash,
                    Status = status == "0x1" ? "confirmed" : "failed",
                    BlockNumber = blockNum != null ? Convert.ToInt32(blockNum, 16) : null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tx status for {TxHash}", txHash);
                return new TransactionStatusResponse { TxHash = txHash, Status = "unknown" };
            }
        }

        private async Task<string> GetErc20BalanceAsync(string address)
        {
            // balanceOf(address)
            var data = "0x70a08231" + PadAddress(address);
            return await RpcCallAsync("eth_call",
                new object[] { new { to = _usdcAddress, data }, "latest" });
        }

        private async Task<string> GetErc20AllowanceAsync(string owner, string spender)
        {
            // allowance(address,address)
            var data = "0xdd62ed3e" + PadAddress(owner) + PadAddress(spender);
            return await RpcCallAsync("eth_call",
                new object[] { new { to = _usdcAddress, data }, "latest" });
        }

        private async Task<string> RpcCallAsync(string method, object[] parameters)
        {
            var payload = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
                id = 1
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(_rpcUrl, content);
            var body = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(body);

            if (json["error"] != null)
            {
                _logger.LogWarning("RPC error: {Error}", json["error"].ToString());
                return "0x0";
            }

            return json["result"]?.ToString() ?? "0x0";
        }

        private static string FormatWei(string hexValue, int decimals)
        {
            if (string.IsNullOrEmpty(hexValue) || hexValue == "0x0" || hexValue == "0x")
                return "0";
            var value = BigInteger.Parse("0" + hexValue.Substring(2), System.Globalization.NumberStyles.HexNumber);
            var divisor = BigInteger.Pow(10, decimals);
            var whole = value / divisor;
            var frac = value % divisor;
            var fracStr = frac.ToString().PadLeft(decimals, '0').TrimEnd('0');
            return fracStr.Length > 0 ? $"{whole}.{fracStr.Substring(0, Math.Min(6, fracStr.Length))}" : whole.ToString();
        }

        private static string FormatUsdc(string hexValue)
        {
            return FormatWei(hexValue, 6);
        }

        private static BigInteger ParseUsdc(string amount)
        {
            var parts = amount.Split('.');
            var whole = BigInteger.Parse(parts[0]) * UsdcDecimals;
            if (parts.Length > 1)
            {
                var frac = parts[1].PadRight(6, '0').Substring(0, 6);
                whole += BigInteger.Parse(frac);
            }
            return whole;
        }

        private static string PadAddress(string address)
        {
            return address.Substring(2).PadLeft(64, '0');
        }

        private static string PadUint256(BigInteger value)
        {
            return value.ToString("x").PadLeft(64, '0');
        }
    }
}
