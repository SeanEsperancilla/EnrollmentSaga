using System.Text.Json.Serialization;
using Contracts;
using EnrollmentService;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // "Confirmed", not 1

builder.Services.AddDbContext<EnrollmentDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("EnrollmentDb")));

// One long-lived connection; a dedicated channel for publishing from the API.
var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
var connection = await Bus.ConnectAsync(rabbitHost, "enrollment-service");
var publishChannel = await connection.CreateChannelAsync();
await Bus.DeclareExchangeAsync(publishChannel);

// Audit queue bound to every routing key so the RabbitMQ console ("Get messages")
// can show the whole saga: enrollment.requested -> tuition.charged / tuition.failed.
await publishChannel.QueueDeclareAsync(Topology.AuditQueue, durable: true, exclusive: false, autoDelete: false,
    arguments: new Dictionary<string, object?> { ["x-max-length"] = 1000 });
await publishChannel.QueueBindAsync(Topology.AuditQueue, Topology.Exchange, "#");

builder.Services.AddSingleton(connection);
builder.Services.AddSingleton(publishChannel);
builder.Services.AddHostedService<BillingOutcomeConsumer>();

var app = builder.Build();

await EnsureDatabaseAsync(app.Services, app.Logger);

// Step 3: start the saga — create Pending, reserve the seat, publish EnrollmentRequested.
app.MapPost("/enrollments", async (EnrollRequest req, EnrollmentDb db, IChannel channel, ILogger<Program> log) =>
{
    var enrollment = new Enrollment
    {
        Id = Guid.NewGuid(), StudentId = req.StudentId,
        SectionId = req.SectionId, Tuition = req.Tuition
    };

    await using (var tx = await db.Database.BeginTransactionAsync())
    {
        // Reserve atomically: UPDATE ... SET SeatsTaken = SeatsTaken + 1 WHERE SeatsTaken < Capacity.
        // Concurrent requests for the last seat can't oversell, and none of them fail spuriously.
        var reserved = await db.Sections
            .Where(s => s.Id == req.SectionId && s.SeatsTaken < s.Capacity)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsTaken, x => x.SeatsTaken + 1));
        if (reserved == 0)
            return Results.BadRequest("No seat available");

        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();
        await tx.CommitAsync();               // one local transaction commits both
    }

    var section = await db.Sections.AsNoTracking().SingleAsync(s => s.Id == req.SectionId);
    log.LogInformation("Enrollment {Id} PENDING; seat reserved in {Section} -> {Taken}/{Cap}",
        enrollment.Id, section.Code, section.SeatsTaken, section.Capacity);

    await Bus.PublishAsync(channel, Topology.EnrollmentRequestedKey,
        new EnrollmentRequested(enrollment.Id, req.StudentId, req.SectionId, req.Tuition));

    return Results.Ok(new { enrollment.Id, Status = "Pending" });
});

app.MapGet("/enrollments/{id:guid}", async (Guid id, EnrollmentDb db) =>
    await db.Enrollments.FindAsync(id) is { } e ? Results.Ok(e) : Results.NotFound());

app.MapGet("/sections", async (EnrollmentDb db) =>
    await db.Sections.AsNoTracking().ToListAsync());

// Everything Enrollment owns, in one view.
app.MapGet("/state", async (EnrollmentDb db) => new
{
    Sections = await db.Sections.AsNoTracking().ToListAsync(),
    Enrollments = await db.Enrollments.AsNoTracking().ToListAsync()
});

app.Run();

static async Task EnsureDatabaseAsync(IServiceProvider services, ILogger log)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<EnrollmentDb>().Database.EnsureCreatedAsync();
            log.LogInformation("EnrollmentDb ready. Demo section CS101 = {Id}", EnrollmentDb.DemoSectionId);
            return;
        }
        catch (SqlException ex) when (attempt < 30)
        {
            log.LogWarning("SQL Server not ready ({Message}), retry {Attempt}/30...", ex.Message, attempt);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

record EnrollRequest(string StudentId, Guid SectionId, decimal Tuition);
