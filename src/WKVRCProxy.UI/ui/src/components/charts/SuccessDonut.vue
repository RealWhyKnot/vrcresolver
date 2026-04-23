<script setup lang="ts">
import { computed } from 'vue'
import { Doughnut } from 'vue-chartjs'
import {
  Chart as ChartJS, ArcElement, Tooltip, Legend, DoughnutController
} from 'chart.js'
import { TOOLTIP_DEFAULTS } from './chartDefaults'

ChartJS.register(ArcElement, Tooltip, Legend, DoughnutController)

const props = defineProps<{
  success: number
  failed: number
  pending?: number
}>()

const chartData = computed(() => {
  const data = [props.success, props.failed, props.pending ?? 0]
  return {
    labels: ['Success', 'Failed', 'Pending'],
    datasets: [{
      data,
      backgroundColor: [
        'rgba(34,197,94,0.85)',
        'rgba(239,68,68,0.6)',
        'rgba(255,255,255,0.12)'
      ],
      borderColor: [
        'rgba(34,197,94,1)',
        'rgba(239,68,68,0.9)',
        'rgba(255,255,255,0.2)'
      ],
      borderWidth: 2,
      hoverOffset: 6
    }]
  }
})

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  cutout: '72%',
  animation: { duration: 900, easing: 'easeOutCubic' as const },
  plugins: {
    legend: { display: false },
    tooltip: TOOLTIP_DEFAULTS as any
  }
}))
</script>

<template>
  <div class="relative w-full h-full">
    <Doughnut :data="chartData" :options="(chartOptions as any)" />
    <slot name="center" />
  </div>
</template>
