import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, type GameReplay } from '../api';
import { Board } from '../components/Board';
import { Avatar } from '../components/Avatar';
import { launchFireworks } from '../components/fireworks';
import { findWinningLine } from '../ttt';

const STEP_MS = 800;

function boardAfter(replay: GameReplay, step: number): string {
  const cells = Array<string>(9).fill('.');
  for (let i = 0; i < step && i < replay.moves.length; i++) {
    const m = replay.moves[i];
    cells[m.cell] = m.symbol;
  }
  return cells.join('');
}

export function Replay() {
  const { id } = useParams();
  const [replay, setReplay] = useState<GameReplay | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [step, setStep] = useState(0); // number of moves revealed
  const [playing, setPlaying] = useState(false);
  const timer = useRef<number | null>(null);
  const celebrated = useRef(false);

  useEffect(() => {
    api.replay(Number(id))
      .then(setReplay)
      .catch(err => setError(err instanceof Error ? err.message : 'Failed to load game'));
  }, [id]);

  // Drive the playback one move at a time.
  useEffect(() => {
    if (!playing || !replay) return;
    if (step >= replay.moves.length) {
      setPlaying(false);
      return;
    }
    timer.current = window.setTimeout(() => setStep(s => s + 1), STEP_MS);
    return () => { if (timer.current) clearTimeout(timer.current); };
  }, [playing, step, replay]);

  const done = !!replay && step >= replay.moves.length && step > 0;
  const board = replay ? boardAfter(replay, step) : '.'.repeat(9);
  const winningLine = done ? findWinningLine(board) : null;

  // Celebrate once when a finished replay reaches the end with a winner.
  useEffect(() => {
    if (done && replay && replay.result !== 'Draw' && !celebrated.current) {
      celebrated.current = true;
      launchFireworks();
    }
  }, [done, replay]);

  const startReplay = useCallback(() => {
    celebrated.current = false;
    setStep(0);
    setPlaying(true);
  }, []);

  if (error) {
    return (
      <div className="card">
        <p className="error">{error}</p>
        <Link to="/history" className="btn btn-ghost">← Back</Link>
      </div>
    );
  }

  if (!replay) {
    return <div className="card"><p className="muted">Loading…</p></div>;
  }

  return (
    <div className="card play">
      <h2>Game #{replay.id} <span className="muted">replay</span></h2>
      <div className="scoreboard">
        <span className="player player-x">
          <Avatar name={replay.player1Name} userId={replay.player1UserId} />
          {replay.player1Name} <b>X</b>
        </span>
        <span className="vs">vs</span>
        <span className="player player-o">
          <b>O</b> {replay.player2Name}
          <Avatar name={replay.player2Name} userId={replay.player2UserId} />
        </span>
      </div>

      <p className="turn muted">Move {step} of {replay.moves.length}</p>

      <Board board={board} winningLine={winningLine} />

      {done && (
        <div className="result-banner">
          {replay.result === 'Draw'
            ? <h2>🤝 It was a draw!</h2>
            : <h2>🎉 Congratulations, {replay.winnerName}!</h2>}
        </div>
      )}

      <div className="row">
        <button className="btn btn-primary" onClick={startReplay} disabled={playing}>
          {playing ? 'Playing…' : done ? '↺ Replay' : '▶ Play'}
        </button>
        <Link to="/history" className="btn btn-ghost">← Back</Link>
      </div>
    </div>
  );
}
