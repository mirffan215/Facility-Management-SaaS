using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Bookings.Persistence;
using Fms.Bookings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.Bookings;

public static class BookingsServiceCollectionExtensions
{
    public static IServiceCollection AddFmsBookings(this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<BookingsDbContext>((sp, options) => options
            .UseSqlServer(connectionString, BookingsDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.Configure<CalendarOptions>(configuration.GetSection(CalendarOptions.SectionName));
        services.AddScoped<ResourceService>();
        services.AddScoped<BookingService>();
        services.AddScoped<CalendarFeedService>();
        services.AddScoped<CalendarSyncService>();
        services.AddSingleton<ICalendarAdapter, FakeCalendarAdapter>();
        services.AddScoped<IOutboxHandler, CalendarSyncHandler>();
        services.AddScoped<IOutboxHandler, BookingPendingHandler>();
        services.AddScoped<IOutboxHandler, BookingDecidedHandler>();
        services.AddScoped<NoShows>();
        services.AddSingleton<BookingMonitor>();
        services.AddHealthChecks().AddDbContextCheck<BookingsDbContext>("bookings-db", tags: ["ready"]);
        services.AddScoped<Fms.BuildingBlocks.Reporting.IDatasetCatalog>(sp => new Fms.BuildingBlocks.Reporting.DatasetCatalog(BookingsDatasets.All(sp.GetRequiredService<Persistence.BookingsDbContext>()).ToList()));
        services.AddScoped<Fms.BuildingBlocks.Reporting.IReportProvider, BookingsReportProvider>();
        return services;
    }
}
