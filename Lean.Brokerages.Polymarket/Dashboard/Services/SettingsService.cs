using System;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Polymarket.Dashboard.Models;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Services
{
    public class SettingsService
    {
        private readonly IDataProtector _protector;
        private readonly ILogger<SettingsService> _logger;
        private readonly string _settingsPath;
        private EncryptedSettings _cached;

        public SettingsService(IDataProtectionProvider protectionProvider, ILogger<SettingsService> logger)
        {
            _protector = protectionProvider.CreateProtector("PolymarketDashboard.Settings");
            _logger = logger;
            _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.enc.json");
            Load();
        }

        public CredentialsResponse GetCredentials()
        {
            return new CredentialsResponse
            {
                ApiKey = Mask(_cached.ApiKey),
                ApiSecret = Mask(_cached.ApiSecret),
                PrivateKey = Mask(_cached.PrivateKey),
                Passphrase = Mask(_cached.Passphrase),
                HasCredentials = !string.IsNullOrEmpty(_cached.ApiKey)
            };
        }

        public void UpdateCredentials(CredentialsUpdateRequest request)
        {
            if (!string.IsNullOrEmpty(request.ApiKey))
                _cached.ApiKey = request.ApiKey;
            if (!string.IsNullOrEmpty(request.ApiSecret))
                _cached.ApiSecret = request.ApiSecret;
            if (!string.IsNullOrEmpty(request.PrivateKey))
                _cached.PrivateKey = request.PrivateKey;
            if (!string.IsNullOrEmpty(request.Passphrase))
                _cached.Passphrase = request.Passphrase;
            Save();
        }

        public string GetRawApiKey() => _cached.ApiKey ?? "";
        public string GetRawApiSecret() => _cached.ApiSecret ?? "";
        public string GetRawPrivateKey() => _cached.PrivateKey ?? "";
        public string GetRawPassphrase() => _cached.Passphrase ?? "";

        public SystemSettingsResponse GetSystemSettings()
        {
            return new SystemSettingsResponse
            {
                DryRunEnabled = _cached.DryRunEnabled,
                InitialBalance = _cached.InitialBalance,
                TickIntervalMs = _cached.TickIntervalMs,
                StrategyName = _cached.StrategyName ?? "MarketMaking",
                AutoSubscribeTopN = _cached.AutoSubscribeTopN
            };
        }

        public void UpdateSystemSettings(SystemSettingsRequest request)
        {
            if (request.DryRunEnabled.HasValue) _cached.DryRunEnabled = request.DryRunEnabled.Value;
            if (request.InitialBalance.HasValue) _cached.InitialBalance = request.InitialBalance.Value;
            if (request.TickIntervalMs.HasValue) _cached.TickIntervalMs = request.TickIntervalMs.Value;
            if (!string.IsNullOrEmpty(request.StrategyName)) _cached.StrategyName = request.StrategyName;
            if (request.AutoSubscribeTopN.HasValue) _cached.AutoSubscribeTopN = request.AutoSubscribeTopN.Value;
            Save();
        }

        public RiskSettings GetRiskSettings()
        {
            return _cached.Risk ?? new RiskSettings();
        }

        public void UpdateRiskSettings(RiskSettings settings)
        {
            _cached.Risk = settings;
            Save();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var encrypted = File.ReadAllText(_settingsPath);
                    var json = _protector.Unprotect(encrypted);
                    _cached = JsonConvert.DeserializeObject<EncryptedSettings>(json) ?? new EncryptedSettings();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load encrypted settings, starting fresh");
            }
            _cached = new EncryptedSettings();
        }

        private void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_cached, Formatting.Indented);
                var encrypted = _protector.Protect(json);
                File.WriteAllText(_settingsPath, encrypted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save encrypted settings");
            }
        }

        private static string Mask(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length <= 6) return "***";
            return value.Substring(0, 3) + "***" + value.Substring(value.Length - 3);
        }

        private class EncryptedSettings
        {
            public string ApiKey { get; set; } = "";
            public string ApiSecret { get; set; } = "";
            public string PrivateKey { get; set; } = "";
            public string Passphrase { get; set; } = "";
            public bool DryRunEnabled { get; set; } = true;
            public decimal InitialBalance { get; set; } = 10000m;
            public int TickIntervalMs { get; set; } = 5000;
            public string StrategyName { get; set; } = "MarketMaking";
            public int AutoSubscribeTopN { get; set; } = 10;
            public RiskSettings Risk { get; set; } = new();
        }
    }
}
