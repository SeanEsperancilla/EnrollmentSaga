using System.Text.Json;
using Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BillingService;

// Step 4: consume EnrollmentRequested, charge in BillingDb, report the outcome.
public class BillingWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<BillingWorker> log) : BackgroundService
{
    private IConnection? _connection;
    private IChannel? _channel;

    private decimal MaxApproved => config.GetValue("Billing:MaxApprovedTuition", 20000m);
    private int DelayMs => config.GetValue("Billing:ProcessingDelayMs", 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureDatabaseAsync(stoppingToken);

        _connection = await Bus.ConnectAsync(config["RabbitMQ:Host"] ?? "localhost", "billing-service", stoppingToken);
        var channel = _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Billing's queue binds only to enrollment.requested.
        await Bus.DeclareExchangeAsync(channel);
        await channel.QueueDeclareAsync(Topology.BillingQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(Topology.BillingQueue, Topology.Exchange, Topology.EnrollmentRequestedKey);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var evt = Bus.Deserialize<EnrollmentRequested>(ea.Body);
                await HandleAsync(channel, evt);
                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "Unreadable EnrollmentRequested; dropping");
                await channel.BasicRejectAsync(ea.DeliveryTag, requeue: false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Charging failed transiently; requeueing");
                await Task.Delay(500);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true);
            }
        };

        await channel.BasicConsumeAsync(Topology.BillingQueue, autoAck: false, consumer: consumer,
            cancellationToken: stoppingToken);
        log.LogInformation("Listening on {Queue} for {Key}; approving tuition <= {Max:N0}",
            Topology.BillingQueue, Topology.EnrollmentRequestedKey, MaxApproved);
    }

    private async Task HandleAsync(IChannel channel, EnrollmentRequested evt)
    {
        if (DelayMs > 0) await Task.Delay(DelayMs);   // optional: slow down to watch Pending in the demo

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDb>();

        // Idempotency: if we already decided this enrollment, don't charge again —
        // just re-announce the recorded outcome (covers a crash between commit and publish).
        var charge = await db.Charges.SingleOrDefaultAsync(c => c.EnrollmentId == evt.EnrollmentId);
        if (charge is not null)
        {
            log.LogInformation("Duplicate request for {Id}; re-publishing recorded outcome {Status}",
                evt.EnrollmentId, charge.Status);
        }
        else
        {
            bool approved = evt.Tuition <= MaxApproved;   // demo rule: decline large amounts

            charge = new TuitionCharge
            {
                Id = Guid.NewGuid(), EnrollmentId = evt.EnrollmentId, Amount = evt.Tuition,
                Status = approved ? "Charged" : "Declined",
                Reason = approved ? null : "Amount exceeds limit"
            };
            db.Charges.Add(charge);
            await db.SaveChangesAsync();                  // Billing's local transaction

            log.LogInformation("Enrollment {Id}: {Amount:N2} {Status}", evt.EnrollmentId, evt.Tuition, charge.Status);
        }

        if (charge.Status == "Charged")
            await Bus.PublishAsync(channel, Topology.TuitionChargedKey,
                new TuitionCharged(evt.EnrollmentId));
        else
            await Bus.PublishAsync(channel, Topology.TuitionFailedKey,
                new TuitionFailed(evt.EnrollmentId, charge.Reason ?? "Declined"));
    }

    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BillingDb>().Database.EnsureCreatedAsync(ct);
                log.LogInformation("BillingDb ready");
                return;
            }
            catch (SqlException ex) when (attempt < 30)
            {
                log.LogWarning("SQL Server not ready ({Message}), retry {Attempt}/30...", ex.Message, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (_channel is not null) await _channel.CloseAsync(ct);
        if (_connection is not null) await _connection.CloseAsync(ct);
    }
}
