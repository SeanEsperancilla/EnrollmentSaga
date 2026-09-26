using BillingService;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<BillingDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("BillingDb")));
builder.Services.AddHostedService<BillingWorker>();

var host = builder.Build();
host.Run();
