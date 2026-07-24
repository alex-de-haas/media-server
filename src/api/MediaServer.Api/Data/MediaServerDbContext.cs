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
    public DbSet<WatchHistorySyncRun> WatchHistorySyncRuns => Set<WatchHistorySyncRun>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();
    public DbSet<TrackedTitle> TrackedTitles => Set<TrackedTitle>();
    public DbSet<TrackedRelease> TrackedReleases => Set<TrackedRelease>();
    public DbSet<WatchlistEntry> WatchlistEntries => Set<WatchlistEntry>();
    public DbSet<ReleaseReminder> ReleaseReminders => Set<ReleaseReminder>();
    public DbSet<ReminderDelivery> ReminderDeliveries => Set<ReminderDelivery>();

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

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
        catalog.HasIndex(entity => entity.Root).IsUnique();
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

        item.HasOne(entity => entity.Catalog)
            .WithMany()
            .HasForeignKey(entity => entity.CatalogId)
            .OnDelete(DeleteBehavior.Cascade);

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
        job.Property(entity => entity.OutputPath).IsRequired();
        job.Property(entity => entity.VideoCodec).IsRequired();
        job.Property(entity => entity.HardwareAcceleration).IsRequired();
        job.Property(entity => entity.State).HasConversion<int>();
        job.HasIndex(entity => entity.EngineJobId).IsUnique();
        job.HasIndex(entity => entity.State);

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
