using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaServer.Api.Tests.Diagnostics;

// EF Core logs every executed statement at Information under
// Microsoft.EntityFrameworkCore.Database.Command — including the parameter values. Left on, it grew
// logs/api.log to hundreds of megabytes and shipped the same records to the OTLP collector. The
// appsettings filters below are the only thing holding that back, in every runtime profile, so they are
// asserted against the files the API actually ships (linked into the test output by the csproj).
public sealed class LoggingConfigurationTests
{
    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    private static IConfiguration LoadApiSettings(string? environment)
    {
        var settingsDir = Path.Combine(AppContext.BaseDirectory, "ApiSettings");
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(settingsDir, "appsettings.json"), optional: false);

        // Mirrors the host's own layering: the base file applies to every environment (the docker runtime
        // sets none, so it runs as Production), with the environment file merged on top.
        if (environment is not null)
        {
            builder.AddJsonFile(Path.Combine(settingsDir, $"appsettings.{environment}.json"), optional: false);
        }

        return builder.Build();
    }

    // Two providers of different types stand in for the two sinks: the stdout stream Core captures into
    // logs/api.log, and the OpenTelemetry logging provider that exports OTLP to the collector. Neither
    // declares a provider-specific `Logging:<Provider>:LogLevel` section, so both are filtered by the same
    // global rules — a record dropped here reaches neither sink.
    private static (ILoggerFactory Factory, CapturingProvider Stdout, CapturingProvider Otlp) BuildFactory(
        string? environment)
    {
        var configuration = LoadApiSettings(environment);
        var stdout = new CapturingProvider();
        var otlp = new OtlpLikeCapturingProvider();

        var factory = LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddProvider(stdout);
            logging.AddProvider(otlp);
        });

        return (factory, stdout, otlp);
    }

    [Theory]
    [InlineData(null)]           // docker runtime — no ASPNETCORE_ENVIRONMENT, so Production/base only
    [InlineData("Development")]  // dev runtime — localCommand via `dotnet run`
    public void Ef_core_command_logging_is_suppressed_below_warning(string? environment)
    {
        var (factory, stdout, otlp) = BuildFactory(environment);
        using (factory)
        {
            var logger = factory.CreateLogger(EfCommandCategory);

            Assert.False(logger.IsEnabled(LogLevel.Information));
            Assert.False(logger.IsEnabled(LogLevel.Debug));

            logger.LogInformation("Executed DbCommand (0ms) [Parameters=[@p0='secret'], CommandType='Text']");

            Assert.Empty(stdout.Records);
            Assert.Empty(otlp.Records);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    public void Ef_core_warnings_and_errors_still_reach_both_sinks(string? environment)
    {
        var (factory, stdout, otlp) = BuildFactory(environment);
        using (factory)
        {
            var logger = factory.CreateLogger(EfCommandCategory);

            logger.LogWarning("Command failed.");
            logger.LogError("Connection lost.");

            Assert.Equal([LogLevel.Warning, LogLevel.Error], stdout.Records);
            Assert.Equal([LogLevel.Warning, LogLevel.Error], otlp.Records);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    public void Application_logging_stays_at_information(string? environment)
    {
        var (factory, stdout, otlp) = BuildFactory(environment);
        using (factory)
        {
            var logger = factory.CreateLogger("MediaServer.Api.Pipeline.IngestOrchestrator");

            logger.LogInformation("Ingest completed.");

            Assert.Equal([LogLevel.Information], stdout.Records);
            Assert.Equal([LogLevel.Information], otlp.Records);
        }
    }

    private class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogLevel> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Records);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<LogLevel> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => records.Enqueue(logLevel);
        }
    }

    // A distinct provider type: rule selection keys off the provider, so this proves the filters are not
    // accidentally scoped to a single sink.
    private sealed class OtlpLikeCapturingProvider : CapturingProvider;
}
