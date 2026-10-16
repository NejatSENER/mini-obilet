using Microsoft.EntityFrameworkCore;

namespace MiniObilet.Api.Data;

public static class SeedData
{
    private static readonly (string Kalkis, string Varis, string Firma, int Saat, decimal Fiyat)[] Hatlar =
    [
        ("İstanbul", "Ankara", "Anadolu Turizm", 9, 750),
        ("İstanbul", "Ankara", "Boğaziçi Seyahat", 14, 690),
        ("İstanbul", "Ankara", "Anadolu Turizm", 23, 820),
        ("Ankara", "İstanbul", "Boğaziçi Seyahat", 10, 700),
        ("Ankara", "İstanbul", "Anadolu Turizm", 22, 800),
        ("İstanbul", "İzmir", "Ege Ekspres", 8, 900),
        ("İstanbul", "İzmir", "Ege Ekspres", 21, 950)
    ];

    public static async Task YukleAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Seferler.AnyAsync(ct)) return;

        var bugun = DateTime.UtcNow.Date;
        for (var gun = 0; gun < 14; gun++)
            foreach (var h in Hatlar)
                db.Seferler.Add(new Sefer
                {
                    Firma = h.Firma,
                    Kalkis = h.Kalkis,
                    Varis = h.Varis,
                    KalkisZamani = bugun.AddDays(gun).AddHours(h.Saat),
                    Fiyat = h.Fiyat,
                    KoltukSayisi = 40
                });

        await db.SaveChangesAsync(ct);
    }
}
