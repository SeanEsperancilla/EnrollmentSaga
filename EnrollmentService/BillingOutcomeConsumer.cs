using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EnrollmentService;

// Step 5: listens for the billing outcome and either confirms (forward) or compensates.
public class BillingOutcomeConsumer(
    IConnection connection,
    IServiceScopeFactory scopes,
    ILogger<BillingOutcomeConsumer> log) : BackgroundService
{
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = _channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await Bus.DeclareExchangeAsync(channel);
        await channel.QueueDeclareAsync(Topology.EnrollmentQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(Topology.EnrollmentQueue, Topology.Exchange, Topology.TuitionChargedKey);
        await channel.QueueBindAsync(Topology.EnrollmentQueue, Topology.Exchange, Topology.TuitionFailedKey);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                await HandleAsync(ea.RoutingKey, ea.Body);
                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "Unreadable message on {Key}; dropping", ea.RoutingKey);
                await channel.BasicRejectAsync(ea.DeliveryTag, requeue: false);
            }
            catch (Exception ex)
            {
                // Transient (DB down, concurrency conflict): put it back and try again.
                log.LogWarning(ex, "Handling {Key} failed; requeueing", ea.RoutingKey);
                await Task.Delay(500);
                await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true);
            }
        };

        await channel.BasicConsumeAsync(Topology.EnrollmentQueue, autoAck: false, consumer: consumer,
            cancellationToken: stoppingToken);
        log.LogInformation("Listening on {Queue} for {Charged} / {Failed}",
            Topology.EnrollmentQueue, Topology.TuitionChargedKey, Topology.TuitionFailedKey);
    }

    private async Task HandleAsync(string key, ReadOnlyMemory<byte> body)
    {
        Guid enrollmentId;
        string? reason = null;
        switch (key)
        {
            case Topology.TuitionChargedKey:
                enrollmentId = Bus.Deserialize<TuitionCharged>(body).EnrollmentId;
                break;
            case Topology.TuitionFailedKey:
                var failed = Bus.Deserialize<TuitionFailed>(body);
                (enrollmentId, reason) = (failed.EnrollmentId, failed.Reason);
                break;
            default:
                log.LogWarning("Ignoring unexpected routing key {Key}", key);
                return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EnrollmentDb>();

        var enrollment = await db.Enrollments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == enrollmentId);
        if (enrollment is null || enrollment.Status != EnrollmentStatus.Pending)
        {
            // Idempotent: a redelivered event is ignored once the saga has ended.
            log.LogInformation("{Key} for {Id} ignored (saga already {Status})",
                key, enrollmentId, enrollment?.Status.ToString() ?? "unknown");
            return;
        }

        var next = key == Topology.TuitionChargedKey ? EnrollmentStatus.Confirmed : EnrollmentStatus.Cancelled;

        await using var tx = await db.Database.BeginTransactionAsync();

        // Pending -> Confirmed/Cancelled only once, even if two copies of the event race.
        var transitioned = await db.Enrollments
            .Where(e => e.Id == enrollmentId && e.Status == EnrollmentStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, next));
        if (transitioned == 0) return;

        if (next == EnrollmentStatus.Confirmed)
        {
            log.LogInformation("Enrollment {Id} CONFIRMED", enrollmentId);          // forward
        }
        else // TuitionFailed -> compensate: release the seat
        {
            await db.Sections
                .Where(s => s.Id == enrollment.SectionId && s.SeatsTaken > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsTaken, x => x.SeatsTaken - 1));
            log.LogWarning("Enrollment {Id} CANCELLED ({Reason}); seat released", enrollmentId, reason);
        }

        await tx.CommitAsync();   // status change + seat release commit together
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (_channel is not null) await _channel.CloseAsync(ct);
    }
}
