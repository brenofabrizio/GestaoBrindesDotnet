using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using QRCoder;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/trade-requests")]
public sealed class TradeRequestsController(ApplicationDbContext database, TradeReceivingService receiving,
    TradeWithdrawalService withdrawal, TradeInvoiceStorage invoices) : ControllerBase
{
    [HttpPost("{id:guid}/receive")]
    [Authorize]
    [RequestSizeLimit(TradeInvoiceStorage.MaximumUploadBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = TradeInvoiceStorage.MaximumUploadBytes + 64 * 1024)]
    public async Task<IActionResult> Receive(Guid id, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "stock.receive") && !User.HasClaim("permission", "stock.entry"))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            ReceiveTradeRequest input;
            IFormFile? invoiceFile = null;
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                var itemsJson = form["items"].ToString();
                input = new ReceiveTradeRequest
                {
                    InvoiceNo = form["invoice_no"].ToString(),
                    Items = string.IsNullOrWhiteSpace(itemsJson)
                        ? null
                        : JsonSerializer.Deserialize<List<ReceiveTradeRequestLine>>(itemsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                };
                invoiceFile = form.Files.GetFile("invoice");
            }
            else
            {
                input = await Request.ReadFromJsonAsync<ReceiveTradeRequest>(cancellationToken: ct) ?? new ReceiveTradeRequest();
            }
            var lines = input.Items?.Select(line => new TradeReceiptLine(line.ItemId, line.Quantity)).ToArray();
            var result = await receiving.ReceiveAsync(id, input.InvoiceNo, invoiceFile, lines, userId,
                Request.Headers["Idempotency-Key"].ToString(), ct);
            var data = new
            {
                result.Request.Id,
                result.Request.Code,
                public_code = result.Request.PublicCode,
                result.Request.Flow,
                result.Request.Status,
                invoice_no = result.Request.InvoiceNo,
                invoice_url = result.Request.InvoicePath is null ? null : $"/api/v1/trade-requests/{id}/invoice",
                items = result.Request.Items.OrderBy(item => item.Position).ThenBy(item => item.Id).Select(item => new
                {
                    item_id = item.ItemId,
                    qty_requested = item.QuantityRequested,
                    qty_received = item.QuantityReceived
                })
            };
            return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
                new { ok = true, data });
        }
        catch (TradeReceivingFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
        catch (TradeInvoiceValidationException ex)
        {
            return UnprocessableEntity(new { ok = false, error = new { code = "VALIDATION_ERROR", message = ex.Message } });
        }
        catch (JsonException)
        {
            return UnprocessableEntity(new { ok = false, error = new { code = "VALIDATION_ERROR", message = "O campo items deve conter uma lista JSON válida." } });
        }
        catch (InventoryFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
    }

    [HttpGet("{id:guid}/invoice")]
    [Authorize]
    public async Task<IActionResult> Invoice(Guid id, CancellationToken ct)
    {
        var allowed = new[] { "requests.view_own", "requests.view_department", "requests.view_all", "stock.receive", "stock.exit_confirm" };
        if (!User.IsInRole("admin") && !allowed.Any(permission => User.HasClaim("permission", permission)))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var request = await database.Requests.AsNoTracking()
            .Where(value => value.Id == id && value.DeletedAt == null && value.Flow == "trade")
            .Select(value => new { value.InvoicePath, value.IndustryId, value.RequesterId, value.DepartmentId })
            .SingleOrDefaultAsync(ct);
        if (request is null)
            return NotFound();
        if (!await CanReadTradeRequestAsync(id, request.RequesterId, request.IndustryId, request.DepartmentId, userId, ct))
            return NotFound();

        var invoicePath = request.InvoicePath;
        if (string.IsNullOrWhiteSpace(invoicePath))
            return NotFound();
        var stream = invoices.OpenRead(invoicePath);
        if (stream is null)
            return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        var extension = Path.GetExtension(invoicePath);
        return File(stream, TradeInvoiceStorage.ContentTypeForPath(invoicePath), $"NF-{id:N}{extension}");
    }

    [HttpGet("qr-info")]
    [HttpGet("/api/trade/qr-info")]
    [Authorize]
    public async Task<IActionResult> QrInfo([FromQuery] string? code, CancellationToken ct)
    {
        var allowed = new[] { "requests.view_own", "requests.view_department", "requests.view_all", "stock.receive", "stock.exit_confirm" };
        if (!User.IsInRole("admin") && !allowed.Any(permission => User.HasClaim("permission", permission)))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var lookupCode = code?.Trim();
        if (string.IsNullOrEmpty(lookupCode))
            return UnprocessableEntity(new
            {
                ok = false,
                error = new
                {
                    code = "VALIDATION_ERROR",
                    message = "Verifique os campos destacados.",
                    fields = new Dictionary<string, string> { ["code"] = "Informe o código da solicitação." }
                }
            });

        var request = await database.Requests.AsNoTracking()
            .Where(value => value.DeletedAt == null && value.Flow == "trade"
                && (value.PublicCode == lookupCode || value.Code == lookupCode))
            .Select(value => new
            {
                value.Id,
                value.Code,
                value.PublicCode,
                value.RequesterId,
                value.IndustryId,
                value.DepartmentId,
                IndustryName = value.Industry == null ? null : value.Industry.Name
            })
            .SingleOrDefaultAsync(ct);
        if (request is null)
            return NotFound(new { ok = false, error = new { code = "NOT_FOUND", message = "Solicitação TRADE não encontrada ou sem permissão." } });
        if (!await CanReadTradeRequestAsync(request.Id, request.RequesterId, request.IndustryId, request.DepartmentId, userId, ct))
            return NotFound(new { ok = false, error = new { code = "NOT_FOUND", message = "Solicitação TRADE não encontrada ou sem permissão." } });

        var lines = await database.RequestItems.AsNoTracking()
            .Where(line => line.RequestId == request.Id)
            .OrderBy(line => line.Position).ThenBy(line => line.Id)
            .Select(line => new { line.ItemId, line.Item.Name, line.Item.Code })
            .ToArrayAsync(ct);
        var balances = new Dictionary<Guid, int>();
        if (request.IndustryId is Guid industryId && lines.Length > 0)
        {
            var itemIds = lines.Select(line => line.ItemId).Distinct().ToArray();
            balances = await database.StockMovements.AsNoTracking()
                .Where(movement => itemIds.Contains(movement.ItemId) && movement.IndustryId == industryId
                    && (movement.Type == "entrada" || movement.Type == "saida" || movement.Type == "ajuste" || movement.Type == "estorno"))
                .GroupBy(movement => movement.ItemId)
                .Select(group => new
                {
                    ItemId = group.Key,
                    Balance = group.Sum(movement => movement.Quantity)
                })
                .ToDictionaryAsync(group => group.ItemId, group => group.Balance, ct);
        }

        var products = lines.Select(line => new
        {
            name = line.Name,
            code = line.Code,
            saldo = Math.Max(0, balances.GetValueOrDefault(line.ItemId))
        }).ToArray();
        return Ok(new { ok = true, data = new { code = request.PublicCode ?? request.Code, industry = request.IndustryName, products } });
    }

    [HttpGet("{id:guid}/qr")]
    [Authorize]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct)
    {
        var allowed = new[] { "requests.view_own", "requests.view_department", "requests.view_all", "stock.receive", "stock.exit_confirm" };
        if (!User.IsInRole("admin") && !allowed.Any(permission => User.HasClaim("permission", permission)))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var request = await database.Requests.AsNoTracking()
            .Where(value => value.Id == id && value.DeletedAt == null && value.Flow == "trade")
            .Select(value => new { value.Code, value.PublicCode, value.IndustryId, value.RequesterId, value.DepartmentId })
            .SingleOrDefaultAsync(ct);
        if (request is null)
            return NotFound();
        if (!await CanReadTradeRequestAsync(id, request.RequesterId, request.IndustryId, request.DepartmentId, userId, ct))
            return NotFound();

        using var qrData = QRCodeGenerator.GenerateQrCode(request.PublicCode ?? request.Code, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(5);
        Response.Headers.CacheControl = "private, max-age=120";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(png, "image/png");
    }

    private async Task<bool> CanReadTradeRequestAsync(Guid requestId, Guid requesterId, Guid? industryId,
        Guid? departmentId, Guid userId, CancellationToken ct)
    {
        if (User.IsInRole("admin"))
            return true;
        if (IsCdOperations(User) && (User.HasClaim("permission", "stock.receive") || User.HasClaim("permission", "stock.exit_confirm")))
            return true;

        var userIndustryId = Guid.TryParse(User.FindFirstValue("industry_id"), out var parsedIndustryId)
            ? parsedIndustryId
            : (Guid?)null;
        if (userIndustryId is Guid scopedIndustryId)
            return industryId == scopedIndustryId;
        if (User.HasClaim("permission", "requests.view_all"))
            return true;
        if (requesterId == userId)
            return true;

        var userDepartmentId = Guid.TryParse(User.FindFirstValue("department_id"), out var parsedDepartmentId)
            ? parsedDepartmentId
            : (Guid?)null;
        if (User.HasClaim("permission", "requests.view_department") && userDepartmentId is Guid scopedDepartmentId
            && departmentId == scopedDepartmentId)
            return true;
        if (!User.HasClaim("permission", "requests.approve"))
            return false;

        var roleIds = await database.UserRoles.Where(userRole => userRole.UserId == userId)
            .Select(userRole => userRole.RoleId).ToArrayAsync(ct);
        return await database.RequestApprovals.AnyAsync(approval => approval.RequestId == requestId
            && (approval.ApproverUserId == userId
                || (approval.ApproverRoleId != null && roleIds.Contains(approval.ApproverRoleId.Value))), ct);
    }

    private static bool IsCdOperations(ClaimsPrincipal user)
    {
        if (user.IsInRole("admin"))
            return false;
        return user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)
            .Concat(user.FindAll("role_slug").Select(claim => claim.Value))
            .Any(role => string.Equals(role, "operations", StringComparison.OrdinalIgnoreCase)
                || role.Contains("cd / estoque", StringComparison.OrdinalIgnoreCase)
                || role.Contains("cd/estoque", StringComparison.OrdinalIgnoreCase));
    }

    [HttpPost("{id:guid}/withdraw")]
    [Authorize]
    public async Task<IActionResult> Withdraw(Guid id, TradeWithdrawalRequest input, CancellationToken ct)
    {
        var allowed = new[] { "requests.process", "stock.exit", "events.withdraw", "stock.exit_confirm" };
        if (!allowed.Any(permission => User.HasClaim("permission", permission)))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            var lines = input.Items?.Select(line => new TradeWithdrawalLine(line.ItemId, line.Quantity)).ToArray();
            var result = await withdrawal.WithdrawAsync(id, input.ReceivedByName, input.ReceivedByEmail,
                input.ReceivedByDocument, input.ReceivedByPhone, input.Recipient, input.Signature,
                input.Notes, lines, userId, User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
                HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers["Idempotency-Key"].ToString(), ct);
            var data = new
            {
                result.Delivery.Id,
                result.Delivery.Code,
                result.Delivery.Type,
                request_id = result.Delivery.RequestId,
                request_status = result.RequestStatus,
                industry_id = result.Delivery.IndustryId,
                received_by_name = result.Delivery.ReceivedByName,
                received_by_email = result.Delivery.ReceivedByEmail,
                notes = result.Delivery.Notes,
                created_at = result.Delivery.CreatedAt,
                items = result.Delivery.Items.Select(item => new
                {
                    item_id = item.ItemId,
                    qty = item.Quantity,
                    balance_after = item.BalanceAfter
                })
            };
            return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
                new { ok = true, data });
        }
        catch (TradeWithdrawalFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
        catch (InventoryFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
        catch (DigitalSignatureValidationException ex)
        {
            return UnprocessableEntity(new { ok = false, error = new { code = "VALIDATION_ERROR", message = ex.Message } });
        }
    }

    [HttpPost("{id:guid}/purchase")]
    [Authorize(Policy = "requests.approve")]
    public async Task<IActionResult> Purchase(Guid id, PurchaseTradeRequest input, CancellationToken ct)
    {
        var ticket = input.PurchaseTicketNo?.Trim();
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 50)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["purchase_ticket_no"] = ["Informe um chamado de compra com até 50 caracteres."] }));
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        InternalRequest? request;
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            request = await database.Requests.FromSqlInterpolated(
                $"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'trade' FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            request = await database.Requests.SingleOrDefaultAsync(value => value.Id == id && value.DeletedAt == null && value.Flow == "trade", ct);
        }
        if (request is null)
            return NotFound();
        var actorScope = await database.Users.AsNoTracking().Where(value => value.Id == userId)
            .Select(value => new { value.IndustryId, value.DepartmentId }).SingleOrDefaultAsync(ct);
        if (actorScope?.IndustryId is Guid actorIndustryId && request.IndustryId != actorIndustryId)
            return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = new { code = "INDUSTRY_FORBIDDEN", message = "Você só pode registrar compras da sua indústria." } });
        if (actorScope?.DepartmentId is Guid actorDepartmentId && request.DepartmentId != actorDepartmentId)
            return NotFound();
        if (request.Status != "aprovada")
            return UnprocessableEntity(new { ok = false, error = "INVALID_TRANSITION", message = "Somente solicitações TRADE aprovadas podem registrar a compra." });

        var now = DateTimeOffset.UtcNow;
        var fromStatus = request.Status;
        request.PurchaseTicketNo = ticket;
        request.Status = "aguardando_recebimento";
        request.StatusChangedAt = now;
        request.UpdatedAt = now;
        database.RequestStatusHistory.AddRange(
            new InternalRequestStatusHistory { RequestId = request.Id, FromStatus = fromStatus, ToStatus = "compra_realizada", UserId = userId, Comment = $"Compra registrada. Chamado: {ticket}", CreatedAt = now },
            new InternalRequestStatusHistory { RequestId = request.Id, FromStatus = "compra_realizada", ToStatus = "aguardando_recebimento", UserId = userId, Comment = "Aguardando chegada no CD para recebimento da NF.", CreatedAt = now });
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "purchase",
            EntityType = "request",
            EntityId = request.Id,
            EntityLabel = request.Code,
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { status = fromStatus, purchase_ticket_no = (string?)null }),
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { status = request.Status, purchase_ticket_no = ticket }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });
        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return Ok(new { ok = true, data = new { request.Id, request.Code, request.Flow, request.Status, purchase_ticket_no = request.PurchaseTicketNo } });
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "requests.approve")]
    public async Task<IActionResult> Decide(Guid id, TradeApprovalDecision input, CancellationToken ct)
    {
        var approved = input.Approved == true;
        var justification = input.Justification?.Trim();
        if (justification?.Length > 2000)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["justification"] = ["A justificativa deve conter até 2000 caracteres."] }));
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        InternalRequest? request;
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            request = await database.Requests.FromSqlInterpolated(
                $"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'trade' FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (request is not null)
                await database.Entry(request).Collection(value => value.Approvals).LoadAsync(ct);
        }
        else
        {
            request = await database.Requests.Include(value => value.Approvals)
                .SingleOrDefaultAsync(value => value.Id == id && value.DeletedAt == null && value.Flow == "trade", ct);
        }
        if (request is null)
            return NotFound();
        var actorScope = await database.Users.AsNoTracking().Where(value => value.Id == userId)
            .Select(value => new { value.IndustryId, value.DepartmentId }).SingleOrDefaultAsync(ct);
        if (actorScope?.IndustryId is Guid actorIndustryId && request.IndustryId != actorIndustryId)
            return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = new { code = "INDUSTRY_FORBIDDEN", message = "Você só pode decidir solicitações da sua indústria." } });
        if (actorScope?.DepartmentId is Guid actorDepartmentId && request.DepartmentId != actorDepartmentId)
            return NotFound();
        if (request.Status != "aguardando_aprovacao")
            return UnprocessableEntity(new { ok = false, error = "INVALID_TRANSITION", message = "Esta solicitação TRADE não está aguardando aprovação." });

        var now = DateTimeOffset.UtcNow;
        var fromStatus = request.Status;
        request.Status = approved ? "aprovada" : "reprovada";
        request.StatusChangedAt = now;
        request.UpdatedAt = now;
        if (approved)
            request.ApprovedAt = now;
        else
            request.CancelReason = justification;
        database.RequestApprovals.Add(new RequestApproval
        {
            RequestId = request.Id,
            ApproverUserId = userId,
            DecidedById = userId,
            Decision = approved ? "aprovado" : "reprovado",
            Justification = justification,
            DecidedAt = now,
            CreatedAt = now
        });
        database.RequestStatusHistory.Add(new InternalRequestStatusHistory
        {
            RequestId = request.Id,
            FromStatus = fromStatus,
            ToStatus = request.Status,
            UserId = userId,
            Comment = justification,
            CreatedAt = now
        });
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = approved ? "approve" : "reject",
            EntityType = "request",
            EntityId = request.Id,
            EntityLabel = request.Code,
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { status = fromStatus }),
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { status = request.Status }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });
        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return Ok(new { ok = true, data = new { request.Id, request.Code, request.Flow, request.Status, request.ApprovedAt, request.CancelReason } });
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : Guid.Empty;
        var canProcess = User.HasClaim("permission", "requests.process") || User.IsInRole("admin");
        if (!User.HasClaim("permission", "requests.create") && !canProcess)
            return Forbid();
        if (userId == Guid.Empty)
            return Unauthorized();

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        InternalRequest? request;
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            request = await database.Requests.FromSqlInterpolated(
                $"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'trade' FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            request = await database.Requests.SingleOrDefaultAsync(value => value.Id == id && value.DeletedAt == null && value.Flow == "trade", ct);
        }
        if (request is null)
            return NotFound();
        var actorScope = await database.Users.AsNoTracking().Where(value => value.Id == userId)
            .Select(value => new { value.IndustryId, value.DepartmentId }).SingleOrDefaultAsync(ct);
        if (actorScope?.IndustryId is Guid actorIndustryId && request.IndustryId != actorIndustryId)
            return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = new { code = "INDUSTRY_FORBIDDEN", message = "Você só pode enviar solicitações da sua indústria." } });
        if (actorScope?.DepartmentId is Guid actorDepartmentId && request.DepartmentId != actorDepartmentId)
            return NotFound();
        if (request.RequesterId != userId && !canProcess)
            return Forbid();
        if (request.Status != "rascunho")
            return UnprocessableEntity(new { ok = false, error = "INVALID_TRANSITION", message = "Somente rascunhos TRADE podem ser enviados." });

        var now = DateTimeOffset.UtcNow;
        request.Status = "aguardando_aprovacao";
        request.SubmittedAt = now;
        request.StatusChangedAt = now;
        request.UpdatedAt = now;
        database.RequestStatusHistory.Add(new InternalRequestStatusHistory
        {
            RequestId = request.Id,
            FromStatus = "rascunho",
            ToStatus = request.Status,
            UserId = userId,
            Comment = "Solicitação TRADE enviada para aprovação.",
            CreatedAt = now
        });
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "submit",
            EntityType = "request",
            EntityId = request.Id,
            EntityLabel = request.Code,
            BeforeJson = "{\"status\":\"rascunho\"}",
            AfterJson = "{\"status\":\"aguardando_aprovacao\"}",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });
        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return Ok(new { ok = true, data = new { request.Id, request.Code, request.Flow, request.Status, requester_id = request.RequesterId, submitted_at = request.SubmittedAt } });
    }

    [HttpPost]
    [Authorize(Policy = "requests.create")]
    public async Task<IActionResult> Create(CreateInternalRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Purpose) || input.Purpose.Trim().Length > 255)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["purpose"] = ["A finalidade é obrigatória e deve conter até 255 caracteres."] }));
        if (input.Items is null or { Count: 0 } || input.Items.Any(line => line is null))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["items"] = ["Inclua pelo menos um brinde válido."] }));
        var lines = input.Items.Select(line => line!).ToArray();
        if (lines.Any(line => line.ItemId == Guid.Empty || line.QuantityRequested is < 1 or > 1_000_000)
            || lines.Select(line => line.ItemId).Distinct().Count() != lines.Length)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["items"] = ["Brindes e quantidades inválidos ou duplicados."] }));
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == userId && value.Active && value.DeletedAt == null, ct);
        if (user is null)
            return Unauthorized();
        if (input.IndustryId is Guid requestedIndustry && user.IndustryId is Guid assignedIndustry && requestedIndustry != assignedIndustry)
            return Forbid();
        if (input.DepartmentId is Guid requestedDepartment && user.DepartmentId is Guid assignedDepartment && requestedDepartment != assignedDepartment)
            return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = new { code = "DEPARTMENT_FORBIDDEN", message = "Você só pode criar solicitações para o seu departamento." } });
        var industryId = user.IndustryId ?? input.IndustryId;
        if (industryId is Guid effectiveIndustry && !await database.Industries.AnyAsync(value => value.Id == effectiveIndustry && value.DeletedAt == null, ct))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] }));

        var itemIds = lines.Select(line => line.ItemId).ToArray();
        var catalog = await database.CatalogItems.Where(item => itemIds.Contains(item.Id) && item.DeletedAt == null && item.Status == "ativo")
            .ToDictionaryAsync(item => item.Id, ct);
        if (catalog.Count != itemIds.Length)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["items"] = ["Um ou mais brindes estão inválidos ou inativos."] }));
        var stocks = await database.Stocks.AsNoTracking().Where(stock => itemIds.Contains(stock.ItemId)).ToDictionaryAsync(stock => stock.ItemId, ct);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        var publicCode = $"EME-{now:yyyy}-{await NextTradeSequenceAsync(now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), ct):D6}";
        var id = Guid.NewGuid();
        var total = decimal.Round(lines.Sum(line => checked((catalog[line.ItemId].UnitValue ?? 0m) * line.QuantityRequested)), 2, MidpointRounding.AwayFromZero);
        var request = new InternalRequest
        {
            Id = id,
            Code = publicCode,
            PublicCode = publicCode,
            RequesterId = userId,
            DepartmentId = input.DepartmentId ?? user.DepartmentId,
            IndustryId = industryId,
            Flow = "trade",
            Status = "rascunho",
            Purpose = input.Purpose.Trim(),
            Recipient = string.IsNullOrWhiteSpace(input.Recipient) ? null : input.Recipient.Trim(),
            NeededDate = input.NeededDate,
            Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
            TotalValue = total,
            NeedsPurchase = lines.Any(line => line.QuantityRequested > (stocks.TryGetValue(line.ItemId, out var stock) ? stock.OnHand - stock.Reserved : 0)),
            CreatedAt = now,
            UpdatedAt = now,
            Items = lines.Select((line, position) => new InternalRequestItem
            {
                RequestId = id,
                ItemId = line.ItemId,
                Position = position,
                QuantityRequested = line.QuantityRequested,
                UnitValue = catalog[line.ItemId].UnitValue,
                Notes = string.IsNullOrWhiteSpace(line.Notes) ? null : line.Notes.Trim()
            }).ToList(),
            History = [new InternalRequestStatusHistory { RequestId = id, ToStatus = "rascunho", UserId = userId, Comment = "Solicitação TRADE criada.", CreatedAt = now }]
        };
        database.Requests.Add(request);
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "create",
            EntityType = "request",
            EntityId = id,
            EntityLabel = request.Code,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { code = request.Code, flow = request.Flow }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = now
        });
        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new { ok = true, data = new { request.Id, request.Code, public_code = request.PublicCode, request.Flow, request.Status, requester_id = userId, request.DepartmentId, request.IndustryId, request.Purpose, request.Recipient, request.NeededDate, needs_purchase = request.NeedsPurchase, total_value = request.TotalValue } });
    }

    private async Task<int> NextTradeSequenceAsync(string period, CancellationToken ct)
    {
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var connection = database.Database.GetDbConnection();
            var closeConnection = connection.State != ConnectionState.Open;
            if (closeConnection)
                await database.Database.OpenConnectionAsync(ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO "Sequences" ("Name", "Period", "LastValue") VALUES ('trade', @period, 1)
                    ON CONFLICT ("Name", "Period") DO UPDATE SET "LastValue" = "Sequences"."LastValue" + 1
                    RETURNING "LastValue"
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "period";
                parameter.Value = period;
                command.Parameters.Add(parameter);
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (closeConnection)
                    await database.Database.CloseConnectionAsync();
            }
        }

        var counter = await database.SequenceCounters.SingleOrDefaultAsync(value => value.Name == "trade" && value.Period == period, ct);
        if (counter is null)
        {
            database.SequenceCounters.Add(new SequenceCounter { Name = "trade", Period = period, LastValue = 1 });
            return 1;
        }
        counter.LastValue = checked(counter.LastValue + 1);
        return counter.LastValue;
    }
}

