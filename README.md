# EnrollmentSaga

A choreography saga over RabbitMQ between two services, each owning its own database.

| Service | Owns | Does |
|---|---|---|
| **EnrollmentService** (ASP.NET Core minimal API, port 5080) | `EnrollmentDb` | Creates a `Pending` enrollment and reserves a seat in one local transaction, publishes `EnrollmentRequested`, then confirms or **compensates** when billing answers. |
| **BillingService** (worker) | `BillingDb` | Charges tuition in its own local transaction and publishes `TuitionCharged` or `TuitionFailed`. Declines anything over 20,000. |
| **Contracts** (class library) | – | Events, routing topology, and the `Bus` publish helper. |

```
             POST /enrollments
                    │
        ┌───────────▼────────────┐   enrollment.requested   ┌──────────────────┐
        │  EnrollmentService     │ ───────────────────────▶ │  BillingService  │
        │  Pending + seat++      │                          │  Charged/Declined│
        │  (EnrollmentDb)        │ ◀─────────────────────── │  (BillingDb)     │
        └────────────────────────┘  tuition.charged → Confirmed               
                                    tuition.failed  → Cancelled + seat--  (compensation)
```

All messages go through the durable topic exchange `saga.events`:

| Queue | Bound to | Consumer |
|---|---|---|
| `billing.queue` | `enrollment.requested` | BillingService |
| `enrollment.queue` | `tuition.charged`, `tuition.failed` | EnrollmentService |
| `saga.audit` | `#` (capped at 1000) | nobody – use **Get messages** in the console to see every event of the saga |

## Prerequisites

- .NET 9 SDK or newer (projects target `net9.0`; a .NET 10 SDK builds them too)
- Docker

## Step 0 – Infrastructure

```bash
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management

docker run -d --name sqlserver -p 1433:1433 \
  -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Your_password123" \
  mcr.microsoft.com/mssql/server:2022-latest
```

(or `docker compose up -d`, which does the same.)

Open the RabbitMQ console at <http://localhost:15672> (`guest` / `guest`) and keep it open.

## Run the services

In two terminals:

```bash
dotnet run --project EnrollmentService      # http://localhost:5080
dotnet run --project BillingService
```

On first start each service creates its database (`EnsureCreated`) and EnrollmentService seeds
section **CS101** (capacity 30) with the fixed id `11111111-1111-1111-1111-111111111111`.
Both services retry while RabbitMQ / SQL Server are still starting.

## Step 6 – Happy path

```bash
curl -X POST http://localhost:5080/enrollments \
  -H "Content-Type: application/json" \
  -d '{"studentId":"2026-00123","sectionId":"11111111-1111-1111-1111-111111111111","tuition":15000}'
```

Response is `Pending`. Moments later `enrollment.requested` → `tuition.charged` flow through
the exchange, the enrollment becomes `Confirmed`, `SeatsTaken` stays at 1, and `BillingDb` has a `Charged` row.

## Step 7 – Compensation path

```bash
curl -X POST http://localhost:5080/enrollments \
  -H "Content-Type: application/json" \
  -d '{"studentId":"2026-00124","sectionId":"11111111-1111-1111-1111-111111111111","tuition":50000}'
```

Response is still `Pending` (the API doesn't wait for billing). Then `tuition.failed` fires, and
EnrollmentService compensates: the enrollment becomes `Cancelled` and `SeatsTaken` goes back down.
`BillingDb` has a `Declined` row, and no money moved.

## Step 8 – Prove consistency

```bash
docker exec -i sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P Your_password123 -W \
  < scripts/consistency.sql
```

This prints the three queries from the walkthrough, a side-by-side join of each enrollment with its
billing outcome, and four invariant checks that should all read `OK`:

```
Confirmed <-> Charged                       OK
Cancelled <-> Declined                      OK
No enrollment left Pending                  OK
No seat stranded (SeatsTaken = #Confirmed)  OK
```

## Scripted demo

With both services running:

```bash
scripts/demo.sh            # Steps 6, 7 and 8 in one go
scripts/demo.sh burst      # 60 concurrent requests for 30 seats, half declined
scripts/demo.sh check      # Step 8 only
```

Windows: `./scripts/demo.ps1` (same steps except `burst`). `EnrollmentService/EnrollmentService.http`
has the requests for VS / Rider / VS Code.

Other endpoints: `GET /state` (sections and enrollments together), `GET /sections`, `GET /enrollments/{id}`.

To start over: `docker exec -i sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P Your_password123 < scripts/reset.sql`, then restart the services.

**Tip:** to see the `Pending` state before it flips, slow billing down:
`dotnet run --project BillingService -- --Billing:ProcessingDelayMs=5000`.

## Where the code deviates from the walkthrough, and why

- **JSON is camelCase on the wire.** `JsonSerializer`'s defaults would write `EnrollmentId`, so the
  walkthrough's `GetProperty("enrollmentId")` would throw. `Bus` uses `JsonSerializerDefaults.Web`
  for both directions.
- **Seats are reserved and released with atomic conditional `UPDATE`s** (`SeatsTaken + 1 WHERE
  SeatsTaken < Capacity`) inside the local transaction, not with read-then-increment. With read-then-increment,
  concurrent requests can oversell the last seat. `scripts/demo.sh burst` shows exactly 30 seats taken and the rest refused.
- **The compensation is guarded by `WHERE Status = 'Pending'`**, so two copies of the same event
  racing each other can't release a seat twice.
- **Billing re-publishes the recorded outcome on a duplicate request** instead of silently acking.
  If Billing crashed after committing the charge but before publishing, the redelivered
  `EnrollmentRequested` would otherwise leave the enrollment `Pending` forever.
- **Consumers are scoped per message.** Each message gets its own `DbContext` from a DI scope, and processing errors
  `nack` + requeue, while unreadable messages are rejected.

## Known limitation (by design, for the demo)

Publishing happens *after* the local commit, so a crash between `SaveChanges` and `BasicPublish`
could leave an enrollment `Pending` with no event sent. The production fix is a
**transactional outbox**: write the event to an `Outbox` table in the same transaction and have a
relay publish it. This demo leaves the outbox out so the saga stays easy to follow.
