// Shared formatting helpers. Keep this module dependency-free so it can be imported by
// stores, composables, components, and chart code without pulling Vue into chart bundles.

/**
 * Format a byte count as a human-readable string. Hardened against `undefined`/`null`/`NaN`
 * — those return `'0 B'` rather than the legacy `'NaN undefined'` (which is what users saw
 * when an in-flight relay event had not yet populated `bytesTransferred`).
 *
 * Also clamps `i` to `[0, sizes.length - 1]` so sub-1-byte inputs (which Chart.js can
 * generate as Y-axis tick candidates when the bandwidth chart has no data — e.g. ticks at
 * 0.2, 0.4, … or oddly-spaced 204.8 when auto-scale picks an unusual stepSize) don't fall
 * off the bottom of the units array and render as `'0.2 undefined'`.
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB']
  const raw = Math.floor(Math.log(bytes) / Math.log(k))
  const i = Math.max(0, Math.min(sizes.length - 1, raw))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

/**
 * Format a millisecond duration as `HH:MM:SS`. Used for the dashboard uptime display
 * (which now reads from a store-owned ticker, so the value persists across view switches).
 */
export function formatUptime(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms) || ms < 0) return '00:00:00'
  const totalSec = Math.floor(ms / 1000)
  const h = String(Math.floor(totalSec / 3600)).padStart(2, '0')
  const m = String(Math.floor((totalSec % 3600) / 60)).padStart(2, '0')
  const s = String(totalSec % 60).padStart(2, '0')
  return h + ':' + m + ':' + s
}
