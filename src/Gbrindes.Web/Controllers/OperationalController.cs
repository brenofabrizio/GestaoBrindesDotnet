using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1")]
public sealed class OperationalController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new { status = "ok", service = "gbrindes-dotnet" });

    [HttpGet("readiness")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Readiness(CancellationToken cancellationToken)
    {
        if (database.Database.IsRelational()
            && string.IsNullOrWhiteSpace(database.Database.GetConnectionString()))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }

        try
        {
            if (await database.Database.CanConnectAsync(cancellationToken))
            {
                return Ok(new { status = "ready" });
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Do not expose provider errors, hosts, credentials, or connection details.
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
    }
}
