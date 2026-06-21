using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Realtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Guards the orchestrator's failure path: a stage that throws mid-save leaves its rejected changes on the
/// shared <see cref="MediaServerDbContext"/>, and the failure-recording SaveChangesAsync must not replay
/// them (which previously bubbled an unhandled exception out of <c>DriveAsync</c> and re-drove forever).
/// </summary>
public sealed class IngestOrchestratorFailureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public IngestOrchestratorFailureTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MediaServerDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.AddScoped<JobService>();
        services.AddScoped<IPipelineStage, PoisoningStage>();
        services.AddSingleton<IngestOrchestrator>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MediaServerDbContext>().Database.Migrate();
    }

    [Fact]
    public async Task A_stage_that_throws_mid_save_parks_for_retry_instead_of_crashing()
    {
        Guid ingestId;
        using (var scope = _provider.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var now = DateTimeOffset.UtcNow;
            var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Test", Type = CatalogType.Movie, Root = Path.Combine(Path.GetTempPath(), "ms-fail-" + Guid.NewGuid().ToString("N")), CreatedAt = now, UpdatedAt = now };
            var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Intake, Status = IngestStatus.Pending, CreatedAt = now, UpdatedAt = now };
            var sourceFile = new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingest.Id, RelativePath = "movie/movie.mkv", SizeBytes = 1, AssignmentStatus = SourceFileAssignmentStatus.Unassigned, CreatedAt = now, UpdatedAt = now };
            database.AddRange(catalog, ingest, sourceFile);
            await database.SaveChangesAsync();
            ingestId = ingest.Id;
        }

        var exception = await Record.ExceptionAsync(() =>
            _provider.GetRequiredService<IngestOrchestrator>().DriveAsync(ingestId, CancellationToken.None));
        Assert.Null(exception);

        using var verify = _provider.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var item = await verifyDb.IngestItems.SingleAsync(candidate => candidate.Id == ingestId);
        Assert.Equal(1, item.AttemptCount);
        Assert.Equal(IngestStatus.Pending, item.Status); // retryable failure → parked for another attempt
        Assert.Equal("boom", item.LastError);

        // The stage's rejected duplicate insert was discarded, not persisted alongside the failure status.
        Assert.Equal(1, await verifyDb.SourceFiles.CountAsync());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>Inserts a duplicate (IngestItemId, RelativePath) and saves, so the unique index throws
    /// inside the stage — leaving the rejected insert tracked on the shared context.</summary>
    private sealed class PoisoningStage(MediaServerDbContext database) : IPipelineStage
    {
        public string Key => "poison";
        public PipelinePhase Phase => PipelinePhase.Processing;
        public int Order => 100;
        public IngestStage Stage => IngestStage.Intake;
        public bool ShouldRun(IngestContext context) => true;

        public async Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            database.SourceFiles.Add(new SourceFile
            {
                Id = Guid.NewGuid(),
                IngestItemId = context.Item.Id,
                RelativePath = context.SourceFiles[0].RelativePath, // duplicate → trips the unique index
                SizeBytes = 1,
                AssignmentStatus = SourceFileAssignmentStatus.Unassigned,
                CreatedAt = now,
                UpdatedAt = now,
            });

            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new StageResult.Failed("boom", Retryable: true);
            }

            return StageResult.Done;
        }
    }
}
