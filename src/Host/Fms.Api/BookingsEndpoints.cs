using Fms.BuildingBlocks.Security;
using Fms.Bookings.Domain;
using Fms.Bookings.Services;

namespace Fms.Api;

public static class BookingsEndpoints
{
    public static void MapBookings(this RouteGroupBuilder v1)
    {
        var r = v1.MapGroup("/resources");
        r.MapGet("", async (ResourceService s, ResourceKind? kind, string? q, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.ListAsync(kind, q, includeArchived ?? false, ct))).RequirePermission(Permissions.BookingsRead);
        r.MapGet("/{id:guid}", async (Guid id, ResourceService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.BookingsRead);
        r.MapPost("", async (SaveResourceRequest x, ResourceService s, CancellationToken ct) => (await s.SaveAsync(null, x, ct)).ToHttp(d => Results.Created($"/api/v1/resources/{d.Id}", d))).RequirePermission(Permissions.BookingsManage);
        r.MapPut("/{id:guid}", async (Guid id, SaveResourceRequest x, ResourceService s, CancellationToken ct) => (await s.SaveAsync(id, x, ct)).ToHttp()).RequirePermission(Permissions.BookingsManage);
        r.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest x, ResourceService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, x.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.BookingsManage);
        r.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest x, ResourceService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, x.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.BookingsManage);

        var b = v1.MapGroup("/bookings");
        b.MapGet("/availability", async (BookingService s, Guid resourceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => (await s.AvailabilityAsync(resourceId, from, to, ct)).ToHttp()).RequirePermission(Permissions.BookingsRead);
        b.MapGet("/mine", async (BookingService s, DateTimeOffset? from, DateTimeOffset? to, BookingStatus? status, CancellationToken ct) => (await s.ListAsync(false, null, from, to, status, false, ct)).ToHttp()).RequirePermission(Permissions.BookingsRead);
        b.MapGet("", async (BookingService s, Guid? resourceId, DateTimeOffset? from, DateTimeOffset? to, BookingStatus? status, bool? pendingOnly, CancellationToken ct) =>
            (await s.ListAsync(true, resourceId, from, to, status, pendingOnly ?? false, ct)).ToHttp()).RequireAnyPermission(Permissions.BookingsApprove, Permissions.BookingsManage);
        b.MapPost("", async (CreateBookingRequest x, BookingService s, CancellationToken ct) => (await s.CreateAsync(x, ct)).ToHttp(d => Results.Created("/api/v1/bookings/mine", d))).RequirePermission(Permissions.BookingsBook);
        b.MapGet("/{id:guid}", async (Guid id, BookingService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.BookingsRead);
        b.MapPost("/{id:guid}/cancel", async (Guid id, CancelBookingRequest x, BookingService s, CancellationToken ct) => (await s.CancelAsync(id, x, ct)).ToHttp()).RequireAnyPermission(Permissions.BookingsBook, Permissions.BookingsManage);
        b.MapPost("/{id:guid}/approve", async (Guid id, DecideBookingRequest x, BookingService s, CancellationToken ct) => (await s.ApproveAsync(id, x, ct)).ToHttp()).RequirePermission(Permissions.BookingsApprove);
        b.MapPost("/{id:guid}/reject", async (Guid id, DecideBookingRequest x, BookingService s, CancellationToken ct) => (await s.RejectAsync(id, x, ct)).ToHttp()).RequirePermission(Permissions.BookingsApprove);
        b.MapPost("/{id:guid}/check-in", async (Guid id, RowVersionRequest x, BookingService s, CancellationToken ct) => (await s.CheckInAsync(id, x.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.BookingsBook);

        // One person's own calendar address. The address is returned once, when it is made.
        b.MapGet("/calendar-feed", async (CalendarFeedService s, CancellationToken ct) => Results.Ok(await s.StatusAsync(ct))).RequirePermission(Permissions.BookingsRead);
        b.MapPost("/calendar-feed", async (CalendarFeedService s, CancellationToken ct) => (await s.CreateAsync(ct)).ToHttp()).RequirePermission(Permissions.BookingsRead);
        b.MapDelete("/calendar-feed", async (CalendarFeedService s, CancellationToken ct) => (await s.RevokeAsync(ct)).ToHttp()).RequirePermission(Permissions.BookingsRead);

        b.MapGet("/calendar-sync", async (CalendarSyncService s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct))).RequirePermission(Permissions.BookingsManage);
        b.MapPut("/calendar-sync", async (SaveCalendarSyncRequest x, CalendarSyncService s, CancellationToken ct) => (await s.SaveAsync(x, ct)).ToHttp()).RequirePermission(Permissions.BookingsManage);

        // What a calendar app reads. It cannot sign in, so the secret in the address is the credential; every wrong address gets the same answer.
        v1.MapGet("/calendar/{token}", async (string token, HttpContext http, CalendarFeedService s, CancellationToken ct) =>
            {
                var feed = await s.RenderAsync(token, ct);
                if (feed is null) return Results.NotFound();
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Text(feed, "text/calendar; charset=utf-8");
            })
            .AllowAnonymous();
    }
}
