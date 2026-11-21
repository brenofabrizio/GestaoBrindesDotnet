using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[AllowAnonymous]
[Route("requests")]
public sealed class RequestsPageController : Controller
{
    [HttpGet]
    [HttpGet("~/solicitacoes")]
    public IActionResult Index() => View();

    [HttpGet("new")]
    [HttpGet("~/solicitacoes/nova")]
    public IActionResult Create() => View("Create");

    [HttpGet("{id:guid}/edit")]
    [HttpGet("~/solicitacoes/{id:guid}/editar")]
    public IActionResult Edit(Guid id)
    {
        ViewData["RequestId"] = id;
        ViewData["IsEdit"] = true;
        return View("Create");
    }

    [HttpGet("{id:guid}")]
    [HttpGet("~/solicitacoes/{id:guid}")]
    public IActionResult Detail(Guid id)
    {
        ViewData["RequestId"] = id;
        return View("Index");
    }

    [HttpGet("~/operacao/{id:guid}/entrega")]
    public IActionResult Deliver(Guid id)
    {
        ViewData["RequestId"] = id;
        return View("Deliver");
    }
}
