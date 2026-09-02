import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, type GameState, type User } from '../api';
import { Board } from '../components/Board';
import { Avatar } from '../components/Avatar';
import { launchFireworks } from '../components/fireworks';

export function Play() {
  const [player1, setPlayer1] = useState('');
  const [player2, setPlayer2] = useState('');
  const [users, setUsers] = useState<User[]>([]);
  const [game, setGame] = useState<GameState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Existing players, for autocomplete + avatar previews on the setup screen.
  useEffect(() => {
    api.listUsers().then(setUsers).catch(() => { /* non-fatal */ });
  }, []);

  const matchUser = (name: string): User | undefined =>
    users.find(u => u.username.toLowerCase() === name.trim().toLowerCase());

  const start = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setGame(await api.createGame(player1 || 'Player 1', player2 || 'Player 2'));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to start game');
    } finally {
      setBusy(false);
    }
  };

  const play = async (cell: number) => {
    if (!game || game.status !== 'InProgress' || busy) return;
    setBusy(true);
    setError(null);
    try {
      const next = await api.makeMove(game.id, cell);
      setGame(next);
      if (next.status !== 'InProgress' && next.status !== 'Draw') {
        launchFireworks();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Move failed');
    } finally {
      setBusy(false);
    }
  };

  const playAgain = () => {
    setGame(null);
  };

  // --- Setup screen --------------------------------------------------------
  if (!game) {
    const p1 = matchUser(player1);
    const p2 = matchUser(player2);
    return (
      <div className="card setup">
        <h2>New Game</h2>
        <p className="muted">Type a name to pick an existing player or create a new one.</p>
        <form onSubmit={start}>
          <label>
            Player 1 <span className="sym sym-x">X</span>
            <div className="name-field">
              <Avatar name={player1 || 'Player 1'} userId={p1?.id} size={40} />
              <input list="playerNames" value={player1} onChange={e => setPlayer1(e.target.value)} placeholder="Player 1" maxLength={40} />
            </div>
          </label>
          <label>
            Player 2 <span className="sym sym-o">O</span>
            <div className="name-field">
              <Avatar name={player2 || 'Player 2'} userId={p2?.id} size={40} />
              <input list="playerNames" value={player2} onChange={e => setPlayer2(e.target.value)} placeholder="Player 2" maxLength={40} />
            </div>
          </label>
          <datalist id="playerNames">
            {users.map(u => <option key={u.id} value={u.username} />)}
          </datalist>
          {error && <p className="error">{error}</p>}
          <div className="row">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? 'Starting…' : 'Start Game'}
            </button>
            <Link to="/players" className="btn btn-secondary">Manage Players</Link>
            <Link to="/" className="btn btn-ghost">Cancel</Link>
          </div>
        </form>
      </div>
    );
  }

  // --- Play screen ---------------------------------------------------------
  const over = game.status !== 'InProgress';
  const turnName = game.currentSymbol === 'X' ? game.player1 : game.player2;

  return (
    <div className="card play">
      <div className="scoreboard">
        <span className="player player-x">
          <Avatar name={game.player1} userId={game.player1UserId} />
          {game.player1} <b>X</b>
        </span>
        <span className="vs">vs</span>
        <span className="player player-o">
          <b>O</b> {game.player2}
          <Avatar name={game.player2} userId={game.player2UserId} />
        </span>
      </div>

      {!over && (
        <p className="turn">
          <span className={game.currentSymbol === 'X' ? 'sym-x' : 'sym-o'}>{game.currentSymbol}</span>
          {' '}— {turnName}'s turn
        </p>
      )}

      <Board
        board={game.board}
        onCellClick={over ? undefined : play}
        disabled={busy}
        winningLine={game.winningLine}
      />

      {error && <p className="error">{error}</p>}

      {over && (
        <div className="result-banner">
          {game.status === 'Draw'
            ? <h2>🤝 It's a draw!</h2>
            : <h2>🎉 Congratulations, {game.winnerName}!</h2>}
          <div className="row">
            <button className="btn btn-primary" onClick={playAgain}>Play Again</button>
            <Link to="/" className="btn btn-ghost">Home</Link>
          </div>
        </div>
      )}
    </div>
  );
}
