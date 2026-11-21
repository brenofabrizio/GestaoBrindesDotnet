using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Notifications;

public sealed class EmailOutboxProcessor(ApplicationDbContext database, IEmailSender sender, IConfiguration configuration, ILogger<EmailOutboxProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(configuration.GetValue("Email:OutboxBatchSize", 40), 1, 200);
        var maxAttempts = Math.Clamp(configuration.GetValue("Email:OutboxMaxAttempts", 5), 1, 20);
        var messages = await database.Notifications
            .Where(item => item.Channel == "email" && item.Status == "fila" && item.ToEmail != null)
            .OrderBy(item => item.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var message in messages)
        {
            message.Attempts++;
            try
            {
                message.Status = "processando";
                await database.SaveChangesAsync(cancellationToken);
                await sender.SendAsync(message.ToEmail!, message.Subject, message.Body ?? string.Empty, cancellationToken);
                message.Status = "enviado";
                message.SentAt = DateTimeOffset.UtcNow;
                message.LastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Status = message.Attempts >= maxAttempts ? "falhou" : "fila";
                message.LastError = exception.Message[..Math.Min(exception.Message.Length, 500)];
                logger.LogWarning(exception, "Falha no envio de notificação de e-mail; tentativa {Attempt}.", message.Attempts);
            }
            await database.SaveChangesAsync(cancellationToken);
            processed++;
        }
        return processed;
    }
}

public sealed class EmailOutboxWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Email:OutboxIntervalSeconds", 60), 10, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Erro no worker do outbox de e-mail."); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
