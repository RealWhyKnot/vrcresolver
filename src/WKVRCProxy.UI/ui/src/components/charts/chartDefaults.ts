import type { ChartOptions, TooltipItem } from 'chart.js'

export const TIER_RGB: Record<string, string> = {
  tier1: 'rgb(59,130,246)',
  tier2: 'rgb(168,85,247)',
  tier3: 'rgb(245,158,11)',
  tier4: 'rgb(156,163,175)'
}

export function tierColor(tier: string, alpha = 1): string {
  const key = tier.split('-')[0]
  const base = TIER_RGB[key] ?? 'rgb(156,163,175)'
  if (alpha === 1) return base
  return base.replace('rgb(', 'rgba(').replace(')', `, ${alpha})`)
}

export const FONT_FAMILY =
  'ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif'

export const TOOLTIP_DEFAULTS = {
  backgroundColor: 'rgba(10,14,26,0.95)',
  borderColor: 'rgba(255,255,255,0.08)',
  borderWidth: 1,
  padding: 10,
  titleColor: 'rgba(255,255,255,0.9)',
  titleFont: { family: FONT_FAMILY, size: 10, weight: 700 as const },
  bodyColor: 'rgba(255,255,255,0.7)',
  bodyFont: { family: FONT_FAMILY, size: 10 },
  cornerRadius: 8,
  boxPadding: 4,
  displayColors: true
}

export function baseOptions(): Partial<ChartOptions<any>> {
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: { duration: 600, easing: 'easeOutCubic' },
    interaction: { intersect: false, mode: 'index' },
    plugins: {
      legend: { display: false },
      tooltip: TOOLTIP_DEFAULTS as any
    },
    scales: {
      x: {
        grid: { display: false, color: 'rgba(255,255,255,0.03)' },
        ticks: {
          color: 'rgba(255,255,255,0.35)',
          font: { family: FONT_FAMILY, size: 9 }
        },
        border: { display: false }
      },
      y: {
        grid: { color: 'rgba(255,255,255,0.04)' },
        ticks: {
          color: 'rgba(255,255,255,0.35)',
          font: { family: FONT_FAMILY, size: 9 }
        },
        border: { display: false }
      }
    }
  } as Partial<ChartOptions<any>>
}

// Re-exported from the shared util so chart consumers (and anyone who already imports
// `formatBytes` from this file) get the hardened version that survives undefined/NaN inputs.
export { formatBytes } from '../../utils/format'
import { formatBytes } from '../../utils/format'

export function bytesTooltip(ctx: TooltipItem<any>): string {
  const value = (ctx.raw as number) ?? 0
  return `${ctx.dataset.label ?? ''}: ${formatBytes(value)}`
}

