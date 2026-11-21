using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Imports;

public sealed class ImportPreviewService(ApplicationDbContext database)
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace DocumentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public async Task<ItemsPreview> PreviewItemsAsync(Stream input, CancellationToken ct)
    {
        var sourceRows = ReadRows(input);
        if (sourceRows.Count == 0)
            throw new InvalidDataException("Planilha vazia.");

        var result = new List<ImportPreviewRow>();
        foreach (var (line, data) in sourceRows)
        {
            var name = Get(data, "nome", "name", "brinde");
            var code = Get(data, "codigo", "code");
            var categoryName = Get(data, "categoria", "category");
            if (categoryName.Length == 0)
                categoryName = "Geral";
            var errors = new List<string>();
            if (name.Length == 0)
                errors.Add("Nome obrigatório");
            if (code.Length > 40 || (code.Length > 0 && (!char.IsAsciiLetterOrDigit(code[0]) || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '/')))))
                errors.Add("Código inválido");
            var unitValueText = Get(data, "valor_unitario", "valor");
            if (unitValueText.Length > 0 && (!decimal.TryParse(unitValueText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var unitValue) || unitValue is < 0 or > 9_999_999_999m))
                errors.Add("Valor unitário inválido");
            var minStockText = Get(data, "estoque_minimo");
            var minStockValid = minStockText.Length == 0 || int.TryParse(minStockText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            var minStock = ParseInt(minStockText);
            if (!minStockValid || minStock is < 0 or > 1_000_000)
                errors.Add("Estoque mínimo inválido");
            var initialQuantityText = Get(data, "quantidade_inicial", "quantidade");
            var initialQuantityValid = initialQuantityText.Length == 0 || int.TryParse(initialQuantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            var initialQuantity = ParseInt(initialQuantityText);
            if (!initialQuantityValid || initialQuantity is < 0 or > 1_000_000)
                errors.Add("Quantidade inicial inválida");

            var categoryId = await database.Categories.AsNoTracking()
                .Where(category => category.DeletedAt == null && category.Name == categoryName)
                .Select(category => (Guid?)category.Id).FirstOrDefaultAsync(ct);
            if (categoryId is null)
                errors.Add($"Categoria não encontrada: {categoryName}");
            var industryName = Get(data, "industria", "industry");
            var industryId = await LookupIndustryAsync(industryName, ct);
            if (industryName.Length > 0 && industryId is null)
                errors.Add($"Indústria não encontrada: {industryName}");

            var locationId = await LookupAsync(database.Locations.Where(location => location.DeletedAt == null), Get(data, "local", "location"), ct);
            var supplierId = await LookupAsync(database.Suppliers.Where(supplier => supplier.DeletedAt == null), Get(data, "fornecedor", "supplier"), ct);
            var payload = new ImportItemPayload(
                code.Length > 0 ? code : null,
                name,
                categoryId,
                industryId,
                locationId,
                supplierId,
                unitValueText.Length > 0 ? unitValueText : null,
                minStock,
                initialQuantity,
                Get(data, "observacoes", "notes") is { Length: > 0 } notes ? notes : null);
            result.Add(new ImportPreviewRow(line, errors.Count == 0, errors, payload, name));
        }
        return new ItemsPreview(result, result.Count(row => !row.Ok), result.Count);
    }

    public async Task<RequestsPreview> PreviewRequestsAsync(Stream input, CancellationToken ct)
    {
        var sourceRows = ReadRows(input);
        if (sourceRows.Count == 0)
            throw new InvalidDataException("Planilha vazia.");

        var result = new List<ImportPreviewRow>();
        foreach (var (line, data) in sourceRows)
        {
            var itemName = Get(data, "brinde", "item", "nome");
            var quantity = ParseInt(Get(data, "quantidade", "qty"));
            var errors = new List<string>();
            var itemId = await database.CatalogItems.AsNoTracking()
                .Where(item => item.DeletedAt == null && (item.Code.ToUpper() == itemName.ToUpper() || item.Name == itemName))
                .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(ct);
            if (itemId is null)
                errors.Add($"Brinde não encontrado: {itemName}");
            if (quantity < 1)
                errors.Add("Quantidade inválida");
            var neededDateText = Get(data, "data_necessaria", "data");
            var neededDate = ParseDate(neededDateText);
            if (neededDate is null && HasRecognizedDateShape(neededDateText))
                errors.Add("Data necessária inválida");
            var payload = new ImportRequestPayload(
                Get(data, "finalidade", "purpose") is { Length: > 0 } purpose ? purpose : "Importado da planilha",
                Get(data, "destinatario", "recipient") is { Length: > 0 } recipient ? recipient : null,
                Get(data, "chamado_compra", "chamado") is { Length: > 0 } ticket ? ticket : null,
                neededDate,
                await LookupIndustryAsync(Get(data, "industria", "industry"), ct),
                await LookupDepartmentAsync(Get(data, "departamento", "department"), ct),
                Get(data, "observacoes") is { Length: > 0 } notes ? notes : null,
                [new ImportRequestItem(itemId, Math.Max(1, quantity))]);
            result.Add(new ImportPreviewRow(line, errors.Count == 0, errors, payload, $"{itemName} × {quantity}"));
        }
        return new RequestsPreview(result, result.Count(row => !row.Ok), result.Count);
    }

    private Task<Guid?> LookupIndustryAsync(string name, CancellationToken ct) => name.Length == 0
        ? Task.FromResult<Guid?>(null)
        : database.Industries.AsNoTracking().Where(industry => industry.DeletedAt == null && industry.Name == name)
            .Select(industry => (Guid?)industry.Id).FirstOrDefaultAsync(ct);

    private Task<Guid?> LookupDepartmentAsync(string name, CancellationToken ct) => name.Length == 0
        ? Task.FromResult<Guid?>(null)
        : database.Departments.AsNoTracking().Where(department => department.DeletedAt == null && department.Name == name)
            .Select(department => (Guid?)department.Id).FirstOrDefaultAsync(ct);

    private static async Task<Guid?> LookupAsync(IQueryable<ContactLookup> source, string name, CancellationToken ct)
    {
        if (name.Length == 0)
            return null;
        return await source.Where(value => value.Name == name).Select(value => (Guid?)value.Id).FirstOrDefaultAsync(ct);
    }

    private static async Task<Guid?> LookupAsync(IQueryable<Location> source, string name, CancellationToken ct)
    {
        if (name.Length == 0)
            return null;
        return await source.Where(value => value.Name == name).Select(value => (Guid?)value.Id).FirstOrDefaultAsync(ct);
    }

    private static int ParseInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static DateOnly? ParseDate(string value)
    {
        if (DateOnly.TryParseExact(value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return date;
        return null;
    }

    private static bool HasRecognizedDateShape(string value) =>
        Regex.IsMatch(value, "^[0-9]{2}/[0-9]{2}/[0-9]{4}$", RegexOptions.CultureInvariant)
        || Regex.IsMatch(value, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$", RegexOptions.CultureInvariant);

    private static string Get(IReadOnlyDictionary<string, string> data, params string[] keys)
    {
        foreach (var key in keys)
            if (data.TryGetValue(key, out var value))
                return value;
        return string.Empty;
    }

    private static List<(int Line, Dictionary<string, string> Data)> ReadRows(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 128)
            throw new InvalidDataException("Arquivo Excel inválido.");
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            expandedBytes += entry.Length;
            if (expandedBytes > 20 * 1024 * 1024)
                throw new InvalidDataException("Arquivo Excel excede o limite permitido.");
        }

        var workbook = ReadXml(archive, "xl/workbook.xml");
        var relationshipId = workbook.Root?.Element(Spreadsheet + "sheets")?.Elements(Spreadsheet + "sheet").FirstOrDefault()?.Attribute(DocumentRelationships + "id")?.Value;
        var relationships = ReadXml(archive, "xl/_rels/workbook.xml.rels");
        var target = relationships.Root?.Elements(PackageRelationships + "Relationship")
            .FirstOrDefault(item => (string?)item.Attribute("Id") == relationshipId)?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidDataException("Planilha Excel sem aba ativa.");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.Replace("../", string.Empty, StringComparison.Ordinal);
        var sheet = ReadXml(archive, sheetPath);
        var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
        var sharedStrings = sharedStringsEntry is null
            ? []
            : ReadXml(archive, "xl/sharedStrings.xml").Root?.Elements(Spreadsheet + "si")
                .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value))).ToArray() ?? [];
        var rows = sheet.Root?.Element(Spreadsheet + "sheetData")?.Elements(Spreadsheet + "row").ToArray() ?? [];
        if (rows.Length == 0)
            return [];

        var values = rows.Select(row => ReadCells(row, sharedStrings)).ToArray();
        var headers = values[0].Select(NormalizeHeader).ToArray();
        var result = new List<(int Line, Dictionary<string, string> Data)>();
        for (var rowIndex = 1; rowIndex < values.Length; rowIndex++)
        {
            var cells = values[rowIndex];
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var column = 0; column < headers.Length; column++)
                if (headers[column].Length > 0)
                    data[headers[column]] = column < cells.Length ? cells[column].Trim() : string.Empty;
            var line = int.TryParse((string?)rows[rowIndex].Attribute("r"), out var declaredLine) ? declaredLine : rowIndex + 1;
            result.Add((line, data));
        }
        return result;
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path) ?? throw new InvalidDataException("Arquivo Excel inválido.");
        using var stream = entry.Open();
        try
        {
            return XDocument.Load(stream, LoadOptions.None);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidDataException("Arquivo Excel inválido.", exception);
        }
    }

    private static string[] ReadCells(XElement row, string[] sharedStrings)
    {
        var cells = new SortedDictionary<int, string>();
        foreach (var cell in row.Elements(Spreadsheet + "c"))
        {
            var reference = (string?)cell.Attribute("r") ?? string.Empty;
            var columnText = Regex.Match(reference, "^[A-Z]+").Value;
            var column = 0;
            foreach (var character in columnText)
                column = column * 26 + character - 'A' + 1;
            if (column == 0)
                continue;
            var value = cell.Attribute("t")?.Value switch
            {
                "inlineStr" => string.Concat(cell.Descendants(Spreadsheet + "t").Select(text => text.Value)),
                "s" => int.TryParse(cell.Element(Spreadsheet + "v")?.Value, out var index) && index >= 0 && index < sharedStrings.Length ? sharedStrings[index] : string.Empty,
                _ => cell.Element(Spreadsheet + "v")?.Value ?? string.Empty
            };
            cells[column - 1] = value;
        }
        return cells.Count == 0 ? [] : Enumerable.Range(0, cells.Keys.Max() + 1).Select(index => cells.GetValueOrDefault(index, string.Empty)).ToArray();
    }

    private static string NormalizeHeader(string header)
    {
        var decomposed = header.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var unaccented = new string(decomposed.Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(unaccented, "[^a-z0-9]+", "_").Trim('_');
    }
}

