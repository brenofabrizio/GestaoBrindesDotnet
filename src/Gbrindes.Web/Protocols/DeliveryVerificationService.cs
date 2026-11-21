using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;

namespace Gbrindes.Web.Protocols;

public sealed record DeliveryQrCode(string Url, string DataUri, byte[] PngBytes, DateTimeOffset ExpiresAt);

public sealed class DeliveryVerificationService
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(7);
    private readonly ITimeLimitedDataProtector _protector;
    private readonly string _publicBaseUrl;

    public DeliveryVerificationService(IDataProtectionProvider dataProtection, IConfiguration configuration)
    {
        _protector = dataProtection.CreateProtector("Gbrindes.Web.DeliveryVerification.v1").ToTimeLimitedDataProtector();
        _publicBaseUrl = configuration["DeliveryVerification:PublicBaseUrl"]?.TrimEnd('/') ?? string.Empty;
    }

    public DeliveryQrCode CreateQr(string code)
    {
        if (string.IsNullOrWhiteSpace(_publicBaseUrl))
            throw new InvalidOperationException("Configure DeliveryVerification:PublicBaseUrl before generating delivery QR codes.");

        var expiresAt = DateTimeOffset.UtcNow.Add(LinkLifetime);
        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = _protector.Protect($"{code}\n{expires}", LinkLifetime);
        var url = $"{_publicBaseUrl}/api/v1/verify/{Uri.EscapeDataString(code)}?expires={expires}&signature={Uri.EscapeDataString(signature)}";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(8);
        var dataUri = $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
        var pngBytes = new PngByteQRCode(data).GetGraphic(8);

        return new DeliveryQrCode(url, dataUri, pngBytes, expiresAt);
    }

    public bool IsValid(string code, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var expiration))
            return false;

        try
        {
            if (DateTimeOffset.FromUnixTimeSeconds(expiration) <= DateTimeOffset.UtcNow)
                return false;

            var payload = _protector.Unprotect(signature);
            return string.Equals(payload, $"{code}\n{expiration.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
