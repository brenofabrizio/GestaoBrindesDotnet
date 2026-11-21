using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("import")]
public sealed class ImportPageController : Controller
{
    [HttpGet("items")]
    public IActionResult Items() => View();
}
