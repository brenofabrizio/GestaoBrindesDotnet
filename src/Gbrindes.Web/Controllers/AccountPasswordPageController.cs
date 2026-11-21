using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("account/password")]
public sealed class AccountPasswordPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
