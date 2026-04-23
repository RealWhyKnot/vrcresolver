<script setup lang="ts">
import { computed } from 'vue'
import { Bar } from 'vue-chartjs'
import {
  Chart as ChartJS, BarController, BarElement, LinearScale, CategoryScale, Tooltip
} from 'chart.js'
import { TOOLTIP_DEFAULTS, FONT_FAMILY } from './chartDefaults'
import type { HistoryEntry } from '../../stores/appStore'

ChartJS.register(BarController, BarElement, LinearScale, CategoryScale, Tooltip)

const props = defineProps<{
  history: HistoryEntry[]
}>()

const BUCKETS: Array<{ label: string; min: number; max: number; color: string }> = [
  { label: '≤480p',  min: 0,    max: 480,  color: 'rgba(156,163,175,0.8)' },
  { label: '720p',   min: 481,  max: 720,  color: 'rgba(34,197,94,0.8)' },
  { label: '1080p',  min: 721,  max: 1080, color: 'rgba(59,130,246,0.85)' },
  { label: '1440p',  min: 1081, max: 1440, color: 'rgba(168,85,247,0.85)' },
  { label: '≥2160p', min: 1441, max: 1e9,  color: 'rgba(236,72,153,0.85)' }
]

const counts = computed(() => {
  const res = BUCKETS.map(() => 0)
  for (const h of props.history) {
    const height = h.ResolutionHeight ?? 0
    if (height <= 0) continue
    for (let i = 0; i < BUCKETS.length; i++) {
      if (height >= BUCKETS[i].min && height <= BUCKETS[i].max) { res[i]++; break }
    }
  }
  return res
})

const chartData = computed(() => ({
  labels: BUCKETS.map(b => b.label),
  datasets: [{
    label: 'Resolutions',
    data: counts.value,
    backgroundColor: BUCKETS.map(b => b.color),
    borderColor: BUCKETS.map(b => b.color),
    borderWidth: 0,
    borderRadius: 4,
    borderSkipped: false
  }]
}))

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: { duration: 600, easing: 'easeOutCubic' as const },
  plugins: {
    legend: { display: false },
    tooltip: TOOLTIP_DEFAULTS as any
  },
  scales: {
    x: {
      grid: { display: false },
      ticks: { color: 'rgba(255,255,255,0.45)', font: { family: FONT_FAMILY, size: 9 } },
      border: { display: false }
    },
    y: {
      grid: { color: 'rgba(255,255,255,0.04)' },
      ticks: { color: 'rgba(255,255,255,0.3)', font: { family: FONT_FAMILY, size: 9 }, precision: 0 },
      border: { display: false },
      beginAtZero: true
    }
  }
}))
</script>

<template>
  <div class="relative w-full h-full">
    <Bar :data="chartData" :options="(chartOptions as any)" />
  </div>
</template>
