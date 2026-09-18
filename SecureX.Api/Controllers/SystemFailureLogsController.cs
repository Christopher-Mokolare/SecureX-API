using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecureX.Api.Services;
using System.Security.Claims;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/admin/system-failures")]
[Authorize(Roles = "Admin")]
public sealed class SystemFailureLogsController(
    SystemFailureLogService failureLogs,
    ILogger<SystemFailureLogsController> logger) : ControllerBase
{
    private string CallerEmail =>
        User.FindFirstValue(ClaimTypes.Email) ??
        User.FindFirstValue(ClaimTypes.NameIdentifier) ??
        "unknown";

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int page = 1,
        [FromQuery] int size = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? severity = null,
        [FromQuery] bool? resolved = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 100);

        try
        {
            var result = await failureLogs.GetAsync(
                page, size, search, severity, resolved, cancellationToken);

            return Ok(new
            {
                total = result.Total,
                page = result.Page,
                size = result.Size,
                pages = result.Pages,
                items = result.Items
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load system failure logs for admin {Caller}", CallerEmail);
            return StatusCode(500, new { error = "Failed to load system failure logs." });
        }
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid id,
        [FromBody] ResolveSystemFailureRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await failureLogs.SetResolvedAsync(
                id, true, CallerEmail, request.Notes, cancellationToken);

            return updated
                ? Ok(new { message = "System failure marked as resolved" })
                : NotFound(new { error = "System failure incident not found." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve system failure {IncidentId}", id);
            return StatusCode(500, new { error = "Failed to resolve system failure incident." });
        }
    }

    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updated = await failureLogs.SetResolvedAsync(
                id, false, CallerEmail, null, cancellationToken);

            return updated
                ? Ok(new { message = "System failure reopened" })
                : NotFound(new { error = "System failure incident not found." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reopen system failure {IncidentId}", id);
            return StatusCode(500, new { error = "Failed to reopen system failure incident." });
        }
    }
}

public sealed record ResolveSystemFailureRequest(string? Notes);
