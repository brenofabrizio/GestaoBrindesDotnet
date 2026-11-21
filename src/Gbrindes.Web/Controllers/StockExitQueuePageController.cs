using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock/queue")]
public sealed class StockExitQueuePageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
