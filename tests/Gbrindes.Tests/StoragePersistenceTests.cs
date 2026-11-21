using Gbrindes.Web.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class StoragePersistenceTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public StoragePersistenceTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public void Private_artifacts_remain_readable_after_storage_services_are_recreated()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var signatureStorage = new DigitalSignatureStorage(configuration, environment);
        var invoiceStorage = new TradeInvoiceStorage(configuration, environment);
        var attachmentStorage = new StockAttachmentStorage(configuration, environment);
        var signatureRequestId = Guid.NewGuid();
        var invoiceRequestId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var signatureData = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var pdf = "%PDF-1.4 persistence-test"u8.ToArray();
        var signature = signatureStorage.Store(signatureRequestId, $"data:image/png;base64,{Convert.ToBase64String(signatureData)}");
        var invoice = invoiceStorage.Store(invoiceRequestId, new PreparedTradeInvoice(pdf, "pdf", "application/pdf", "test-invoice"));
        var attachment = attachmentStorage.Store(itemId, new PreparedStockAttachment(pdf, "pdf", "application/pdf", "test-attachment"));

        try
        {
            var recreatedSignatures = new DigitalSignatureStorage(configuration, environment);
            var recreatedInvoices = new TradeInvoiceStorage(configuration, environment);
            var recreatedAttachments = new StockAttachmentStorage(configuration, environment);
            var storedSignature = recreatedSignatures.Read(signature.Path);
            Assert.NotNull(storedSignature);
            Assert.Equal(signatureData, storedSignature!.Bytes);
            using var storedInvoice = recreatedInvoices.OpenRead(invoice.Path);
            Assert.NotNull(storedInvoice);
            using var storedAttachment = recreatedAttachments.OpenRead(attachment.Path);
            Assert.NotNull(storedAttachment);
            Assert.Equal(pdf, ReadAll(storedInvoice!));
            Assert.Equal(pdf, ReadAll(storedAttachment!));
        }
        finally
        {
            signatureStorage.Delete(signature.Path);
            invoiceStorage.Delete(invoice.Path);
            attachmentStorage.Delete(attachment.Path);
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
