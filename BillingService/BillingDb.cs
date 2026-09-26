using Microsoft.EntityFrameworkCore;

namespace BillingService;

public class TuitionCharge
{
    public Guid Id { get; set; }
    public Guid EnrollmentId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "";   // "Charged" | "Declined"
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class BillingDb : DbContext
{
    public BillingDb(DbContextOptions<BillingDb> o) : base(o) { }
    public DbSet<TuitionCharge> Charges => Set<TuitionCharge>();   // table: TuitionCharges

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TuitionCharge>(e =>
        {
            e.ToTable("TuitionCharges");
            e.HasIndex(c => c.EnrollmentId).IsUnique();   // at most one charge per enrollment
            e.Property(c => c.Amount).HasPrecision(18, 2);
            e.Property(c => c.Status).HasMaxLength(16);
            e.Property(c => c.Reason).HasMaxLength(200);
        });
    }
}
