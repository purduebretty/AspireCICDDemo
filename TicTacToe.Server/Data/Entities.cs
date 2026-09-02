using Microsoft.EntityFrameworkCore;

namespace TicTacToe.Server.Data;

/// <summary>
/// A reusable player. Created on demand when starting a game, and optionally given an
/// avatar image that is stored in the "userImages" blob container (see the avatar API).
/// </summary>
public class User
{
    public int Id { get; set; }

    /// <summary>Unique, case-insensitive display name.</summary>
    public string Username { get; set; } = "";

    /// <summary>True once an avatar has been uploaded to blob storage for this user.</summary>
    public bool HasImage { get; set; }

    /// <summary>Content type of the stored avatar (e.g. "image/png"), used when serving it back.</summary>
    public string? ImageContentType { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>A finished game, persisted once play is complete.</summary>
public class Game
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Player1Name { get; set; } = "";
    public string Player2Name { get; set; } = "";

    /// <summary>Optional links to the <see cref="User"/> records, so avatars show in history.</summary>
    public int? Player1UserId { get; set; }
    public int? Player2UserId { get; set; }

    /// <summary>"XWins", "OWins" or "Draw".</summary>
    public string Result { get; set; } = "";

    public string? WinnerName { get; set; }

    public List<Move> Moves { get; set; } = new();
}

/// <summary>A single move within a game. Ordered by MoveNumber to replay the game.</summary>
public class Move
{
    public int Id { get; set; }
    public int GameId { get; set; }

    /// <summary>1-based order in which this move was played.</summary>
    public int MoveNumber { get; set; }

    /// <summary>Board cell 0..8 (row-major).</summary>
    public int Cell { get; set; }

    /// <summary>"X" or "O".</summary>
    public string Symbol { get; set; } = "";
}

public class GamesDbContext(DbContextOptions<GamesDbContext> options) : DbContext(options)
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Move> Moves => Set<Move>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Game>().ToTable("games");
        modelBuilder.Entity<Move>().ToTable("moves");

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.Username).HasMaxLength(40);
            e.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<Move>()
            .HasOne<Game>()
            .WithMany(g => g.Moves)
            .HasForeignKey(m => m.GameId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Move>()
            .HasIndex(m => new { m.GameId, m.MoveNumber });

        // Optional links from a game to the players' user records. Deleting a user
        // nulls the link rather than deleting the historical game.
        modelBuilder.Entity<Game>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(g => g.Player1UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Game>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(g => g.Player2UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
