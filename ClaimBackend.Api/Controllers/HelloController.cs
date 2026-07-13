using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimBackend.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class HelloController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok("this is returned from the API!");
}