public sealed record ItemsPreview(IReadOnlyList<ImportPreviewRow> Rows, int Errors, int Total);
public sealed record ImportPreviewRow(int Line, bool Ok, IReadOnlyList<string> Errors, object Payload, string Preview);
public sealed record ImportItemPayload(string? Code, string Name, Guid? CategoryId, Guid? IndustryId, Guid? LocationId, Guid? SupplierId, string? UnitValue, int MinStock, int InitialQuantity, string? Notes)
{
    [JsonPropertyName("code")] public string? Code { get; init; } = Code;
    [JsonPropertyName("name")] public string Name { get; init; } = Name;
    [JsonPropertyName("category_id")] public Guid? CategoryId { get; init; } = CategoryId;
    [JsonPropertyName("industry_id")] public Guid? IndustryId { get; init; } = IndustryId;
    [JsonPropertyName("location_id")] public Guid? LocationId { get; init; } = LocationId;
    [JsonPropertyName("supplier_id")] public Guid? SupplierId { get; init; } = SupplierId;
    [JsonPropertyName("unit_value")] public string? UnitValue { get; init; } = UnitValue;
    [JsonPropertyName("min_stock")] public int MinStock { get; init; } = MinStock;
    [JsonPropertyName("initial_quantity")] public int InitialQuantity { get; init; } = InitialQuantity;
    [JsonPropertyName("notes")] public string? Notes { get; init; } = Notes;
}

