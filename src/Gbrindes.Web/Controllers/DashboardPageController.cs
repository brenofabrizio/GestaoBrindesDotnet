using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("dashboard")]
public sealed class DashboardPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
