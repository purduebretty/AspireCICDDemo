import { useState } from 'react';
import { avatarUrl } from '../api';

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

interface AvatarProps {
  name: string;
  userId?: number | null;
  size?: number;
  /** Bump to force the image to reload after an upload (cache-busting). */
  version?: number | string;
  className?: string;
}

/**
 * Round avatar for a player. If the user has an uploaded image it is shown; otherwise
 * (no user, or no image / load error) it falls back to the player's initials.
 */
export function Avatar({ name, userId, size = 36, version, className = '' }: AvatarProps) {
  const [failed, setFailed] = useState(false);
  const dimension = { width: size, height: size, fontSize: size * 0.4 };

  if (userId && !failed) {
    return (
      <img
        className={`avatar ${className}`}
        style={dimension}
        src={avatarUrl(userId, version)}
        alt={name}
        onError={() => setFailed(true)}
      />
    );
  }

  return (
    <span className={`avatar avatar-fallback ${className}`} style={dimension} aria-label={name}>
      {initials(name)}
    </span>
  );
}
