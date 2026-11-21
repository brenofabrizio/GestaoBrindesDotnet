using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("roles")]
public sealed class RolesPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
