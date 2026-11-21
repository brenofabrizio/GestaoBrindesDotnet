using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
public sealed class TradeQrInfoPageController : Controller
{
    [HttpGet("/trade/lookup")]
    public IActionResult Lookup([FromQuery] string? code = null) => Render(code);

    [HttpGet("/consulta-trade/{code}")]
    public IActionResult Legacy(string code) => Render(code);

    private IActionResult Render(string? code)
    {
        ViewData["Code"] = code ?? string.Empty;
        return View("~/Views/TradeQrInfoPage/Index.cshtml");
    }
}
