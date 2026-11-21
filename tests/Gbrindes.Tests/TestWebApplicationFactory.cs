using Gbrindes.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gbrindes.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"gbrindes-tests-{Guid.NewGuid():N}";
    private readonly string _photoRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-test-photos-{Guid.NewGuid():N}");
    private readonly string _signatureRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-test-signatures-{Guid.NewGuid():N}");
    private readonly string _invoiceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-test-invoices-{Guid.NewGuid():N}");
    private readonly string _brandingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-test-branding-{Guid.NewGuid():N}");

    public string SignatureRoot => _signatureRoot;
    public string InvoiceRoot => _invoiceRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:ItemPhotos:RootPath", _photoRoot);
        builder.UseSetting("Storage:Signatures:RootPath", _signatureRoot);
        builder.UseSetting("Storage:Invoices:RootPath", _invoiceRoot);
        builder.UseSetting("Storage:Branding:RootPath", _brandingRoot);
        builder.UseSetting("DeliveryVerification:PublicBaseUrl", "https://gbrindes.example");
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_photoRoot))
            Directory.Delete(_photoRoot, recursive: true);
        if (disposing && Directory.Exists(_signatureRoot))
            Directory.Delete(_signatureRoot, recursive: true);
        if (disposing && Directory.Exists(_invoiceRoot))
            Directory.Delete(_invoiceRoot, recursive: true);
        if (disposing && Directory.Exists(_brandingRoot))
            Directory.Delete(_brandingRoot, recursive: true);
    }
}
