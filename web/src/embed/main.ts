import '../shared/player.css';
import './embed.css';
import { mountPlayer, type PlayerSource } from '../shared/player';

// The server renders the shell and the boot data (escaped JSON); this script only plays it.
const container = document.getElementById('player');
const boot = document.getElementById('boot');

if (container && boot?.textContent) {
  const source = JSON.parse(boot.textContent) as PlayerSource;
  container.classList.add('ff-player');
  mountPlayer(container, source);
}
