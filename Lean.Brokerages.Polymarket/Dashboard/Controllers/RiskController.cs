using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuantConnect.Brokerages.Polymarket.Dashboard.Services;

namespace QuantConnect.Brokerages.Polymarket.Dashboard.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/risk")]
    public class RiskController : ControllerBase
    {
        private readonly RiskManager _riskManager;

        public RiskController(RiskManager riskManager)
        {
            _riskManager = riskManager;
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            return Ok(_riskManager.GetStatus());
        }

        [HttpGet("alerts")]
        public IActionResult GetAlerts()
        {
            return Ok(_riskManager.GetAlerts());
        }
    }
}
