using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock/by-industry")]
public sealed class StockByIndustryPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
