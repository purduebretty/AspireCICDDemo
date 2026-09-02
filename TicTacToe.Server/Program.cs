using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicTacToe.Server.Data;
using TicTacToe.Server.Game;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Redis — live game-state cache. The connection name is "cache" locally, but when several
// environments share one Azure Container Apps environment the AppHost suffixes the cache
// resource per environment (cache-dev, …) and passes the actual name via Cache:ConnectionName.
builder.AddRedisClient(builder.Configuration["Cache:ConnectionName"] ?? "cache");

// Postgres via EF Core (connection name "gamesdb" matches the AppHost).
builder.AddNpgsqlDbContext<GamesDbContext>("gamesdb");

// Azure Blob Storage — user avatar images (connection name "blobs" matches the AppHost).
// Azurite locally, a real storage account in Azure.
builder.AddAzureBlobServiceClient("blobs");

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Apply any pending EF Core migrations on startup so the schema is always current.
// Postgres may still be starting up (e.g. in Azure Container Apps), so retry briefly.
// Self-heal: a database created by the earlier EnsureCreated approach has the app tables
// but no migrations-history table, so migrations can't be layered on top of it. For this
// demo's throwaway data we recreate it cleanly rather than crash on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GamesDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            // Only inspect for a legacy (pre-migrations) schema when the database already exists.
            // On a fresh deploy (e.g. the Azure Postgres container) the "gamesdb" database doesn't
            // exist yet — a raw connection to it would fail, so skip the inspect and let
            // MigrateAsync create the database and apply migrations.
            if (await db.Database.CanConnectAsync())
            {
                var (gamesTable, migrationsHistory) = await InspectSchemaAsync(db);
                if (gamesTable && !migrationsHistory)
                {
                    app.Logger.LogWarning(
                        "Found a pre-migrations database (created by EnsureCreated). Recreating it so migrations can be applied.");
                    await db.Database.EnsureDeletedAsync();
                }
            }

            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < 15)
        {
            app.Logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in 3s…", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

// Detects whether the application table ("games") and the EF migrations-history table exist,
// so a legacy pre-migrations database can be told apart from a fresh or already-migrated one.
static async Task<(bool GamesTable, bool MigrationsHistory)> InspectSchemaAsync(GamesDbContext db)
{
    var connection = db.Database.GetDbConnection();
    var wasClosed = connection.State != System.Data.ConnectionState.Open;
    if (wasClosed)
        await connection.OpenAsync();
    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_regclass('public.games') IS NOT NULL, to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }
    finally
    {
        if (wasClosed)
            await connection.CloseAsync();
    }
}

// Azure blob container names must be lowercase (letters, numbers, hyphens).
const string AvatarContainer = "userimages";
var redisExpiry = TimeSpan.FromHours(2);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

static string RedisKey(string id) => $"game:{id}";

// Look up a user by username (case-insensitive), creating one if it doesn't exist yet.
// Returns null for blank names (anonymous play).
static async Task<User?> ResolveUserAsync(GamesDbContext db, string? name)
{
    var username = name?.Trim();
    if (string.IsNullOrWhiteSpace(username))
        return null;

    var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
    if (user is null)
    {
        user = new User { Username = username, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    return user;
}

var api = app.MapGroup("/api");

// --- Users & avatars -------------------------------------------------------

// All known players, for the "pick an existing player" list.
api.MapGet("/users", async (GamesDbContext db) =>
{
    var users = await db.Users
        .OrderBy(u => u.Username)
        .Select(u => new UserDto(u.Id, u.Username, u.HasImage))
        .ToListAsync();

    return Results.Ok(users);
});

// Create a player (or return the existing one with the same name).
api.MapPost("/users", async (CreateUserRequest req, GamesDbContext db) =>
{
    var user = await ResolveUserAsync(db, req.Username);
    if (user is null)
        return Results.BadRequest(new { error = "Username is required." });

    return Results.Ok(new UserDto(user.Id, user.Username, user.HasImage));
});

// Upload (or replace) a user's avatar. Stored in the private "userImages" container
// under the user's id, and served back through the GET endpoint below.
api.MapPost("/users/{id:int}/image", async (int id, IFormFile file, GamesDbContext db, BlobServiceClient blobs) =>
{
    var user = await db.Users.FindAsync(id);
    if (user is null)
        return Results.NotFound();

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No image was uploaded." });

    if (file.Length > 5 * 1024 * 1024)
        return Results.BadRequest(new { error = "Image must be 5 MB or smaller." });

    var contentType = file.ContentType;
    if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "File must be an image." });

    var container = blobs.GetBlobContainerClient(AvatarContainer);
    await container.CreateIfNotExistsAsync();

    var blob = container.GetBlobClient(id.ToString());
    await using (var stream = file.OpenReadStream())
    {
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        });
    }

    user.HasImage = true;
    user.ImageContentType = contentType;
    await db.SaveChangesAsync();

    return Results.Ok(new UserDto(user.Id, user.Username, user.HasImage));
}).DisableAntiforgery();

// Serve a user's avatar image (proxied from blob storage — the container is private).
api.MapGet("/users/{id:int}/image", async (int id, GamesDbContext db, BlobServiceClient blobs) =>
{
    var user = await db.Users.FindAsync(id);
    if (user is null || !user.HasImage)
        return Results.NotFound();

    var blob = blobs.GetBlobContainerClient(AvatarContainer).GetBlobClient(id.ToString());
    if (!await blob.ExistsAsync())
        return Results.NotFound();

    var download = await blob.DownloadStreamingAsync();
    return Results.Stream(download.Value.Content, user.ImageContentType ?? "application/octet-stream");
});

