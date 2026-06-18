using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Data;

public sealed class MediaServerDbContext(DbContextOptions<MediaServerDbContext> options)
    : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Catalog> Catalogs => Set<Catalog>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<MediaSource> MediaSources => Set<MediaSource>();
    public DbSet<MediaStream> MediaStreams => Set<MediaStream>();
    public DbSet<MetadataRecord> MetadataRecords => Set<MetadataRecord>();
    public DbSet<ImageAsset> ImageAssets => Set<ImageAsset>();
    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<IngestItem> IngestItems => Set<IngestItem>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JellyfinCredential> JellyfinCredentials => Set<JellyfinCredential>();
    public DbSet<JellyfinAccessToken> JellyfinAccessTokens => Set<JellyfinAccessToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAppUser(modelBuilder);
        ConfigureCatalog(modelBuilder);
        ConfigureMediaItem(modelBuilder);
        ConfigureMediaSource(modelBuilder);
        ConfigureMetadataRecord(modelBuilder);
        ConfigureImageAsset(modelBuilder);
        ConfigureDownload(modelBuilder);
        ConfigureSourceFile(modelBuilder);
        ConfigureIngestItem(modelBuilder);
        ConfigureJob(modelBuilder);
        ConfigureJellyfinCredential(modelBuilder);
    }

    /// <summary>
    /// Bumps the optimistic-concurrency token on every persisted <see cref="IngestItem"/> change.
    /// SQLite has no native rowversion, so the orchestrator relies on this application-managed token
    /// to detect concurrent edits between the reconciler and operator actions.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<IngestItem>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
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

    private static void ConfigureSourceFile(ModelBuilder modelBuilder)
    {
        var sourceFile = modelBuilder.Entity<SourceFile>();
        sourceFile.HasKey(entity => entity.Id);
        sourceFile.Property(entity => entity.RelativePath).IsRequired();
        sourceFile.Property(entity => entity.AssignmentStatus).HasConversion<int>();

        sourceFile.HasOne(entity => entity.Download)
            .WithMany(entity => entity.SourceFiles)
            .HasForeignKey(entity => entity.DownloadId)
            .OnDelete(DeleteBehavior.Cascade);

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
}
