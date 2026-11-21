using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Security;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/reports")]
[Authorize(Policy = "reports.view")]
public sealed class ReportsController(ApplicationDbContext database) : ControllerBase
{
    private static readonly (string Id, string Label)[] Types =
    [
        ("stock", "Estoque atual"),
        ("entries", "Histórico de entradas"),
        ("exits", "Histórico de saídas"),
        ("movements", "Movimentação por período"),
        ("by_item", "Distribuição por brinde"),
        ("by_department", "Distribuição por departamento"),
        ("by_requester", "Distribuição por solicitante"),
        ("by_industry", "Distribuição por indústria"),
        ("requests", "Solicitações"),
        ("approvals", "Aprovações"),
        ("low_stock", "Itens abaixo do estoque mínimo"),
        ("protocols", "Protocolos"),
        ("event_balance", "Saldo por indústria no evento"),
        ("stock_industry", "Estoque por indústria (recebido / retirado / saldo)"),
        ("withdrawals", "Relatório de retiradas"),
        ("event_report", "Relatório do evento (Feirão)")
    ];

    [HttpGet]
    public IActionResult ListTypes() => Ok(new
    {
        ok = true,
        data = Types.Select(type => new { id = type.Id, label = type.Label })
    });

    [HttpGet("stock")]
    public async Task<IActionResult> Stock(CancellationToken ct)
    {
        var report = await GetStockReportAsync(ct);
        return Ok(new { ok = true, data = report });
    }

