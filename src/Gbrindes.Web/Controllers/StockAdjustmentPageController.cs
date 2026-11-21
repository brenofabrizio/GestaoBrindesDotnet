using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock/adjust")]
public sealed class StockAdjustmentPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
