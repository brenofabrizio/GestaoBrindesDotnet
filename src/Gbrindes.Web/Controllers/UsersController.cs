using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/users")]
public sealed class UsersController(
    ApplicationDbContext database,
    UserManager<Gbrindes.Web.Identity.ApplicationUser> users,
    RoleManager<Gbrindes.Web.Identity.ApplicationRole> roles) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "users.view")]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery(Name = "role_id")] string? roleId,
        [FromQuery(Name = "department_id")] string? departmentId, [FromQuery] string? active,
        [FromQuery] string? trashed, [FromQuery] string? sort, [FromQuery] int page = 1,
        [FromQuery(Name = "per_page")] int perPage = 25, CancellationToken ct = default)
    {
        var includeTrash = IsTrue(trashed);
        var query = database.Users.AsNoTracking()
            .Where(user => includeTrash ? user.DeletedAt != null : user.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLowerInvariant();
            query = query.Where(user => user.FullName.ToLower().Contains(search)
                || (user.Email != null && user.Email.ToLower().Contains(search)));
        }
        if (Guid.TryParse(roleId, out var selectedRoleId))
            query = query.Where(user => database.UserRoles.Any(userRole =>
                userRole.UserId == user.Id && userRole.RoleId == selectedRoleId));
        if (Guid.TryParse(departmentId, out var selectedDepartmentId))
            query = query.Where(user => user.DepartmentId == selectedDepartmentId);
        if (active is "0" or "1")
        {
            var isActive = active == "1";
            query = query.Where(user => user.Active == isActive);
        }

        page = Math.Clamp(page, 1, 1_000_000);
        perPage = Math.Clamp(perPage, 1, 100);
        var total = await query.CountAsync(ct);
        var ordered = ApplySort(query, sort);
        var pageUsers = await ordered.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        var userIds = pageUsers.Select(user => user.Id).ToArray();
        var roleAssignments = await (
            from userRole in database.UserRoles.AsNoTracking()
            where userIds.Contains(userRole.UserId)
            join role in database.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            orderby role.Name
            select new { userRole.UserId, role.Id, role.Name }
        ).ToListAsync(ct);
        var departmentIds = pageUsers.Where(user => user.DepartmentId.HasValue)
            .Select(user => user.DepartmentId!.Value).Distinct().ToArray();
        var departments = await database.Departments.AsNoTracking()
            .Where(department => departmentIds.Contains(department.Id))
            .ToDictionaryAsync(department => department.Id, department => new { id = department.Id, name = department.Name }, ct);
        var industryIds = pageUsers.Where(user => user.IndustryId.HasValue)
            .Select(user => user.IndustryId!.Value).Distinct().ToArray();
        var industries = await database.Industries.AsNoTracking()
            .Where(industry => industryIds.Contains(industry.Id))
            .ToDictionaryAsync(industry => industry.Id, industry => new { id = industry.Id, name = industry.Name }, ct);
        var data = pageUsers.Select(user => new
        {
            id = user.Id,
            name = user.FullName,
            email = user.Email,
            phone = user.PhoneNumber,
            roles = roleAssignments.Where(role => role.UserId == user.Id)
                .Select(role => new { id = role.Id, name = role.Name }).ToArray(),
            department = user.DepartmentId is Guid deptId && departments.TryGetValue(deptId, out var department) ? department : null,
            industry = user.IndustryId is Guid industryId && industries.TryGetValue(industryId, out var industry) ? industry : null,
            active = user.Active,
            must_change_password = user.MustChangePassword,
            last_login_at = user.LastLoginAt,
            created_at = user.CreatedAt,
            updated_at = user.UpdatedAt,
            deleted_at = user.DeletedAt
        }).ToArray();
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)perPage));

        return Ok(new { ok = true, data, meta = new { page, per_page = perPage, total, last_page = lastPage } });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "users.view")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == id, ct);
        return user is null ? NotFound() : Ok(new { ok = true, data = await ToViewAsync(user, ct) });
    }

    [HttpGet("create-options")]
    [Authorize(Policy = "users.manage")]
    public async Task<IActionResult> CreateOptions(CancellationToken ct)
    {
        var data = await roles.Roles.AsNoTracking()
            .OrderBy(role => role.Name)
            .Select(role => new { id = role.Id, name = role.Name, slug = role.Slug })
            .ToListAsync(ct);
        var departments = await database.Departments.AsNoTracking()
            .Where(department => department.DeletedAt == null)
            .OrderBy(department => department.Name)
            .Select(department => new { id = department.Id, name = department.Name, active = department.Active })
            .ToListAsync(ct);
        var industries = await database.Industries.AsNoTracking()
            .Where(industry => industry.DeletedAt == null)
            .OrderBy(industry => industry.Name)
            .Select(industry => new { id = industry.Id, name = industry.Name, active = industry.Active })
            .ToListAsync(ct);
        return Ok(new { ok = true, data, departments, industries });
    }

    [HttpPost]
    [Authorize(Policy = "users.manage")]
    public async Task<IActionResult> Create(UserCreateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var email = request.Email?.Trim();
        var phone = request.Phone?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 150)
            return Invalid("name", "Informe um nome com até 150 caracteres.");
        if (string.IsNullOrWhiteSpace(email) || email.Length > 190 || !new EmailAddressAttribute().IsValid(email))
            return Invalid("email", "Informe um e-mail válido com até 190 caracteres.");
        var passwordError = ValidateAdminPassword(request.Password);
        if (passwordError is not null)
            return Invalid("password", passwordError);
        if (phone?.Length > 30)
            return Invalid("phone", "O telefone deve ter até 30 caracteres.");
        if (request.RoleId is null || request.RoleId == Guid.Empty)
            return Invalid("role_id", "Selecione um perfil existente.");

        var role = await roles.FindByIdAsync(request.RoleId.Value.ToString());
        if (role is null)
            return Invalid("role_id", "O perfil informado não existe.");
        if (request.DepartmentId is Guid departmentId
            && !await database.Departments.AnyAsync(department => department.Id == departmentId && department.DeletedAt == null, ct))
            return Invalid("department_id", "O departamento informado não existe ou está na lixeira.");
        if (request.IndustryId is Guid industryId
            && !await database.Industries.AnyAsync(industry => industry.Id == industryId && industry.DeletedAt == null, ct))
            return Invalid("industry_id", "A indústria informada não existe ou está na lixeira.");

        var now = DateTimeOffset.UtcNow;
        var user = new Gbrindes.Web.Identity.ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = name,
            PhoneNumber = phone,
            DepartmentId = request.DepartmentId,
            IndustryId = request.IndustryId,
            Active = request.Active ?? true,
            MustChangePassword = request.MustChangePassword ?? true,
            CreatedAt = now,
            UpdatedAt = now
        };
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var created = await users.CreateAsync(user, request.Password!);
            if (!created.Succeeded)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                var duplicateEmail = created.Errors.Any(error => error.Code.Contains("Duplicate", StringComparison.Ordinal));
                return Invalid(duplicateEmail ? "email" : "password",
                    duplicateEmail ? "Já existe uma conta com este e-mail." : string.Join(" ", created.Errors.Select(error => error.Description)));
            }

            var assigned = await users.AddToRoleAsync(user, role.Name!);
            if (!assigned.Succeeded)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return Invalid("role_id", "Não foi possível atribuir o perfil selecionado.");
            }

            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : (Guid?)null;
            var actorName = actorId is Guid currentActorId
                ? await database.Users.Where(actor => actor.Id == currentActorId).Select(actor => actor.FullName).SingleOrDefaultAsync(ct)
                : null;
            var userAgent = Request.Headers["User-Agent"].ToString();
            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = actorName,
                Action = "create",
                EntityType = "user",
                EntityId = user.Id,
                EntityLabel = user.FullName,
                AfterJson = JsonSerializer.Serialize(new
                {
                    name = user.FullName,
                    email = user.Email,
                    role_id = role.Id,
                    department_id = user.DepartmentId,
                    industry_id = user.IndustryId,
                    phone = user.PhoneNumber,
                    active = user.Active,
                    must_change_password = user.MustChangePassword
                }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return Created($"/api/v1/users/{user.Id}", new { ok = true, data = await ToViewAsync(user, ct) });
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

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "users.manage")]
    public async Task<IActionResult> Update(Guid id, UserUpdateRequest request, CancellationToken ct)
    {
        var nameSpecified = request.Name.ValueKind != JsonValueKind.Undefined;
        var emailSpecified = request.Email.ValueKind != JsonValueKind.Undefined;
        var name = nameSpecified && request.Name.ValueKind == JsonValueKind.String ? request.Name.GetString()?.Trim() : null;
        var email = emailSpecified && request.Email.ValueKind == JsonValueKind.String ? request.Email.GetString()?.Trim() : null;
        if (nameSpecified && (string.IsNullOrWhiteSpace(name) || name.Length > 150))
            return Invalid("name", "Informe um nome com até 150 caracteres.");
        if (nameSpecified && request.Name.ValueKind != JsonValueKind.String)
            return Invalid("name", "Informe um nome válido.");
        if (emailSpecified && (string.IsNullOrWhiteSpace(email) || email.Length > 190 || !new EmailAddressAttribute().IsValid(email)))
            return Invalid("email", "Informe um e-mail válido com até 190 caracteres.");
        if (emailSpecified && request.Email.ValueKind != JsonValueKind.String)
            return Invalid("email", "Informe um e-mail válido.");

        var roleSpecified = request.RoleId.ValueKind != JsonValueKind.Undefined;
        if (!TryReadGuid(request.RoleId, out var requestedRoleId) || roleSpecified && requestedRoleId is null)
            return Invalid("role_id", "Selecione um perfil existente.");
        var departmentSpecified = request.DepartmentId.ValueKind != JsonValueKind.Undefined;
        if (!TryReadGuid(request.DepartmentId, out var departmentId))
            return Invalid("department_id", "Informe um departamento válido ou null.");
        var industrySpecified = request.IndustryId.ValueKind != JsonValueKind.Undefined;
        if (!TryReadGuid(request.IndustryId, out var industryId))
            return Invalid("industry_id", "Informe uma indústria válida ou null.");

        var phoneSpecified = request.Phone.ValueKind != JsonValueKind.Undefined;
        string? phone = null;
        if (phoneSpecified && request.Phone.ValueKind == JsonValueKind.String)
            phone = request.Phone.GetString()?.Trim();
        else if (phoneSpecified && request.Phone.ValueKind != JsonValueKind.Null)
            return Invalid("phone", "Informe um telefone válido ou null.");
        if (phone?.Length > 30)
            return Invalid("phone", "O telefone deve ter até 30 caracteres.");

        bool? mustChangePassword = request.MustChangePassword.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            _ => null
        };
        if (request.MustChangePassword.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
            return Invalid("must_change_password", "Informe um valor booleano.");

        string? password = null;
        if (request.Password.ValueKind == JsonValueKind.String)
            password = request.Password.GetString();
        else if (request.Password.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            return Invalid("password", "Informe uma senha válida.");
        if (!string.IsNullOrEmpty(password))
        {
            var passwordError = ValidateAdminPassword(password);
            if (passwordError is not null)
                return Invalid("password", passwordError);
        }
        else
        {
            password = null;
        }

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var query = database.Users.Where(candidate => candidate.Id == id);
            if (database.Database.IsRelational())
                query = database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE");
            var user = await query.SingleOrDefaultAsync(ct);
            if (user is null || user.DeletedAt is not null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return NotFound();
            }

            var requestedRole = roleSpecified
                ? await roles.FindByIdAsync(requestedRoleId!.Value.ToString())
                : null;
            if (roleSpecified && requestedRole is null)
                return Invalid("role_id", "O perfil informado não existe.");
            if (departmentSpecified && departmentId is Guid deptId
                && !await database.Departments.AnyAsync(department => department.Id == deptId && department.DeletedAt == null, ct))
                return Invalid("department_id", "O departamento informado não existe ou está na lixeira.");
            if (industrySpecified && industryId is Guid indId
                && !await database.Industries.AnyAsync(industry => industry.Id == indId && industry.DeletedAt == null, ct))
                return Invalid("industry_id", "A indústria informada não existe ou está na lixeira.");
            if (emailSpecified)
            {
                var emailOwner = await users.FindByEmailAsync(email!);
                if (emailOwner is not null && emailOwner.Id != id)
                    return Invalid("email", emailOwner.DeletedAt is not null
                        ? "Este e-mail pertence a um usuário na lixeira. Restaure-o em vez de criar outro."
                        : "Já existe um usuário com este e-mail.");
            }

            var currentRoleIds = await database.UserRoles.Where(link => link.UserId == id)
                .Select(link => link.RoleId).OrderBy(roleId => roleId).ToListAsync(ct);
            var roleChanged = roleSpecified && (currentRoleIds.Count != 1 || currentRoleIds[0] != requestedRoleId);
            var currentRoleNames = await users.GetRolesAsync(user);
            var isSelf = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) && actorId == id;
            if (roleChanged && isSelf)
                return RuleViolation("SELF_ROLE_CHANGE", "Você não pode alterar o seu próprio perfil.");
            if (roleChanged && user.Active && currentRoleNames.Contains("admin", StringComparer.OrdinalIgnoreCase)
                && !string.Equals(requestedRole!.Name, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var adminRoleId = await LockAdminRoleAsync(ct);
                var otherAdmins = adminRoleId is Guid roleId
                    ? await CountOtherActiveAdminsAsync(id, roleId, ct)
                    : 0;
                if (otherAdmins == 0)
                    return RuleViolation("LAST_ADMIN", "É necessário manter pelo menos um administrador ativo.");
            }

            var before = new
            {
                name = user.FullName,
                email = user.Email,
                role_ids = currentRoleIds,
                department_id = user.DepartmentId,
                industry_id = user.IndustryId,
                phone = user.PhoneNumber,
                active = user.Active,
                must_change_password = user.MustChangePassword
            };
            var changed = false;
            if (nameSpecified && user.FullName != name)
            {
                user.FullName = name!;
                changed = true;
            }
            if (emailSpecified && user.Email != email)
            {
                user.Email = email;
                user.UserName = email;
                changed = true;
            }
            if (departmentSpecified && user.DepartmentId != departmentId)
            {
                user.DepartmentId = departmentId;
                changed = true;
            }
            if (industrySpecified && user.IndustryId != industryId)
            {
                user.IndustryId = industryId;
                changed = true;
            }
            if (phoneSpecified && user.PhoneNumber != phone)
            {
                user.PhoneNumber = phone;
                changed = true;
            }
            if (mustChangePassword is bool mustChange && user.MustChangePassword != mustChange)
            {
                user.MustChangePassword = mustChange;
                changed = true;
            }
            if (password is not null)
            {
                user.MustChangePassword = mustChangePassword ?? true;
                user.PasswordChangedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
            if (roleChanged)
            {
                var removed = await users.RemoveFromRolesAsync(user, currentRoleNames);
                if (!removed.Succeeded)
                    return Invalid("role_id", string.Join(" ", removed.Errors.Select(error => error.Description)));
                var added = await users.AddToRoleAsync(user, requestedRole!.Name!);
                if (!added.Succeeded)
                    return Invalid("role_id", string.Join(" ", added.Errors.Select(error => error.Description)));
                changed = true;
            }

            if (!changed)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return Ok(new { ok = true, data = await ToViewAsync(user, ct) });
            }

            var now = DateTimeOffset.UtcNow;
            user.UpdatedAt = now;
            var updateResult = password is not null
                ? await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password)
                : await users.UpdateAsync(user);
            if (!updateResult.Succeeded)
                return Invalid(password is not null ? "password" : emailSpecified ? "email" : "name",
                    string.Join(" ", updateResult.Errors.Select(error => error.Description)));

            if (roleChanged)
            {
                var stampResult = await users.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                    return Invalid("role_id", string.Join(" ", stampResult.Errors.Select(error => error.Description)));
            }

            var afterRoleIds = await database.UserRoles.Where(link => link.UserId == id)
                .Select(link => link.RoleId).OrderBy(roleId => roleId).ToListAsync(ct);
            database.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentActorId)
                    ? currentActorId
                    : null,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "update",
                EntityType = "user",
                EntityId = id,
                EntityLabel = user.FullName,
                BeforeJson = JsonSerializer.Serialize(before),
                AfterJson = JsonSerializer.Serialize(new
                {
                    name = user.FullName,
                    email = user.Email,
                    role_ids = afterRoleIds,
                    department_id = user.DepartmentId,
                    industry_id = user.IndustryId,
                    phone = user.PhoneNumber,
                    active = user.Active,
                    must_change_password = user.MustChangePassword
                }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers["User-Agent"].ToString() is { Length: > 255 } userAgent
                    ? userAgent[..255]
                    : Request.Headers["User-Agent"].ToString(),
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return Ok(new { ok = true, data = await ToViewAsync(user, ct) });
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

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = "users.manage")]
    public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetActiveAsync(id, true, ct);

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "users.manage")]
    public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetActiveAsync(id, false, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "users.manage")]
    public Task<IActionResult> Trash(Guid id, CancellationToken ct) => TrashAsync(id, ct);

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = "users.manage")]
    public Task<IActionResult> Restore(Guid id, CancellationToken ct) => RestoreAsync(id, ct);

    [HttpPost("{id:guid}/purge")]
    [Authorize(Policy = "users.manage")]
    public Task<IActionResult> Purge(Guid id, UserPurgeRequest request, CancellationToken ct) => PurgeAsync(id, request, ct);

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var options = await (
            from user in database.Users.AsNoTracking()
            where user.Active && user.DeletedAt == null
            join department in database.Departments.AsNoTracking()
                on user.DepartmentId equals (Guid?)department.Id into departments
            from department in departments.DefaultIfEmpty()
            orderby user.FullName, user.Id
            select new
            {
                id = user.Id,
                name = user.FullName,
                department = department == null ? null : new { id = (Guid?)department.Id, name = department.Name }
            })
            .ToListAsync(ct);

        return Ok(new { ok = true, data = options });
    }

    private async Task<object> ToViewAsync(Gbrindes.Web.Identity.ApplicationUser user, CancellationToken ct)
    {
        var roleViews = await (
            from userRole in database.UserRoles.AsNoTracking()
            where userRole.UserId == user.Id
            join role in database.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            orderby role.Name
            select new { id = role.Id, name = role.Name }
        ).ToListAsync(ct);
        var department = user.DepartmentId is Guid departmentId
            ? await database.Departments.AsNoTracking().Where(item => item.Id == departmentId)
                .Select(item => new { id = (Guid?)item.Id, name = item.Name }).SingleOrDefaultAsync(ct)
            : null;
        var industry = user.IndustryId is Guid industryId
            ? await database.Industries.AsNoTracking().Where(item => item.Id == industryId)
                .Select(item => new { id = (Guid?)item.Id, name = item.Name }).SingleOrDefaultAsync(ct)
            : null;

        return new
        {
            id = user.Id,
            name = user.FullName,
            email = user.Email,
            phone = user.PhoneNumber,
            roles = roleViews,
            department,
            industry,
            active = user.Active,
            must_change_password = user.MustChangePassword,
            last_login_at = user.LastLoginAt,
            created_at = user.CreatedAt,
            updated_at = user.UpdatedAt,
            deleted_at = user.DeletedAt,
            deleted_by = user.DeletedBy
        };
    }

    private IActionResult Invalid(string field, string message) => UnprocessableEntity(new
    {
        ok = false,
        error = "VALIDATION_ERROR",
        errors = new Dictionary<string, string[]> { [field] = [message] }
    });

    private IActionResult RuleViolation(string code, string message) => UnprocessableEntity(new
    {
        ok = false,
        error = code,
        message
    });

    private async Task<IActionResult> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var query = database.Users.Where(candidate => candidate.Id == id);
            if (database.Database.IsRelational())
                query = database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE");
            var user = await query.SingleOrDefaultAsync(ct);
            if (user is null || user.DeletedAt is not null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return NotFound();
            }
            if (user.Active == active)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return Ok(new { ok = true, data = await ToViewAsync(user, ct) });
            }

            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : (Guid?)null;
            if (!active && actorId == id)
                return RuleViolation("SELF_ACTION", "Você não pode inativar o seu próprio usuário.");

            var roleNames = await users.GetRolesAsync(user);
            if (!active && roleNames.Contains("admin", StringComparer.OrdinalIgnoreCase))
            {
                var adminRoleId = await LockAdminRoleAsync(ct);
                var otherAdmins = adminRoleId is Guid roleId
                    ? await CountOtherActiveAdminsAsync(id, roleId, ct)
                    : 0;
                if (otherAdmins == 0)
                    return RuleViolation("LAST_ADMIN", "É necessário manter pelo menos um administrador ativo.");
            }

            var wasActive = user.Active;
            var now = DateTimeOffset.UtcNow;
            user.Active = active;
            user.UpdatedAt = now;
            var update = await users.UpdateSecurityStampAsync(user);
            if (!update.Succeeded)
                return Invalid("active", string.Join(" ", update.Errors.Select(error => error.Description)));

            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = active ? "activate" : "deactivate",
                EntityType = "user",
                EntityId = id,
                EntityLabel = user.FullName,
                BeforeJson = JsonSerializer.Serialize(new { active = wasActive }),
                AfterJson = JsonSerializer.Serialize(new { active }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers["User-Agent"].ToString() is { Length: > 255 } userAgent
                    ? userAgent[..255]
                    : Request.Headers["User-Agent"].ToString(),
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = await ToViewAsync(user, ct) });
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

    private async Task<IActionResult> TrashAsync(Guid id, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var query = database.Users.Where(candidate => candidate.Id == id);
            if (database.Database.IsRelational())
                query = database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE");
            var user = await query.SingleOrDefaultAsync(ct);
            if (user is null || user.DeletedAt is not null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return NotFound();
            }

            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : (Guid?)null;
            if (actorId == id)
                return RuleViolation("SELF_ACTION", "Você não pode excluir o seu próprio usuário.");

            var roleNames = await users.GetRolesAsync(user);
            if (user.Active && roleNames.Contains("admin", StringComparer.OrdinalIgnoreCase))
            {
                var adminRoleId = await LockAdminRoleAsync(ct);
                var otherAdmins = adminRoleId is Guid roleId
                    ? await CountOtherActiveAdminsAsync(id, roleId, ct)
                    : 0;
                if (otherAdmins == 0)
                    return RuleViolation("LAST_ADMIN", "É necessário manter pelo menos um administrador ativo.");
            }

            var before = new
            {
                name = user.FullName,
                email = user.Email,
                role_ids = await database.UserRoles.Where(link => link.UserId == id)
                    .Select(link => link.RoleId).OrderBy(roleId => roleId).ToListAsync(ct),
                department_id = user.DepartmentId,
                industry_id = user.IndustryId,
                phone = user.PhoneNumber,
                active = user.Active,
                must_change_password = user.MustChangePassword
            };
            var now = DateTimeOffset.UtcNow;
            user.DeletedAt = now;
            user.DeletedBy = actorId;
            user.UpdatedAt = now;
            var update = await users.UpdateSecurityStampAsync(user);
            if (!update.Succeeded)
                return Invalid("user_id", string.Join(" ", update.Errors.Select(error => error.Description)));

            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "delete",
                EntityType = "user",
                EntityId = id,
                EntityLabel = user.FullName,
                BeforeJson = JsonSerializer.Serialize(before),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers["User-Agent"].ToString() is { Length: > 255 } userAgent
                    ? userAgent[..255]
                    : Request.Headers["User-Agent"].ToString(),
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = new { id, deleted = true } });
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

    private async Task<IActionResult> RestoreAsync(Guid id, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var query = database.Users.Where(candidate => candidate.Id == id);
            if (database.Database.IsRelational())
                query = database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE");
            var user = await query.SingleOrDefaultAsync(ct);
            if (user is null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return NotFound();
            }
            if (user.DeletedAt is null)
                return Conflict(new { ok = false, error = "NOT_IN_TRASH", message = "Este usuário não está na lixeira." });

            user.DeletedAt = null;
            user.DeletedBy = null;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
                return Invalid("user_id", string.Join(" ", updated.Errors.Select(error => error.Description)));

            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : (Guid?)null;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "restore",
                EntityType = "user",
                EntityId = id,
                EntityLabel = user.FullName,
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers["User-Agent"].ToString() is { Length: > 255 } userAgent
                    ? userAgent[..255]
                    : Request.Headers["User-Agent"].ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = await ToViewAsync(user, ct) });
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

    private async Task<IActionResult> PurgeAsync(Guid id, UserPurgeRequest request, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var query = database.Users.Where(candidate => candidate.Id == id);
            if (database.Database.IsRelational())
                query = database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE");
            var user = await query.SingleOrDefaultAsync(ct);
            if (user is null)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return NotFound();
            }
            if (user.DeletedAt is null)
                return Conflict(new { ok = false, error = "NOT_IN_TRASH", message = "Envie o usuário para a lixeira antes de excluir definitivamente." });
            if (string.IsNullOrWhiteSpace(user.Email)
                || !string.Equals(request.Confirm?.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
                return Invalid("confirm", $"Digite \"{user.Email}\" para confirmar a exclusão definitiva.");

            var uses = new List<string>();
            var auditCount = await database.AuditLogs.CountAsync(log => log.UserId == id, ct);
            if (auditCount > 0)
                uses.Add($"{auditCount} registro(s) de auditoria");
            var movementCount = await database.StockMovements.CountAsync(movement => movement.UserId == id, ct);
            if (movementCount > 0)
                uses.Add($"{movementCount} movimentação(ões) de estoque");
            var requesterExitCount = await database.StockExitOrders.CountAsync(order => order.RequesterId == id, ct);
            if (requesterExitCount > 0)
                uses.Add($"{requesterExitCount} saída(s) como solicitante");
            var authorizedExitCount = await database.StockExitOrders.CountAsync(order => order.AuthorizedBy == id, ct);
            if (authorizedExitCount > 0)
                uses.Add($"{authorizedExitCount} saída(s) autorizada(s)");
            var confirmedExitCount = await database.StockExitOrders.CountAsync(order => order.ConfirmedBy == id, ct);
            if (confirmedExitCount > 0)
                uses.Add($"{confirmedExitCount} saída(s) confirmada(s)");
            var deliveryCount = await database.Deliveries.CountAsync(delivery => delivery.DeliveredById == id, ct);
            if (deliveryCount > 0)
                uses.Add($"{deliveryCount} protocolo(s) de entrega");
            var requestCount = await database.Requests.CountAsync(entity => entity.RequesterId == id, ct);
            if (requestCount > 0)
                uses.Add($"{requestCount} solicitação(ões)");
            var ruleCount = await database.ApprovalRules.CountAsync(rule => rule.ApproverUserId == id, ct);
            if (ruleCount > 0)
                uses.Add($"{ruleCount} regra(s) de aprovação");
            var historyCount = await database.RequestStatusHistory.CountAsync(history => history.UserId == id, ct);
            if (historyCount > 0)
                uses.Add($"{historyCount} histórico(s) de solicitação");
            var approvalCount = await database.RequestApprovals.CountAsync(approval =>
                approval.ApproverUserId == id || approval.DecidedById == id, ct);
            if (approvalCount > 0)
                uses.Add($"{approvalCount} aprovação(ões)");
            if (uses.Count > 0)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return Conflict(new
                {
                    ok = false,
                    error = "IN_USE",
                    message = "Não é possível excluir definitivamente: o usuário mantém histórico ou referências.",
                    details = new { uses }
                });
            }

            var before = new
            {
                name = user.FullName,
                email = user.Email,
                role_ids = await database.UserRoles.Where(link => link.UserId == id)
                    .Select(link => link.RoleId).OrderBy(roleId => roleId).ToListAsync(ct),
                department_id = user.DepartmentId,
                industry_id = user.IndustryId,
                phone = user.PhoneNumber,
                active = user.Active,
                deleted_at = user.DeletedAt
            };
            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : (Guid?)null;
            var deleted = await users.DeleteAsync(user);
            if (!deleted.Succeeded)
                return Conflict(new { ok = false, error = "IN_USE", message = string.Join(" ", deleted.Errors.Select(error => error.Description)) });

            var now = DateTimeOffset.UtcNow;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "purge",
                EntityType = "user",
                EntityId = id,
                EntityLabel = user.FullName,
                BeforeJson = JsonSerializer.Serialize(before),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers["User-Agent"].ToString() is { Length: > 255 } userAgent
                    ? userAgent[..255]
                    : Request.Headers["User-Agent"].ToString(),
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = new { id, purged = true } });
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

    private async Task<Guid?> LockAdminRoleAsync(CancellationToken ct)
    {
        if (!database.Database.IsRelational())
            return await database.Roles.Where(role => role.NormalizedName == "ADMIN")
                .Select(role => (Guid?)role.Id).SingleOrDefaultAsync(ct);

        return await database.Roles.FromSqlInterpolated(
                $"SELECT * FROM \"AspNetRoles\" WHERE \"NormalizedName\" = {"ADMIN"} FOR UPDATE")
            .Select(role => (Guid?)role.Id).SingleOrDefaultAsync(ct);
    }

    private Task<int> CountOtherActiveAdminsAsync(Guid excludedUserId, Guid adminRoleId, CancellationToken ct)
    {
        var adminUserIds = database.UserRoles.Where(link => link.RoleId == adminRoleId).Select(link => link.UserId);
        return database.Users.CountAsync(user => user.Id != excludedUserId && user.Active && user.DeletedAt == null
            && adminUserIds.Contains(user.Id), ct);
    }

    private static bool TryReadGuid(JsonElement element, out Guid? value)
    {
        value = null;
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static string? ValidateAdminPassword(string? password)
    {
        if (password is null)
            return "Informe uma senha válida.";
        var length = password.EnumerateRunes().Count();
        if (length < 8)
            return "A senha deve ter no mínimo 8 caracteres.";
        if (length > 72)
            return "A senha deve ter no máximo 72 caracteres.";
        if (!password.Any(character => character is >= 'A' and <= 'Z'))
            return "A senha deve conter ao menos uma letra maiúscula.";
        if (!password.Any(character => character is >= 'a' and <= 'z'))
            return "A senha deve conter ao menos uma letra minúscula.";
        if (!password.Any(character => character is >= '0' and <= '9'))
            return "A senha deve conter ao menos um número.";
        if (!password.Any(character => !char.IsAsciiLetterOrDigit(character)))
            return "A senha deve conter ao menos um caractere especial.";
        return null;
    }

    private static bool IsTrue(string? value) => value == "1" || bool.TryParse(value, out var parsed) && parsed;

    private static IOrderedQueryable<Gbrindes.Web.Identity.ApplicationUser> ApplySort(
        IQueryable<Gbrindes.Web.Identity.ApplicationUser> query, string? sort)
    {
        var descending = sort?.StartsWith("-", StringComparison.Ordinal) == true;
        var key = descending ? sort![1..] : sort;
        return key switch
        {
            "email" when descending => query.OrderByDescending(user => user.Email).ThenBy(user => user.Id),
            "email" => query.OrderBy(user => user.Email).ThenBy(user => user.Id),
            "last_login_at" when descending => query.OrderByDescending(user => user.LastLoginAt).ThenBy(user => user.Id),
            "last_login_at" => query.OrderBy(user => user.LastLoginAt).ThenBy(user => user.Id),
            "created_at" when descending => query.OrderByDescending(user => user.CreatedAt).ThenBy(user => user.Id),
            "created_at" => query.OrderBy(user => user.CreatedAt).ThenBy(user => user.Id),
            "name" when descending => query.OrderByDescending(user => user.FullName).ThenBy(user => user.Id),
            _ when descending => query.OrderByDescending(user => user.FullName).ThenBy(user => user.Id),
            _ => query.OrderBy(user => user.FullName).ThenBy(user => user.Id)
        };
    }
}
