using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniObilet.Api;

namespace MiniObilet.Tests;

public class TestUygulamasi : WebApplicationFactory<Program>
{
    private readonly string _dbYolu = Path.Combine(Path.GetTempPath(), $"miniobilet-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Db:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Db", $"Data Source={_dbYolu}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbYolu)) File.Delete(_dbYolu);
    }
}

public class ApiTests(TestUygulamasi uygulama) : IClassFixture<TestUygulamasi>
{
    private readonly HttpClient _client = uygulama.CreateClient();
    private static readonly string Yarin = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd");

    private async Task<SeferOzetDto> IlkSeferAsync(string kalkis = "İstanbul", string varis = "Ankara", int gunSonra = 1)
    {
        var tarih = DateTime.UtcNow.Date.AddDays(gunSonra).ToString("yyyy-MM-dd");
        var seferler = await _client.GetFromJsonAsync<List<SeferOzetDto>>($"/api/v1/seferler?kalkis={kalkis}&varis={varis}&tarih={tarih}");
        return seferler!.First();
    }

    private Task<HttpResponseMessage> BiletAlAsync(int seferId, int koltukNo, string eposta = "yolcu@ornek.com") =>
        _client.PostAsJsonAsync("/api/v1/biletler", new BiletIstegi(seferId, koltukNo, "Test Yolcu", eposta));

    [Fact]
    public async Task SeferAra_GecerliRota_SeferleriVeBosKoltuklariDoner()
    {
        var seferler = await _client.GetFromJsonAsync<List<SeferOzetDto>>($"/api/v1/seferler?kalkis=İstanbul&varis=İzmir&tarih={Yarin}");

        Assert.NotNull(seferler);
        Assert.Equal(2, seferler.Count);
        Assert.All(seferler, s => Assert.Equal(40, s.BosKoltuk));
        Assert.True(seferler[0].KalkisZamani < seferler[1].KalkisZamani);
    }

    [Fact]
    public async Task SeferAra_KalkisEksik_400Doner()
    {
        var yanit = await _client.GetAsync($"/api/v1/seferler?varis=Ankara&tarih={Yarin}");
        Assert.Equal(HttpStatusCode.BadRequest, yanit.StatusCode);
    }

    [Fact]
    public async Task BiletAl_Basarili_201VeKoltukDoluGorunur()
    {
        var sefer = await IlkSeferAsync(gunSonra: 2);

        var yanit = await BiletAlAsync(sefer.Id, 5);

        Assert.Equal(HttpStatusCode.Created, yanit.StatusCode);
        Assert.NotNull(yanit.Headers.Location);
        var harita = await _client.GetFromJsonAsync<KoltukHaritasiDto>($"/api/v1/seferler/{sefer.Id}/koltuklar");
        Assert.True(harita!.Koltuklar.Single(k => k.No == 5).Dolu);
        Assert.False(harita.Koltuklar.Single(k => k.No == 6).Dolu);
    }

    [Fact]
    public async Task BiletAl_AyniKoltukIkinciKez_409Doner()
    {
        var sefer = await IlkSeferAsync(gunSonra: 3);
        await BiletAlAsync(sefer.Id, 10);

        var ikinci = await BiletAlAsync(sefer.Id, 10, "baskasi@ornek.com");

        Assert.Equal(HttpStatusCode.Conflict, ikinci.StatusCode);
    }

    [Fact]
    public async Task BiletAl_AyniKoltugaEsZamanli10Istek_SadeceBiriBasarili()
    {
        var sefer = await IlkSeferAsync(gunSonra: 4);

        var yanitlar = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => BiletAlAsync(sefer.Id, 14, $"yolcu{i}@ornek.com")));

        Assert.Equal(1, yanitlar.Count(y => y.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, yanitlar.Count(y => y.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task BiletIptal_SonrasindaKoltukTekrarSatilabilir()
    {
        var sefer = await IlkSeferAsync(gunSonra: 5);
        var bilet = await (await BiletAlAsync(sefer.Id, 20, "iptal@ornek.com")).Content.ReadFromJsonAsync<BiletDto>();

        var iptal = await _client.DeleteAsync($"/api/v1/biletler/{bilet!.Pnr}?eposta=iptal@ornek.com");
        var yeniden = await BiletAlAsync(sefer.Id, 20, "yeni@ornek.com");

        Assert.Equal(HttpStatusCode.NoContent, iptal.StatusCode);
        Assert.Equal(HttpStatusCode.Created, yeniden.StatusCode);
    }

    [Fact]
    public async Task BiletGetir_YanlisEposta_404Doner()
    {
        var sefer = await IlkSeferAsync(gunSonra: 6);
        var bilet = await (await BiletAlAsync(sefer.Id, 1, "sahibi@ornek.com")).Content.ReadFromJsonAsync<BiletDto>();

        var dogru = await _client.GetAsync($"/api/v1/biletler/{bilet!.Pnr}?eposta=sahibi@ornek.com");
        var yanlis = await _client.GetAsync($"/api/v1/biletler/{bilet.Pnr}?eposta=baskasi@ornek.com");

        Assert.Equal(HttpStatusCode.OK, dogru.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, yanlis.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    public async Task BiletAl_GecersizKoltukNo_400Doner(int koltukNo)
    {
        var sefer = await IlkSeferAsync(gunSonra: 7);
        var yanit = await BiletAlAsync(sefer.Id, koltukNo);
        Assert.Equal(HttpStatusCode.BadRequest, yanit.StatusCode);
    }

    [Fact]
    public async Task Health_VeritabaniErisilebilir_200Doner()
    {
        var yanit = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, yanit.StatusCode);
    }
}
