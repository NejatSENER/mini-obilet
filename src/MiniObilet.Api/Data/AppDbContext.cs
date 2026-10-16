using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MiniObilet.Api.Data;

public class Sefer
{
    public int Id { get; set; }
    public required string Firma { get; set; }
    public required string Kalkis { get; set; }
    public required string Varis { get; set; }
    public DateTime KalkisZamani { get; set; }
    public decimal Fiyat { get; set; }
    public int KoltukSayisi { get; set; }
    public List<Bilet> Biletler { get; set; } = [];
}

public class Bilet
{
    public int Id { get; set; }
    public required string Pnr { get; set; }
    public int SeferId { get; set; }
    public Sefer? Sefer { get; set; }
    public int KoltukNo { get; set; }
    public required string YolcuAdi { get; set; }
    public required string Eposta { get; set; }
    public bool Iptal { get; set; }
    public DateTime OlusturmaZamani { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Sefer> Seferler => Set<Sefer>();
    public DbSet<Bilet> Biletler => Set<Bilet>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Sefer>(e =>
        {
            e.Property(s => s.Firma).HasMaxLength(100);
            e.Property(s => s.Kalkis).HasMaxLength(50);
            e.Property(s => s.Varis).HasMaxLength(50);
            e.Property(s => s.Fiyat).HasPrecision(10, 2);
            e.HasIndex(s => new { s.Kalkis, s.Varis, s.KalkisZamani });
        });

        model.Entity<Bilet>(e =>
        {
            e.Property(b => b.Pnr).HasMaxLength(6);
            e.Property(b => b.YolcuAdi).HasMaxLength(100);
            e.Property(b => b.Eposta).HasMaxLength(200);
            e.HasIndex(b => b.Pnr).IsUnique();

            // Çift satışa karşı son güvence: iptal edilmemiş biletlerde aynı sefer + koltuk tek olabilir
            e.HasIndex(b => new { b.SeferId, b.KoltukNo })
                .IsUnique()
                .HasFilter("[Iptal] = 0");
        });
    }
}

// dotnet ef migrations add için (SQL Server migration'ları üretir, bağlantı açmaz)
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=miniobilet;Trusted_Connection=True")
            .Options);
}