public sealed record RequestsPreview(IReadOnlyList<ImportPreviewRow> Rows, int Errors, int Total);
public sealed record ImportRequestItem(Guid? ItemId, int QtyRequested)
{
    [JsonPropertyName("item_id")] public Guid? ItemId { get; init; } = ItemId;
    [JsonPropertyName("qty_requested")] public int QtyRequested { get; init; } = QtyRequested;
}

public sealed record ImportRequestPayload(string Purpose, string? Recipient, string? PurchaseTicketNo, DateOnly? NeededDate, Guid? IndustryId, Guid? DepartmentId, string? Notes, IReadOnlyList<ImportRequestItem> Items)
{
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = Purpose;
    [JsonPropertyName("recipient")] public string? Recipient { get; init; } = Recipient;
    [JsonPropertyName("purchase_ticket_no")] public string? PurchaseTicketNo { get; init; } = PurchaseTicketNo;
    [JsonPropertyName("needed_date")] public DateOnly? NeededDate { get; init; } = NeededDate;
    [JsonPropertyName("industry_id")] public Guid? IndustryId { get; init; } = IndustryId;
    [JsonPropertyName("department_id")] public Guid? DepartmentId { get; init; } = DepartmentId;
    [JsonPropertyName("notes")] public string? Notes { get; init; } = Notes;
    [JsonPropertyName("items")] public IReadOnlyList<ImportRequestItem> Items { get; init; } = Items;
}
