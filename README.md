# Tic-Tac-Toe — .NET Aspire Demo

A small, self-contained app for demonstrating [.NET Aspire](https://aspire.dev). Two players
enter their names and play tic-tac-toe in the browser. Live game state is cached in **Redis**;
when a game finishes it is evicted from the cache and persisted to **Postgres**, where it can be
browsed and **replayed move-by-move** — with fireworks for the winner. 🎉

## Architecture

Four orchestrated resources, wired together by a **C# Aspire AppHost**:

```
┌──────────────┐      /api      ┌──────────────┐   live state   ┌─────────┐
│  webfrontend │ ─────────────► │    server    │ ─────────────► │  Redis  │  (cache)
│ Vite + React │  (Vite proxy)  │ ASP.NET Core │                └─────────┘
│   (static)   │                │  Minimal API │   finished     ┌─────────┐
└──────────────┘                │              │ ─────────────► │ Postgres│  (history)
                                └──────────────┘   games+moves  └─────────┘
```

| Resource      | Tech                          | Role                                            |
|---------------|-------------------------------|-------------------------------------------------|
| `webfrontend` | Vite + React + TypeScript     | Static SPA: menu, play, history, replay         |
| `server`      | ASP.NET Core Minimal API (.NET 10) | Game rules, REST API                       |
| `cache`       | Redis                         | Live in-progress game state (JSON, 2h TTL)      |
| `postgres`    | PostgreSQL (`gamesdb`)        | Persisted finished games + moves, for replay    |

`TicTacToe.AppHost/AppHost.cs` is the single place that declares and connects all four.

## Data flow

- **Start a game** → a `GameState` (board, players, whose turn) is written to Redis at `game:{id}`.
- **Each move** → the API validates the move, updates the board, re-checks for a win/draw, and
  writes the updated state back to Redis.
- **Game over** → the game is saved to Postgres (one `games` row + one `moves` row per move),
  then the Redis key is deleted.
- **History / replay** → read straight from Postgres.

### Postgres schema (created automatically on startup)

- **`games`** — `id`, `created_at`, `player1_name`, `player2_name`, `result` (`XWins`/`OWins`/`Draw`), `winner_name`
- **`moves`** — `id`, `game_id`, `move_number`, `cell` (0–8), `symbol` (`X`/`O`)

Moves are stored as an ordered list of `(cell, symbol)`, which is all that's needed to replay any
game exactly. X is always Player 1 and moves first.

## API

| Method | Route                   | Description                                          |
|--------|-------------------------|------------------------------------------------------|
| POST   | `/api/games`            | Start a game `{ player1, player2 }` → `GameState`    |
| GET    | `/api/games/{id}`       | Current live state (from Redis)                      |
| POST   | `/api/games/{id}/move`  | Play a cell `{ cell: 0..8 }` → updated `GameState`   |
| GET    | `/api/history`          | Last 30 finished games                               |
| GET    | `/api/history/{id}`     | One finished game with full move list (for replay)   |

## Running it

Prerequisites: **.NET 10 SDK**, the **Aspire CLI**, **Node 20+**, and **Docker** (for the Redis
and Postgres containers).

```bash
aspire run
```

This builds and launches all four resources and prints a dashboard URL (e.g.
`https://localhost:17135`). Open the **`webfrontend`** endpoint from the dashboard to play.

## Project layout

```
TicTacToe.AppHost/      C# Aspire AppHost — orchestrates the four resources
TicTacToe.Server/       ASP.NET Core Minimal API
  Game/GameState.cs     Live state model (cached in Redis)
  Game/GameEngine.cs    Pure tic-tac-toe rules (move/win/draw)
  Data/Entities.cs      EF Core entities + DbContext (games, moves)
  Program.cs            DI wiring + API endpoints
frontend/               Vite + React + TypeScript SPA
  src/pages/            Home, Play, History, Replay
  src/components/       Board, fireworks
  src/api.ts            Typed API client
```
