using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("")]
public sealed class PasswordRecoveryPageController : Controller
{
    [HttpGet("esqueci-senha")]
    public IActionResult Forgot() => View("Forgot");

    [HttpGet("redefinir-senha")]
    public IActionResult Reset() => View("Reset");
}
