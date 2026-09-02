// Typed client for the TicTacToe API. Calls are proxied to the backend via Vite (see vite.config.ts).

export type GameStatus = 'InProgress' | 'XWins' | 'OWins' | 'Draw';

export interface GameState {
  id: string;
  player1: string; // X
  player2: string; // O
  player1UserId: number | null;
  player2UserId: number | null;
  board: string; // 9 chars: '.', 'X', 'O'
  currentSymbol: 'X' | 'O';
  moves: number[];
  status: GameStatus;
  winnerName: string | null;
  winningLine: number[] | null;
}

export interface HistoryItem {
  id: number;
  player1Name: string;
  player2Name: string;
  player1UserId: number | null;
  player2UserId: number | null;
  result: GameStatus;
  winnerName: string | null;
  createdAt: string;
  moveCount: number;
}

export interface MoveDto {
  moveNumber: number;
  cell: number;
  symbol: 'X' | 'O';
}

export interface GameReplay {
  id: number;
  player1Name: string;
  player2Name: string;
  player1UserId: number | null;
  player2UserId: number | null;
  result: GameStatus;
  winnerName: string | null;
  createdAt: string;
  moves: MoveDto[];
}

export interface User {
  id: number;
  username: string;
  hasImage: boolean;
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    try {
      const body = await res.json();
      if (body?.error) message = body.error;
    } catch { /* ignore */ }
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

/** URL for a user's avatar image, served (proxied) from blob storage by the API. */
export function avatarUrl(userId: number, version?: number | string): string {
  const base = `/api/users/${userId}/image`;
  return version === undefined ? base : `${base}?v=${version}`;
}

export const api = {
  createGame: (player1: string, player2: string) =>
    fetch('/api/games', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ player1, player2 }),
    }).then(r => json<GameState>(r)),

  getGame: (id: string) =>
    fetch(`/api/games/${id}`).then(r => json<GameState>(r)),

  makeMove: (id: string, cell: number) =>
    fetch(`/api/games/${id}/move`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cell }),
    }).then(r => json<GameState>(r)),

  history: () => fetch('/api/history').then(r => json<HistoryItem[]>(r)),

  replay: (id: number) => fetch(`/api/history/${id}`).then(r => json<GameReplay>(r)),

  // --- Users & avatars ---
  listUsers: () => fetch('/api/users').then(r => json<User[]>(r)),

  createUser: (username: string) =>
    fetch('/api/users', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username }),
    }).then(r => json<User>(r)),

  uploadAvatar: (userId: number, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return fetch(`/api/users/${userId}/image`, { method: 'POST', body: form }).then(r => json<User>(r));
  },
};
