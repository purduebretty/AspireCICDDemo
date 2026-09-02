import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, type User } from '../api';
import { Avatar } from '../components/Avatar';

export function Players() {
  const [users, setUsers] = useState<User[]>([]);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Bumped after every upload so avatar <img> src changes and the browser re-fetches.
  const [version, setVersion] = useState(0);
  const fileInputs = useRef<Record<number, HTMLInputElement | null>>({});

  const load = () =>
    api.listUsers()
      .then(setUsers)
      .catch(err => setError(err instanceof Error ? err.message : 'Failed to load players'));

  useEffect(() => { load(); }, []);

  const createPlayer = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setBusy(true);
    setError(null);
    try {
      await api.createUser(name.trim());
      setName('');
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create player');
    } finally {
      setBusy(false);
    }
  };

  const uploadImage = async (userId: number, file: File | undefined) => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      await api.uploadAvatar(userId, file);
      setVersion(v => v + 1);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to upload image');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card players">
      <h2>Players</h2>
      <p className="muted">Create players and give them an avatar. Avatars appear next to their name in games.</p>

      <form className="player-create" onSubmit={createPlayer}>
        <input
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="New player name"
          maxLength={40}
          aria-label="New player name"
        />
        <button type="submit" className="btn btn-primary" disabled={busy || !name.trim()}>
          {busy ? 'Saving…' : 'Add Player'}
        </button>
      </form>

      {error && <p className="error">{error}</p>}

      <ul className="player-list">
        {users.map(u => (
          <li key={u.id}>
            <div className="player-identity">
              <Avatar name={u.username} userId={u.id} size={44} version={version} />
              <span className="player-name">{u.username}</span>
            </div>
            <div className="row" style={{ margin: 0 }}>
              <input
                ref={el => { fileInputs.current[u.id] = el; }}
                type="file"
                accept="image/*"
                hidden
                onChange={e => uploadImage(u.id, e.target.files?.[0])}
              />
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                disabled={busy}
                onClick={() => fileInputs.current[u.id]?.click()}
              >
                {u.hasImage ? 'Change image' : 'Add image'}
              </button>
            </div>
          </li>
        ))}
        {users.length === 0 && <p className="muted">No players yet — add one above.</p>}
      </ul>

      <Link to="/" className="btn btn-ghost">← Home</Link>
    </div>
  );
}
