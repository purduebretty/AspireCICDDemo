import { Link } from 'react-router-dom';

export function Home() {
  return (
    <div className="home">
      <p className="tagline">A tiny .NET Aspire demo — React + API + Redis + Postgres.</p>
      <div className="home-actions">
        <Link to="/play" className="btn btn-primary btn-lg">▶ New Game</Link>
        <Link to="/players" className="btn btn-secondary btn-lg">👤 Players</Link>
        <Link to="/history" className="btn btn-secondary btn-lg">🕑 Past Games</Link>
      </div>
    </div>
  );
}
