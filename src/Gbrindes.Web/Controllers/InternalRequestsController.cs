using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Gbrindes.Web.Imports;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/requests")]
public sealed class InternalRequestsController(
    ApplicationDbContext database,
    ImportPreviewService importPreviews,
    InternalRequestDeliveryService requestDeliveries) : ControllerBase
{
    private static readonly Dictionary<string, string> StatusLabels = new(StringComparer.Ordinal)
    {
        ["rascunho"] = "Rascunho",
        ["solicitada"] = "Solicitada",
        ["aguardando_aprovacao"] = "Aguardando aprovação",
        ["aprovada"] = "Aprovada",
        ["em_separacao"] = "Em separação",
        ["pronta"] = "Pronto para retirada",
        ["finalizada"] = "Entregue / Finalizada",
        ["reprovada"] = "Reprovada",
        ["cancelada"] = "Cancelada",
        ["compra_realizada"] = "Compra realizada",
        ["aguardando_recebimento"] = "Aguardando recebimento",
        ["recebido_cd"] = "Recebido no CD",
        ["retirado"] = "Retirado",
        ["entregue"] = "Entregue"
    };

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery(Name = "status_in")] string? statusIn,
        [FromQuery(Name = "department_id")] Guid? departmentId,
        [FromQuery(Name = "industry_id")] Guid? industryId,
        [FromQuery(Name = "requester_id")] Guid? requesterId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int page = 1,
        [FromQuery(Name = "per_page")] int perPage = 25,
        CancellationToken ct = default)
    {
        var canViewAll = User.HasClaim("permission", "requests.view_all") || User.IsInRole("admin");
        var canViewOwn = User.HasClaim("permission", "requests.view_own");
        var canViewDepartment = User.HasClaim("permission", "requests.view_department");
        var canApprove = User.HasClaim("permission", "requests.approve");
        if (!canViewAll && !canViewOwn && !canViewDepartment && !canApprove)
            return Forbid();

        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : Guid.Empty;
        if (!canViewAll && userId == Guid.Empty)
            return Unauthorized();

        Guid? userDepartmentId = Guid.TryParse(User.FindFirstValue("department_id"), out var parsedDepartmentId)
            ? parsedDepartmentId
            : null;
        Guid? userIndustryId = Guid.TryParse(User.FindFirstValue("industry_id"), out var parsedIndustryId)
            ? parsedIndustryId
            : null;
        var scopedQuery = database.Requests.AsNoTracking()
            .Where(request => request.DeletedAt == null && request.Status != "rascunho" && request.Flow == "interna");
        if (userIndustryId is Guid scopedIndustryId)
        {
            scopedQuery = scopedQuery.Where(request => request.IndustryId == scopedIndustryId);
        }
        else if (!canViewAll)
        {
            if (canApprove)
            {
                var roleIds = await database.UserRoles.Where(userRole => userRole.UserId == userId)
                    .Select(userRole => userRole.RoleId).ToArrayAsync(ct);
                scopedQuery = scopedQuery.Where(request => request.RequesterId == userId ||
                    (canViewDepartment && userDepartmentId != null && request.DepartmentId == userDepartmentId) ||
                    (canApprove && request.Approvals.Any(approval => approval.ApproverUserId == userId ||
                        (approval.ApproverRoleId != null && roleIds.Contains(approval.ApproverRoleId.Value)))));
            }
            else
            {
                scopedQuery = scopedQuery.Where(request => request.RequesterId == userId ||
                    (canViewDepartment && userDepartmentId != null && request.DepartmentId == userDepartmentId));
            }
        }

        var groupedCounts = await scopedQuery.GroupBy(request => request.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(ct);
        var counts = StatusLabels.Keys.ToDictionary(statusCode => statusCode, _ => 0, StringComparer.Ordinal);
        counts["all"] = 0;
        foreach (var group in groupedCounts)
        {
            if (counts.ContainsKey(group.Status))
                counts[group.Status] = group.Count;
            counts["all"] += group.Count;
        }

        var query = scopedQuery;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLowerInvariant();
            query = query.Where(request => request.Code.ToLower().Contains(search)
                || request.Purpose.ToLower().Contains(search)
                || (request.Recipient != null && request.Recipient.ToLower().Contains(search))
                || request.Requester.FullName.ToLower().Contains(search));
        }
        if (status is not null && StatusLabels.ContainsKey(status))
            query = query.Where(request => request.Status == status);
        if (!string.IsNullOrWhiteSpace(statusIn))
        {
            var allowedStatuses = statusIn.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(StatusLabels.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
            if (allowedStatuses.Length > 0)
                query = query.Where(request => allowedStatuses.Contains(request.Status));
        }
        if (departmentId is not null)
            query = query.Where(request => request.DepartmentId == departmentId);
        if (industryId is not null)
            query = query.Where(request => request.IndustryId == industryId);
        if (requesterId is not null)
            query = query.Where(request => request.RequesterId == requesterId);
        if (DateOnly.TryParse(from, out var fromDate))
        {
            var start = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(request => request.CreatedAt >= start);
        }
        if (DateOnly.TryParse(to, out var toDate))
        {
            if (toDate == DateOnly.MaxValue)
                query = query.Where(request => request.CreatedAt <= DateTimeOffset.MaxValue);
            else
            {
                var end = new DateTimeOffset(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                query = query.Where(request => request.CreatedAt < end);
            }
        }

        page = Math.Clamp(page, 1, 1_000_000);
        perPage = Math.Clamp(perPage, 1, 100);
        var total = await query.CountAsync(ct);
        var requests = await query
            .Include(request => request.Requester)
            .Include(request => request.Department)
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync(ct);
        var data = requests.Select(request => new
        {
            id = request.Id,
            code = request.Code,
            status = request.Status,
            status_label = StatusLabels.GetValueOrDefault(request.Status, request.Status),
            requester_id = request.RequesterId,
            requester = new { id = request.RequesterId, name = request.Requester.FullName, email = request.Requester.Email },
            department = request.DepartmentId is null ? null : new { id = request.DepartmentId.Value, name = request.Department?.Name },
            industry_id = request.IndustryId,
            purpose = request.Purpose,
            recipient = request.Recipient,
            needed_date = request.NeededDate,
            needs_purchase = request.NeedsPurchase,
            total_value = request.TotalValue,
            notes = request.Notes,
            flow = request.Flow,
            created_at = request.CreatedAt,
            submitted_at = request.SubmittedAt,
            approved_at = request.ApprovedAt,
            updated_at = request.UpdatedAt
        }).ToArray();

        return Ok(new { ok = true, data, meta = new { page, per_page = perPage, total, counts } });
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = "requests.create")]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : Guid.Empty;
        if (userId == Guid.Empty)
            return Unauthorized();

        return await SubmitCoreAsync(id, userId, ct);
    }

    private async Task<IActionResult> SubmitCoreAsync(Guid id, Guid userId, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        var ownsTransaction = database.Database.IsRelational() && database.Database.CurrentTransaction is null;
        if (ownsTransaction)
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            InternalRequest? request;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                request = await database.Requests
                    .FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'interna' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                if (request is not null)
                {
                    await database.Entry(request).Collection(x => x.Items).Query().Include(x => x.Item).LoadAsync(ct);
                    await database.Entry(request).Collection(x => x.History).LoadAsync(ct);
                }
            }
            else
            {
                request = await database.Requests
                    .Include(x => x.Items).ThenInclude(x => x.Item)
                    .Include(x => x.History)
                    .SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null && x.Flow == "interna", ct);
            }

            if (request is null)
                return NotFound();
            if (request.RequesterId != userId)
                return Forbid();
            if (request.Status != "rascunho")
                return UnprocessableEntity(new { ok = false, error = "ALREADY_SUBMITTED", message = "Somente rascunhos podem ser enviados." });
            if (request.Items.Count == 0)
                return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Inclua pelo menos um brinde."] });

            var now = DateTimeOffset.UtcNow;
            var categoryIds = new HashSet<Guid>();
            foreach (var line in request.Items.OrderBy(item => item.ItemId))
            {
                CatalogItem? item;
                if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    item = await database.CatalogItems
                        .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {line.ItemId} FOR UPDATE")
                        .SingleOrDefaultAsync(ct);
                }
                else
                {
                    item = await database.CatalogItems.SingleOrDefaultAsync(x => x.Id == line.ItemId, ct);
                }

                if (item is null || item.DeletedAt is not null || item.Status != "ativo")
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Um ou mais brindes estão inválidos ou inativos."] });
                line.Item = item;
                categoryIds.Add(item.CategoryId);
            }

            var requesterRoleIds = await database.UserRoles.Where(userRole => userRole.UserId == request.RequesterId)
                .Select(userRole => userRole.RoleId).ToHashSetAsync(ct);
            var matchedRule = await database.ApprovalRules.AsNoTracking()
                .Where(rule => rule.Active && rule.DeletedAt == null)
                .OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id)
                .ToListAsync(ct);
            var applicableRule = matchedRule.FirstOrDefault(rule => MatchesRule(rule, request, requesterRoleIds, categoryIds));
            if (applicableRule is not null)
            {
                var stockByItem = await database.Stocks.AsNoTracking()
                    .Where(stock => request.Items.Select(item => item.ItemId).Contains(stock.ItemId))
                    .ToDictionaryAsync(stock => stock.ItemId, ct);
                request.NeedsPurchase = request.Items.Any(line =>
                {
                    var available = stockByItem.TryGetValue(line.ItemId, out var stock) ? stock.OnHand - stock.Reserved : 0;
                    return line.QuantityRequested > available;
                });
                request.Status = "aguardando_aprovacao";
                request.SubmittedAt = now;
                request.StatusChangedAt = now;
                request.UpdatedAt = now;
                database.RequestApprovals.Add(new RequestApproval
                {
                    RequestId = request.Id,
                    RuleId = applicableRule.Id,
                    ApproverRoleId = applicableRule.ApproverRoleId,
                    ApproverUserId = applicableRule.ApproverUserId,
                    Decision = "pendente",
                    CreatedAt = now
                });
                database.RequestStatusHistory.Add(new InternalRequestStatusHistory
                {
                    RequestId = request.Id,
                    FromStatus = "rascunho",
                    ToStatus = "aguardando_aprovacao",
                    UserId = userId,
                    Comment = $"Aprovação pendente pela regra '{applicableRule.Name}'.",
                    CreatedAt = now
                });
                AddRequestAudit(request, "submit", new { status = "rascunho" }, new { status = request.Status }, userId, now);
                var directApproverId = applicableRule.ApproverUserId;
                var approverRoleId = applicableRule.ApproverRoleId;
                var approverIds = await database.Users
                    .Where(candidate => candidate.Active && candidate.DeletedAt == null
                        && ((directApproverId.HasValue && candidate.Id == directApproverId.Value)
                            || (approverRoleId.HasValue && database.UserRoles.Any(userRole =>
                                userRole.UserId == candidate.Id && userRole.RoleId == approverRoleId.Value))))
                    .Select(candidate => candidate.Id)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var approverId in approverIds.Where(candidateId => candidateId != request.RequesterId))
                {
                    database.Notifications.Add(new Notification
                    {
                        UserId = approverId,
                        Channel = "in_app",
                        Subject = "Nova solicitação aguardando aprovação",
                        Body = $"A solicitação {request.Code} precisa da sua aprovação.",
                        LinkUrl = $"/solicitacoes/{request.Id}",
                        RelatedType = "request",
                        RelatedId = request.Id,
                        Status = "enviado",
                        DedupeKey = $"request-submit-{request.Id:N}-approver-{approverId:N}",
                        SentAt = now,
                        CreatedAt = now
                    });
                }
                await AddRequestProcessingNotificationsAsync(request, now, ct);
                await database.SaveChangesAsync(ct);
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return Ok(new { ok = true, data = ToDetailsView(request) });
            }

            var needsPurchase = false;
            foreach (var line in request.Items.OrderBy(item => item.ItemId))
            {
                var item = line.Item;
                var stock = await database.Stocks.SingleOrDefaultAsync(x => x.ItemId == line.ItemId, ct);
                if (stock is null)
                {
                    stock = new InventoryStock { ItemId = line.ItemId, Item = item, UpdatedAt = now };
                    database.Stocks.Add(stock);
                }
                var available = stock.OnHand - stock.Reserved;
                if (available < 0)
                    return Conflict(new { error = "STOCK_INVALID", message = "O saldo reservado excede o estoque disponível." });

                line.QuantityApproved = line.QuantityRequested;
                var reserved = Math.Min(line.QuantityApproved.Value, available);
                line.QuantityReserved = reserved;
                stock.Reserved = checked(stock.Reserved + reserved);
                stock.UpdatedAt = now;
                needsPurchase |= reserved < line.QuantityApproved.Value;
                line.Item = item;
            }

            request.Status = "aprovada";
            request.SubmittedAt = now;
            request.ApprovedAt = now;
            request.StatusChangedAt = now;
            request.UpdatedAt = now;
            request.NeedsPurchase = needsPurchase;
            var statusHistory = new InternalRequestStatusHistory
            {
                RequestId = request.Id,
                FromStatus = "rascunho",
                ToStatus = "aprovada",
                UserId = userId,
                Comment = "Aprovação automática: nenhuma regra aplicável.",
                CreatedAt = now
            };
            database.RequestStatusHistory.Add(statusHistory);
            AddRequestAudit(request, "submit", new { status = "rascunho" }, new { status = request.Status }, userId, now);
            await AddRequestProcessingNotificationsAsync(request, now, ct);
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = ToDetailsView(request) });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "requests.approve")]
    public Task<IActionResult> Approve(Guid id, ApprovalDecision input, CancellationToken ct) => DecideApproval(id, true, input, ct);

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "requests.approve")]
    public Task<IActionResult> Reject(Guid id, ApprovalDecision input, CancellationToken ct) => DecideApproval(id, false, input, ct);

    private async Task<IActionResult> DecideApproval(Guid id, bool approve, ApprovalDecision input, CancellationToken ct)
    {
        var justification = input.Justification?.Trim();
        if (string.IsNullOrWhiteSpace(justification) || justification.Length < 3 || justification.Length > 1000)
            return ValidationProblem(new Dictionary<string, string[]> { ["justification"] = ["A justificativa deve conter entre 3 e 1000 caracteres."] });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            InternalRequest? request;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                request = await database.Requests
                    .FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'interna' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                if (request is not null)
                {
                    await database.Entry(request).Collection(entity => entity.Items).Query().Include(item => item.Item).LoadAsync(ct);
                    await database.Entry(request).Collection(entity => entity.History).LoadAsync(ct);
                    await database.Entry(request).Collection(entity => entity.Approvals).LoadAsync(ct);
                }
            }
            else
            {
                request = await database.Requests
                    .Include(entity => entity.Items).ThenInclude(item => item.Item)
                    .Include(entity => entity.History)
                    .Include(entity => entity.Approvals)
                    .SingleOrDefaultAsync(entity => entity.Id == id && entity.DeletedAt == null && entity.Flow == "interna", ct);
            }

            if (request is null)
                return NotFound();
            if (request.Status != "aguardando_aprovacao")
                return UnprocessableEntity(new { ok = false, error = "INVALID_TRANSITION", message = "A solicitação não está aguardando aprovação." });
            var pending = request.Approvals.SingleOrDefault(approval => approval.Decision == "pendente");
            if (pending is null)
                return Conflict(new { ok = false, error = "APPROVAL_MISSING", message = "Não há uma aprovação pendente associada à solicitação." });
            var isAdmin = User.IsInRole("admin");
            if (request.RequesterId == userId)
                return Forbid();
            var assigned = isAdmin || pending.ApproverUserId == userId;
            if (!assigned && pending.ApproverRoleId is Guid approverRoleId)
                assigned = await database.UserRoles.AnyAsync(userRole => userRole.UserId == userId && userRole.RoleId == approverRoleId, ct);
            if (!assigned)
                return Forbid();

            var now = DateTimeOffset.UtcNow;
            var fromStatus = request.Status;
            var approvedQuantities = new Dictionary<Guid, int>();
            if (approve && input.Items is { Count: > 0 })
            {
                if (input.Items.Any(line => line is null))
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["A lista de quantidades aprovadas é inválida."] });
                var requestedByItem = request.Items.ToDictionary(line => line.ItemId, line => line.QuantityRequested);
                foreach (var line in input.Items.Select(line => line!))
                {
                    if (line.ItemId == Guid.Empty || line.QuantityApproved is null || !requestedByItem.TryGetValue(line.ItemId, out var requested))
                        return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Informe brinde e quantidade aprovada válidos."] });
                    if (line.QuantityApproved < 0 || line.QuantityApproved > requested)
                        return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["A quantidade aprovada não pode ser negativa nem exceder a solicitada."] });
                    if (!approvedQuantities.TryAdd(line.ItemId, line.QuantityApproved.Value))
                        return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["O mesmo brinde não pode aparecer duas vezes."] });
                }
            }
            if (approve)
            {
                var needsPurchase = false;
                foreach (var line in request.Items.OrderBy(item => item.ItemId))
                {
                    CatalogItem? item;
                    if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                        item = await database.CatalogItems.FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {line.ItemId} FOR UPDATE").SingleOrDefaultAsync(ct);
                    else
                        item = line.Item;
                    if (item is null || item.DeletedAt is not null || item.Status != "ativo")
                        return Conflict(new { ok = false, error = "ITEM_UNAVAILABLE", message = "Um brinde da solicitação está inativo ou indisponível." });

                    var stock = await database.Stocks.SingleOrDefaultAsync(entity => entity.ItemId == line.ItemId, ct);
                    if (stock is null)
                    {
                        stock = new InventoryStock { ItemId = line.ItemId, Item = item, UpdatedAt = now };
                        database.Stocks.Add(stock);
                    }
                    var available = stock.OnHand - stock.Reserved;
                    if (available < 0)
                        return Conflict(new { ok = false, error = "STOCK_INVALID", message = "O saldo reservado excede o estoque disponível." });
                    line.Item = item;
                    var approvedQuantity = approvedQuantities.TryGetValue(line.ItemId, out var specifiedQuantity)
                        ? specifiedQuantity
                        : line.QuantityRequested;
                    line.QuantityApproved = approvedQuantity;
                    line.QuantityReserved = Math.Min(approvedQuantity, available);
                    stock.Reserved = checked(stock.Reserved + line.QuantityReserved);
                    stock.UpdatedAt = now;
                    needsPurchase |= line.QuantityReserved < approvedQuantity;
                }
                request.Status = "aprovada";
                request.ApprovedAt = now;
                request.NeedsPurchase = needsPurchase;
            }
            else
            {
                request.Status = "reprovada";
                request.CancelReason = justification.Length > 255 ? justification[..255] : justification;
                foreach (var line in request.Items)
                {
                    line.QuantityApproved = 0;
                    line.QuantityReserved = 0;
                }
            }
            request.StatusChangedAt = now;
            request.UpdatedAt = now;
            pending.Decision = approve ? "aprovado" : "reprovado";
            pending.Justification = justification;
            pending.DecidedById = userId;
            pending.DecidedAt = now;
            database.RequestStatusHistory.Add(new InternalRequestStatusHistory
            {
                RequestId = request.Id,
                FromStatus = fromStatus,
                ToStatus = request.Status,
                UserId = userId,
                Comment = justification,
                CreatedAt = now
            });
            AddRequestAudit(request, approve ? "approve" : "reject", new { status = fromStatus }, new { status = request.Status }, userId, now);
            var decisionText = approve ? "aprovada" : "reprovada";
            database.Notifications.Add(new Notification
            {
                UserId = request.RequesterId,
                Channel = "in_app",
                Subject = $"Solicitação {request.Code} {decisionText}",
                Body = approve
                    ? $"Sua solicitação {request.Code} foi aprovada."
                    : $"Sua solicitação {request.Code} foi reprovada. Motivo: {justification}",
                LinkUrl = $"/solicitacoes/{request.Id}",
                RelatedType = "request",
                RelatedId = request.Id,
                Status = "enviado",
                SentAt = now,
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = ToDetailsView(request) });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task AddRequestProcessingNotificationsAsync(InternalRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var recipients = await database.Users
            .Where(candidate => candidate.Active && candidate.DeletedAt == null
                && (database.UserClaims.Any(claim => claim.UserId == candidate.Id
                        && claim.ClaimType == "permission" && claim.ClaimValue == "requests.process")
                    || database.UserRoles.Any(userRole => userRole.UserId == candidate.Id
                        && (database.RoleClaims.Any(claim => claim.RoleId == userRole.RoleId
                                && claim.ClaimType == "permission" && claim.ClaimValue == "requests.process")
                            || database.Roles.Any(role => role.Id == userRole.RoleId && role.NormalizedName == "ADMIN")))))
            .Select(candidate => candidate.Id)
            .Distinct()
            .ToListAsync(ct);

        foreach (var recipientId in recipients)
        {
            database.Notifications.Add(new Notification
            {
                UserId = recipientId,
                Channel = "in_app",
                Subject = "Nova solicitação",
                Body = $"A solicitação {request.Code} foi enviada.",
                LinkUrl = $"/solicitacoes/{request.Id}",
                RelatedType = "request",
                RelatedId = request.Id,
                Status = "enviado",
                SentAt = now,
                CreatedAt = now
            });
        }
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    public async Task<IActionResult> Cancel(Guid id, CancelInternalRequest input, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? "Cancelada" : input.Reason.Trim();
        if (reason.Length > 255)
            return ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["O motivo deve conter até 255 caracteres."] });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var canCancelAny = User.HasClaim("permission", "requests.cancel_any") || User.IsInRole("admin");
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            InternalRequest? request;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                request = await database.Requests
                    .FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'interna' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                if (request is not null)
                {
                    await database.Entry(request).Collection(entity => entity.Items).Query().Include(item => item.Item).LoadAsync(ct);
                    await database.Entry(request).Collection(entity => entity.History).LoadAsync(ct);
                }
            }
            else
            {
                request = await database.Requests
                    .Include(entity => entity.Items).ThenInclude(item => item.Item)
                    .Include(entity => entity.History)
                    .SingleOrDefaultAsync(entity => entity.Id == id && entity.DeletedAt == null && entity.Flow == "interna", ct);
            }

            if (request is null)
                return NotFound();
            var actorScope = await database.Users.AsNoTracking().Where(value => value.Id == userId)
                .Select(value => new { value.IndustryId, value.DepartmentId }).SingleOrDefaultAsync(ct);
            if (actorScope?.IndustryId is Guid actorIndustryId && request.IndustryId != actorIndustryId)
                return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = new { code = "INDUSTRY_FORBIDDEN", message = "Você só pode cancelar solicitações da sua indústria." } });
            if (actorScope?.DepartmentId is Guid actorDepartmentId && request.DepartmentId != actorDepartmentId)
                return NotFound();
            if (!canCancelAny && request.RequesterId != userId)
                return Forbid();
            if (!canCancelAny && request.Status is "aprovada" or "em_separacao")
                return Forbid();
            if (request.Status is not ("rascunho" or "solicitada" or "aguardando_aprovacao" or "aprovada" or "em_separacao" or "pronta" or "compra_realizada" or "aguardando_recebimento" or "recebido_cd"))
                return UnprocessableEntity(new { ok = false, error = "INVALID_TRANSITION", message = "Esta solicitação não pode ser cancelada no status atual." });

            var now = DateTimeOffset.UtcNow;
            var releasePlans = new List<(InternalRequestItem Line, InventoryStock Stock, int Quantity)>();
            foreach (var line in request.Items.OrderBy(item => item.ItemId))
            {
                var release = line.QuantityReserved - line.QuantityDelivered;
                if (release <= 0)
                    continue;
                if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    var lockedItem = await database.CatalogItems
                        .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {line.ItemId} FOR UPDATE")
                        .SingleOrDefaultAsync(ct);
                    if (lockedItem is null)
                        return Conflict(new { ok = false, error = "ITEM_UNAVAILABLE", message = "Não foi possível localizar o brinde para liberar a reserva." });
                    line.Item = lockedItem;
                }
                var stock = await database.Stocks.SingleOrDefaultAsync(entity => entity.ItemId == line.ItemId, ct);
                if (stock is null || stock.Reserved < release)
                    return Conflict(new { ok = false, error = "RESERVATION_INVALID", message = "A reserva do estoque não corresponde à quantidade da solicitação." });
                releasePlans.Add((line, stock, release));
            }

            foreach (var plan in releasePlans)
            {
                plan.Stock.Reserved -= plan.Quantity;
                plan.Stock.UpdatedAt = now;
                plan.Line.QuantityReserved = plan.Line.QuantityDelivered;
            }
            var fromStatus = request.Status;
            request.Status = "cancelada";
            request.CancelledAt = now;
            request.CancelReason = reason;
            request.StatusChangedAt = now;
            request.UpdatedAt = now;
            database.RequestStatusHistory.Add(new InternalRequestStatusHistory
            {
                RequestId = request.Id,
                FromStatus = fromStatus,
                ToStatus = "cancelada",
                UserId = userId,
                Comment = reason,
                CreatedAt = now
            });
            AddRequestAudit(request, "cancel", new { status = fromStatus }, new { status = request.Status }, userId, now);
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = ToDetailsView(request) });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    [HttpPost("{id:guid}/start-picking")]
    [Authorize(Policy = "requests.process")]
    public Task<IActionResult> StartPicking(Guid id, CancellationToken ct) =>
        TransitionStatus(id, "aprovada", "em_separacao", ct);

    [HttpPost("{id:guid}/ready")]
    [Authorize(Policy = "requests.process")]
    public Task<IActionResult> MarkReady(Guid id, CancellationToken ct) =>
        TransitionStatus(id, "em_separacao", "pronta", ct);

    [HttpPost("{id:guid}/deliver")]
    [Authorize(Policy = "requests.process")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Deliver(Guid id, InternalRequestDeliveryRequest input, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            var result = await requestDeliveries.DeliverAsync(id, input.ReceivedByName, input.ReceivedByEmail,
                input.ReceivedByDocument, input.ReceivedByPhone, input.Signature, input.Notes, userId,
                User.FindFirstValue(ClaimTypes.Name) ?? string.Empty, HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers["Idempotency-Key"].ToString(), ct);
            var data = new
            {
                result.Delivery.Id,
                result.Delivery.Code,
                result.Delivery.Type,
                request_id = result.Delivery.RequestId,
                request_status = result.RequestStatus,
                industry_id = result.Delivery.IndustryId,
                delivered_by_id = result.Delivery.DeliveredById,
                received_by_name = result.Delivery.ReceivedByName,
                received_by_email = result.Delivery.ReceivedByEmail,
                received_by_document = result.Delivery.ReceivedByDocument,
                received_by_phone = result.Delivery.ReceivedByPhone,
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
        catch (InternalRequestDeliveryFailureException exception)
        {
            return StatusCode(exception.StatusCode, new { ok = false, error = new { code = exception.Code, message = exception.Message } });
        }
        catch (InventoryFailureException exception)
        {
            return StatusCode(exception.StatusCode, new { ok = false, error = new { code = exception.Code, message = exception.Message } });
        }
        catch (DigitalSignatureValidationException exception)
        {
            return UnprocessableEntity(new { ok = false, error = new { code = "VALIDATION_ERROR", message = exception.Message } });
        }
    }

    private async Task<IActionResult> TransitionStatus(Guid id, string fromStatus, string toStatus, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            InternalRequest? request;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                request = await database.Requests
                    .FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'interna' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                if (request is not null)
                {
                    await database.Entry(request).Collection(entity => entity.Items).Query().Include(item => item.Item).LoadAsync(ct);
                    await database.Entry(request).Collection(entity => entity.History).LoadAsync(ct);
                }
            }
            else
            {
                request = await database.Requests
                    .Include(entity => entity.Items).ThenInclude(item => item.Item)
                    .Include(entity => entity.History)
                    .SingleOrDefaultAsync(entity => entity.Id == id && entity.DeletedAt == null && entity.Flow == "interna", ct);
            }

            if (request is null)
                return NotFound();
            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : Guid.Empty;
            var actorScope = actorId == Guid.Empty
                ? null
                : await database.Users.AsNoTracking().Where(value => value.Id == actorId)
                    .Select(value => new { value.IndustryId, value.DepartmentId }).SingleOrDefaultAsync(ct);
            if (actorScope?.IndustryId is Guid actorIndustryId && request.IndustryId != actorIndustryId)
                return StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = new { code = "INDUSTRY_FORBIDDEN", message = "Você só pode movimentar solicitações da sua indústria." } });
            if (actorScope?.DepartmentId is Guid actorDepartmentId && request.DepartmentId != actorDepartmentId)
                return NotFound();
            if (request.Status != fromStatus)
                return UnprocessableEntity(new { ok = false, error = "INVALID_TRANSITION", message = $"A solicitação precisa estar no status '{fromStatus}'." });

            var now = DateTimeOffset.UtcNow;
            request.Status = toStatus;
            request.StatusChangedAt = now;
            request.UpdatedAt = now;
            database.RequestStatusHistory.Add(new InternalRequestStatusHistory
            {
                RequestId = request.Id,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                UserId = actorId == Guid.Empty ? null : actorId,
                CreatedAt = now
            });
            if (actorId != Guid.Empty)
                AddRequestAudit(request, "update", new { status = fromStatus }, new { status = toStatus }, actorId, now);
            if (toStatus == "pronta")
            {
                database.Notifications.Add(new Notification
                {
                    UserId = request.RequesterId,
                    Channel = "in_app",
                    Subject = $"Brindes prontos para retirada — {request.Code}",
                    Body = $"A solicitação {request.Code} está pronta para retirada/entrega.",
                    LinkUrl = $"/solicitacoes/{request.Id}",
                    RelatedType = "request",
                    RelatedId = request.Id,
                    Status = "enviado",
                    SentAt = now,
                    CreatedAt = now
                });
            }
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = ToDetailsView(request) });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : Guid.Empty;
        var canViewAll = User.HasClaim("permission", "requests.view_all") || User.IsInRole("admin");
        var canViewOwn = User.HasClaim("permission", "requests.view_own");
        var canViewDepartment = User.HasClaim("permission", "requests.view_department");
        var canApprove = User.HasClaim("permission", "requests.approve");
        if (!canViewAll && !canViewOwn && !canViewDepartment && !canApprove)
            return Forbid();

        var query = database.Requests.AsNoTracking().Where(request => request.Id == id && request.DeletedAt == null && request.Flow == "interna");
        Guid? userIndustryId = Guid.TryParse(User.FindFirstValue("industry_id"), out var parsedIndustryId)
            ? parsedIndustryId
            : null;
        if (userIndustryId is Guid scopedIndustryId)
        {
            query = query.Where(request => request.IndustryId == scopedIndustryId);
        }
        else if (!canViewAll)
        {
            Guid? userDepartmentId = Guid.TryParse(User.FindFirstValue("department_id"), out var parsedDepartmentId)
                ? parsedDepartmentId
                : null;
            if (canApprove)
            {
                var roleIds = await database.UserRoles.Where(userRole => userRole.UserId == userId)
                    .Select(userRole => userRole.RoleId).ToArrayAsync(ct);
                query = query.Where(request => (canViewOwn && request.RequesterId == userId) ||
                    (canViewDepartment && userDepartmentId != null && request.DepartmentId == userDepartmentId) ||
                    (canApprove && request.Approvals.Any(approval => approval.ApproverUserId == userId ||
                        (approval.ApproverRoleId != null && roleIds.Contains(approval.ApproverRoleId.Value)))));
            }
            else
            {
                query = query.Where(request => (canViewOwn && request.RequesterId == userId) ||
                    (canViewDepartment && userDepartmentId != null && request.DepartmentId == userDepartmentId));
            }
        }

        var request = await query
            .AsSplitQuery()
            .Include(entity => entity.Requester)
            .Include(entity => entity.Department)
            .Include(entity => entity.Industry)
            .Include(entity => entity.Items).ThenInclude(item => item.Item)
            .Include(entity => entity.History)
            .SingleOrDefaultAsync(ct);
        if (request is null)
            return NotFound();

        return Ok(new { ok = true, data = ToDetailsView(request, request.Requester.FullName, request.Department?.Name, request.Industry?.Name) });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "requests.create")]
    public async Task<IActionResult> Update(Guid id, UpdateInternalRequest input, CancellationToken ct)
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : Guid.Empty;
        if (userId == Guid.Empty)
            return Unauthorized();

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            InternalRequest? request;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                request = await database.Requests
                    .FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'interna' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                if (request is not null)
                {
                    await database.Entry(request).Collection(x => x.Items).Query().Include(x => x.Item).LoadAsync(ct);
                    await database.Entry(request).Collection(x => x.History).LoadAsync(ct);
                }
            }
            else
            {
                request = await database.Requests
                    .Include(x => x.Items).ThenInclude(x => x.Item)
                    .Include(x => x.History)
                    .SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null && x.Flow == "interna", ct);
            }

            if (request is null)
                return NotFound();
            if (request.RequesterId != userId)
                return Forbid();
            if (request.Status != "rascunho")
                return UnprocessableEntity(new { ok = false, error = "NOT_EDITABLE", message = "Somente rascunhos podem ser editados." });

            var actor = await database.Users.AsNoTracking()
                .SingleOrDefaultAsync(user => user.Id == userId && user.Active && user.DeletedAt == null, ct);
            if (actor is null)
                return Unauthorized();
            if (actor.IndustryId is Guid assignedIndustryId && request.IndustryId != assignedIndustryId)
                return Forbid();
            if (actor.DepartmentId is Guid assignedDepartmentId && request.DepartmentId != assignedDepartmentId)
                return NotFound();

            var originalPurpose = request.Purpose;
            var originalRecipient = request.Recipient;
            var originalDepartmentId = request.DepartmentId;
            var originalIndustryId = request.IndustryId;
            var originalNeededDate = request.NeededDate;
            var originalPurchaseTicketNo = request.PurchaseTicketNo;
            var originalNotes = request.Notes;
            var originalTotalValue = request.TotalValue;
            var originalNeedsPurchase = request.NeedsPurchase;

            if (input.Purpose.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Purpose.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(input.Purpose.GetString()) || input.Purpose.GetString()!.Trim().Length > 255)
                    return ValidationProblem(new Dictionary<string, string[]> { ["purpose"] = ["A finalidade deve conter até 255 caracteres."] });
                request.Purpose = input.Purpose.GetString()!.Trim();
            }

            if (input.Recipient.ValueKind != JsonValueKind.Undefined)
            {
                if (!TryReadNullableText(input.Recipient, 150, out var recipient))
                    return ValidationProblem(new Dictionary<string, string[]> { ["recipient"] = ["O destinatário deve conter até 150 caracteres."] });
                request.Recipient = recipient;
            }
            if (input.DepartmentId.ValueKind != JsonValueKind.Undefined)
            {
                if (!TryReadNullableGuid(input.DepartmentId, out var departmentId))
                    return ValidationProblem(new Dictionary<string, string[]> { ["department_id"] = ["Departamento inválido."] });
                if (departmentId is Guid selectedDepartmentId
                    && !await database.Departments.AnyAsync(department => department.Id == selectedDepartmentId, ct))
                    return ValidationProblem(new Dictionary<string, string[]> { ["department_id"] = ["Departamento inválido."] });
                request.DepartmentId = departmentId;
            }
            if (input.IndustryId.ValueKind != JsonValueKind.Undefined)
            {
                if (!TryReadNullableGuid(input.IndustryId, out var industryId))
                    return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });
                if (industryId is Guid selectedIndustryId
                    && !await database.Industries.AnyAsync(industry => industry.Id == selectedIndustryId, ct))
                    return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });
                if (actor.IndustryId is Guid assignedIndustry && industryId is Guid requestedIndustry && assignedIndustry != requestedIndustry)
                    return Forbid();
                request.IndustryId = actor.IndustryId ?? industryId;
            }
            if (input.NeededDate.ValueKind != JsonValueKind.Undefined)
            {
                if (!TryReadNullableDateOnly(input.NeededDate, out var neededDate))
                    return ValidationProblem(new Dictionary<string, string[]> { ["needed_date"] = ["A data necessária é inválida."] });
                request.NeededDate = neededDate;
            }
            if (input.PurchaseTicketNo.ValueKind != JsonValueKind.Undefined)
            {
                if (!TryReadNullableText(input.PurchaseTicketNo, 50, out var purchaseTicketNo))
                    return ValidationProblem(new Dictionary<string, string[]> { ["purchase_ticket_no"] = ["O chamado de compra deve conter até 50 caracteres."] });
                request.PurchaseTicketNo = purchaseTicketNo;
            }
            if (input.Notes.ValueKind != JsonValueKind.Undefined)
            {
                if (!TryReadNullableText(input.Notes, 2000, out var notes))
                    return ValidationProblem(new Dictionary<string, string[]> { ["notes"] = ["As observações devem conter até 2000 caracteres."] });
                request.Notes = notes;
            }

            Dictionary<Guid, CatalogItem>? catalogItems = null;
            if (input.Items.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Items.ValueKind != JsonValueKind.Array)
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["A lista de brindes é inválida."] });
                List<CreateInternalRequestItem?>? lines;
                try
                {
                    lines = JsonSerializer.Deserialize<List<CreateInternalRequestItem?>>(input.Items.GetRawText());
                }
                catch (JsonException)
                {
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["A lista de brindes é inválida."] });
                }
                if (lines is null or { Count: 0 } || lines.Any(line => line is null))
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Inclua pelo menos um brinde válido."] });
                var validLines = lines.Select(line => line!).ToList();
                if (validLines.Any(line => line.ItemId == Guid.Empty || line.QuantityRequested is < 1 or > 1_000_000))
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Brinde e quantidade inválidos."] });
                if (validLines.Select(line => line.ItemId).Distinct().Count() != validLines.Count)
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["O mesmo brinde não pode aparecer duas vezes."] });

                var itemIds = validLines.Select(line => line.ItemId).ToArray();
                catalogItems = await database.CatalogItems
                    .Where(item => itemIds.Contains(item.Id) && item.DeletedAt == null && item.Status == "ativo")
                    .ToDictionaryAsync(item => item.Id, ct);
                if (catalogItems.Count != itemIds.Length)
                    return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Um ou mais brindes são inválidos ou inativos."] });

                var existingItems = request.Items.ToDictionary(item => item.ItemId);
                var replacements = validLines.ToDictionary(line => line.ItemId);
                var positions = validLines.Select((line, position) => (line.ItemId, position))
                    .ToDictionary(value => value.ItemId, value => value.position);
                foreach (var existing in request.Items.ToArray())
                {
                    if (!replacements.TryGetValue(existing.ItemId, out var replacement))
                    {
                        database.RequestItems.Remove(existing);
                        request.Items.Remove(existing);
                        existingItems.Remove(existing.ItemId);
                        continue;
                    }

                    existing.QuantityRequested = replacement.QuantityRequested;
                    existing.Position = positions[existing.ItemId];
                    existing.UnitValue = catalogItems[replacement.ItemId].UnitValue;
                    existing.Notes = string.IsNullOrWhiteSpace(replacement.Notes) ? null : replacement.Notes.Trim();
                }
                foreach (var (line, position) in validLines.Select((line, position) => (line, position))
                    .Where(value => !existingItems.ContainsKey(value.line.ItemId)))
                {
                    request.Items.Add(new InternalRequestItem
                    {
                        RequestId = request.Id,
                        ItemId = line.ItemId,
                        Position = position,
                        QuantityRequested = line.QuantityRequested,
                        UnitValue = catalogItems[line.ItemId].UnitValue,
                        Notes = string.IsNullOrWhiteSpace(line.Notes) ? null : line.Notes.Trim()
                    });
                }
                var total = validLines.Sum(line => checked((catalogItems[line.ItemId].UnitValue ?? 0m) * line.QuantityRequested));
                request.TotalValue = decimal.Round(total, 2, MidpointRounding.AwayFromZero);
                var stocks = await database.Stocks.AsNoTracking().Where(stock => itemIds.Contains(stock.ItemId)).ToDictionaryAsync(stock => stock.ItemId, ct);
                request.NeedsPurchase = validLines.Any(line =>
                {
                    var available = stocks.TryGetValue(line.ItemId, out var stock) ? stock.OnHand - stock.Reserved : 0;
                    return line.QuantityRequested > available;
                });
            }

            var beforeAudit = new Dictionary<string, object?>();
            var afterAudit = new Dictionary<string, object?>();
            if (originalPurpose != request.Purpose)
            {
                beforeAudit["purpose"] = originalPurpose;
                afterAudit["purpose"] = request.Purpose;
            }
            if (originalRecipient != request.Recipient)
            {
                beforeAudit["recipient"] = originalRecipient;
                afterAudit["recipient"] = request.Recipient;
            }
            if (originalDepartmentId != request.DepartmentId)
            {
                beforeAudit["department_id"] = originalDepartmentId;
                afterAudit["department_id"] = request.DepartmentId;
            }
            if (originalIndustryId != request.IndustryId)
            {
                beforeAudit["industry_id"] = originalIndustryId;
                afterAudit["industry_id"] = request.IndustryId;
            }
            if (originalNeededDate != request.NeededDate)
            {
                beforeAudit["needed_date"] = originalNeededDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                afterAudit["needed_date"] = request.NeededDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (originalPurchaseTicketNo != request.PurchaseTicketNo)
            {
                beforeAudit["purchase_ticket_no"] = originalPurchaseTicketNo;
                afterAudit["purchase_ticket_no"] = request.PurchaseTicketNo;
            }
            if (originalNotes != request.Notes)
            {
                beforeAudit["notes"] = originalNotes;
                afterAudit["notes"] = request.Notes;
            }
            if (originalTotalValue != request.TotalValue)
            {
                beforeAudit["total_value"] = originalTotalValue;
                afterAudit["total_value"] = request.TotalValue;
            }
            if (originalNeedsPurchase != request.NeedsPurchase)
            {
                beforeAudit["needs_purchase"] = originalNeedsPurchase;
                afterAudit["needs_purchase"] = request.NeedsPurchase;
            }
            var updatedAt = DateTimeOffset.UtcNow;
            if (afterAudit.Count > 0)
                AddRequestAudit(request, "update", beforeAudit, afterAudit, userId, updatedAt);
            request.UpdatedAt = updatedAt;
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            if (catalogItems is not null)
                foreach (var line in request.Items)
                    line.Item = catalogItems[line.ItemId];
            return Ok(new { ok = true, data = ToDetailsView(request) });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static bool TryReadNullableGuid(JsonElement value, out Guid? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return true;
        if (!Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
            return false;
        result = parsed;
        return true;
    }

    private static bool TryReadNullableText(JsonElement value, int maxLength, out string? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return true;
        if (text.Length > maxLength)
            return false;
        result = text;
        return true;
    }

    private static bool TryReadNullableDateOnly(JsonElement value, out DateOnly? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return true;
        if (!DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        result = parsed;
        return true;
    }

    private static bool MatchesRule(ApprovalRule rule, InternalRequest request, HashSet<Guid> requesterRoleIds, HashSet<Guid> categoryIds)
    {
        if (rule.Criterion is "quantidade" or "valor")
        {
            var actual = rule.Criterion == "quantidade"
                ? request.Items.Sum(item => (decimal)item.QuantityRequested)
                : request.TotalValue;
            if (rule.Operator == "in")
            {
                var expectedValues = rule.Value.Split(',', StringSplitOptions.TrimEntries)
                    .Select(value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                        ? ToInteger(parsed)
                        : 0)
                    .ToHashSet();
                return expectedValues.Contains(ToInteger(actual));
            }
            if (!decimal.TryParse(rule.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var threshold))
                return false;
            return rule.Operator switch
            {
                ">" => actual > threshold,
                ">=" => actual >= threshold,
                "=" => Math.Abs(actual - threshold) < 0.0001m,
                _ => false
            };
        }

        IEnumerable<Guid> actualIds = rule.Criterion switch
        {
            "categoria" => categoryIds,
            "departamento" when request.DepartmentId is Guid departmentId => [departmentId],
            "perfil" => requesterRoleIds,
            _ => []
        };
        if (rule.Operator == "=")
            return Guid.TryParse(rule.Value, out var expectedId) && actualIds.Contains(expectedId);
        if (rule.Operator == "in")
        {
            var expectedIds = rule.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty).ToHashSet();
            return expectedIds.Count > 0 && actualIds.Any(expectedIds.Contains);
        }
        return false;

        static int ToInteger(decimal value) => (int)Math.Clamp(decimal.Truncate(value), int.MinValue, int.MaxValue);
    }

    private static InternalRequestDetailsView ToDetailsView(InternalRequest request,
        string? requesterName = null, string? departmentName = null, string? industryName = null) => new(
        request.Id,
        request.Code,
        request.Flow,
        request.Status,
        request.RequesterId,
        request.DepartmentId,
        request.IndustryId,
        request.Purpose,
        request.Recipient,
        request.NeededDate,
        request.NeedsPurchase,
        request.TotalValue,
        request.CreatedAt,
        request.SubmittedAt,
        request.ApprovedAt,
        request.CancelledAt,
        request.CancelReason,
        request.PurchaseTicketNo,
        request.Notes,
        requesterName,
        departmentName,
        industryName,
        request.Items.OrderBy(item => item.Position).ThenBy(item => item.Id).Select(item => new InternalRequestDetailItemView(
            item.Id, item.ItemId, item.Item.Code, item.Item.Name, item.QuantityRequested,
            item.QuantityApproved, item.QuantityReserved, item.QuantityDelivered, item.UnitValue, item.Notes)).ToArray(),
        request.History.OrderBy(history => history.CreatedAt).ThenBy(history => history.Id).Select(history => new InternalRequestHistoryView(
            history.FromStatus, history.ToStatus, history.UserId, history.Comment, history.CreatedAt)).ToArray());

    [HttpPost]
    [Authorize(Policy = "requests.create")]
    public async Task<IActionResult> Create(CreateInternalRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Purpose))
            return ValidationProblem(new Dictionary<string, string[]> { ["purpose"] = ["A finalidade é obrigatória."] });
        if (request.Items is null or { Count: 0 })
            return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Inclua pelo menos um brinde."] });
        if (request.Items.Any(item => item is null))
            return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["A lista contém um item inválido."] });
        var lines = request.Items.Select(item => item!).ToList();
        if (lines.Any(item => item.ItemId == Guid.Empty || item.QuantityRequested is < 1 or > 1_000_000))
            return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Brinde e quantidade inválidos."] });
        if (lines.Select(item => item.ItemId).Distinct().Count() != lines.Count)
            return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["O mesmo brinde não pode aparecer duas vezes."] });

        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId)
            ? parsedUserId
            : Guid.Empty;
        if (userId == Guid.Empty)
            return Unauthorized();

        var itemIds = lines.Select(item => item.ItemId).ToArray();
        var items = await database.CatalogItems
            .Where(item => itemIds.Contains(item.Id) && item.DeletedAt == null && item.Status == "ativo")
            .ToDictionaryAsync(item => item.Id, ct);
        if (items.Count != itemIds.Length)
            return ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Um ou mais brindes são inválidos ou inativos."] });

        if (request.DepartmentId is not null && !await database.Departments.AnyAsync(x => x.Id == request.DepartmentId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["department_id"] = ["Departamento inválido."] });
        if (request.IndustryId is Guid requestedIndustryId
            && !await database.Industries.AnyAsync(x => x.Id == requestedIndustryId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });

        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.Active && x.DeletedAt == null, ct);
        if (user is null)
            return Unauthorized();
        if (user.IndustryId is Guid assignedIndustryId
            && request.IndustryId is Guid requestedIndustry
            && assignedIndustryId != requestedIndustry)
            return Forbid();
        var requestIndustryId = user.IndustryId ?? request.IndustryId;
        if (requestIndustryId is Guid effectiveIndustryId
            && !await database.Industries.AnyAsync(x => x.Id == effectiveIndustryId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });

        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var total = decimal.Round(lines.Sum(line => checked((items[line.ItemId].UnitValue ?? 0m) * line.QuantityRequested)), 2, MidpointRounding.AwayFromZero);
        var stockByItem = await database.Stocks.AsNoTracking().Where(stock => itemIds.Contains(stock.ItemId)).ToDictionaryAsync(stock => stock.ItemId, ct);
        var needsPurchase = lines.Any(line =>
        {
            var available = stockByItem.TryGetValue(line.ItemId, out var stock) ? stock.OnHand - stock.Reserved : 0;
            return line.QuantityRequested > available;
        });

        var entity = new InternalRequest
        {
            Id = id,
            Code = $"SOL-{now:yyyy}-{id.ToString("N")[..12].ToUpperInvariant()}",
            RequesterId = userId,
            DepartmentId = request.DepartmentId ?? user.DepartmentId,
            IndustryId = requestIndustryId,
            Flow = "interna",
            Status = "rascunho",
            Purpose = request.Purpose.Trim(),
            Recipient = string.IsNullOrWhiteSpace(request.Recipient) ? null : request.Recipient.Trim(),
            PurchaseTicketNo = string.IsNullOrWhiteSpace(request.PurchaseTicketNo) ? null : request.PurchaseTicketNo.Trim(),
            NeededDate = request.NeededDate,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            NeedsPurchase = needsPurchase,
            TotalValue = total,
            CreatedAt = now,
            UpdatedAt = now,
            Items = lines.Select((line, position) => new InternalRequestItem
            {
                RequestId = id,
                ItemId = line.ItemId,
                Position = position,
                QuantityRequested = line.QuantityRequested,
                QuantityReserved = 0,
                QuantityDelivered = 0,
                UnitValue = items[line.ItemId].UnitValue,
                Notes = string.IsNullOrWhiteSpace(line.Notes) ? null : line.Notes.Trim()
            }).ToList(),
            History =
            [
                new InternalRequestStatusHistory
                {
                    RequestId = id,
                    FromStatus = null,
                    ToStatus = "rascunho",
                    UserId = userId,
                    Comment = "Solicitação criada.",
                    CreatedAt = now
                }
            ]
        };

        database.Requests.Add(entity);
        AddRequestAudit(entity, "create", null, new { code = entity.Code, purpose = entity.Purpose }, userId, now);
        await database.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new
        {
            ok = true,
            data = new InternalRequestView(
                entity.Id,
                entity.Code,
                entity.Status,
                entity.RequesterId,
                entity.DepartmentId,
                entity.IndustryId,
                entity.Purpose,
                entity.Recipient,
                entity.NeededDate,
                entity.NeedsPurchase,
                entity.TotalValue,
                entity.CreatedAt,
                entity.Items.OrderBy(line => line.Position).ThenBy(line => line.Id)
                    .Select(line => new InternalRequestItemView(line.ItemId, items[line.ItemId].Code, items[line.ItemId].Name, line.QuantityRequested, line.UnitValue, line.Notes)).ToArray())
        });
    }

    [HttpPost("~/api/v1/import/requests/apply")]
    [HttpPost("~/api/import/requests/commit")]
    [Authorize(Policy = "import.run")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ApplyImportedRequests([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = ["Selecione a planilha."] } });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        RequestsPreview preview;
        try
        {
            await using var stream = file.OpenReadStream();
            preview = await importPreviews.PreviewRequestsAsync(stream, ct);
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = [exception.Message] } });
        }

        var actor = await database.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == userId && user.Active && user.DeletedAt == null, ct);
        if (actor is null)
            return Unauthorized();
        var legacyEnvelope = string.Equals(Request.Path.Value, "/api/import/requests/commit", StringComparison.OrdinalIgnoreCase);
        var created = 0;
        var skipped = preview.Errors;
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;

        foreach (var row in preview.Rows.Where(row => row.Ok))
        {
            if (row.Payload is not ImportRequestPayload payload || payload.Items.Count == 0
                || payload.Items.Any(line => line.ItemId is null || line.QtyRequested is < 1 or > 1_000_000))
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["items"] = ["Brinde e quantidade inválidos."] } });
            if (payload.Purpose.Length > 255 || (payload.Recipient?.Length ?? 0) > 150
                || (payload.PurchaseTicketNo?.Length ?? 0) > 50 || (payload.Notes?.Length ?? 0) > 2000)
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["request"] = ["Um ou mais campos excedem o limite permitido."] } });

            var itemIds = payload.Items.Select(line => line.ItemId!.Value).ToArray();
            if (itemIds.Distinct().Count() != itemIds.Length)
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["items"] = ["O mesmo brinde não pode aparecer duas vezes."] } });
            var items = await database.CatalogItems
                .Where(item => itemIds.Contains(item.Id) && item.DeletedAt == null && item.Status == "ativo")
                .ToDictionaryAsync(item => item.Id, ct);
            if (items.Count != itemIds.Length)
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["items"] = ["Um ou mais brindes são inválidos ou inativos."] } });
            if (payload.DepartmentId is Guid departmentId
                && !await database.Departments.AnyAsync(department => department.Id == departmentId && department.DeletedAt == null, ct))
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["department_id"] = ["Departamento inválido."] } });
            if (payload.IndustryId is Guid industryId
                && !await database.Industries.AnyAsync(industry => industry.Id == industryId && industry.DeletedAt == null, ct))
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] } });
            if (actor.IndustryId is Guid actorIndustryId && payload.IndustryId is Guid requestedIndustryId && actorIndustryId != requestedIndustryId)
                return Forbid();

            var importIdentityHash = HashImportValue(new
            {
                payload.DepartmentId,
                payload.IndustryId,
                Purpose = payload.Purpose.Trim(),
                Recipient = payload.Recipient?.Trim(),
                Items = payload.Items.Select(line => line.ItemId).OrderBy(id => id).ToArray()
            });
            var importContentHash = HashImportValue(new
            {
                payload.DepartmentId,
                payload.IndustryId,
                Purpose = payload.Purpose.Trim(),
                Recipient = payload.Recipient?.Trim(),
                payload.NeededDate,
                PurchaseTicketNo = payload.PurchaseTicketNo?.Trim(),
                Notes = payload.Notes?.Trim(),
                Items = payload.Items.Select(line => new { line.ItemId, line.QtyRequested }).ToArray()
            });
            var previousImport = await database.Requests.AsNoTracking()
                .SingleOrDefaultAsync(request => request.RequesterId == userId && request.ImportIdentityHash == importIdentityHash, ct);
            if (previousImport is not null)
            {
                if (!string.Equals(previousImport.ImportContentHash, importContentHash, StringComparison.Ordinal))
                    return Conflict(new { ok = false, code = "IMPORT_ROW_CONFLICT", message = $"A linha {row.Line} já foi importada com dados diferentes." });
                skipped++;
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var requestId = Guid.NewGuid();
            var entity = new InternalRequest
            {
                Id = requestId,
                Code = $"SOL-{now:yyyy}-{requestId.ToString("N")[..12].ToUpperInvariant()}",
                RequesterId = userId,
                DepartmentId = payload.DepartmentId ?? actor.DepartmentId,
                IndustryId = actor.IndustryId ?? payload.IndustryId,
                Flow = "interna",
                Status = "rascunho",
                Purpose = payload.Purpose.Trim(),
                Recipient = string.IsNullOrWhiteSpace(payload.Recipient) ? null : payload.Recipient.Trim(),
                PurchaseTicketNo = string.IsNullOrWhiteSpace(payload.PurchaseTicketNo) ? null : payload.PurchaseTicketNo.Trim(),
                NeededDate = payload.NeededDate,
                Notes = string.IsNullOrWhiteSpace(payload.Notes) ? null : payload.Notes.Trim(),
                ImportIdentityHash = importIdentityHash,
                ImportContentHash = importContentHash,
                TotalValue = decimal.Round(payload.Items.Sum(line => checked((items[line.ItemId!.Value].UnitValue ?? 0m) * line.QtyRequested)), 2, MidpointRounding.AwayFromZero),
                CreatedAt = now,
                UpdatedAt = now,
                Items = payload.Items.Select((line, position) => new InternalRequestItem
                {
                    RequestId = requestId,
                    ItemId = line.ItemId!.Value,
                    Position = position,
                    QuantityRequested = line.QtyRequested,
                    UnitValue = items[line.ItemId.Value].UnitValue,
                    Item = items[line.ItemId.Value]
                }).ToList(),
                History =
                [
                    new InternalRequestStatusHistory
                    {
                        RequestId = requestId,
                        ToStatus = "rascunho",
                        UserId = userId,
                        Comment = "Solicitação criada por importação.",
                        CreatedAt = now
                    }
                ]
            };
            database.Requests.Add(entity);
            AddRequestAudit(entity, "create", null, new { code = entity.Code, purpose = entity.Purpose }, userId, now);

            await database.SaveChangesAsync(ct);
            var submitted = await SubmitCoreAsync(requestId, userId, ct);
            if (submitted is not OkObjectResult)
                return submitted;
            created++;
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return legacyEnvelope
            ? Ok(new { ok = true, data = new { created, skipped } })
            : Ok(new { created, skipped, total = preview.Total });
    }

    private static string HashImportValue(object value)
    {
        var canonical = JsonSerializer.Serialize(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private void AddRequestAudit(InternalRequest request, string action, object? before, object after, Guid userId, DateTimeOffset createdAt)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = action,
            EntityType = "request",
            EntityId = request.Id,
            EntityLabel = request.Code,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
            CreatedAt = createdAt
        });
    }

    private IActionResult ValidationProblem(Dictionary<string, string[]> errors) => UnprocessableEntity(new ValidationProblemDetails(errors));
}

