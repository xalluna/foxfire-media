import { useEffect, useRef } from 'react';
import { mountPlayer, type PlayerSource } from '../../shared/player';

/** React wrapper around the shared HLS player (the same one the embed page uses). */
export function Player({ source }: { source: PlayerSource }) {
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!container.current) return;
    const handle = mountPlayer(container.current, source);
    return () => handle.destroy();
  }, [source]);

  return <div ref={container} className="ff-player watch-player" />;
}
