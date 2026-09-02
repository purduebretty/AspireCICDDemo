export const LINES = [
  [0, 1, 2], [3, 4, 5], [6, 7, 8],
  [0, 3, 6], [1, 4, 7], [2, 5, 8],
  [0, 4, 8], [2, 4, 6],
];

// Returns the winning line of cells for the given board, or null.
export function findWinningLine(board: string): number[] | null {
  for (const line of LINES) {
    const [a, b, c] = line;
    if (board[a] !== '.' && board[a] === board[b] && board[b] === board[c]) {
      return line;
    }
  }
  return null;
}
