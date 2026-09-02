using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicTacToe.Server.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model at design time without running the Aspire host.
/// The connection string is a placeholder — generating a migration only needs the Npgsql
/// provider to produce correct SQL; no database is contacted.
/// </summary>
public sealed class GamesDbContextFactory : IDesignTimeDbContextFactory<GamesDbContext>
{
    public GamesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GamesDbContext>()
            .UseNpgsql("Host=localhost;Database=gamesdb;Username=postgres;Password=postgres")
            .Options;

        return new GamesDbContext(options);
    }
}
