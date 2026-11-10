import '../shared/player.css';
import './embed.css';
import { mountPlayer, type PlayerSource } from '../shared/player';
import { attachBridge } from './bridge';

interface EmbedBoot extends PlayerSource {
  /** Origins allowed to frame (and therefore control) this player. */
  parentOrigins: string[];
}

// The server renders the shell and the boot data (escaped JSON); this script only plays it.
const container = document.getElementById('player');
const boot = document.getElementById('boot');

if (container && boot?.textContent) {
  const source = JSON.parse(boot.textContent) as EmbedBoot;
  container.classList.add('ff-player');
  const player = mountPlayer(container, source);
  attachBridge(player.video, source.parentOrigins);
}