    [HttpGet("stock/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportStockCsv(CancellationToken ct)
    {
        var report = await GetStockReportAsync(ct);
        return CsvFile(report, "stock");
    }

    [HttpGet("stock/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportStockXlsx(CancellationToken ct)
        => XlsxFile(await GetStockReportAsync(ct), "stock");

    private async Task<ReportTable> GetStockReportAsync(CancellationToken ct)
    {
        var rows = await (
            from item in database.CatalogItems.AsNoTracking()
            join stock in database.Stocks.AsNoTracking() on item.Id equals stock.ItemId
            join categoryRow in database.Categories.AsNoTracking() on item.CategoryId equals categoryRow.Id into categories
            from category in categories.DefaultIfEmpty()
            join locationRow in database.Locations.AsNoTracking() on item.LocationId equals (Guid?)locationRow.Id into locations
            from location in locations.DefaultIfEmpty()
            where item.DeletedAt == null
            orderby item.Name
            select new
            {
                item.Id,
                item.Code,
                item.Name,
                Category = category == null ? null : category.Name,
                Location = location == null ? null : location.Name,
                stock.OnHand,
                stock.Reserved,
                item.MinStock,
                item.UnitValue,
                item.Status
            }).ToListAsync(ct);

        var ids = rows.Select(row => row.Id).ToArray();
        var industries = ids.Length == 0
            ? []
            : await (from movement in database.StockMovements.AsNoTracking()
                     join industry in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industry.Id
                     where ids.Contains(movement.ItemId)
                     select new { movement.ItemId, industry.Name }).Distinct().ToListAsync(ct);
        var industryByItem = industries.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal)));

        var resultRows = rows.Select(row => new object?[]
        {
            row.Code, row.Name, row.Category, row.Location,
            industryByItem.GetValueOrDefault(row.Id) is { Length: > 0 } names ? names : "—",
            row.OnHand, row.Reserved, row.OnHand - row.Reserved, row.MinStock, row.UnitValue, row.Status
        }).ToArray();

        return new ReportTable(
            Types[0].Label,
            ["Código", "Nome", "Categoria", "Local", "Indústria", "Em estoque", "Reservado", "Disponível", "Mínimo", "Valor unit.", "Status"],
            resultRows);
    }

    private static void AppendCsvRow(StringBuilder csv, IEnumerable<object?> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
                csv.Append(';');
            first = false;
            var cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (value is string && cell.Length > 0)
            {
                var firstNonWhitespace = cell.TrimStart(' ', '\t', '\r', '\n');
                if (cell[0] is '\t' or '\r' or '\n'
                    || (firstNonWhitespace.Length > 0 && firstNonWhitespace[0] is '=' or '+' or '-' or '@'))
                    cell = "'" + cell;
            }
            csv.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        csv.Append("\r\n");
    }

    private static DateTimeOffset EndExclusive(DateOnly date)
        => date == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private sealed record ReportTable(string Title, string[] Columns, object?[][] Rows);

    private async Task<(Guid? IndustryId, Guid? DepartmentId)> GetUserScopeAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return (null, null);

        var scope = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId, user.DepartmentId })
            .SingleOrDefaultAsync(ct);
        return scope is null ? (null, null) : (scope.IndustryId, scope.DepartmentId);
    }

    private IActionResult CsvFile(ReportTable report, string fileNamePrefix)
    {
        var csv = new StringBuilder().Append('\uFEFF');
        AppendCsvRow(csv, report.Columns);
        foreach (var row in report.Rows)
            AppendCsvRow(csv, row);

        var fileName = $"{fileNamePrefix}_{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", fileName);
    }

    private IActionResult XlsxFile(ReportTable report, string fileNamePrefix)
    {
        using var output = new MemoryStream();
        var sheetName = new string(report.Title.Where(character => !"[]:*?/\\".Contains(character)).Take(31).ToArray());
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddXlsxEntry(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            AddXlsxEntry(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            AddXlsxEntry(archive, "xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{SecurityElement.Escape(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            AddXlsxEntry(archive, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            var rows = new List<object?[]> { report.Columns.Cast<object?>().ToArray() };
            rows.AddRange(report.Rows);
            var sheetRows = rows.Select((row, rowIndex) =>
                $"<row r=\"{rowIndex + 1}\">{string.Concat(row.Select((value, columnIndex) => XlsxCell(value, columnIndex + 1, rowIndex + 1)))}</row>");
            AddXlsxEntry(archive, "xl/worksheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{string.Concat(sheetRows)}</sheetData></worksheet>");
        }

        var fileName = $"{fileNamePrefix}_{DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.xlsx";
        return File(output.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    private static string XlsxCell(object? value, int column, int row)
    {
        var reference = $"{XlsxColumn(column)}{row}";
        if (value is null)
            return $"<c r=\"{reference}\" t=\"inlineStr\"><is><t></t></is></c>";
        if (value is bool boolean)
            return $"<c r=\"{reference}\" t=\"b\"><v>{(boolean ? 1 : 0)}</v></c>";
        if (value is byte or short or int or long or float or double or decimal)
            return $"<c r=\"{reference}\"><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>";
        var rawText = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var validXmlText = new StringBuilder(rawText.Length);
        foreach (var rune in rawText.EnumerateRunes())
        {
            var codePoint = rune.Value;
            if (codePoint is 0x9 or 0xA or 0xD || codePoint is >= 0x20 and <= 0xD7FF || codePoint is >= 0xE000 and <= 0xFFFD || codePoint is >= 0x10000 and <= 0x10FFFF)
                validXmlText.Append(rune);
        }

        var text = SecurityElement.Escape(validXmlText.ToString()) ?? string.Empty;
        return $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{text}</t></is></c>";
    }

    private static string XlsxColumn(int number)
    {
        var result = new StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }

    private static void AddXlsxEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [HttpGet("low_stock")]
    public async Task<IActionResult> LowStock(CancellationToken ct)
        => Ok(new { ok = true, data = await GetLowStockReportAsync(ct) });

    [HttpGet("low_stock/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportLowStockCsv(CancellationToken ct)
        => CsvFile(await GetLowStockReportAsync(ct), "low_stock");

    [HttpGet("low_stock/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportLowStockXlsx(CancellationToken ct)
        => XlsxFile(await GetLowStockReportAsync(ct), "low_stock");

    private async Task<ReportTable> GetLowStockReportAsync(CancellationToken ct)
    {
        var rows = await (
            from item in database.CatalogItems.AsNoTracking()
            join stock in database.Stocks.AsNoTracking() on item.Id equals stock.ItemId
            where item.DeletedAt == null && item.Status == "ativo"
                && (stock.OnHand - stock.Reserved <= 0
                    || (item.MinStock > 0 && stock.OnHand - stock.Reserved <= item.MinStock))
            orderby stock.OnHand - stock.Reserved
            select new
            {
                item.Id,
                item.Code,
                item.Name,
                Available = stock.OnHand - stock.Reserved,
                item.MinStock
            }).ToListAsync(ct);

        var ids = rows.Select(row => row.Id).ToArray();
        var industries = ids.Length == 0
            ? []
            : await (from movement in database.StockMovements.AsNoTracking()
                     join industry in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industry.Id
                     where ids.Contains(movement.ItemId)
                     select new { movement.ItemId, industry.Name }).Distinct().ToListAsync(ct);
        var industryByItem = industries.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal)));
        var reportRows = rows.Select(row => new object?[]
        {
            row.Code,
            row.Name,
            industryByItem.GetValueOrDefault(row.Id) is { Length: > 0 } names ? names : "—",
            row.Available,
            row.MinStock
        }).ToArray();

        return new ReportTable("Abaixo do mínimo", ["Código", "Nome", "Indústria", "Disponível", "Mínimo"], reportRows);
    }

    [HttpGet("entries")]
    public async Task<IActionResult> Entries([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => Ok(new { ok = true, data = await GetMovementReportAsync("entries", "entrada", from, to, itemId, ct) });

    [HttpGet("entries/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportEntriesCsv([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => CsvFile(await GetMovementReportAsync("entries", "entrada", from, to, itemId, ct), "entries");

    [HttpGet("entries/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportEntriesXlsx([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => XlsxFile(await GetMovementReportAsync("entries", "entrada", from, to, itemId, ct), "entries");

    [HttpGet("exits")]
    public async Task<IActionResult> Exits([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => Ok(new { ok = true, data = await GetMovementReportAsync("exits", "saida", from, to, itemId, ct) });

    [HttpGet("exits/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportExitsCsv([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => CsvFile(await GetMovementReportAsync("exits", "saida", from, to, itemId, ct), "exits");

    [HttpGet("exits/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportExitsXlsx([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => XlsxFile(await GetMovementReportAsync("exits", "saida", from, to, itemId, ct), "exits");

    [HttpGet("movements")]
    public async Task<IActionResult> Movements([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => Ok(new { ok = true, data = await GetMovementReportAsync("movements", null, from, to, itemId, ct) });

    [HttpGet("movements/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportMovementsCsv([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => CsvFile(await GetMovementReportAsync("movements", null, from, to, itemId, ct), "movements");

    [HttpGet("movements/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportMovementsXlsx([FromQuery] string? from, [FromQuery] string? to, [FromQuery(Name = "item_id")] string? itemId, CancellationToken ct)
        => XlsxFile(await GetMovementReportAsync("movements", null, from, to, itemId, ct), "movements");

    private async Task<ReportTable> GetMovementReportAsync(string reportId, string? movementType, string? from, string? to, string? itemId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);

        var movements = from movement in database.StockMovements.AsNoTracking()
                        join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
                        join user in database.Users.AsNoTracking() on movement.UserId equals (Guid?)user.Id
                        join industryRow in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industryRow.Id into industries
                        from industry in industries.DefaultIfEmpty()
                        where movement.CreatedAt >= start && movement.CreatedAt < end
                        select new
                        {
                            movement.CreatedAt,
                            movement.Type,
                            item.Code,
                            ItemName = item.Name,
                            IndustryName = industry == null ? null : industry.Name,
                            movement.Quantity,
                            movement.BalanceAfter,
                            UserName = user.FullName,
                            movement.Recipient,
                            movement.Purpose,
                            movement.PurchaseTicketNo,
                            movement.ItemId
                        };
        if (movementType is not null)
            movements = movements.Where(movement => movement.Type == movementType);
        if (Guid.TryParse(itemId, out var parsedItemId))
            movements = movements.Where(movement => movement.ItemId == parsedItemId);

        var rows = await movements.OrderByDescending(movement => movement.CreatedAt).ToListAsync(ct);
        return new ReportTable(
            Types.Single(type => type.Id == reportId).Label,
            ["Data", "Tipo", "Código", "Brinde", "Indústria", "Qtd", "Saldo após", "Usuário", "Destinatário", "Finalidade", "Chamado"],
            rows.Select(row => new object?[]
            {
                row.CreatedAt, row.Type, row.Code, row.ItemName, row.IndustryName ?? "—", row.Quantity,
                row.BalanceAfter, row.UserName, row.Recipient, row.Purpose, row.PurchaseTicketNo
            }).ToArray());
    }

    [HttpGet("approvals")]
    public async Task<IActionResult> Approvals([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => Ok(new { ok = true, data = await GetApprovalsReportAsync(from, to, ct) });

    [HttpGet("approvals/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportApprovalsCsv([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => CsvFile(await GetApprovalsReportAsync(from, to, ct), "approvals");

    [HttpGet("approvals/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportApprovalsXlsx([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => XlsxFile(await GetApprovalsReportAsync(from, to, ct), "approvals");

    private async Task<ReportTable> GetApprovalsReportAsync(string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);

        var rows = await (
            from approval in database.RequestApprovals.AsNoTracking()
            join request in database.Requests.AsNoTracking() on approval.RequestId equals request.Id
            join userRow in database.Users.AsNoTracking() on approval.DecidedById equals (Guid?)userRow.Id into users
            from user in users.DefaultIfEmpty()
            join ruleRow in database.ApprovalRules.AsNoTracking() on approval.RuleId equals (Guid?)ruleRow.Id into rules
            from rule in rules.DefaultIfEmpty()
            where approval.CreatedAt >= start && approval.CreatedAt < end
            orderby approval.Id descending
            select new
            {
                RequestCode = request.Code,
                approval.Decision,
                approval.Justification,
                DecidedBy = user == null ? null : user.FullName,
                approval.DecidedAt,
                RuleName = rule == null ? null : rule.Name
            }).ToListAsync(ct);

        return new ReportTable(
            Types.Single(type => type.Id == "approvals").Label,
            ["Solicitação", "Decisão", "Justificativa", "Por", "Em", "Regra"],
            rows.Select(row => new object?[]
            {
                row.RequestCode, row.Decision, row.Justification, row.DecidedBy, row.DecidedAt, row.RuleName
            }).ToArray());
    }

    [HttpGet("protocols")]
    public async Task<IActionResult> Protocols(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "event_id")] string? eventId,
        CancellationToken ct)
        => Ok(new { ok = true, data = await GetProtocolReportAsync(from, to, eventId, ct) });

    [HttpGet("protocols/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportProtocolsCsv(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "event_id")] string? eventId,
        CancellationToken ct)
        => CsvFile(await GetProtocolReportAsync(from, to, eventId, ct), "protocols");

    [HttpGet("protocols/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportProtocolsXlsx(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "event_id")] string? eventId,
        CancellationToken ct)
        => XlsxFile(await GetProtocolReportAsync(from, to, eventId, ct), "protocols");

    private async Task<ReportTable> GetProtocolReportAsync(string? from, string? to, string? eventId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);

        var protocols = from delivery in database.Deliveries.AsNoTracking()
                        join user in database.Users.AsNoTracking() on delivery.DeliveredById equals (Guid?)user.Id
                        join eventRow in database.Events.AsNoTracking() on delivery.EventId equals (Guid?)eventRow.Id into events
                        from eventRecord in events.DefaultIfEmpty()
                        join industryRow in database.Industries.AsNoTracking() on delivery.IndustryId equals (Guid?)industryRow.Id into industries
                        from industry in industries.DefaultIfEmpty()
                        where delivery.CreatedAt >= start && delivery.CreatedAt < end
                        select new
                        {
                            delivery.Id,
                            delivery.Code,
                            delivery.Type,
                            delivery.ReceivedByName,
                            DeliveredBy = user.FullName,
                            EventName = eventRecord == null ? null : eventRecord.Name,
                            IndustryName = industry == null ? null : industry.Name,
                            delivery.CreatedAt,
                            delivery.EventId
                        };
        if (Guid.TryParse(eventId, out var parsedEventId))
            protocols = protocols.Where(protocol => protocol.EventId == parsedEventId);

        var rows = await protocols.OrderByDescending(protocol => protocol.Id).ToListAsync(ct);
        return new ReportTable(
            Types.Single(type => type.Id == "protocols").Label,
            ["Código", "Tipo", "Recebedor", "Entregue por", "Evento", "Indústria", "Data"],
            rows.Select(row => new object?[]
            {
                row.Code, row.Type, row.ReceivedByName, row.DeliveredBy, row.EventName, row.IndustryName, row.CreatedAt
            }).ToArray());
    }

    [HttpGet("event_balance")]
    public async Task<IActionResult> EventBalance([FromQuery(Name = "event_id")] string? eventId, CancellationToken ct)
    {
        if (!Guid.TryParse(eventId, out var parsedEventId))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["event_id"] = ["Informe o evento."] }));

        return Ok(new { ok = true, data = await GetEventBalanceReportAsync(parsedEventId, ct) });
    }

    [HttpGet("event_balance/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportEventBalanceCsv([FromQuery(Name = "event_id")] string? eventId, CancellationToken ct)
    {
        if (!Guid.TryParse(eventId, out var parsedEventId))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["event_id"] = ["Informe o evento."] }));

        return CsvFile(await GetEventBalanceReportAsync(parsedEventId, ct), "event_balance");
    }

    [HttpGet("event_balance/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportEventBalanceXlsx([FromQuery(Name = "event_id")] string? eventId, CancellationToken ct)
    {
        if (!Guid.TryParse(eventId, out var parsedEventId))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["event_id"] = ["Informe o evento."] }));

        return XlsxFile(await GetEventBalanceReportAsync(parsedEventId, ct), "event_balance");
    }

    private async Task<ReportTable> GetEventBalanceReportAsync(Guid eventId, CancellationToken ct)
    {
        var rows = await (
            from allocation in database.EventAllocations.AsNoTracking()
            join industry in database.Industries.AsNoTracking() on allocation.IndustryId equals industry.Id
            join item in database.CatalogItems.AsNoTracking() on allocation.ItemId equals item.Id
            where allocation.EventId == eventId
            orderby industry.Name, item.Name
            select new
            {
                IndustryName = industry.Name,
                item.Code,
                ItemName = item.Name,
                allocation.QuantityAllocated,
                allocation.QuantityWithdrawn,
                Balance = allocation.QuantityAllocated - allocation.QuantityWithdrawn
            }).ToListAsync(ct);

        return new ReportTable(
            Types.Single(type => type.Id == "event_balance").Label,
            ["Indústria", "Código", "Brinde", "Alocado", "Retirado", "Saldo"],
            rows.Select(row => new object?[]
            {
                row.IndustryName, row.Code, row.ItemName, row.QuantityAllocated, row.QuantityWithdrawn, row.Balance
            }).ToArray());
    }

    [HttpGet("stock_industry")]
    public async Task<IActionResult> StockIndustry([FromQuery(Name = "industry_id")] string? industryId, CancellationToken ct)
        => Ok(new { ok = true, data = await GetStockIndustryReportAsync(industryId, ct) });

    [HttpGet("stock_industry/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportStockIndustryCsv([FromQuery(Name = "industry_id")] string? industryId, CancellationToken ct)
        => CsvFile(await GetStockIndustryReportAsync(industryId, ct), "stock_industry");

    [HttpGet("stock_industry/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportStockIndustryXlsx([FromQuery(Name = "industry_id")] string? industryId, CancellationToken ct)
        => XlsxFile(await GetStockIndustryReportAsync(industryId, ct), "stock_industry");

    private async Task<ReportTable> GetStockIndustryReportAsync(string? industryId, CancellationToken ct)
    {
        Guid? selectedIndustryId = Guid.TryParse(industryId, out var parsedIndustryId) ? parsedIndustryId : null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var userIndustryId = await database.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.IndustryId)
                .SingleOrDefaultAsync(ct);
            if (userIndustryId.HasValue)
                selectedIndustryId = userIndustryId;
        }

        var rows = await (
            from movement in database.StockMovements.AsNoTracking()
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            join industry in database.Industries.AsNoTracking() on (movement.IndustryId ?? item.IndustryId) equals (Guid?)industry.Id
            where item.DeletedAt == null
                && (movement.Type == "entrada" || movement.Type == "saida" || movement.Type == "ajuste" || movement.Type == "estorno")
                && (!selectedIndustryId.HasValue || (movement.IndustryId ?? item.IndustryId) == selectedIndustryId.Value)
            group movement by new
            {
                IndustryId = industry.Id,
                IndustryName = industry.Name,
                ItemId = item.Id,
                ItemCode = item.Code,
                ItemName = item.Name,
                ItemKind = item.Kind
            }
            into grouped
            orderby grouped.Key.IndustryName, grouped.Key.ItemName
            select new
            {
                IndustryId = grouped.Key.IndustryId,
                ItemId = grouped.Key.ItemId,
                grouped.Key.IndustryName,
                grouped.Key.ItemCode,
                grouped.Key.ItemName,
                Received = grouped.Sum(movement => movement.Type == "entrada" ? movement.Quantity : 0),
                Withdrawn = grouped.Sum(movement => movement.Type == "saida" ? -movement.Quantity : 0),
                Balance = grouped.Sum(movement => movement.Quantity)
            }).ToListAsync(ct);

        var unattributedReceiptAdditions = await StockIndustryAttribution.GetUnassignedReceiptAdditionsAsync(database, ct);
        var attributedRows = rows.Select(row =>
        {
            var addition = unattributedReceiptAdditions.TryGetValue((row.IndustryId, row.ItemId), out var quantity) ? quantity : 0;
            return new
            {
                row.IndustryName,
                row.ItemCode,
                row.ItemName,
                Received = row.Received + addition,
                row.Withdrawn,
                Balance = row.Balance + addition
            };
        }).ToList();

        return new ReportTable(
            Types.Single(type => type.Id == "stock_industry").Label,
            ["Indústria", "Código", "Brinde", "Recebido", "Retirado", "Saldo"],
            attributedRows.Select(row => new object?[]
            {
                row.IndustryName, row.ItemCode, row.ItemName, row.Received, row.Withdrawn, row.Balance
            }).ToArray());
    }

    [HttpGet("withdrawals")]
    public async Task<IActionResult> Withdrawals(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "industry_id")] string? industryId,
        [FromQuery(Name = "event_id")] string? eventId,
        CancellationToken ct)
        => Ok(new { ok = true, data = await GetWithdrawalsReportAsync(from, to, industryId, eventId, ct) });

    [HttpGet("withdrawals/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportWithdrawalsCsv(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "industry_id")] string? industryId,
        [FromQuery(Name = "event_id")] string? eventId,
        CancellationToken ct)
        => CsvFile(await GetWithdrawalsReportAsync(from, to, industryId, eventId, ct), "withdrawals");

    [HttpGet("withdrawals/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportWithdrawalsXlsx(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery(Name = "industry_id")] string? industryId,
        [FromQuery(Name = "event_id")] string? eventId,
        CancellationToken ct)
        => XlsxFile(await GetWithdrawalsReportAsync(from, to, industryId, eventId, ct), "withdrawals");

    private async Task<ReportTable> GetWithdrawalsReportAsync(string? from, string? to, string? industryId, string? eventId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);
        Guid? selectedIndustryId = Guid.TryParse(industryId, out var parsedIndustryId) ? parsedIndustryId : null;
        Guid? selectedEventId = Guid.TryParse(eventId, out var parsedEventId) ? parsedEventId : null;

        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var userIndustryId = await database.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.IndustryId)
                .SingleOrDefaultAsync(ct);
            if (userIndustryId.HasValue)
                selectedIndustryId = userIndustryId;
        }

        var withdrawals = from movement in database.StockMovements.AsNoTracking()
                          join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
                          join user in database.Users.AsNoTracking() on movement.UserId equals (Guid?)user.Id
                          join industryRow in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industryRow.Id into industries
                          from industry in industries.DefaultIfEmpty()
                          join deliveryRow in database.Deliveries.AsNoTracking() on movement.DeliveryId equals (Guid?)deliveryRow.Id into deliveries
                          from delivery in deliveries.DefaultIfEmpty()
                          join eventRow in database.Events.AsNoTracking() on delivery.EventId equals (Guid?)eventRow.Id into events
                          from eventRecord in events.DefaultIfEmpty()
                          where movement.Type == "saida" && movement.CreatedAt >= start && movement.CreatedAt < end
                              && (!selectedIndustryId.HasValue || movement.IndustryId == selectedIndustryId.Value)
                              && (!selectedEventId.HasValue || (delivery != null && delivery.EventId == selectedEventId.Value))
                          orderby movement.CreatedAt descending
                          select new
                          {
                              movement.CreatedAt,
                              IndustryName = industry == null ? null : industry.Name,
                              item.Code,
                              ItemName = item.Name,
                              Quantity = -movement.Quantity,
                              movement.Recipient,
                              UserName = user.FullName,
                              EventName = eventRecord == null ? null : eventRecord.Name,
                              movement.Purpose
                          };
        var rows = await withdrawals.ToListAsync(ct);

        return new ReportTable(
            "Retiradas",
            ["Data/hora", "Indústria", "Código", "Brinde", "Qtd", "Quem retirou", "Responsável", "Evento", "Finalidade"],
            rows.Select(row => new object?[]
            {
                row.CreatedAt, row.IndustryName, row.Code, row.ItemName, row.Quantity,
                row.Recipient, row.UserName, row.EventName, row.Purpose
            }).ToArray());
    }

    [HttpGet("by_requester")]
    public async Task<IActionResult> ByRequester([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => Ok(new { ok = true, data = await GetByRequesterReportAsync(from, to, ct) });

    [HttpGet("by_requester/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByRequesterCsv([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => CsvFile(await GetByRequesterReportAsync(from, to, ct), "by_requester");

    [HttpGet("by_requester/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByRequesterXlsx([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => XlsxFile(await GetByRequesterReportAsync(from, to, ct), "by_requester");

    private async Task<ReportTable> GetByRequesterReportAsync(string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);
        var scope = await GetUserScopeAsync(ct);
        var movements = await (
            from movement in database.StockMovements.AsNoTracking()
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            join deliveryRow in database.Deliveries.AsNoTracking() on movement.DeliveryId equals (Guid?)deliveryRow.Id into deliveries
            from delivery in deliveries.DefaultIfEmpty()
            join requestRow in database.Requests.AsNoTracking() on (delivery == null ? null : delivery.RequestId) equals (Guid?)requestRow.Id into requests
            from linkedRequest in requests.DefaultIfEmpty()
            join requester in database.Users.AsNoTracking()
                on (linkedRequest == null ? movement.UserId : (Guid?)linkedRequest.RequesterId) equals (Guid?)requester.Id
            join industryRow in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industryRow.Id into industries
            from industry in industries.DefaultIfEmpty()
            where movement.Type == "saida" && movement.CreatedAt >= start && movement.CreatedAt < end
                && (!scope.IndustryId.HasValue || movement.IndustryId == scope.IndustryId.Value)
            select new
            {
                RequesterName = requester.FullName,
                IndustryName = industry == null ? null : industry.Name,
                movement.Quantity,
                item.UnitValue
            }).ToListAsync(ct);
        var grouped = movements.GroupBy(row => row.RequesterName, StringComparer.Ordinal)
            .Select(group => new
            {
                Name = group.Key,
                Industry = string.Join(", ", group.Where(row => row.IndustryName is not null)
                    .Select(row => row.IndustryName!).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)),
                Units = group.Sum(row => -row.Quantity),
                Value = group.Sum(row => -(decimal)row.Quantity * (row.UnitValue ?? 0m))
            })
            .OrderByDescending(row => row.Units)
            .ToArray();

        return new ReportTable(
            Types.Single(type => type.Id == "by_requester").Label,
            ["Nome", "Indústria", "Unidades", "Valor"],
            grouped.Select(row => new object?[]
            { row.Name, row.Industry.Length > 0 ? row.Industry : "—", row.Units, row.Value }).ToArray());
    }

    [HttpGet("by_industry")]
    public async Task<IActionResult> ByIndustry([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => Ok(new { ok = true, data = await GetByIndustryReportAsync(from, to, ct) });

    [HttpGet("by_industry/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByIndustryCsv([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => CsvFile(await GetByIndustryReportAsync(from, to, ct), "by_industry");

    [HttpGet("by_industry/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByIndustryXlsx([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => XlsxFile(await GetByIndustryReportAsync(from, to, ct), "by_industry");

    private async Task<ReportTable> GetByIndustryReportAsync(string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);
        var scope = await GetUserScopeAsync(ct);
        var grouped = await (
            from movement in database.StockMovements.AsNoTracking()
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            join industryRow in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industryRow.Id into industries
            from industry in industries.DefaultIfEmpty()
            where movement.Type == "saida" && movement.CreatedAt >= start && movement.CreatedAt < end
                && (!scope.IndustryId.HasValue || movement.IndustryId == scope.IndustryId.Value)
            group new { movement.Quantity, item.UnitValue }
                by new { IndustryId = industry == null ? (Guid?)null : industry.Id, IndustryName = industry == null ? null : industry.Name } into movements
            select new
            {
                movements.Key.IndustryId,
                movements.Key.IndustryName,
                Units = movements.Sum(row => -row.Quantity),
                Value = movements.Sum(row => -(decimal)row.Quantity * (row.UnitValue ?? 0m))
            }).OrderByDescending(row => row.Units).ToListAsync(ct);

        return new ReportTable(
            Types.Single(type => type.Id == "by_industry").Label,
            ["Nome", "Unidades", "Valor"],
            grouped.Select(row => new object?[] { row.IndustryName ?? "—", row.Units, row.Value }).ToArray());
    }

    [HttpGet("by_department")]
    public async Task<IActionResult> ByDepartment([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => Ok(new { ok = true, data = await GetByDepartmentReportAsync(from, to, ct) });

    [HttpGet("by_department/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByDepartmentCsv([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => CsvFile(await GetByDepartmentReportAsync(from, to, ct), "by_department");

    [HttpGet("by_department/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByDepartmentXlsx([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => XlsxFile(await GetByDepartmentReportAsync(from, to, ct), "by_department");

    private async Task<ReportTable> GetByDepartmentReportAsync(string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);
        var scope = await GetUserScopeAsync(ct);
        var exits = database.StockMovements.AsNoTracking()
            .Where(movement => movement.Type == "saida" && movement.CreatedAt >= start && movement.CreatedAt < end
                && (!scope.IndustryId.HasValue || movement.IndustryId == scope.IndustryId.Value)
                && (!scope.DepartmentId.HasValue || movement.DepartmentId == scope.DepartmentId.Value));
        var grouped = await (
            from movement in exits
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            join departmentRow in database.Departments.AsNoTracking() on movement.DepartmentId equals (Guid?)departmentRow.Id into departments
            from department in departments.DefaultIfEmpty()
            group new { movement.Quantity, movement.ItemId, item.UnitValue }
                by new { DepartmentId = department == null ? (Guid?)null : department.Id, DepartmentName = department == null ? null : department.Name } into movements
            select new
            {
                movements.Key.DepartmentId,
                movements.Key.DepartmentName,
                Units = movements.Sum(row => -row.Quantity),
                Value = movements.Sum(row => -(decimal)row.Quantity * (row.UnitValue ?? 0m))
            }).OrderByDescending(row => row.Units).ToListAsync(ct);
        var departmentIds = grouped.Where(row => row.DepartmentId.HasValue).Select(row => row.DepartmentId!.Value).ToArray();
        var industryRows = departmentIds.Length == 0
            ? []
            : await (from movement in exits
                     join industry in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industry.Id
                     where movement.DepartmentId.HasValue && departmentIds.Contains(movement.DepartmentId.Value)
                     select new { movement.DepartmentId, industry.Name }).Distinct().ToListAsync(ct);
        var industryByDepartment = industryRows.GroupBy(row => row.DepartmentId!.Value)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal)));

        return new ReportTable(
            Types.Single(type => type.Id == "by_department").Label,
            ["Nome", "Indústria", "Unidades", "Valor"],
            grouped.Select(row => new object?[]
            {
                row.DepartmentName ?? "—",
                row.DepartmentId.HasValue && industryByDepartment.TryGetValue(row.DepartmentId.Value, out var industries) && industries.Length > 0 ? industries : "—",
                row.Units, row.Value
            }).ToArray());
    }

    [HttpGet("by_item")]
    public async Task<IActionResult> ByItem([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => Ok(new { ok = true, data = await GetByItemReportAsync(from, to, ct) });

    [HttpGet("by_item/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByItemCsv([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => CsvFile(await GetByItemReportAsync(from, to, ct), "by_item");

    [HttpGet("by_item/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportByItemXlsx([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => XlsxFile(await GetByItemReportAsync(from, to, ct), "by_item");

    private async Task<ReportTable> GetByItemReportAsync(string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);
        var scope = await GetUserScopeAsync(ct);
        var exits = database.StockMovements.AsNoTracking()
            .Where(movement => movement.Type == "saida" && movement.CreatedAt >= start && movement.CreatedAt < end
                && (!scope.IndustryId.HasValue || movement.IndustryId == scope.IndustryId.Value));
        var grouped = await (
            from movement in exits
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            group movement by new { item.Id, item.Code, item.Name, item.UnitValue } into movements
            select new
            {
                movements.Key.Id,
                movements.Key.Code,
                movements.Key.Name,
                Units = movements.Sum(movement => -movement.Quantity),
                Value = movements.Sum(movement => -(decimal)movement.Quantity * (movements.Key.UnitValue ?? 0m))
            }).OrderByDescending(row => row.Units).ToListAsync(ct);
        var itemIds = grouped.Select(row => row.Id).ToArray();
        var industryRows = itemIds.Length == 0
            ? []
            : await (from movement in exits
                     join industry in database.Industries.AsNoTracking() on movement.IndustryId equals (Guid?)industry.Id
                     where itemIds.Contains(movement.ItemId)
                     select new { movement.ItemId, industry.Name }).Distinct().ToListAsync(ct);
        var industryByItem = industryRows.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal)));

        return new ReportTable(
            Types.Single(type => type.Id == "by_item").Label,
            ["Código", "Nome", "Indústria", "Unidades", "Valor"],
            grouped.Select(row => new object?[]
            {
                row.Code, row.Name,
                industryByItem.GetValueOrDefault(row.Id) is { Length: > 0 } industries ? industries : "—",
                row.Units, row.Value
            }).ToArray());
    }

    [HttpGet("requests")]
    public async Task<IActionResult> Requests([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => Ok(new { ok = true, data = await GetRequestsReportAsync(from, to, ct) });

    [HttpGet("requests/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportRequestsCsv([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => CsvFile(await GetRequestsReportAsync(from, to, ct), "requests");

    [HttpGet("requests/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportRequestsXlsx([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
        => XlsxFile(await GetRequestsReportAsync(from, to, ct), "requests");

    private async Task<ReportTable> GetRequestsReportAsync(string? from, string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var endDate = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = EndExclusive(endDate);

        var rows = await (
            from request in database.Requests.AsNoTracking()
            join requester in database.Users.AsNoTracking() on request.RequesterId equals requester.Id
            join departmentRow in database.Departments.AsNoTracking() on request.DepartmentId equals (Guid?)departmentRow.Id into departments
            from department in departments.DefaultIfEmpty()
            join industryRow in database.Industries.AsNoTracking() on request.IndustryId equals (Guid?)industryRow.Id into industries
            from industry in industries.DefaultIfEmpty()
            where request.DeletedAt == null && request.Flow == "interna"
                && request.CreatedAt >= start && request.CreatedAt < end
            orderby request.CreatedAt descending
            select new
            {
                request.Code,
                request.Status,
                Requester = requester.FullName,
                Department = department == null ? null : department.Name,
                Industry = industry == null ? null : industry.Name,
                request.Purpose,
                request.Recipient,
                request.PurchaseTicketNo,
                request.TotalValue,
                request.CreatedAt,
                FinalizedAt = request.Status == "finalizada" ? request.StatusChangedAt : null
            }).ToListAsync(ct);

        return new ReportTable(
            "Solicitações",
            ["Código", "Status", "Solicitante", "Depto", "Indústria", "Finalidade", "Destinatário", "Chamado", "Valor", "Criada", "Finalizada"],
            rows.Select(row => new object?[]
            {
                row.Code, row.Status, row.Requester, row.Department, row.Industry, row.Purpose, row.Recipient,
                row.PurchaseTicketNo, row.TotalValue, row.CreatedAt, row.FinalizedAt
            }).ToArray());
    }

    [HttpGet("event_report")]
    public async Task<IActionResult> EventReport([FromQuery(Name = "event_id")] string? eventId, CancellationToken ct)
    {
        if (!Guid.TryParse(eventId, out var parsedEventId))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["event_id"] = ["Informe o evento."] }));

        return Ok(new { ok = true, data = await GetEventReportAsync(parsedEventId, ct) });
    }

    [HttpGet("event_report/export.csv")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportEventReportCsv([FromQuery(Name = "event_id")] string? eventId, CancellationToken ct)
    {
        if (!Guid.TryParse(eventId, out var parsedEventId))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["event_id"] = ["Informe o evento."] }));

        return CsvFile(await GetEventReportAsync(parsedEventId, ct), "event_report");
    }

    [HttpGet("event_report/export.xlsx")]
    [Authorize(Policy = "reports.export")]
    public async Task<IActionResult> ExportEventReportXlsx([FromQuery(Name = "event_id")] string? eventId, CancellationToken ct)
    {
        if (!Guid.TryParse(eventId, out var parsedEventId))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["event_id"] = ["Informe o evento."] }));

        return XlsxFile(await GetEventReportAsync(parsedEventId, ct), "event_report");
    }

    private async Task<ReportTable> GetEventReportAsync(Guid parsedEventId, CancellationToken ct)
    {
        var eventName = await database.Events.AsNoTracking()
            .Where(eventRecord => eventRecord.Id == parsedEventId)
            .Select(eventRecord => eventRecord.Name)
            .SingleOrDefaultAsync(ct);
        var allocations = await (
            from allocation in database.EventAllocations.AsNoTracking()
            join industry in database.Industries.AsNoTracking() on allocation.IndustryId equals industry.Id
            join item in database.CatalogItems.AsNoTracking() on allocation.ItemId equals item.Id
            where allocation.EventId == parsedEventId
            orderby industry.Name, item.Name
            select new
            {
                Industry = industry.Name,
                item.Code,
                ItemName = item.Name,
                Initial = allocation.QuantityAllocated,
                Withdrawn = allocation.QuantityWithdrawn,
                Balance = allocation.QuantityAllocated - allocation.QuantityWithdrawn
            }).ToListAsync(ct);
        var deliveries = await (
            from delivery in database.Deliveries.AsNoTracking()
            join deliveryItem in database.DeliveryItems.AsNoTracking() on delivery.Id equals deliveryItem.DeliveryId
            join item in database.CatalogItems.AsNoTracking() on deliveryItem.ItemId equals item.Id
            join industryRow in database.Industries.AsNoTracking() on delivery.IndustryId equals (Guid?)industryRow.Id into industries
            from industry in industries.DefaultIfEmpty()
            where delivery.EventId == parsedEventId
            orderby delivery.CreatedAt
            select new
            {
                delivery.CreatedAt,
                delivery.ReceivedByName,
                ItemName = item.Name,
                deliveryItem.Quantity,
                Industry = industry == null ? null : industry.Name
            }).ToListAsync(ct);

        var rows = allocations.Select(row => new object?[]
            { row.Industry, row.Code, row.ItemName, row.Initial, row.Withdrawn, row.Balance })
            .Concat(deliveries.Select(row => new object?[]
            { "RETIRADA", row.CreatedAt, row.Industry, row.ItemName, row.Quantity, row.ReceivedByName }))
            .ToArray();

        return new ReportTable(
            $"Evento: {eventName ?? parsedEventId.ToString()}",
            ["Indústria / tipo", "Código / data", "Brinde", "Estoque inicial", "Retirado", "Saldo / quem"],
            rows);
    }
}
