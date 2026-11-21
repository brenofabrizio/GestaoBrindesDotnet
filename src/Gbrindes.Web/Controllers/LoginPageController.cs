using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("login")]
public sealed class LoginPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
