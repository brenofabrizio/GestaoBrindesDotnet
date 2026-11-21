using Gbrindes.Web.Data;
using SkiaSharp;

namespace Gbrindes.Web.Protocols;

public sealed class DeliveryPdfGenerator
{
    private const float PageWidth = 595;
    private const float PageHeight = 842;
    private const float LeftMargin = 44;
    private const float RightMargin = 44;
    private const float BottomMargin = 48;

    public byte[] Generate(Delivery delivery, byte[]? signatureBytes, byte[]? verificationQrBytes = null)
    {
        using var output = new MemoryStream();
        using var document = SKDocument.CreatePdf(output);
        var canvas = document.BeginPage(PageWidth, PageHeight);
        var cursorY = 48f;

        using var titleFont = CreateFont(20);
        using var headingFont = CreateFont(13);
        using var bodyFont = CreateFont(10);
        using var mutedFont = CreateFont(9);
        using var titlePaint = CreatePaint(SKColors.DarkSlateGray);
        using var headingPaint = CreatePaint(SKColors.DarkSlateGray);
        using var bodyPaint = CreatePaint(SKColors.Black);
        using var mutedPaint = CreatePaint(SKColors.DarkSlateGray);
        using var rulePaint = new SKPaint { Color = SKColors.LightSlateGray, StrokeWidth = 0.7f, IsAntialias = true };

        void EnsureSpace(float height)
        {
            if (cursorY + height <= PageHeight - BottomMargin)
                return;
            document.EndPage();
            canvas = document.BeginPage(PageWidth, PageHeight);
            cursorY = 48;
        }

        void DrawWrapped(string? text, SKFont font, SKPaint paint, float indent = LeftMargin)
        {
            var availableWidth = PageWidth - RightMargin - indent;
            foreach (var line in WrapText(string.IsNullOrWhiteSpace(text) ? "—" : text, font, paint, availableWidth))
            {
                EnsureSpace(font.Size + 8);
                canvas.DrawText(line, indent, cursorY + font.Size, SKTextAlign.Left, font, paint);
                cursorY += font.Size + 8;
            }
        }

        DrawWrapped($"Protocolo de entrega {delivery.Code}", titleFont, titlePaint);
        cursorY += 8;
        canvas.DrawLine(LeftMargin, cursorY, PageWidth - RightMargin, cursorY, rulePaint);
        cursorY += 14;
        DrawWrapped($"Data: {delivery.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}", bodyFont, bodyPaint);
        DrawWrapped($"Tipo: {delivery.Type}", bodyFont, bodyPaint);
        DrawWrapped($"Recebido por: {delivery.ReceivedByName}", bodyFont, bodyPaint);
        if (!string.IsNullOrWhiteSpace(delivery.Request?.Code) || !string.IsNullOrWhiteSpace(delivery.Request?.Purpose))
            DrawWrapped($"Solicitação: {delivery.Request?.Code ?? "—"} — {delivery.Request?.Purpose ?? "—"}", bodyFont, bodyPaint);
        if (!string.IsNullOrWhiteSpace(delivery.Notes))
            DrawWrapped($"Observações: {delivery.Notes}", bodyFont, bodyPaint);

        cursorY += 10;
        DrawWrapped("Itens entregues", headingFont, headingPaint);
        canvas.DrawLine(LeftMargin, cursorY, PageWidth - RightMargin, cursorY, rulePaint);
        cursorY += 9;
        foreach (var line in delivery.Items.OrderBy(item => item.Id))
        {
            var itemName = line.Item?.Name ?? "Item";
            var itemCode = line.Item?.Code ?? "—";
            DrawWrapped($"{itemCode} — {itemName}", bodyFont, bodyPaint);
            DrawWrapped($"Quantidade: {line.Quantity}    Saldo após: {line.BalanceAfter}", mutedFont, mutedPaint, LeftMargin + 12);
            cursorY += 4;
        }

        cursorY += 8;
        DrawWrapped("Assinatura", headingFont, headingPaint);
        using var signature = signatureBytes is { Length: > 0 } ? SKBitmap.Decode(signatureBytes) : null;
        if (signature is not null)
        {
            const float maxWidth = 260;
            const float maxHeight = 96;
            var scale = Math.Min(maxWidth / signature.Width, maxHeight / signature.Height);
            var width = signature.Width * scale;
            var height = signature.Height * scale;
            EnsureSpace(height + 12);
            canvas.DrawBitmap(signature, new SKRect(LeftMargin, cursorY, LeftMargin + width, cursorY + height), SKSamplingOptions.Default);
            cursorY += height + 12;
        }
        else
        {
            DrawWrapped("Assinatura não disponível", bodyFont, bodyPaint);
        }

        cursorY += 8;
        DrawWrapped("Verificação do protocolo", headingFont, headingPaint);
        using var verificationQr = verificationQrBytes is { Length: > 0 } ? SKBitmap.Decode(verificationQrBytes) : null;
        if (verificationQr is not null)
        {
            const float qrSize = 120;
            EnsureSpace(qrSize + 12);
            canvas.DrawBitmap(verificationQr, new SKRect(LeftMargin, cursorY, LeftMargin + qrSize, cursorY + qrSize), SKSamplingOptions.Default);
            cursorY += qrSize + 12;
        }
        else
        {
            DrawWrapped("QR de verificação não disponível", bodyFont, bodyPaint);
        }

        document.EndPage();
        document.Close();
        return output.ToArray();
    }

    private static IEnumerable<string> WrapText(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            yield return "—";
            yield break;
        }

        var line = words[0];
        for (var index = 1; index < words.Length; index++)
        {
            var candidate = $"{line} {words[index]}";
            if (font.MeasureText(candidate, paint) <= maxWidth)
            {
                line = candidate;
                continue;
            }

            yield return line;
            line = words[index];
        }
        yield return line;
    }

    private static SKFont CreateFont(float size) => new() { Size = size };

    private static SKPaint CreatePaint(SKColor color) => new()
    {
        Color = color,
        IsAntialias = true
    };
}