public sealed class CreateInternalRequest
{
    [JsonPropertyName("department_id")] public Guid? DepartmentId { get; set; }
    [JsonPropertyName("industry_id")] public Guid? IndustryId { get; set; }
    [Required, StringLength(255)] public string Purpose { get; set; } = string.Empty;
    [JsonPropertyName("recipient"), StringLength(150)] public string? Recipient { get; set; }
    [JsonPropertyName("needed_date")] public DateOnly? NeededDate { get; set; }
    [JsonPropertyName("purchase_ticket_no"), StringLength(50)] public string? PurchaseTicketNo { get; set; }
    [StringLength(2000)] public string? Notes { get; set; }
    [Required] public List<CreateInternalRequestItem?>? Items { get; set; }
}

public sealed class CreateInternalRequestItem
{
    [JsonPropertyName("item_id")] public Guid ItemId { get; set; }
    [JsonPropertyName("qty_requested"), Range(1, 1_000_000)] public int QuantityRequested { get; set; }
    [StringLength(255)] public string? Notes { get; set; }
}

public sealed class UpdateInternalRequest
{
    public JsonElement Purpose { get; set; }
    public JsonElement Items { get; set; }
    [JsonPropertyName("recipient")] public JsonElement Recipient { get; set; }
    [JsonPropertyName("department_id")] public JsonElement DepartmentId { get; set; }
    [JsonPropertyName("industry_id")] public JsonElement IndustryId { get; set; }
    [JsonPropertyName("needed_date")] public JsonElement NeededDate { get; set; }
    [JsonPropertyName("purchase_ticket_no")] public JsonElement PurchaseTicketNo { get; set; }
    [JsonPropertyName("notes")] public JsonElement Notes { get; set; }
}

