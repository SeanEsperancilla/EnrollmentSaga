using Microsoft.EntityFrameworkCore;

namespace EnrollmentService;

public enum EnrollmentStatus { Pending, Confirmed, Cancelled }

public class Section
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public int Capacity { get; set; }
    public int SeatsTaken { get; set; }
}

public class Enrollment
{
    public Guid Id { get; set; }
    public string StudentId { get; set; } = "";
    public Guid SectionId { get; set; }
    public decimal Tuition { get; set; }
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Pending;
}

public class EnrollmentDb : DbContext
{
    // Fixed id so the demo curl commands can use it directly.
    public static readonly Guid DemoSectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public EnrollmentDb(DbContextOptions<EnrollmentDb> o) : base(o) { }
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Section>(e =>
        {
            e.Property(s => s.Code).HasMaxLength(32);
            e.HasData(new Section { Id = DemoSectionId, Code = "CS101", Capacity = 30, SeatsTaken = 0 });
        });

        b.Entity<Enrollment>(e =>
        {
            e.Property(x => x.StudentId).HasMaxLength(32);
            e.Property(x => x.Tuition).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}
