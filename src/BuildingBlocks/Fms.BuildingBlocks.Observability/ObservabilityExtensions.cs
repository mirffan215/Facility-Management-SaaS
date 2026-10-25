using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace Fms.BuildingBlocks.Observability;

public static class ObservabilityExtensions
{
    /// <summary>Meters and activity sources created by FMS code; add new ones here so they are exported.</summary>
    public const string MeterPrefix = "Fms.*";

    /// <summary>
    /// Structured JSON logs (Serilog, to stdout for the container runtime), plus OpenTelemetry traces and metrics. Telemetry is
    /// exported over OTLP only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, so an air-gapped on-prem install sends nothing
    /// anywhere. Sinks registered in DI as ILogEventSink are honoured (tests, future shippers).
    /// </summary>
    public static TBuilder AddFmsObservability<TBuilder>(this TBuilder builder, string serviceName) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSerilog((sp, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(sp)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(MeterPrefix))
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter(MeterPrefix));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