// --- Live game play (state lives in Redis) ---------------------------------

// Start a new game. Player names are resolved to (or create) User records so avatars
// can be shown while playing and in history.
api.MapPost("/games", async (CreateGameRequest req, IConnectionMultiplexer redis, GamesDbContext db) =>
{
    var p1 = await ResolveUserAsync(db, req.Player1);
    var p2 = await ResolveUserAsync(db, req.Player2);

    var state = new GameState
    {
        Player1 = p1?.Username ?? (string.IsNullOrWhiteSpace(req.Player1) ? "Player 1" : req.Player1.Trim()),
        Player2 = p2?.Username ?? (string.IsNullOrWhiteSpace(req.Player2) ? "Player 2" : req.Player2.Trim()),
        Player1UserId = p1?.Id,
        Player2UserId = p2?.Id,
    };

    await redis.GetDatabase().StringSetAsync(
        RedisKey(state.Id), JsonSerializer.Serialize(state, jsonOptions), redisExpiry);

    return Results.Ok(state);
});

// Read the current state of an in-progress game.
api.MapGet("/games/{id}", async (string id, IConnectionMultiplexer redis) =>
{
    var json = await redis.GetDatabase().StringGetAsync(RedisKey(id));
    if (json.IsNullOrEmpty)
        return Results.NotFound();

    return Results.Ok(JsonSerializer.Deserialize<GameState>((string)json!, jsonOptions));
});

// Make a move. When the game ends, persist it to Postgres and clear the Redis cache.
api.MapPost("/games/{id}/move", async (string id, MoveRequest req,
    IConnectionMultiplexer redis, GamesDbContext db) =>
{
    var rdb = redis.GetDatabase();
    var json = await rdb.StringGetAsync(RedisKey(id));
    if (json.IsNullOrEmpty)
        return Results.NotFound();

    var state = JsonSerializer.Deserialize<GameState>((string)json!, jsonOptions)!;

    var error = GameEngine.ApplyMove(state, req.Cell);
    if (error is not null)
        return Results.BadRequest(new { error });

    if (state.Status == "InProgress")
    {
        // Game continues — write the updated state back to the cache.
        await rdb.StringSetAsync(RedisKey(id), JsonSerializer.Serialize(state, jsonOptions), redisExpiry);
    }
    else
    {
        // Game over — persist to Postgres, then evict from the cache.
        var game = new Game
        {
            CreatedAt = DateTime.UtcNow,
            Player1Name = state.Player1,
            Player2Name = state.Player2,
            Player1UserId = state.Player1UserId,
            Player2UserId = state.Player2UserId,
            Result = state.Status,
            WinnerName = state.WinnerName,
            Moves = state.Moves.Select((cell, i) => new Move
            {
                MoveNumber = i + 1,
                Cell = cell,
                Symbol = i % 2 == 0 ? "X" : "O",
            }).ToList(),
        };
        db.Games.Add(game);
        await db.SaveChangesAsync();

        await rdb.KeyDeleteAsync(RedisKey(id));
    }

    return Results.Ok(state);
});

// --- History & replay (persisted in Postgres) ------------------------------

// The 30 most recent finished games.
api.MapGet("/history", async (GamesDbContext db) =>
{
    var games = await db.Games
        .OrderByDescending(g => g.Id)
        .Take(30)
        .Select(g => new HistoryItem(
            g.Id, g.Player1Name, g.Player2Name, g.Player1UserId, g.Player2UserId,
            g.Result, g.WinnerName, g.CreatedAt, g.Moves.Count))
        .ToListAsync();

    return Results.Ok(games);
});

// A single finished game with its full move list, for replay.
api.MapGet("/history/{id:int}", async (int id, GamesDbContext db) =>
{
    var game = await db.Games
        .Include(g => g.Moves)
        .FirstOrDefaultAsync(g => g.Id == id);

    if (game is null)
        return Results.NotFound();

    var replay = new GameReplay(
        game.Id, game.Player1Name, game.Player2Name, game.Player1UserId, game.Player2UserId,
        game.Result, game.WinnerName, game.CreatedAt,
        game.Moves.OrderBy(m => m.MoveNumber)
            .Select(m => new MoveDto(m.MoveNumber, m.Cell, m.Symbol))
            .ToList());

    return Results.Ok(replay);
});

app.MapDefaultEndpoints();

app.UseFileServer();

app.Run();

// --- Request / response DTOs ----------------------------------------------

record CreateGameRequest(string Player1, string Player2);
record CreateUserRequest(string Username);
record UserDto(int Id, string Username, bool HasImage);
record MoveRequest(int Cell);
record HistoryItem(int Id, string Player1Name, string Player2Name, int? Player1UserId, int? Player2UserId,
    string Result, string? WinnerName, DateTime CreatedAt, int MoveCount);
record MoveDto(int MoveNumber, int Cell, string Symbol);
record GameReplay(int Id, string Player1Name, string Player2Name, int? Player1UserId, int? Player2UserId,
    string Result, string? WinnerName, DateTime CreatedAt, List<MoveDto> Moves);
