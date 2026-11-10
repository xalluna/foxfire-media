// hls.js ships its light build without a separate declaration file; its API is the same.
declare module 'hls.js/light' {
  export * from 'hls.js';
  export { default } from 'hls.js';
}
