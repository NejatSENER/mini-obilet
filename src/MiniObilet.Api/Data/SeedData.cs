using Microsoft.EntityFrameworkCore;

namespace MiniObilet.Api.Data;

public static class SeedData
{
    private static readonly (string Origin, string Destination, string Company, int Hour, decimal Price)[] Routes =
    [
        ("İstanbul", "Ankara", "Anadolu Turizm", 9, 750),
        ("İstanbul", "Ankara", "Boğaziçi Seyahat", 14, 690),
        ("İstanbul", "Ankara", "Anadolu Turizm", 23, 820),
        ("Ankara", "İstanbul", "Boğaziçi Seyahat", 10, 700),
        ("Ankara", "İstanbul", "Anadolu Turizm", 22, 800),
        ("İstanbul", "İzmir", "Ege Ekspres", 8, 900),
        ("İstanbul", "İzmir", "Ege Ekspres", 21, 950)
    ];

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Trips.AnyAsync(ct)) return;

        var today = DateTime.UtcNow.Date;
        for (var day = 0; day < 14; day++)
            foreach (var r in Routes)
                db.Trips.Add(new Trip
                {
                    Company = r.Company,
                    Origin = r.Origin,
                    Destination = r.Destination,
                    DepartureTime = today.AddDays(day).AddHours(r.Hour),
                    Price = r.Price,
                    SeatCount = 40
                });

        await db.SaveChangesAsync(ct);
    }
}
