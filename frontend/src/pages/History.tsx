import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, type HistoryItem } from '../api';
import { Avatar } from '../components/Avatar';

function resultLabel(item: HistoryItem) {
  if (item.result === 'Draw') return 'Draw';
  return `${item.winnerName} won`;
}

export function History() {
  const [games, setGames] = useState<HistoryItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.history()
      .then(setGames)
      .catch(err => setError(err instanceof Error ? err.message : 'Failed to load history'))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="card history">
      <h2>Past Games <span className="muted">(last 30)</span></h2>

      {loading && <p className="muted">Loading…</p>}
      {error && <p className="error">{error}</p>}
      {!loading && !error && games.length === 0 && (
        <p className="muted">No games played yet. Go win one!</p>
      )}

      <ul className="game-list">
        {games.map(g => (
          <li key={g.id}>
            <div className="game-info">
              <span className="game-no">#{g.id}</span>
              <span className="game-players">
                <span className="player-tag sym-x">
                  <Avatar name={g.player1Name} userId={g.player1UserId} size={24} />{g.player1Name}
                </span>
                {' vs '}
                <span className="player-tag sym-o">
                  <Avatar name={g.player2Name} userId={g.player2UserId} size={24} />{g.player2Name}
                </span>
              </span>
              <span className={`game-result ${g.result === 'Draw' ? 'draw' : 'win'}`}>{resultLabel(g)}</span>
              <span className="muted">{new Date(g.createdAt).toLocaleString()}</span>
            </div>
            <Link to={`/replay/${g.id}`} className="btn btn-primary btn-sm">▶ Play</Link>
          </li>
        ))}
      </ul>

      <Link to="/" className="btn btn-ghost">← Home</Link>
    </div>
  );
}
