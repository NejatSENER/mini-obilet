using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MiniObilet.Api.Data;

namespace MiniObilet.Api;

public record SeferOzetDto(int Id, string Firma, string Kalkis, string Varis, DateTime KalkisZamani, decimal Fiyat, int BosKoltuk);
public record KoltukDto(int No, bool Dolu);
public record KoltukHaritasiDto(int SeferId, IReadOnlyList<KoltukDto> Koltuklar);
public record BiletIstegi(int SeferId, int KoltukNo, string YolcuAdi, string Eposta);
public record BiletDto(string Pnr, int SeferId, int KoltukNo, string YolcuAdi, string Kalkis, string Varis, DateTime KalkisZamani, decimal Fiyat, bool Iptal);

public static class Endpoints
{
    public static void MapMiniObilet(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/seferler", SeferAra);
        api.MapGet("/seferler/{id:int}/koltuklar", KoltukHaritasi);
        api.MapPost("/biletler", BiletAl);
        api.MapGet("/biletler/{pnr}", BiletGetir);
        api.MapDelete("/biletler/{pnr}", BiletIptal);
    }

    static async Task<IResult> SeferAra(string? kalkis, string? varis, DateOnly? tarih, AppDbContext db, CancellationToken ct)
    {
        var hatalar = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(kalkis)) hatalar["kalkis"] = ["Kalkış şehri zorunlu."];
        if (string.IsNullOrWhiteSpace(varis)) hatalar["varis"] = ["Varış şehri zorunlu."];
        if (tarih is null) hatalar["tarih"] = ["Tarih zorunlu (yyyy-MM-dd)."];
        if (hatalar.Count > 0) return Results.ValidationProblem(hatalar);

        var baslangic = tarih!.Value.ToDateTime(TimeOnly.MinValue);
        var bitis = baslangic.AddDays(1);

        var seferler = await db.Seferler.AsNoTracking()
            .Where(s => s.Kalkis == kalkis && s.Varis == varis && s.KalkisZamani >= baslangic && s.KalkisZamani < bitis)
            .OrderBy(s => s.KalkisZamani)
            .Select(s => new SeferOzetDto(s.Id, s.Firma, s.Kalkis, s.Varis, s.KalkisZamani, s.Fiyat,
                s.KoltukSayisi - s.Biletler.Count(b => !b.Iptal)))
            .ToListAsync(ct);

        return Results.Ok(seferler);
    }

    static async Task<IResult> KoltukHaritasi(int id, AppDbContext db, CancellationToken ct)
    {
        var sefer = await db.Seferler.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.KoltukSayisi, Dolu = s.Biletler.Where(b => !b.Iptal).Select(b => b.KoltukNo).ToList() })
            .FirstOrDefaultAsync(ct);
        if (sefer is null) return Results.NotFound();

        var dolu = sefer.Dolu.ToHashSet();
        var koltuklar = Enumerable.Range(1, sefer.KoltukSayisi).Select(no => new KoltukDto(no, dolu.Contains(no))).ToList();
        return Results.Ok(new KoltukHaritasiDto(id, koltuklar));
    }

    static async Task<IResult> BiletAl(BiletIstegi istek, AppDbContext db, ILogger<Program> logger, CancellationToken ct)
    {
        var hatalar = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(istek.YolcuAdi)) hatalar["yolcuAdi"] = ["Yolcu adı zorunlu."];
        if (string.IsNullOrWhiteSpace(istek.Eposta) || !istek.Eposta.Contains('@')) hatalar["eposta"] = ["Geçerli bir e-posta girin."];
        if (hatalar.Count > 0) return Results.ValidationProblem(hatalar);

        var sefer = await db.Seferler.AsNoTracking().FirstOrDefaultAsync(s => s.Id == istek.SeferId, ct);
        if (sefer is null) return Results.NotFound();
        if (istek.KoltukNo < 1 || istek.KoltukNo > sefer.KoltukSayisi)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["koltukNo"] = [$"Koltuk 1 ile {sefer.KoltukSayisi} arasında olmalı."] });

        if (await KoltukDoluMu(db, istek, ct)) return KoltukDolu(istek.KoltukNo);

        var bilet = new Bilet
        {
            Pnr = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPRSTUVYZ23456789", 6),
            SeferId = istek.SeferId,
            KoltukNo = istek.KoltukNo,
            YolcuAdi = istek.YolcuAdi.Trim(),
            Eposta = istek.Eposta.Trim().ToLowerInvariant(),
            OlusturmaZamani = DateTime.UtcNow
        };
        db.Biletler.Add(bilet);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Kontrolden sonra başka bir istek aynı koltuğu aldıysa unique index ikinci kaydı reddeder
            db.ChangeTracker.Clear();
            if (await KoltukDoluMu(db, istek, ct))
            {
                logger.LogInformation("Eşzamanlı satış engellendi {SeferId} koltuk {KoltukNo}", istek.SeferId, istek.KoltukNo);
                return KoltukDolu(istek.KoltukNo);
            }
            throw;
        }

        logger.LogInformation("Bilet satıldı {Pnr} {SeferId} koltuk {KoltukNo}", bilet.Pnr, bilet.SeferId, bilet.KoltukNo);
        return Results.Created($"/api/v1/biletler/{bilet.Pnr}", ToDto(bilet, sefer));
    }

    // PNR tek başına yetmez: e-posta da eşleşmeli (başkasının biletine erişimi önler)
    static async Task<IResult> BiletGetir(string pnr, string? eposta, AppDbContext db, CancellationToken ct)
    {
        var bilet = await BulAsync(db, pnr, eposta, ct, takip: false);
        return bilet is null ? Results.NotFound() : Results.Ok(ToDto(bilet, bilet.Sefer!));
    }

    static async Task<IResult> BiletIptal(string pnr, string? eposta, AppDbContext db, CancellationToken ct)
    {
        var bilet = await BulAsync(db, pnr, eposta, ct, takip: true);
        if (bilet is null) return Results.NotFound();
        if (!bilet.Iptal)
        {
            bilet.Iptal = true;
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    static Task<bool> KoltukDoluMu(AppDbContext db, BiletIstegi istek, CancellationToken ct) =>
        db.Biletler.AnyAsync(b => b.SeferId == istek.SeferId && b.KoltukNo == istek.KoltukNo && !b.Iptal, ct);

    static IResult KoltukDolu(int koltukNo) =>
        Results.Problem(title: "Koltuk dolu", detail: $"{koltukNo} numaralı koltuk az önce satıldı.", statusCode: StatusCodes.Status409Conflict);

    static Task<Bilet?> BulAsync(AppDbContext db, string pnr, string? eposta, CancellationToken ct, bool takip)
    {
        if (string.IsNullOrWhiteSpace(eposta)) return Task.FromResult<Bilet?>(null);
        var sorgu = takip ? db.Biletler : db.Biletler.AsNoTracking();
        var normal = eposta.Trim().ToLowerInvariant();
        return sorgu.Include(b => b.Sefer)
            .FirstOrDefaultAsync(b => b.Pnr == pnr.ToUpperInvariant() && b.Eposta == normal, ct);
    }

    static BiletDto ToDto(Bilet b, Sefer s) =>
        new(b.Pnr, b.SeferId, b.KoltukNo, b.YolcuAdi, s.Kalkis, s.Varis, s.KalkisZamani, s.Fiyat, b.Iptal);
}
