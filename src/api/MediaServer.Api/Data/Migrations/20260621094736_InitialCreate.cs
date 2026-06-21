using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaServer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Catalogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Root = table.Column<string>(type: "TEXT", nullable: false),
                    NamingTemplate = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultKeepSeeding = table.Column<bool>(type: "INTEGER", nullable: false),
                    MetadataLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    OfflineSince = table.Column<string>(type: "TEXT", nullable: true),
                    LowDiskSince = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Catalogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    RelatedType = table.Column<string>(type: "TEXT", nullable: true),
                    RelatedId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JellyfinCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    HostUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PinHash = table.Column<string>(type: "TEXT", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntil = table.Column<string>(type: "TEXT", nullable: true),
                    PermanentlyLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JellyfinCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JellyfinCredentials_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceType = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepSeeding = table.Column<bool>(type: "INTEGER", nullable: false),
                    SavePath = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUri = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Downloads_Catalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalTable: "Catalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", nullable: true),
                    CatalogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                    OriginalLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    IndexNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    IndexNumberEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentIndexNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    LibraryPath = table.Column<string>(type: "TEXT", nullable: true),
                    IdentityProvider = table.Column<string>(type: "TEXT", nullable: true),
                    IdentityProviderId = table.Column<string>(type: "TEXT", nullable: true),
                    IdentitySeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    IdentityEpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Providers = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaItems_Catalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalTable: "Catalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaItems_MediaItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JellyfinAccessTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Client = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    AppVersion = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<string>(type: "TEXT", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JellyfinAccessTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JellyfinAccessTokens_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JellyfinAccessTokens_JellyfinCredentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "JellyfinCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Stage = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StagesCompleted = table.Column<string>(type: "TEXT", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<string>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ReviewCandidates = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestItems_Catalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalTable: "Catalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngestItems_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ImageAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImageType = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    RemotePath = table.Column<string>(type: "TEXT", nullable: false),
                    LocalPath = table.Column<string>(type: "TEXT", nullable: true),
                    Tag = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageAssets_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Container = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaSources_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetadataRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    Tagline = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: false),
                    OfficialRating = table.Column<string>(type: "TEXT", nullable: true),
                    CommunityRating = table.Column<double>(type: "REAL", nullable: true),
                    ReleaseDate = table.Column<string>(type: "TEXT", nullable: true),
                    RuntimeTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    Cast = table.Column<string>(type: "TEXT", nullable: true),
                    Crew = table.Column<string>(type: "TEXT", nullable: true),
                    Raw = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetadataRecords_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserItemData",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaybackPositionTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Played = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastPlayedDate = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserItemData", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserItemData_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserItemData_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IngestItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    TorrentFileIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssignmentStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceFiles_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SourceFiles_IngestItems_IngestItemId",
                        column: x => x.IngestItemId,
                        principalTable: "IngestItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceFiles_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MediaStreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaSourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StreamType = table.Column<int>(type: "INTEGER", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    Profile = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    FrameRate = table.Column<double>(type: "REAL", nullable: true),
                    BitDepth = table.Column<int>(type: "INTEGER", nullable: true),
                    HdrFormat = table.Column<string>(type: "TEXT", nullable: true),
                    Channels = table.Column<int>(type: "INTEGER", nullable: true),
                    SampleRate = table.Column<int>(type: "INTEGER", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsForced = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsExternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExternalPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaStreams_MediaSources_MediaSourceId",
                        column: x => x.MediaSourceId,
                        principalTable: "MediaSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Email",
                table: "AppUsers",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_HostUserId",
                table: "AppUsers",
                column: "HostUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Catalogs_Root",
                table: "Catalogs",
                column: "Root",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_CatalogId",
                table: "Downloads",
                column: "CatalogId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_InfoHash",
                table: "Downloads",
                column: "InfoHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_MediaItemId",
                table: "ImageAssets",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestItems_CatalogId",
                table: "IngestItems",
                column: "CatalogId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestItems_DownloadId",
                table: "IngestItems",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestItems_Status",
                table: "IngestItems",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JellyfinAccessTokens_AppUserId",
                table: "JellyfinAccessTokens",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JellyfinAccessTokens_CredentialId",
                table: "JellyfinAccessTokens",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_JellyfinAccessTokens_TokenHash",
                table: "JellyfinAccessTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JellyfinCredentials_AppUserId",
                table: "JellyfinCredentials",
                column: "AppUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JellyfinCredentials_Username",
                table: "JellyfinCredentials",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_RelatedType_RelatedId",
                table: "Jobs",
                columns: new[] { "RelatedType", "RelatedId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_CatalogId_IdentityProvider_IdentityProviderId",
                table: "MediaItems",
                columns: new[] { "CatalogId", "IdentityProvider", "IdentityProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_ParentId",
                table: "MediaItems",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_PublicId",
                table: "MediaItems",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaSources_MediaItemId",
                table: "MediaSources",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaStreams_MediaSourceId",
                table: "MediaStreams",
                column: "MediaSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MetadataRecords_MediaItemId_Provider_Language",
                table: "MetadataRecords",
                columns: new[] { "MediaItemId", "Provider", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_DownloadId",
                table: "SourceFiles",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_IngestItemId_RelativePath",
                table: "SourceFiles",
                columns: new[] { "IngestItemId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceFiles_MediaItemId",
                table: "SourceFiles",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UserItemData_AppUserId_MediaItemId",
                table: "UserItemData",
                columns: new[] { "AppUserId", "MediaItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserItemData_MediaItemId",
                table: "UserItemData",
                column: "MediaItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageAssets");

            migrationBuilder.DropTable(
                name: "JellyfinAccessTokens");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "MediaStreams");

            migrationBuilder.DropTable(
                name: "MetadataRecords");

            migrationBuilder.DropTable(
                name: "SourceFiles");

            migrationBuilder.DropTable(
                name: "UserItemData");

            migrationBuilder.DropTable(
                name: "JellyfinCredentials");

            migrationBuilder.DropTable(
                name: "MediaSources");

            migrationBuilder.DropTable(
                name: "IngestItems");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "MediaItems");

            migrationBuilder.DropTable(
                name: "Downloads");

            migrationBuilder.DropTable(
                name: "Catalogs");
        }
    }
}
