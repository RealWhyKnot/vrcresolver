<script setup lang="ts">
import { computed } from 'vue'
import { Line } from 'vue-chartjs'
import {
  Chart as ChartJS, LineController, LineElement, PointElement, LinearScale, CategoryScale,
  Tooltip, Filler
} from 'chart.js'
import { TOOLTIP_DEFAULTS } from './chartDefaults'

ChartJS.register(LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip, Filler)

interface Point {
  /** y value — typically 1 for success, 0 for fail */
  value: number
  /** optional secondary label shown in tooltip */
  label?: string
  /** tooltip time */
  timestamp?: string
  /** true = success, false = fail — controls point color */
  success?: boolean
}

const props = defineProps<{ points: Point[] }>()

const chartData = computed(() => {
  const pts = props.points
  return {
    labels: pts.map((_, i) => String(i)),
    datasets: [{
      label: 'Activity',
      data: pts.map(p => p.value),
      borderColor: 'rgb(59,130,246)',
      backgroundColor: (ctx: any) => {
        const chart = ctx.chart
        const { ctx: c, chartArea } = chart
        if (!chartArea) return 'rgba(59,130,246,0.15)'
        const g = c.createLinearGradient(0, chartArea.top, 0, chartArea.bottom)
        g.addColorStop(0, 'rgba(59,130,246,0.35)')
        g.addColorStop(1, 'rgba(34,211,238,0.02)')
        return g
      },
      borderWidth: 2,
      fill: true,
      tension: 0.4,
      pointRadius: (ctx: any) => {
        const p = pts[ctx.dataIndex]
        return p?.success === false ? 3 : 2
      },
      pointBackgroundColor: (ctx: any) => {
        const p = pts[ctx.dataIndex]
        return p?.success === false ? 'rgb(239,68,68)' : 'rgb(34,211,238)'
      },
      pointBorderColor: 'rgba(10,14,26,0.9)',
      pointBorderWidth: 1,
      pointHoverRadius: 5
    }]
  }
})

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: { duration: 500, easing: 'easeOutCubic' as const },
  interaction: { intersect: false, mode: 'index' as const },
  plugins: {
    legend: { display: false },
    tooltip: {
      ...TOOLTIP_DEFAULTS,
      callbacks: {
        title: (items: any[]) => {
          const i = items[0]?.dataIndex ?? 0
          const p = props.points[i]
          return p?.timestamp ? new Date(p.timestamp).toLocaleTimeString() : ''
        },
        label: (item: any) => {
          const p = props.points[item.dataIndex]
          if (!p) return ''
          const status = p.success === false ? 'Failed' : p.success === true ? 'Success' : ''
          return [status, p.label].filter(Boolean).join(' · ')
        }
      }
    }
  },
  scales: {
    x: { display: false },
    y: {
      display: false,
      min: -0.1,
      max: 1.1
    }
  }
}))

</script>

<template>
  <div class="relative w-full h-full">
    <Line :data="chartData" :options="(chartOptions as any)" />
  </div>
</template>
