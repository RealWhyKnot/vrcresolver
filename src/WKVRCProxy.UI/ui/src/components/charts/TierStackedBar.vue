<script setup lang="ts">
import { computed } from 'vue'
import { Bar } from 'vue-chartjs'
import {
  Chart as ChartJS, BarController, BarElement, LinearScale, CategoryScale, Tooltip
} from 'chart.js'
import { TOOLTIP_DEFAULTS, tierColor } from './chartDefaults'
import { TIER_DISPLAY } from '../../stores/appStore'

ChartJS.register(BarController, BarElement, LinearScale, CategoryScale, Tooltip)

const props = defineProps<{
  tierStats: Record<string, number>
}>()

const chartData = computed(() => {
  const keys = ['tier1', 'tier2', 'tier3', 'tier4'] as const
  return {
    labels: ['Tiers'],
    datasets: keys.map(key => ({
      label: TIER_DISPLAY[key]?.short ?? key,
      data: [props.tierStats[key] ?? 0],
      backgroundColor: tierColor(key, 0.85),
      borderColor: tierColor(key),
      borderWidth: 0,
      borderRadius: 4,
      borderSkipped: false
    }))
  }
})

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  indexAxis: 'y' as const,
  animation: { duration: 800, easing: 'easeOutCubic' as const },
  plugins: {
    legend: { display: false },
    tooltip: {
      ...TOOLTIP_DEFAULTS,
      callbacks: {
        label: (ctx: any) => `${ctx.dataset.label}: ${ctx.raw}`
      }
    }
  },
  scales: {
    x: { display: false, stacked: true },
    y: { display: false, stacked: true }
  }
}))
</script>

<template>
  <div class="relative w-full h-full">
    <Bar :data="chartData" :options="(chartOptions as any)" />
  </div>
</template>
