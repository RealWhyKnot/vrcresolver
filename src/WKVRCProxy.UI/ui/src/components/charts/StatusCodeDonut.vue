<script setup lang="ts">
import { computed } from 'vue'
import { Doughnut } from 'vue-chartjs'
import {
  Chart as ChartJS, ArcElement, Tooltip, DoughnutController
} from 'chart.js'
import { TOOLTIP_DEFAULTS } from './chartDefaults'
import type { RelayEvent } from '../../stores/appStore'

ChartJS.register(ArcElement, Tooltip, DoughnutController)

const props = defineProps<{
  events: RelayEvent[]
}>()

const counts = computed(() => {
  let pending = 0, s2xx = 0, s3xx = 0, s4xx = 0, s5xx = 0
  for (const e of props.events) {
    if (e.statusCode === 0) pending++
    else if (e.statusCode < 300) s2xx++
    else if (e.statusCode < 400) s3xx++
    else if (e.statusCode < 500) s4xx++
    else s5xx++
  }
  return { pending, s2xx, s3xx, s4xx, s5xx }
})

const chartData = computed(() => ({
  labels: ['2xx OK', '3xx Redirect', '4xx Client', '5xx Server', 'Pending'],
  datasets: [{
    data: [counts.value.s2xx, counts.value.s3xx, counts.value.s4xx, counts.value.s5xx, counts.value.pending],
    backgroundColor: [
      'rgba(34,197,94,0.85)',
      'rgba(249,115,22,0.8)',
      'rgba(239,68,68,0.7)',
      'rgba(168,85,247,0.7)',
      'rgba(59,130,246,0.6)'
    ],
    borderColor: 'rgba(10,14,26,0.9)',
    borderWidth: 2,
    hoverOffset: 5
  }]
}))

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  cutout: '65%',
  animation: { duration: 700, easing: 'easeOutCubic' as const },
  plugins: {
    legend: { display: false },
    tooltip: TOOLTIP_DEFAULTS as any
  }
}))
</script>

<template>
  <div class="relative w-full h-full">
    <Doughnut :data="chartData" :options="(chartOptions as any)" />
  </div>
</template>
