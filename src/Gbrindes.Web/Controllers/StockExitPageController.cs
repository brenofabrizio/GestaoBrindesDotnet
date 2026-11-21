using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock/exit")]
public sealed class StockExitPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
