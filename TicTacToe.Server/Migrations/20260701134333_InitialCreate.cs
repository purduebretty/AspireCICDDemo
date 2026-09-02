using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicTacToe.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HasImage = table.Column<bool>(type: "boolean", nullable: false),
                    ImageContentType = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Player1Name = table.Column<string>(type: "text", nullable: false),
                    Player2Name = table.Column<string>(type: "text", nullable: false),
                    Player1UserId = table.Column<int>(type: "integer", nullable: true),
                    Player2UserId = table.Column<int>(type: "integer", nullable: true),
                    Result = table.Column<string>(type: "text", nullable: false),
                    WinnerName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_games_users_Player1UserId",
                        column: x => x.Player1UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_games_users_Player2UserId",
                        column: x => x.Player2UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "moves",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameId = table.Column<int>(type: "integer", nullable: false),
                    MoveNumber = table.Column<int>(type: "integer", nullable: false),
                    Cell = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_moves_games_GameId",
                        column: x => x.GameId,
                        principalTable: "games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_games_Player1UserId",
                table: "games",
                column: "Player1UserId");

            migrationBuilder.CreateIndex(
                name: "IX_games_Player2UserId",
                table: "games",
                column: "Player2UserId");

            migrationBuilder.CreateIndex(
                name: "IX_moves_GameId_MoveNumber",
                table: "moves",
                columns: new[] { "GameId", "MoveNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "moves");

            migrationBuilder.DropTable(
                name: "games");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
