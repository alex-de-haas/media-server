using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MediaServer.Api.Hosty;

// OpenTelemetry wiring for the Hosty observability collector.
//
// Hosty Core injects the standard OTEL_* environment (endpoint, http/protobuf protocol, service name,
// resource attributes, and the trace sampler) into the docker runtime of an app whose manifest opts
// into telemetry — but only when the operator has enabled observability and the collector is running.
// When that endpoint is absent (the dev/localCommand runtime, or observability turned off) we wire
// nothing, so the SDK never falls back to localhost:4318 and spams export failures (see
// docs/features/observability.md in the Hosty Core platform repo, not this one). The parameterless
// AddOtlpExporter() on each signal (traces, metrics, and the logging provider) reads every OTEL_* value
// from the environment, so there is no app-specific exporter configuration to keep in sync.
//
// Log levels: the OTLP log records are filtered by the same provider-agnostic `Logging:LogLevel` rules in
// appsettings.json as the stdout sink Core captures into logs/api.log — category filtering happens in
// ILogger before any provider is invoked. Notably that is what keeps EF Core's per-statement
// `Microsoft.EntityFrameworkCore.Database.Command` chatter (which embeds query parameter values) out of
// both sinks. Do not add a `Logging:OpenTelemetry:LogLevel` section without re-stating those categories:
// provider-specific rules win over the global ones and would send the flood to the collector again.
internal static class HostyTelemetry
{
    public static WebApplicationBuilder AddHostyTelemetry(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return builder;
        }

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.AddOtlpExporter();
        });

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return builder;
    }
}
