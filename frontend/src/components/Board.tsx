interface BoardProps {
  board: string; // 9 chars: '.', 'X', 'O'
  onCellClick?: (cell: number) => void;
  disabled?: boolean;
  winningLine?: number[] | null;
}

export function Board({ board, onCellClick, disabled, winningLine }: BoardProps) {
  return (
    <div className="board" role="grid" aria-label="Tic tac toe board">
      {Array.from({ length: 9 }).map((_, i) => {
        const value = board[i];
        const filled = value === 'X' || value === 'O';
        const isWinning = winningLine?.includes(i) ?? false;
        return (
          <button
            key={i}
            type="button"
            className={`cell ${value === 'X' ? 'x' : value === 'O' ? 'o' : ''} ${isWinning ? 'winning' : ''}`}
            onClick={() => onCellClick?.(i)}
            disabled={disabled || filled || !onCellClick}
            aria-label={`Cell ${i + 1}${filled ? `, ${value}` : ', empty'}`}
          >
            {filled ? value : ''}
          </button>
        );
      })}
    </div>
  );
}
