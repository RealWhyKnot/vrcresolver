<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted, watch } from 'vue'
import { Line } from 'vue-chartjs'
import {
  Chart as ChartJS, LineController, LineElement, PointElement, LinearScale, CategoryScale,
  Tooltip, Filler
} from 'chart.js'
import { TOOLTIP_DEFAULTS, formatBytes, FONT_FAMILY } from './chartDefaults'
import type { RelayEvent } from '../../stores/appStore'

ChartJS.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip, Filler)

const props = withDefaults(defineProps<{
  events: RelayEvent[]
  /** Number of 1-second buckets to show. Default 60. */
  seconds?: number
}>(), { seconds: 60 })

// Tick once per second so the window slides even with no new events.
const tick = ref(0)
let intervalId = 0

onMounted(() => {
  intervalId = window.setInterval(() => { tick.value++ }, 1000)
})
onUnmounted(() => {
  if (intervalId) clearInterval(intervalId)
})

watch(() => props.events.length, () => { tick.value++ })

const buckets = computed(() => {
  // Depend on tick so this recomputes even if props don't change identity
  void tick.value
  const now = Date.now()
  const windowMs = props.seconds * 1000
  const bytes = new Array(props.seconds).fill(0)
  const requests = new Array(props.seconds).fill(0)
  for (const e of props.events) {
    const t = new Date(e.timestamp).getTime()
    if (!Number.isFinite(t)) continue
    const age = now - t
    if (age < 0 || age >= windowMs) continue
    const idx = props.seconds - 1 - Math.floor(age / 1000)
    if (idx < 0 || idx >= props.seconds) continue
    bytes[idx] += e.bytesTransferred || 0
    requests[idx] += 1
  }
  return { bytes, requests }
})

const chartData = computed(() => {
  const labels = Array.from({ length: props.seconds }, (_, i) => {
    const offset = props.seconds - 1 - i
    return offset === 0 ? 'now' : `-${offset}s`
  })
  return {
    labels,
    datasets: [{
      label: 'Bytes/sec',
      data: buckets.value.bytes,
      yAxisID: 'y',
      borderColor: 'rgb(59,130,246)',
      backgroundColor: (ctx: any) => {
        const chart = ctx.chart
        const { ctx: c, chartArea } = chart
        if (!chartArea) return 'rgba(59,130,246,0.15)'
        const g = c.createLinearGradient(0, chartArea.top, 0, chartArea.bottom)
        g.addColorStop(0, 'rgba(59,130,246,0.4)')
        g.addColorStop(1, 'rgba(59,130,246,0.02)')
        return g
      },
      borderWidth: 2,
      fill: true,
      tension: 0.4,
      pointRadius: 0,
      pointHoverRadius: 4
    },
    {
      label: 'Requests/sec',
      data: buckets.value.requests,
      yAxisID: 'y1',
      borderColor: 'rgb(168,85,247)',
      backgroundColor: 'rgba(168,85,247,0)',
      borderWidth: 1.5,
      borderDash: [4, 3],
      fill: false,
      tension: 0.3,
      pointRadius: 0,
      pointHoverRadius: 4
    }]
  }
})

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: false as const,
  interaction: { intersect: false, mode: 'index' as const },
  plugins: {
    legend: { display: false },
    tooltip: {
      ...TOOLTIP_DEFAULTS,
      callbacks: {
        label: (ctx: any) => {
          if (ctx.dataset.label === 'Bytes/sec') {
            return `${ctx.dataset.label}: ${formatBytes(ctx.raw)}`
          }
          return `${ctx.dataset.label}: ${ctx.raw}`
        }
      }
    }
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
      border: { display: false }
    },
    y: {
      position: 'left' as const,
      grid: { color: 'rgba(255,255,255,0.04)' },
      ticks: {
        color: 'rgba(59,130,246,0.55)',
        font: { family: FONT_FAMILY, size: 9 },
        callback: (v: any) => formatBytes(v)
      },
      border: { display: false },
      beginAtZero: true
    },
    y1: {
      position: 'right' as const,
      grid: { display: false },
      ticks: {
        color: 'rgba(168,85,247,0.55)',
        font: { family: FONT_FAMILY, size: 9 },
        precision: 0
      },
      border: { display: false },
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
