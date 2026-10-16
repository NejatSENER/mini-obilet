using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MiniObilet.Api.Data;

public class Trip
{
    public int Id { get; set; }
    public required string Company { get; set; }
    public required string Origin { get; set; }
    public required string Destination { get; set; }
    public DateTime DepartureTime { get; set; }
    public decimal Price { get; set; }
    public int SeatCount { get; set; }
    public List<Ticket> Tickets { get; set; } = [];
}

public class Ticket
{
    public int Id { get; set; }
    public required string Pnr { get; set; }
    public int TripId { get; set; }
    public Trip? Trip { get; set; }
    public int SeatNumber { get; set; }
    public required string PassengerName { get; set; }
    public required string Email { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Trip>(e =>
        {
            e.Property(t => t.Company).HasMaxLength(100);
            e.Property(t => t.Origin).HasMaxLength(50);
            e.Property(t => t.Destination).HasMaxLength(50);
            e.Property(t => t.Price).HasPrecision(10, 2);
            e.HasIndex(t => new { t.Origin, t.Destination, t.DepartureTime });
        });

        model.Entity<Ticket>(e =>
        {
            e.Property(t => t.Pnr).HasMaxLength(6);
            e.Property(t => t.PassengerName).HasMaxLength(100);
            e.Property(t => t.Email).HasMaxLength(200);
            e.HasIndex(t => t.Pnr).IsUnique();

            // Double-booking guard: one active ticket per trip and seat
            e.HasIndex(t => new { t.TripId, t.SeatNumber })
                .IsUnique()
                .HasFilter("[IsCancelled] = 0");
        });
    }
}

public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=miniobilet;Trusted_Connection=True")
            .Options);
}
