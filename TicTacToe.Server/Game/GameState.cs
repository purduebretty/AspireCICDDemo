namespace TicTacToe.Server.Game;

/// <summary>
/// The live state of an in-progress game. Stored as JSON in Redis under "game:{Id}"
/// while the game is being played, then deleted once the game finishes.
/// X is always Player1 and moves first; O is Player2.
/// </summary>
public class GameState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Player1 { get; set; } = "Player 1";  // X
    public string Player2 { get; set; } = "Player 2";  // O

    /// <summary>Optional links to the players' User records, so avatars can be shown while playing.</summary>
    public int? Player1UserId { get; set; }
    public int? Player2UserId { get; set; }

    /// <summary>9-character board, one char per cell: '.', 'X' or 'O'. Index 0..8, row-major.</summary>
    public string Board { get; set; } = new string('.', 9);

    /// <summary>Whose turn it is: "X" or "O".</summary>
    public string CurrentSymbol { get; set; } = "X";

    /// <summary>Ordered list of cells played (0..8). The first entry is X, then alternating.</summary>
    public List<int> Moves { get; set; } = new();

    /// <summary>"InProgress", "XWins", "OWins" or "Draw".</summary>
    public string Status { get; set; } = "InProgress";

    public string? WinnerName { get; set; }

    /// <summary>The three winning cell indices, when there is a winner (for highlighting).</summary>
    public int[]? WinningLine { get; set; }
}
