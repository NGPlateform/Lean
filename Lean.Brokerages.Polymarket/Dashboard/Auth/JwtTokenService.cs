using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Auth
{
    public class JwtTokenService
    {
        private readonly AuthSettings _settings;

        public JwtTokenService(AuthSettings settings)
        {
            _settings = settings;
        }

        public (string Token, DateTime ExpiresAt) GenerateToken(string address)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.JwtSecret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expiresAt = DateTime.UtcNow.AddHours(_settings.TokenExpiryHours);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, address.ToLowerInvariant()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("addr", address.ToLowerInvariant())
            };

            var token = new JwtSecurityToken(
                issuer: "polymarket-dashboard",
                audience: "polymarket-dashboard",
                claims: claims,
                expires: expiresAt,
                signingCredentials: credentials);

            return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
        }
    }
}
