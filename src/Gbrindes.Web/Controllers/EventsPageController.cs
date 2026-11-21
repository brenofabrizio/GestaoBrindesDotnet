using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("events")]
public sealed class EventsPageController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet("{id:guid}")]
    public IActionResult Detail(Guid id)
    {
        ViewData["EventId"] = id;
        return View();
    }
}
