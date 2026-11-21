using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock/entry")]
public sealed class StockEntryPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
