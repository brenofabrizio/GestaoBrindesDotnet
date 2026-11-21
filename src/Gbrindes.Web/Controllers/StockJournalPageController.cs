using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("stock")]
public sealed class StockJournalPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();
}
