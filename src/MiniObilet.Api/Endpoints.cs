using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MiniObilet.Api.Data;

namespace MiniObilet.Api;

public record TripSummaryDto(int Id, string Company, string Origin, string Destination, DateTime DepartureTime, decimal Price, int AvailableSeats);
public record SeatDto(int Number, bool IsTaken);
public record SeatMapDto(int TripId, IReadOnlyList<SeatDto> Seats);
public record TicketRequest(int TripId, int SeatNumber, string PassengerName, string Email);
public record TicketDto(string Pnr, int TripId, int SeatNumber, string PassengerName, string Origin, string Destination, DateTime DepartureTime, decimal Price, bool IsCancelled);

public static class Endpoints
{
    private const string PnrAlphabet = "ABCDEFGHJKLMNPRSTUVYZ23456789";

    public static void MapMiniObiletApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/trips", SearchTrips);
        api.MapGet("/trips/{id:int}/seats", GetSeatMap);
        api.MapPost("/tickets", BuyTicket);
        api.MapGet("/tickets/{pnr}", GetTicket);
        api.MapDelete("/tickets/{pnr}", CancelTicket);
    }

    static async Task<IResult> SearchTrips(string? origin, string? destination, DateOnly? date, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(origin)) errors["origin"] = ["Origin is required."];
        if (string.IsNullOrWhiteSpace(destination)) errors["destination"] = ["Destination is required."];
        if (date is null) errors["date"] = ["Date is required (yyyy-MM-dd)."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var from = date!.Value.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(1);

        var trips = await db.Trips.AsNoTracking()
            .Where(t => t.Origin == origin && t.Destination == destination && t.DepartureTime >= from && t.DepartureTime < to)
            .OrderBy(t => t.DepartureTime)
            .Select(t => new TripSummaryDto(t.Id, t.Company, t.Origin, t.Destination, t.DepartureTime, t.Price,
                t.SeatCount - t.Tickets.Count(x => !x.IsCancelled)))
            .ToListAsync(ct);

        return Results.Ok(trips);
    }

    static async Task<IResult> GetSeatMap(int id, AppDbContext db, CancellationToken ct)
    {
        var trip = await db.Trips.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.SeatCount, Taken = t.Tickets.Where(x => !x.IsCancelled).Select(x => x.SeatNumber).ToList() })
            .FirstOrDefaultAsync(ct);
        if (trip is null) return Results.NotFound();

        var taken = trip.Taken.ToHashSet();
        var seats = Enumerable.Range(1, trip.SeatCount).Select(n => new SeatDto(n, taken.Contains(n))).ToList();
        return Results.Ok(new SeatMapDto(id, seats));
    }

    static async Task<IResult> BuyTicket(TicketRequest request, AppDbContext db, ILogger<Program> logger, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.PassengerName)) errors["passengerName"] = ["Passenger name is required."];
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@')) errors["email"] = ["A valid email is required."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var trip = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TripId, ct);
        if (trip is null) return Results.NotFound();
        if (request.SeatNumber < 1 || request.SeatNumber > trip.SeatCount)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["seatNumber"] = [$"Seat must be between 1 and {trip.SeatCount}."] });

        if (await IsSeatTakenAsync(db, request, ct)) return SeatTaken(request.SeatNumber);

        var ticket = new Ticket
        {
            Pnr = RandomNumberGenerator.GetString(PnrAlphabet, 6),
            TripId = request.TripId,
            SeatNumber = request.SeatNumber,
            PassengerName = request.PassengerName.Trim(),
            Email = request.Email.Trim().ToLowerInvariant(),
            CreatedAt = DateTime.UtcNow
        };
        db.Tickets.Add(ticket);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request took the seat after our check; the unique index rejected this insert
            db.ChangeTracker.Clear();
            if (await IsSeatTakenAsync(db, request, ct))
            {
                logger.LogInformation("Concurrent sale blocked {TripId} seat {SeatNumber}", request.TripId, request.SeatNumber);
                return SeatTaken(request.SeatNumber);
            }
            throw;
        }

        logger.LogInformation("Ticket sold {Pnr} {TripId} seat {SeatNumber}", ticket.Pnr, ticket.TripId, ticket.SeatNumber);
        return Results.Created($"/api/v1/tickets/{ticket.Pnr}", ToDto(ticket, trip));
    }

    // PNR alone is not enough: the email must match too (prevents IDOR)
    static async Task<IResult> GetTicket(string pnr, string? email, AppDbContext db, CancellationToken ct)
    {
        var ticket = await FindTicketAsync(db, pnr, email, tracked: false, ct);
        return ticket is null ? Results.NotFound() : Results.Ok(ToDto(ticket, ticket.Trip!));
    }

    static async Task<IResult> CancelTicket(string pnr, string? email, AppDbContext db, CancellationToken ct)
    {
        var ticket = await FindTicketAsync(db, pnr, email, tracked: true, ct);
        if (ticket is null) return Results.NotFound();
        if (!ticket.IsCancelled)
        {
            ticket.IsCancelled = true;
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    static Task<bool> IsSeatTakenAsync(AppDbContext db, TicketRequest request, CancellationToken ct) =>
        db.Tickets.AnyAsync(t => t.TripId == request.TripId && t.SeatNumber == request.SeatNumber && !t.IsCancelled, ct);

    static IResult SeatTaken(int seatNumber) =>
        Results.Problem(title: "Seat taken", detail: $"Seat {seatNumber} has just been sold.", statusCode: StatusCodes.Status409Conflict);

    static Task<Ticket?> FindTicketAsync(AppDbContext db, string pnr, string? email, bool tracked, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return Task.FromResult<Ticket?>(null);
        var query = tracked ? db.Tickets : db.Tickets.AsNoTracking();
        var normalizedEmail = email.Trim().ToLowerInvariant();
        return query.Include(t => t.Trip)
            .FirstOrDefaultAsync(t => t.Pnr == pnr.ToUpperInvariant() && t.Email == normalizedEmail, ct);
    }

    static TicketDto ToDto(Ticket t, Trip trip) =>
        new(t.Pnr, t.TripId, t.SeatNumber, t.PassengerName, trip.Origin, trip.Destination, trip.DepartureTime, trip.Price, t.IsCancelled);
}
