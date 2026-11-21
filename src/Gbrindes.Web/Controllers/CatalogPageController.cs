using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("items")]
public sealed class CatalogPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet("{id:guid}")]
    public IActionResult Detail(Guid id)
    {
        ViewData["ItemId"] = id;
        return View();
    }
}
