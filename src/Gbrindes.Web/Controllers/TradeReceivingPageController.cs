using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("recebimento")]
public sealed class TradeReceivingPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
