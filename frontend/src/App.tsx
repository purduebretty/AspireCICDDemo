import { Link, Route, Routes } from 'react-router-dom';
import './App.css';
import { Home } from './pages/Home';
import { Play } from './pages/Play';
import { History } from './pages/History';
import { Replay } from './pages/Replay';
import { Players } from './pages/Players';

function App() {
  return (
    <div className="app">
      <header className="app-header">
        <Link to="/" className="brand">⬛ Tic-Tac-Toe</Link>
        <nav className="app-nav">
          <Link to="/play">Play</Link>
          <Link to="/players">Players</Link>
          <Link to="/history">History</Link>
        </nav>
      </header>
      <main className="main">
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/play" element={<Play />} />
          <Route path="/players" element={<Players />} />
          <Route path="/history" element={<History />} />
          <Route path="/replay/:id" element={<Replay />} />
        </Routes>
      </main>
      <footer className="app-footer">
        Powered by .NET Aspire · React · Redis · Postgres
      </footer>
    </div>
  );
}

export default App;
