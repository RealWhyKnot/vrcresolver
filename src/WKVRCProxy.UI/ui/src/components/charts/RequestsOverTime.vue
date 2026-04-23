<script setup lang="ts">
import { computed } from 'vue'
import { Line } from 'vue-chartjs'
import {
  Chart as ChartJS, LineController, LineElement, PointElement, LinearScale, CategoryScale,
  Tooltip, Filler
} from 'chart.js'
import { TOOLTIP_DEFAULTS, tierColor, FONT_FAMILY } from './chartDefaults'
import { TIER_DISPLAY, type HistoryEntry } from '../../stores/appStore'

ChartJS.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip, Filler)

const props = withDefaults(defineProps<{
  history: HistoryEntry[]
  minutes?: number
}>(), { minutes: 60 })

// Bucket history into 1-minute cells over the last `minutes` min, per tier family.
const bucketed = computed(() => {
  const now = Date.now()
  const span = props.minutes * 60 * 1000
  const buckets: Record<string, number[]> = {
    tier1: new Array(props.minutes).fill(0),
    tier2: new Array(props.minutes).fill(0),
    tier3: new Array(props.minutes).fill(0),
    tier4: new Array(props.minutes).fill(0)
  }
  for (const h of props.history) {
    const t = new Date(h.Timestamp).getTime()
    if (!Number.isFinite(t)) continue
    const ageMs = now - t
    if (ageMs < 0 || ageMs > span) continue
    const idx = props.minutes - 1 - Math.floor(ageMs / 60000)
    if (idx < 0 || idx >= props.minutes) continue
    const key = (h.Tier ?? '').split('-')[0] || 'tier4'
    if (buckets[key]) buckets[key][idx] += 1
  }
  return buckets
})

const chartData = computed(() => {
  const labels: string[] = []
  for (let i = props.minutes - 1; i >= 0; i--) {
    labels.push(i === 0 ? 'now' : `-${i}m`)
  }
  const b = bucketed.value
  return {
    labels,
    datasets: (['tier1', 'tier2', 'tier3', 'tier4'] as const).map(key => ({
      label: TIER_DISPLAY[key]?.short ?? key,
      data: b[key],
      borderColor: tierColor(key),
      backgroundColor: tierColor(key, 0.25),
      borderWidth: 1.5,
      fill: true,
      tension: 0.35,
      pointRadius: 0,
      pointHoverRadius: 4
    }))
  }
})

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: { duration: 500, easing: 'easeOutCubic' as const },
  interaction: { intersect: false, mode: 'index' as const },
  plugins: {
    legend: { display: false },
    tooltip: TOOLTIP_DEFAULTS as any
  },
  scales: {
    x: {
      grid: { display: false },
      ticks: {
        color: 'rgba(255,255,255,0.3)',
        font: { family: FONT_FAMILY, size: 9 },
        maxTicksLimit: 6,
        autoSkip: true
      },
      border: { display: false },
      stacked: true
    },
    y: {
      grid: { color: 'rgba(255,255,255,0.04)' },
      ticks: {
        color: 'rgba(255,255,255,0.3)',
        font: { family: FONT_FAMILY, size: 9 },
        precision: 0
      },
      border: { display: false },
      stacked: true,
      beginAtZero: true
    }
  }
}))
</script>

<template>
  <div class="relative w-full h-full">
    <Line :data="chartData" :options="(chartOptions as any)" />
  </div>
</template>
