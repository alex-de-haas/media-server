using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Data;

public sealed class MediaServerDbContext(DbContextOptions<MediaServerDbContext> options)
    : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Catalog> Catalogs => Set<Catalog>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MovieCollection> MovieCollections => Set<MovieCollection>();
    public DbSet<MediaSource> MediaSources => Set<MediaSource>();
    public DbSet<MediaStream> MediaStreams => Set<MediaStream>();
    public DbSet<MetadataRecord> MetadataRecords => Set<MetadataRecord>();
    public DbSet<ImageAsset> ImageAssets => Set<ImageAsset>();
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<MediaItemPerson> MediaItemPersons => Set<MediaItemPerson>();
    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<TranscodeJob> TranscodeJobs => Set<TranscodeJob>();

    public DbSet<TranscodeJobOutput> TranscodeJobOutputs => Set<TranscodeJobOutput>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<IngestItem> IngestItems => Set<IngestItem>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JellyfinCredential> JellyfinCredentials => Set<JellyfinCredential>();
    public DbSet<JellyfinAccessToken> JellyfinAccessTokens => Set<JellyfinAccessToken>();
    public DbSet<UserItemData> UserItemData => Set<UserItemData>();
    public DbSet<PlaybackSession> PlaybackSessions => Set<PlaybackSession>();
    public DbSet<PlaybackHistoryEntry> PlaybackHistoryEntries => Set<PlaybackHistoryEntry>();
    public DbSet<WatchHistoryProviderConnection> WatchHistoryConnections => Set<WatchHistoryProviderConnection>();
    public DbSet<WatchHistoryProviderAuthorization> WatchHistoryAuthorizations => Set<WatchHistoryProviderAuthorization>();
    public DbSet<WatchHistoryOutboxEvent> WatchHistoryOutboxEvents => Set<WatchHistoryOutboxEvent>();

    public DbSet<WatchHistoryFavoriteState> WatchHistoryFavoriteStates => Set<WatchHistoryFavoriteState>();
    public DbSet<WatchHistorySyncRun> WatchHistorySyncRuns => Set<WatchHistorySyncRun>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<TrackedTitle> TrackedTitles => Set<TrackedTitle>();
    public DbSet<TrackedRelease> TrackedReleases => Set<TrackedRelease>();
    public DbSet<WatchlistEntry> WatchlistEntries => Set<WatchlistEntry>();
    public DbSet<ReleaseReminder> ReleaseReminders => Set<ReleaseReminder>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();
    public DbSet<RecommendationHide> RecommendationHides => Set<RecommendationHide>();
    public DbSet<TmdbRecommendationCacheEntry> TmdbRecommendationCache => Set<TmdbRecommendationCacheEntry>();
    public DbSet<RecommendationShelfItem> RecommendationShelfItems => Set<RecommendationShelfItem>();
    public DbSet<RecommendationShelfGeneration> RecommendationShelfGenerations => Set<RecommendationShelfGeneration>();
    public DbSet<TmdbTitleDetailCacheEntry> TmdbTitleDetailCache => Set<TmdbTitleDetailCacheEntry>();
    public DbSet<RecommendationPreference> RecommendationPreferences => Set<RecommendationPreference>();
    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();
    public DbSet<PlaybackPreference> PlaybackPreferences => Set<PlaybackPreference>();

    /// <summary>
    /// Registers <see cref="UtcDateTimeOffsetConverter"/> for every <see cref="DateTimeOffset"/> and
    /// <see cref="Nullable{DateTimeOffset}"/> property. This is the permanent guardrail that lets SQLite
    /// order and compare timestamps in SQL — without it the provider throws on <c>ORDER BY</c>/<c>WHERE</c>
    /// over a <see cref="DateTimeOffset"/> column. A value-type registration also covers its nullable form.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAppUser(modelBuilder);
        ConfigureCatalog(modelBuilder);
        ConfigureMediaItem(modelBuilder);
        ConfigureMovieCollection(modelBuilder);
        ConfigureMediaSource(modelBuilder);
        ConfigureMetadataRecord(modelBuilder);
        ConfigureImageAsset(modelBuilder);
        ConfigurePerson(modelBuilder);
        ConfigureDownload(modelBuilder);
        ConfigureTranscodeJob(modelBuilder);
        ConfigureSourceFile(modelBuilder);
        ConfigureIngestItem(modelBuilder);
        ConfigureJob(modelBuilder);
        ConfigureJellyfinCredential(modelBuilder);
        ConfigureUserItemData(modelBuilder);
        ConfigureAppSettings(modelBuilder);
        ConfigureReleaseTracking(modelBuilder);
        ConfigureChangeLog(modelBuilder);
        ConfigurePlaybackPreference(modelBuilder);
    }

    private static void ConfigurePlaybackPreference(ModelBuilder modelBuilder)
    {
        var preference = modelBuilder.Entity<PlaybackPreference>();
        preference.HasKey(row => row.Id);

        // One preference per scope: the user's default, and at most one override per title. A second
        // row for the same scope would make "which one wins" a question nobody can answer.
        preference.HasIndex(row => new { row.AppUserId, row.MediaItemId }).IsUnique();

        // The composite index above does not constrain the default: SQL treats NULLs as distinct, so
        // two rows of (user, NULL) satisfy it and the user ends up with two defaults. A filtered index
        // is what actually enforces one.
        preference.HasIndex(row => row.AppUserId)
            .IsUnique()
            .HasFilter("\"MediaItemId\" IS NULL")
            .HasDatabaseName("IX_PlaybackPreferences_AppUserId_Global");

        preference.HasOne<AppUser>().WithMany()
            .HasForeignKey(row => row.AppUserId).OnDelete(DeleteBehavior.Cascade);

        // A deleted title takes its override with it; the user's default is untouched.
        preference.HasOne<MediaItem>().WithMany()
            .HasForeignKey(row => row.MediaItemId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureChangeLog(ModelBuilder modelBuilder)
    {
        var log = modelBuilder.Entity<ChangeLogEntry>();
        log.HasKey(entry => entry.Sequence);
        log.Property(entry => entry.Sequence).ValueGeneratedOnAdd();
        log.Property(entry => entry.EntityId).IsRequired();

        // Sync pages by sequence and filters per-user rows, so the read is a range scan over exactly
        // this pair.
        log.HasIndex(entry => new { entry.Sequence, entry.AppUserId });

        // Pruning is by age; the index makes finding the cut cheap.
        log.HasIndex(entry => entry.OccurredAt);
    }

    /// <summary>
    /// Bumps the application-managed concurrency tokens SQLite cannot provide: the
    /// <see cref="IngestItem"/> row version, and the <see cref="UserItemData.StateRevision"/> a
    /// long-running sync captures before reading a provider and re-checks before applying.
    /// </summary>
    /// <remarks>
    /// Done here rather than at each mutation site so a future code path cannot forget it. Forgetting
    /// would be quiet and expensive: a sync would apply a stale remote snapshot over a play recorded
    /// while it was running, and nothing would look wrong at the time.
    /// </remarks>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// The synchronous twin. It exists because a hook that only covers the async overload has a hole
    /// exactly the width of one `SaveChanges()` call, and the resulting miss is silent: the row saves,
    /// the notification does not.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private void BeforeSave()
    {
        foreach (var entry in ChangeTracker.Entries<IngestItem>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
            }
        }

        foreach (var entry in ChangeTracker.Entries<UserItemData>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.StateRevision++;
            }
        }

        AppendChangeLog();
    }

    /// <summary>
    /// Records what a native client mirrors, in the same unit of work as the mutation, so the two
    /// commit together or not at all. Same reasoning as the concurrency tokens above: a per-site call
    /// is a call a later contributor forgets, and the failure is invisible — the row simply stops
    /// reaching clients.
    /// </summary>
    /// <remarks>
    /// This covers writes that go through the change tracker. <c>ExecuteDelete</c>/<c>ExecuteUpdate</c>
    /// bypass it entirely and must append explicitly; see <c>LibraryDeleteService</c>, which does so
    /// inside its own transaction.
    /// </remarks>
    private void AppendChangeLog()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<ChangeLogEntry>();

        foreach (var entry in ChangeTracker.Entries<MediaItem>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                rows.Add(new ChangeLogEntry
                {
                    EntityType = ChangeEntityType.MediaItem,
                    EntityId = entry.Entity.Id.ToString("N"),
                    Kind = entry.State == EntityState.Deleted ? ChangeKind.Delete : ChangeKind.Upsert,
                    OccurredAt = now,
                });
            }
        }

        foreach (var entry in ChangeTracker.Entries<UserItemData>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                rows.Add(new ChangeLogEntry
                {
                    EntityType = ChangeEntityType.UserItemData,
                    EntityId = entry.Entity.MediaItemId.ToString("N"),
                    AppUserId = entry.Entity.AppUserId,
                    Kind = entry.State == EntityState.Deleted ? ChangeKind.Delete : ChangeKind.Upsert,
                    OccurredAt = now,
                });
            }
        }

        foreach (var entry in ChangeTracker.Entries<PlaybackPreference>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                rows.Add(new ChangeLogEntry
                {
                    EntityType = ChangeEntityType.PlaybackPreference,
                    // The scope it applies to, so a client can key its local copy the same way.
                    EntityId = entry.Entity.MediaItemId?.ToString("N") ?? "global",
                    AppUserId = entry.Entity.AppUserId,
                    Kind = entry.State == EntityState.Deleted ? ChangeKind.Delete : ChangeKind.Upsert,
                    OccurredAt = now,
                });
            }
        }

        if (rows.Count > 0)
        {
            ChangeLog.AddRange(rows);
        }
    }

    private static void ConfigureAppUser(ModelBuilder modelBuilder)
    {
        var appUser = modelBuilder.Entity<AppUser>();
        appUser.HasKey(user => user.Id);
        appUser.Property(user => user.HostUserId).IsRequired();
        appUser.HasIndex(user => user.HostUserId).IsUnique();
        appUser.HasIndex(user => user.Email);
        appUser.Property(user => user.Role).HasConversion<int>();
    }

    private static void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        var catalog = modelBuilder.Entity<Catalog>();
        catalog.HasKey(entity => entity.Id);
        catalog.Property(entity => entity.Name).IsRequired();
        catalog.Property(entity => entity.Root).IsRequired();
        catalog.Property(entity => entity.Type).HasConversion<int>();

        // Identity is the mount label + the path within it, so two catalogs can't claim one directory even
        // though the absolute Root they resolve to differs per runtime. SQLite treats NULLs as distinct in
        // a unique index, so standalone rows (no label) never collide here...
        catalog.HasIndex(entity => new { entity.MountLabel, entity.MountRelativePath }).IsUnique();
        // ...and are instead kept unique on their free-text absolute Root. Filtered to those rows only:
        // an anchored Root is rewritten on every start, and a swapped pair of mount labels would trip an
        // unfiltered unique index mid-rewrite.
        catalog.HasIndex(entity => entity.Root).IsUnique().HasFilter("\"MountLabel\" IS NULL");
    }

    private static void ConfigureMediaItem(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<MediaItem>();
        item.HasKey(entity => entity.Id);
        item.Property(entity => entity.Kind).HasConversion<int>();
        item.Property(entity => entity.Title).IsRequired();
        item.Property(entity => entity.Providers).HasJsonDictionaryConversion();
        item.HasIndex(entity => entity.PublicId).IsUnique();
        item.HasIndex(entity => new { entity.CatalogId, entity.IdentityProvider, entity.IdentityProviderId });

        // No unique index enforces "one catalog per work" at the database level, deliberately. A partial
        // unique index over published movie/series identity was built and rejected on evidence: creating
        // it fails outright (SQLite error 19) on any existing database that already holds a duplicate
        // pair, so upgrading such a server would leave the app unable to start — and the pair can only
        // be repaired *while both rows exist*, by moving one onto the other. The rule is enforced where
        // duplicates are born instead (IdentifyService's cross-catalog gate), audited by the library
        // scan, and tolerated everywhere that already copes with two copies (watch-history's ambiguous
        // identity, recommendations' multi-copy handling).

        // SetNull (not Cascade): CatalogService.DeleteAsync decides explicitly what a catalog delete
        // takes with it — tombstones with user data survive catalog-less. The FK is only a safety net,
        // and a safety net must never be the thing that erases a user's history.
        item.HasOne(entity => entity.Catalog)
            .WithMany()
            .HasForeignKey(entity => entity.CatalogId)
            .OnDelete(DeleteBehavior.SetNull);

        // Self-hierarchy: Season → Series, Episode → Season.
        item.HasOne<MediaItem>()
            .WithMany()
            .HasForeignKey(entity => entity.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Movie → franchise/collection (one-to-many). SetNull (not Cascade): pruning a collection must never
        // delete its movies, it just unlinks them.
        item.HasIndex(entity => entity.CollectionId);
        item.HasOne(entity => entity.Collection)
            .WithMany(entity => entity.Movies)
            .HasForeignKey(entity => entity.CollectionId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureMovieCollection(ModelBuilder modelBuilder)
    {
        var collection = modelBuilder.Entity<MovieCollection>();
        collection.HasKey(entity => entity.Id);
        collection.Property(entity => entity.Provider).IsRequired();
        collection.Property(entity => entity.ProviderId).IsRequired();
        collection.Property(entity => entity.Name).IsRequired();
        // One row per provider identity; the upsert keys on this pair so a collection is shared across its movies.
        collection.HasIndex(entity => new { entity.Provider, entity.ProviderId }).IsUnique();
    }

    private static void ConfigureMediaSource(ModelBuilder modelBuilder)
    {
        var source = modelBuilder.Entity<MediaSource>();
        source.HasKey(entity => entity.Id);
        source.Property(entity => entity.Container).IsRequired();
        source.Property(entity => entity.Path).IsRequired();

        source.HasOne(entity => entity.MediaItem)
            .WithMany(entity => entity.Sources)
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        var stream = modelBuilder.Entity<MediaStream>();
        stream.HasKey(entity => entity.Id);
        stream.Property(entity => entity.StreamType).HasConversion<int>();
        stream.HasOne(entity => entity.MediaSource)
            .WithMany(entity => entity.Streams)
            .HasForeignKey(entity => entity.MediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMetadataRecord(ModelBuilder modelBuilder)
    {
        var record = modelBuilder.Entity<MetadataRecord>();
        record.HasKey(entity => entity.Id);
        record.Property(entity => entity.Provider).IsRequired();
        record.Property(entity => entity.Language).IsRequired();
        record.Property(entity => entity.Genres).HasJsonListConversion();
        record.HasIndex(entity => new { entity.MediaItemId, entity.Provider, entity.Language }).IsUnique();

        record.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureImageAsset(ModelBuilder modelBuilder)
    {
        var image = modelBuilder.Entity<ImageAsset>();
        image.HasKey(entity => entity.Id);
        image.Property(entity => entity.ImageType).HasConversion<int>();
        image.Property(entity => entity.Provider).IsRequired();
        image.Property(entity => entity.RemotePath).IsRequired();
        image.Property(entity => entity.Tag).IsRequired();

        image.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigurePerson(ModelBuilder modelBuilder)
    {
        var person = modelBuilder.Entity<Person>();
        person.HasKey(entity => entity.Id);
        person.Property(entity => entity.Provider).IsRequired();
        person.Property(entity => entity.ProviderId).IsRequired();
        person.Property(entity => entity.Name).IsRequired();
        // One row per provider identity; the upsert keys on this pair so a person is shared across items.
        person.HasIndex(entity => new { entity.Provider, entity.ProviderId }).IsUnique();

        var credit = modelBuilder.Entity<MediaItemPerson>();
        credit.HasKey(entity => entity.Id);
        credit.Property(entity => entity.Role).HasConversion<int>();
        credit.HasIndex(entity => entity.PersonId);
        credit.HasIndex(entity => entity.MediaItemId);

        credit.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        credit.HasOne(entity => entity.Person)
            .WithMany(entity => entity.Credits)
            .HasForeignKey(entity => entity.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureDownload(ModelBuilder modelBuilder)
    {
        var download = modelBuilder.Entity<Download>();
        download.HasKey(entity => entity.Id);
        download.Property(entity => entity.InfoHash).IsRequired();
        download.Property(entity => entity.SavePath).IsRequired();
        download.Property(entity => entity.SourceType).HasConversion<int>();
        download.Property(entity => entity.State).HasConversion<int>();
        download.HasIndex(entity => entity.InfoHash).IsUnique();

        download.HasOne(entity => entity.Catalog)
            .WithMany()
            .HasForeignKey(entity => entity.CatalogId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureTranscodeJob(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<TranscodeJob>();
        job.HasKey(entity => entity.Id);
        job.Property(entity => entity.EngineJobId).IsRequired();
        job.Property(entity => entity.InputPath).IsRequired();
        job.Property(entity => entity.VideoCodec).IsRequired();
        job.Property(entity => entity.HardwareAcceleration).IsRequired();
        job.Property(entity => entity.State).HasConversion<int>();
        job.Property(entity => entity.Kind).HasConversion<int>();
        job.HasIndex(entity => entity.EngineJobId).IsUnique();
        job.HasIndex(entity => entity.State);

        // Cascade: an extraction's outputs describe that job and nothing else, so they go with it.
        job.HasMany(entity => entity.Outputs)
            .WithOne(output => output.TranscodeJob)
            .HasForeignKey(output => output.TranscodeJobId)
            .OnDelete(DeleteBehavior.Cascade);

        var output = modelBuilder.Entity<TranscodeJobOutput>();
        output.HasKey(entity => entity.Id);
        output.Property(entity => entity.RelativePath).IsRequired();
        output.Property(entity => entity.StreamType).HasConversion<int>();

        // Cascade from the source: removing the original source (e.g. after a verified replace) drops its
        // job history too. The movie/catalog links are denormalized for listing.
        job.HasOne(entity => entity.MediaSource)
            .WithMany()
            .HasForeignKey(entity => entity.MediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        job.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        job.HasOne(entity => entity.Catalog)
            .WithMany()
            .HasForeignKey(entity => entity.CatalogId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSourceFile(ModelBuilder modelBuilder)
    {
        var sourceFile = modelBuilder.Entity<SourceFile>();
        sourceFile.HasKey(entity => entity.Id);
        sourceFile.Property(entity => entity.RelativePath).IsRequired();
        sourceFile.Property(entity => entity.AssignmentStatus).HasConversion<int>();

        // An ingest cannot have two rows for the same file. Concurrent coordinator handlers
        // (metadata + completion for a re-added, already-complete torrent) once raced and inserted
        // duplicates; the unique index makes the loser's insert fail so the upsert falls back to update.
        sourceFile.HasIndex(entity => new { entity.IngestItemId, entity.RelativePath }).IsUnique();

        // Owned by the ingest item for its whole lifetime (deleting the ingest removes its source files).
        sourceFile.HasOne(entity => entity.IngestItem)
            .WithMany(entity => entity.SourceFiles)
            .HasForeignKey(entity => entity.IngestItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // The download is transient: deleting it at the download→identify hand-off leaves the file owned
        // by the ingest with a null DownloadId.
        sourceFile.HasOne(entity => entity.Download)
            .WithMany(entity => entity.SourceFiles)
            .HasForeignKey(entity => entity.DownloadId)
            .OnDelete(DeleteBehavior.SetNull);

        sourceFile.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureIngestItem(ModelBuilder modelBuilder)
    {
        var ingest = modelBuilder.Entity<IngestItem>();
        ingest.HasKey(entity => entity.Id);
        ingest.Property(entity => entity.Stage).HasConversion<int>();
        ingest.Property(entity => entity.Status).HasConversion<int>();
        ingest.Property(entity => entity.TargetKind).HasConversion<int>();
        ingest.Property(entity => entity.StagesCompleted).HasJsonListConversion();
        ingest.Property(entity => entity.RowVersion).IsConcurrencyToken();
        ingest.HasIndex(entity => entity.Status);

        ingest.HasOne(entity => entity.Catalog)
            .WithMany()
            .HasForeignKey(entity => entity.CatalogId)
            .OnDelete(DeleteBehavior.Restrict);

        ingest.HasOne(entity => entity.Download)
            .WithMany()
            .HasForeignKey(entity => entity.DownloadId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    private static void ConfigureJob(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Entity<Job>();
        job.HasKey(entity => entity.Id);
        job.Property(entity => entity.Type).IsRequired();
        job.Property(entity => entity.Status).HasConversion<int>();
        job.HasIndex(entity => new { entity.RelatedType, entity.RelatedId });
    }

    private static void ConfigureJellyfinCredential(ModelBuilder modelBuilder)
    {
        var credential = modelBuilder.Entity<JellyfinCredential>();
        credential.HasKey(entity => entity.Id);
        credential.Property(entity => entity.HostUserId).IsRequired();
        credential.Property(entity => entity.Username).IsRequired();
        credential.Property(entity => entity.PinHash).IsRequired();
        // One credential per internal user; the username (Hosty email) is the login handle.
        credential.HasIndex(entity => entity.AppUserId).IsUnique();
        credential.HasIndex(entity => entity.Username).IsUnique();

        credential.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var token = modelBuilder.Entity<JellyfinAccessToken>();
        token.HasKey(entity => entity.Id);
        token.Property(entity => entity.TokenHash).IsRequired();
        token.HasIndex(entity => entity.TokenHash).IsUnique();
        token.HasIndex(entity => entity.AppUserId);

        token.HasOne(entity => entity.Credential)
            .WithMany(entity => entity.Tokens)
            .HasForeignKey(entity => entity.CredentialId)
            .OnDelete(DeleteBehavior.Cascade);

        token.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAppSettings(ModelBuilder modelBuilder)
    {
        var appSettings = modelBuilder.Entity<AppSettings>();
        appSettings.HasKey(entity => entity.Id);
        // Single fixed-id row; never auto-generate the key so the upsert always targets row 1.
        appSettings.Property(entity => entity.Id).ValueGeneratedNever();
        appSettings.Property(entity => entity.CustomReleaseGroups).HasJsonListConversion();
    }

    private static void ConfigureReleaseTracking(ModelBuilder modelBuilder)
    {
        var title = modelBuilder.Entity<TrackedTitle>();
        title.HasKey(entity => entity.Id);
        title.Property(entity => entity.Kind).HasConversion<int>();
        title.Property(entity => entity.IdentityProvider).IsRequired();
        title.Property(entity => entity.IdentityProviderId).IsRequired();
        title.Property(entity => entity.Title).IsRequired();
        title.Property(entity => entity.Providers).HasJsonDictionaryConversion();
        // One row per canonical provider identity — a title tracked by several users is stored/synced once.
        title.HasIndex(entity => new { entity.IdentityProvider, entity.IdentityProviderId }).IsUnique();
        title.HasIndex(entity => entity.MediaItemId);

        // Deleting the library item unlinks the tracked title back to wishlist state (never deletes it).
        title.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.SetNull);

        var release = modelBuilder.Entity<TrackedRelease>();
        release.HasKey(entity => entity.Id);
        release.Property(entity => entity.Type).HasConversion<int>();
        // Unique release-event identity via two filtered indexes: SQLite treats NULLs as distinct in a
        // plain unique constraint, so a single (…, Region, …, Season, Episode) key would not deduplicate.
        release.HasIndex(entity => new { entity.TrackedTitleId, entity.Region, entity.Type })
            .IsUnique()
            .HasFilter("\"Region\" IS NOT NULL")
            .HasDatabaseName("IX_TrackedReleases_MovieIdentity");
        release.HasIndex(entity => new { entity.TrackedTitleId, entity.Type, entity.Season, entity.Episode })
            .IsUnique()
            .HasFilter("\"Region\" IS NULL")
            .HasDatabaseName("IX_TrackedReleases_EpisodeIdentity");
        release.HasIndex(entity => entity.Date);

        release.HasOne(entity => entity.TrackedTitle)
            .WithMany(entity => entity.Releases)
            .HasForeignKey(entity => entity.TrackedTitleId)
            .OnDelete(DeleteBehavior.Cascade);

        var entry = modelBuilder.Entity<WatchlistEntry>();
        entry.HasKey(item => item.Id);
        entry.Property(item => item.MonitorScope).HasConversion<int?>();
        entry.Property(item => item.MonitoredSeasons).HasJsonIntListConversion();
        // One subscription per user per title.
        entry.HasIndex(item => new { item.AppUserId, item.TrackedTitleId }).IsUnique();

        entry.HasOne(item => item.AppUser)
            .WithMany()
            .HasForeignKey(item => item.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        entry.HasOne(item => item.TrackedTitle)
            .WithMany(item => item.Entries)
            .HasForeignKey(item => item.TrackedTitleId)
            .OnDelete(DeleteBehavior.Cascade);

        var reminder = modelBuilder.Entity<ReleaseReminder>();
        reminder.HasKey(item => item.Id);
        reminder.Property(item => item.ReleaseType).HasConversion<int>();
        // A reminder targets a (title, type), not a date — one per user per pair.
        reminder.HasIndex(item => new { item.AppUserId, item.TrackedTitleId, item.ReleaseType }).IsUnique();

        reminder.HasOne(item => item.AppUser)
            .WithMany()
            .HasForeignKey(item => item.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        reminder.HasOne(item => item.TrackedTitle)
            .WithMany()
            .HasForeignKey(item => item.TrackedTitleId)
            .OnDelete(DeleteBehavior.Cascade);

        var delivery = modelBuilder.Entity<ReminderDelivery>();
        delivery.HasKey(item => item.Id);
        // Exactly one notification per (reminder, concrete release event).
        delivery.HasIndex(item => new { item.ReminderId, item.TrackedReleaseId }).IsUnique();

        delivery.HasOne(item => item.Reminder)
            .WithMany(item => item.Deliveries)
            .HasForeignKey(item => item.ReminderId)
            .OnDelete(DeleteBehavior.Cascade);

        delivery.HasOne(item => item.TrackedRelease)
            .WithMany()
            .HasForeignKey(item => item.TrackedReleaseId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureUserItemData(ModelBuilder modelBuilder)
    {
        var userData = modelBuilder.Entity<UserItemData>();
        userData.HasKey(entity => entity.Id);
        // One row per (user, item); the read path looks data up on this pair.
        userData.HasIndex(entity => new { entity.AppUserId, entity.MediaItemId }).IsUnique();

        userData.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        userData.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        var session = modelBuilder.Entity<PlaybackSession>();
        session.HasKey(entity => entity.Id);
        // One row per (user, item, client session): the progress path looks a session up on this
        // triple on every report, and the uniqueness is what makes "count this viewing once" hold.
        session.HasIndex(entity => new { entity.AppUserId, entity.MediaItemId, entity.SessionKey }).IsUnique();
        // Age-based cleanup scans this.
        session.HasIndex(entity => entity.LastReportAt);
        session.Property(entity => entity.SessionKey).HasMaxLength(200);

        session.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        session.HasOne<MediaItem>()
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        var hides = modelBuilder.Entity<RecommendationHide>();
        hides.HasKey(entity => entity.Id);
        // One hide per user and title: hiding twice is the same intent, and a duplicate would make
        // the un-hide ambiguous.
        hides.HasIndex(entity => new { entity.AppUserId, entity.Kind, entity.TmdbId }).IsUnique();
        hides.Property(entity => entity.Kind).HasConversion<int>();
        hides.Property(entity => entity.TmdbId).HasMaxLength(32);
        hides.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var shelf = modelBuilder.Entity<RecommendationShelfItem>();
        shelf.HasKey(entity => entity.Id);
        // A rank is a position in one user's shelf, so it can hold only one title; the whole shelf is
        // rewritten on refresh rather than patched, and uniqueness makes a half-written one impossible
        // to commit.
        shelf.HasIndex(entity => new { entity.AppUserId, entity.Rank }).IsUnique();
        shelf.HasIndex(entity => entity.AppUserId);
        shelf.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);
        // Cascade from the item too: a title removed from the library must not leave a rank pointing
        // at nothing, and a shelf with a hole is better than a read that throws.
        shelf.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        var shelfGeneration = modelBuilder.Entity<RecommendationShelfGeneration>();
        shelfGeneration.HasKey(entity => entity.AppUserId);
        shelfGeneration.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var recommendationCache = modelBuilder.Entity<TmdbRecommendationCacheEntry>();
        recommendationCache.HasKey(entity => entity.Id);
        // The engine looks a seed up by its coordinates; uniqueness keeps a refresh replacing the row
        // rather than growing a pile of generations.
        // The generator leads the key: one seed can be asked more than one question, and each answer
        // is its own row. Existing rows are `/recommendations` answers, which is Generator 0.
        recommendationCache.HasIndex(entity => new { entity.Generator, entity.Kind, entity.TmdbId }).IsUnique();
        recommendationCache.Property(entity => entity.Generator).HasConversion<int>();
        recommendationCache.Property(entity => entity.Kind).HasConversion<int>();
        recommendationCache.Property(entity => entity.TmdbId).HasMaxLength(32);

        var titleDetailCache = modelBuilder.Entity<TmdbTitleDetailCacheEntry>();
        titleDetailCache.HasKey(entity => entity.Id);
        // One row per title and language; a refresh replaces it rather than piling up generations.
        titleDetailCache.HasIndex(entity => new { entity.Kind, entity.TmdbId, entity.Language }).IsUnique();
        titleDetailCache.Property(entity => entity.Kind).HasConversion<int>();
        titleDetailCache.Property(entity => entity.TmdbId).HasMaxLength(32);
        titleDetailCache.Property(entity => entity.Language).HasMaxLength(16);

        var recommendationPreference = modelBuilder.Entity<RecommendationPreference>();
        recommendationPreference.HasKey(entity => entity.Id);
        recommendationPreference.HasIndex(entity => entity.AppUserId).IsUnique();
        recommendationPreference.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var history = modelBuilder.Entity<PlaybackHistoryEntry>();
        history.HasKey(entity => entity.Id);
        // The projection reads a user's plays for an item, newest first.
        history.HasIndex(entity => new { entity.AppUserId, entity.MediaItemId, entity.WatchedAt });
        // The calendar scans one user's plays over a date range; the index above leads with the
        // item, so it cannot serve that without a full scan.
        history.HasIndex(entity => new { entity.AppUserId, entity.WatchedAt });
        // One session yields one play: this is what stops a rewind past the threshold recording a
        // second. Filtered, because every non-playback origin leaves the session id null and SQLite
        // would otherwise treat those nulls as distinct and let duplicates through unnoticed.
        history.HasIndex(entity => new { entity.AppUserId, entity.MediaItemId, entity.PlaySessionId })
            .IsUnique()
            .HasFilter("\"PlaySessionId\" IS NOT NULL");
        // Resolving a remote id back to its local entry during sync.
        history.HasIndex(entity => new { entity.ProviderKey, entity.ProviderHistoryId });
        history.Property(entity => entity.Origin).HasConversion<int>();
        history.Property(entity => entity.LinkStatus).HasConversion<int>();
        history.Property(entity => entity.PlaySessionId).HasMaxLength(200);
        history.Property(entity => entity.ProviderKey).HasMaxLength(64);
        history.Property(entity => entity.ProviderHistoryId).HasMaxLength(128);

        history.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        // History follows the item: a deleted item's plays cannot be projected or exported.
        history.HasOne(entity => entity.MediaItem)
            .WithMany()
            .HasForeignKey(entity => entity.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);

        var connection = modelBuilder.Entity<WatchHistoryProviderConnection>();
        connection.HasKey(entity => entity.Id);
        connection.HasIndex(entity => new { entity.AppUserId, entity.ProviderKey }).IsUnique();
        connection.Property(entity => entity.ProviderKey).HasMaxLength(64);
        connection.Property(entity => entity.SecretKey).HasMaxLength(200);
        connection.Property(entity => entity.ProviderAccountId).HasMaxLength(128);
        connection.Property(entity => entity.ProviderAccountName).HasMaxLength(256);
        connection.Property(entity => entity.LastError).HasMaxLength(1024);
        connection.Property(entity => entity.Status).HasConversion<int>();

        connection.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var authorization = modelBuilder.Entity<WatchHistoryProviderAuthorization>();
        authorization.HasKey(entity => entity.Id);
        // At most one attempt in flight per user and provider; starting again replaces it.
        authorization.HasIndex(entity => new { entity.AppUserId, entity.ProviderKey }).IsUnique();
        authorization.Property(entity => entity.ProviderKey).HasMaxLength(64);
        authorization.Property(entity => entity.UserCode).HasMaxLength(64);
        authorization.Property(entity => entity.VerificationUrl).HasMaxLength(512);
        authorization.Property(entity => entity.Status).HasConversion<int>();

        authorization.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        var outbox = modelBuilder.Entity<WatchHistoryOutboxEvent>();
        outbox.HasKey(entity => entity.Id);
        // A duplicate enqueue must be a no-op: Trakt does not deduplicate history by item and
        // timestamp, so a retried add would surface as a second viewing.
        outbox.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        // The worker's claim query.
        outbox.HasIndex(entity => new { entity.Status, entity.NextAttemptAt });
        outbox.Property(entity => entity.IdempotencyKey).HasMaxLength(256);
        outbox.Property(entity => entity.LastError).HasMaxLength(1024);
        outbox.Property(entity => entity.Operation).HasConversion<int>();
        outbox.Property(entity => entity.Status).HasConversion<int>();

        // Deleting a connection drops its undelivered work: there is no longer an account to send it to.
        outbox.HasOne(entity => entity.Connection)
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        var favoriteState = modelBuilder.Entity<WatchHistoryFavoriteState>();
        favoriteState.HasKey(entity => entity.Id);
        // One row per connection and canonical identity: reconciliation looks the work up by exactly this.
        favoriteState.HasIndex(entity => new { entity.ConnectionId, entity.Kind, entity.IdentityProvider, entity.IdentityProviderId })
            .IsUnique();
        favoriteState.Property(entity => entity.Kind).HasConversion<int>();
        favoriteState.Property(entity => entity.IdentityProvider).HasMaxLength(64);
        favoriteState.Property(entity => entity.IdentityProviderId).HasMaxLength(128);

        // Deleting a connection drops what it remembered about a provider it no longer speaks to.
        favoriteState.HasOne(entity => entity.Connection)
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        var syncRun = modelBuilder.Entity<WatchHistorySyncRun>();
        syncRun.HasKey(entity => entity.Id);
        syncRun.HasIndex(entity => new { entity.AppUserId, entity.CreatedAt });
        syncRun.Property(entity => entity.Status).HasConversion<int>();
        syncRun.Property(entity => entity.LastError).HasMaxLength(1024);

        syncRun.HasOne(entity => entity.Connection)
            .WithMany()
            .HasForeignKey(entity => entity.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        syncRun.HasOne(entity => entity.AppUser)
            .WithMany()
            .HasForeignKey(entity => entity.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
