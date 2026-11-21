using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("notificacoes")]
public sealed class NotificationsPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
