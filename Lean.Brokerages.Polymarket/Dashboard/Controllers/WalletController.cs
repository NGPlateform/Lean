using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuantConnect.Brokerages.Polymarket.Dashboard.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/wallet")]
    public class WalletController : ControllerBase
    {
        private readonly WalletService _walletService;

        public WalletController(WalletService walletService)
        {
            _walletService = walletService;
        }

        [HttpGet("balance")]
        public async Task<IActionResult> GetBalance([FromQuery] string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                address = User.FindFirst("addr")?.Value;
                if (string.IsNullOrWhiteSpace(address))
                    return BadRequest(new { error = "Address required" });
            }

            try
            {
                var balance = await _walletService.GetBalanceAsync(address);
                return Ok(balance);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("deposit")]
        public IActionResult BuildDeposit([FromBody] TransferRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Amount) || string.IsNullOrWhiteSpace(request.FromAddress))
                return BadRequest(new { error = "Amount and fromAddress required" });

            try
            {
                var txParams = _walletService.BuildDepositTxParams(request.FromAddress, request.Amount);
                return Ok(txParams);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("withdraw")]
        public IActionResult BuildWithdraw([FromBody] TransferRequest request)
        {
            var toAddress = User.FindFirst("addr")?.Value;
            if (string.IsNullOrWhiteSpace(request?.Amount) || string.IsNullOrWhiteSpace(request.FromAddress))
                return BadRequest(new { error = "Amount and fromAddress required" });

            try
            {
                var txParams = _walletService.BuildWithdrawTxParams(request.FromAddress, toAddress ?? request.FromAddress, request.Amount);
                return Ok(txParams);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("approve")]
        public IActionResult BuildApprove([FromBody] TransferRequest request)
        {
            var amount = request?.Amount ?? "max";
            try
            {
                var txParams = _walletService.BuildApproveTxParams(amount);
                return Ok(txParams);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("tx/{txHash}")]
        public async Task<IActionResult> GetTransactionStatus(string txHash)
        {
            if (string.IsNullOrWhiteSpace(txHash))
                return BadRequest(new { error = "Transaction hash required" });

            try
            {
                var status = await _walletService.GetTransactionStatusAsync(txHash);
                return Ok(status);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
