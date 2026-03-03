using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuantConnect.Brokerages.Polymarket.Dashboard.Auth;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly NonceStore _nonceStore;
        private readonly SignatureVerifier _verifier;
        private readonly JwtTokenService _jwtService;
        private readonly AuthSettings _authSettings;

        public AuthController(NonceStore nonceStore, SignatureVerifier verifier,
            JwtTokenService jwtService, AuthSettings authSettings)
        {
            _nonceStore = nonceStore;
            _verifier = verifier;
            _jwtService = jwtService;
            _authSettings = authSettings;
        }

        [AllowAnonymous]
        [HttpGet("nonce")]
        public IActionResult GetNonce([FromQuery] string addr)
        {
            if (string.IsNullOrWhiteSpace(addr) || !addr.StartsWith("0x") || addr.Length != 42)
                return BadRequest(new { error = "Invalid Ethereum address" });

            var nonce = _nonceStore.GenerateNonce(addr);
            var message = $"Sign this message to authenticate with Polymarket Dashboard.\n\nNonce: {nonce}\nAddress: {addr.ToLowerInvariant()}";

            return Ok(new NonceResponse { Nonce = nonce, Message = message });
        }

        [AllowAnonymous]
        [HttpPost("verify")]
        public IActionResult Verify([FromBody] VerifyRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Address) ||
                string.IsNullOrWhiteSpace(request.Signature) ||
                string.IsNullOrWhiteSpace(request.Nonce))
                return BadRequest(new { error = "Address, signature, and nonce required" });

            if (!_nonceStore.ConsumeNonce(request.Address, request.Nonce))
                return BadRequest(new { error = "Invalid or expired nonce" });

            var message = $"Sign this message to authenticate with Polymarket Dashboard.\n\nNonce: {request.Nonce}\nAddress: {request.Address.ToLowerInvariant()}";

            if (!_verifier.Verify(message, request.Signature, request.Address))
                return Unauthorized(new { error = "Signature verification failed" });

            // Check whitelist
            if (_authSettings.WhitelistedAddresses.Count > 0 &&
                !_authSettings.WhitelistedAddresses.Any(a =>
                    string.Equals(a, request.Address, StringComparison.OrdinalIgnoreCase)))
            {
                return Unauthorized(new { error = "Address not whitelisted" });
            }

            var (token, expiresAt) = _jwtService.GenerateToken(request.Address);
            return Ok(new AuthResponse
            {
                Token = token,
                ExpiresAt = expiresAt,
                Address = request.Address.ToLowerInvariant()
            });
        }

        [Authorize]
        [HttpGet("me")]
        public IActionResult GetMe()
        {
            var address = User.FindFirst("addr")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Ok(new { address, authenticated = true });
        }
    }
}
