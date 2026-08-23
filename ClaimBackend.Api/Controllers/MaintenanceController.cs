using System.Security.Cryptography;
using System.Text;
using ClaimBackend.Api.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ClaimBackend.Api.Controllers;

public class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    /// <summary>
    /// Shared secret the scheduler presents. Empty means the endpoint does not exist — this is
    /// the same app behind the same public API Gateway as everything else, so without a key
    /// anyone could drive the game clock.
    /// </summary>
    public string Key { get; set; } = string.Empty;
}

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class MaintenanceController(
    GameSweeper sweeper,
    IOptions<MaintenanceOptions> options) : ControllerBase
{
    private const string KeyHeader = "X-Maintenance-Key";

    /// <summary>
    /// Brings every running game's clock forward and notifies on what moved. Called on a
    /// schedule, because the hot borough rotating is worth hearing about exactly when nobody has
    /// the app open to advance it themselves.
    /// </summary>
    [HttpPost("sweep")]
    public async Task<ActionResult<SweepResult>> Sweep(CancellationToken cancellationToken)
    {
        var expected = options.Value.Key;

        if (string.IsNullOrWhiteSpace(expected))
        {
            // Not configured: behave as though the route was never mapped.
            return NotFound();
        }

        if (!IsAuthorised(expected))
        {
            return Unauthorized();
        }

        return await sweeper.SweepAsync(cancellationToken);
    }

    /// <summary>Compared in constant time so the key can't be recovered by timing the replies.</summary>
    private bool IsAuthorised(string expected)
    {
        if (!Request.Headers.TryGetValue(KeyHeader, out var supplied))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied.ToString()),
            Encoding.UTF8.GetBytes(expected));
    }
}
