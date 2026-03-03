using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuantConnect.Brokerages.Polymarket.Dashboard.Models;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/settings")]
    public class SettingsController : ControllerBase
    {
        private readonly SettingsService _settingsService;
        private readonly RiskManager _riskManager;

        public SettingsController(SettingsService settingsService, RiskManager riskManager)
        {
            _settingsService = settingsService;
            _riskManager = riskManager;
        }

        [HttpGet("credentials")]
        public IActionResult GetCredentials()
        {
            return Ok(_settingsService.GetCredentials());
        }

        [HttpPut("credentials")]
        public IActionResult UpdateCredentials([FromBody] CredentialsUpdateRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "Request body required" });

            _settingsService.UpdateCredentials(request);
            return Ok(new { success = true, message = "Credentials updated" });
        }

        [HttpGet("system")]
        public IActionResult GetSystemSettings()
        {
            return Ok(_settingsService.GetSystemSettings());
        }

        [HttpPut("system")]
        public IActionResult UpdateSystemSettings([FromBody] SystemSettingsRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "Request body required" });

            _settingsService.UpdateSystemSettings(request);
            return Ok(new { success = true, message = "System settings updated. Restart required for some changes." });
        }

        [HttpGet("risk")]
        public IActionResult GetRiskSettings()
        {
            return Ok(_settingsService.GetRiskSettings());
        }

        [HttpPut("risk")]
        public IActionResult UpdateRiskSettings([FromBody] RiskSettings request)
        {
            if (request == null)
                return BadRequest(new { error = "Request body required" });

            _settingsService.UpdateRiskSettings(request);
            _riskManager.UpdateSettings(request);
            return Ok(new { success = true, message = "Risk settings updated" });
        }
    }
}
