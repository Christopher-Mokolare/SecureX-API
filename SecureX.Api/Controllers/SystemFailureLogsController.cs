using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Services;
using System.Security.Claims;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/admin/system-failures")]
[Authorize(Roles = "Admin")]
public class SystemFailureLogsController(AppDbContext db) : ControllerBase
{
    private string CallerEmail => User.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int page = 1,
        [FromQuery] int size = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? severity = null,
        [FromQuery] bool? resolved = null,
        CancellationToken cancellationToken = default)
    {
        var service = new SystemFailureLogService(db);
        var result = await service.GetAsync(page, size, search, severity, resolved, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid id,
        [FromBody] ResolveSystemFailureRequest request,
        CancellationToken cancellationToken = default)
    {
        var service = new SystemFailureLogService(db);
        var updated = await service.SetResolvedAsync(id, true, CallerEmail, request.Notes, cancellationToken);
        return updated ? Ok(new { message = "System failure marked as resolved" }) : NotFound();
    }

    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken cancellationToken = default)
    {
        var service = new SystemFailureLogService(db);
        var updated = await service.SetResolvedAsync(id, false, CallerEmail, null, cancellationToken);
        return updated ? Ok(new { message = "System failure reopened" }) : NotFound();
    }
}

public sealed record ResolveSystemFailureRequest(string? Notes);
