using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock/transfer")]
public sealed class StockTransferPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