public sealed class ApprovalDecision
{
    [JsonPropertyName("justification")] public string? Justification { get; set; }
    [JsonPropertyName("items")] public List<ApprovalDecisionItem?>? Items { get; set; }
}

public sealed class ApprovalDecisionItem
{
    [JsonPropertyName("item_id")] public Guid ItemId { get; set; }
    [JsonPropertyName("qty_approved")] public int? QuantityApproved { get; set; }
}

public sealed class CancelInternalRequest
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class InternalRequestDeliveryRequest
{
    [JsonPropertyName("received_by_name")] public string? ReceivedByName { get; set; }
    [JsonPropertyName("received_by_email")] public string? ReceivedByEmail { get; set; }
    [JsonPropertyName("received_by_document")] public string? ReceivedByDocument { get; set; }
    [JsonPropertyName("received_by_phone")] public string? ReceivedByPhone { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

public sealed record InternalRequestView(
    Guid Id,
    string Code,
    string Status,
    [property: JsonPropertyName("requester_id")] Guid RequesterId,
    [property: JsonPropertyName("department_id")] Guid? DepartmentId,
    [property: JsonPropertyName("industry_id")] Guid? IndustryId,
    string Purpose,
    string? Recipient,
    [property: JsonPropertyName("needed_date")] DateOnly? NeededDate,
    [property: JsonPropertyName("needs_purchase")] bool NeedsPurchase,
    [property: JsonPropertyName("total_value")] decimal TotalValue,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    IReadOnlyList<InternalRequestItemView> Items);

public sealed record InternalRequestItemView(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    string Code,
    string Name,
    [property: JsonPropertyName("qty_requested")] int QuantityRequested,
    [property: JsonPropertyName("unit_value")] decimal? UnitValue,
    string? Notes);

public sealed record InternalRequestDetailsView(
    Guid Id,
    string Code,
    string Flow,
    string Status,
    [property: JsonPropertyName("requester_id")] Guid RequesterId,
    [property: JsonPropertyName("department_id")] Guid? DepartmentId,
    [property: JsonPropertyName("industry_id")] Guid? IndustryId,
    string Purpose,
    string? Recipient,
    [property: JsonPropertyName("needed_date")] DateOnly? NeededDate,
    [property: JsonPropertyName("needs_purchase")] bool NeedsPurchase,
    [property: JsonPropertyName("total_value")] decimal TotalValue,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt,
    [property: JsonPropertyName("approved_at")] DateTimeOffset? ApprovedAt,
    [property: JsonPropertyName("cancelled_at")] DateTimeOffset? CancelledAt,
    [property: JsonPropertyName("cancel_reason")] string? CancelReason,
    [property: JsonPropertyName("purchase_ticket_no")] string? PurchaseTicketNo,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("requester_name")] string? RequesterName,
    [property: JsonPropertyName("department_name")] string? DepartmentName,
    [property: JsonPropertyName("industry_name")] string? IndustryName,
    IReadOnlyList<InternalRequestDetailItemView> Items,
    IReadOnlyList<InternalRequestHistoryView> History);

public sealed record InternalRequestDetailItemView(
    Guid Id,
    [property: JsonPropertyName("item_id")] Guid ItemId,
    string Code,
    string Name,
    [property: JsonPropertyName("qty_requested")] int QuantityRequested,
    [property: JsonPropertyName("qty_approved")] int? QuantityApproved,
    [property: JsonPropertyName("qty_reserved")] int QuantityReserved,
    [property: JsonPropertyName("qty_delivered")] int QuantityDelivered,
    [property: JsonPropertyName("unit_value")] decimal? UnitValue,
    string? Notes);

public sealed record InternalRequestHistoryView(
    string? From,
    string To,
    [property: JsonPropertyName("user_id")] Guid? UserId,
    string? Comment,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
