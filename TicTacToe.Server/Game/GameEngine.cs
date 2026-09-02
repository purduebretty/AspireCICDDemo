namespace TicTacToe.Server.Game;

/// <summary>Pure tic-tac-toe rules: applying moves and detecting wins/draws.</summary>
public static class GameEngine
{
    public static readonly int[][] Lines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],   // rows
        [0, 3, 6], [1, 4, 7], [2, 5, 8],   // columns
        [0, 4, 8], [2, 4, 6],              // diagonals
    ];

    /// <summary>
    /// Applies a move to the given state, mutating it in place. Returns null on success,
    /// or an error message if the move is illegal.
    /// </summary>
    public static string? ApplyMove(GameState state, int cell)
    {
        if (state.Status != "InProgress")
            return "Game is already over.";
        if (cell < 0 || cell > 8)
            return "Cell must be between 0 and 8.";
        if (state.Board[cell] != '.')
            return "That cell is already taken.";

        var symbol = state.CurrentSymbol[0];
        var board = state.Board.ToCharArray();
        board[cell] = symbol;
        state.Board = new string(board);
        state.Moves.Add(cell);

        var line = FindWinningLine(state.Board, symbol);
        if (line is not null)
        {
            state.Status = symbol == 'X' ? "XWins" : "OWins";
            state.WinnerName = symbol == 'X' ? state.Player1 : state.Player2;
            state.WinningLine = line;
        }
        else if (!state.Board.Contains('.'))
        {
            state.Status = "Draw";
        }
        else
        {
            state.CurrentSymbol = symbol == 'X' ? "O" : "X";
        }

        return null;
    }

    private static int[]? FindWinningLine(string board, char symbol)
    {
        foreach (var line in Lines)
        {
            if (board[line[0]] == symbol && board[line[1]] == symbol && board[line[2]] == symbol)
                return line;
        }
        return null;
    }
}
