using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SecureX.Api.Models;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/config")]
public class ConfigController(IOptions<TransactionLimitsOptions> transactionLimits) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("transaction-limits")]
    public IActionResult GetTransactionLimits()
    {
        var limits = transactionLimits.Value;

        return Ok(new
        {
            minimumAmount = limits.MinimumAmount,
            maximumAmount = limits.MaximumAmount,
            currency = "ZAR"
        });
    }
}
