using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("users")]
public sealed class UsersPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