public sealed class TradeApprovalDecision
{
    [System.ComponentModel.DataAnnotations.Required, System.Text.Json.Serialization.JsonPropertyName("approved")] public bool? Approved { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("justification")] public string? Justification { get; set; }
}

public sealed class PurchaseTradeRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("purchase_ticket_no")] public string? PurchaseTicketNo { get; set; }
}

public sealed class ReceiveTradeRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("invoice_no")] public string? InvoiceNo { get; set; }
    public List<ReceiveTradeRequestLine>? Items { get; set; }
}

public sealed class ReceiveTradeRequestLine
{
    [System.Text.Json.Serialization.JsonPropertyName("item_id")] public Guid ItemId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("qty")] public int Quantity { get; set; }
}

public sealed class TradeWithdrawalRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("received_by_name")] public string? ReceivedByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("received_by_email")] public string? ReceivedByEmail { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("received_by_document")] public string? ReceivedByDocument { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("received_by_phone")] public string? ReceivedByPhone { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("recipient")] public string? Recipient { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("signature")] public string? Signature { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("notes")] public string? Notes { get; set; }
    public List<TradeWithdrawalRequestLine>? Items { get; set; }
}

public sealed class TradeWithdrawalRequestLine
{
    [System.Text.Json.Serialization.JsonPropertyName("item_id")] public Guid ItemId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("qty")] public int Quantity { get; set; }
}

