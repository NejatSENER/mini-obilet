using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniObilet.Api;

namespace MiniObilet.Tests;

public class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"miniobilet-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Db:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Db", $"Data Source={_dbPath}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

public class ApiTests(TestApp app) : IClassFixture<TestApp>
{
    private readonly HttpClient _client = app.CreateClient();
    private static readonly string Tomorrow = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd");

    private async Task<TripSummaryDto> FirstTripAsync(int daysAhead, string origin = "İstanbul", string destination = "Ankara")
    {
        var date = DateTime.UtcNow.Date.AddDays(daysAhead).ToString("yyyy-MM-dd");
        var trips = await _client.GetFromJsonAsync<List<TripSummaryDto>>($"/api/v1/trips?origin={origin}&destination={destination}&date={date}");
        return trips!.First();
    }

    private Task<HttpResponseMessage> BuyTicketAsync(int tripId, int seatNumber, string email = "passenger@example.com") =>
        _client.PostAsJsonAsync("/api/v1/tickets", new TicketRequest(tripId, seatNumber, "Test Passenger", email));

    [Fact]
    public async Task SearchTrips_ValidRoute_ReturnsTripsWithAvailableSeats()
    {
        var trips = await _client.GetFromJsonAsync<List<TripSummaryDto>>($"/api/v1/trips?origin=İstanbul&destination=İzmir&date={Tomorrow}");

        Assert.NotNull(trips);
        Assert.Equal(2, trips.Count);
        Assert.All(trips, t => Assert.Equal(40, t.AvailableSeats));
        Assert.True(trips[0].DepartureTime < trips[1].DepartureTime);
    }

    [Fact]
    public async Task SearchTrips_MissingOrigin_Returns400()
    {
        var response = await _client.GetAsync($"/api/v1/trips?destination=Ankara&date={Tomorrow}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BuyTicket_Success_Returns201AndSeatIsTaken()
    {
        var trip = await FirstTripAsync(daysAhead: 2);

        var response = await BuyTicketAsync(trip.Id, 5);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var map = await _client.GetFromJsonAsync<SeatMapDto>($"/api/v1/trips/{trip.Id}/seats");
        Assert.True(map!.Seats.Single(s => s.Number == 5).IsTaken);
        Assert.False(map.Seats.Single(s => s.Number == 6).IsTaken);
    }

    [Fact]
    public async Task BuyTicket_SameSeatTwice_Returns409()
    {
        var trip = await FirstTripAsync(daysAhead: 3);
        await BuyTicketAsync(trip.Id, 10);

        var second = await BuyTicketAsync(trip.Id, 10, "other@example.com");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task BuyTicket_TenConcurrentRequestsForSameSeat_OnlyOneSucceeds()
    {
        var trip = await FirstTripAsync(daysAhead: 4);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(i => BuyTicketAsync(trip.Id, 14, $"passenger{i}@example.com")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task CancelTicket_SeatCanBeSoldAgain()
    {
        var trip = await FirstTripAsync(daysAhead: 5);
        var ticket = await (await BuyTicketAsync(trip.Id, 20, "cancel@example.com")).Content.ReadFromJsonAsync<TicketDto>();

        var cancel = await _client.DeleteAsync($"/api/v1/tickets/{ticket!.Pnr}?email=cancel@example.com");
        var resold = await BuyTicketAsync(trip.Id, 20, "new@example.com");

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Created, resold.StatusCode);
    }

    [Fact]
    public async Task GetTicket_WrongEmail_Returns404()
    {
        var trip = await FirstTripAsync(daysAhead: 6);
        var ticket = await (await BuyTicketAsync(trip.Id, 1, "owner@example.com")).Content.ReadFromJsonAsync<TicketDto>();

        var owner = await _client.GetAsync($"/api/v1/tickets/{ticket!.Pnr}?email=owner@example.com");
        var stranger = await _client.GetAsync($"/api/v1/tickets/{ticket.Pnr}?email=stranger@example.com");

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    public async Task BuyTicket_InvalidSeatNumber_Returns400(int seatNumber)
    {
        var trip = await FirstTripAsync(daysAhead: 7);
        var response = await BuyTicketAsync(trip.Id, seatNumber);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_DatabaseReachable_Returns200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
