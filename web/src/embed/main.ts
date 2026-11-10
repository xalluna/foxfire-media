// Embed player entry. The server renders the page shell and boot data; the player itself
// (hls.js with token refresh) is added in a later change.
const container = document.getElementById('player');
if (container) {
  container.textContent = 'Loading…';
}
