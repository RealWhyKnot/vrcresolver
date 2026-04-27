<script setup lang="ts">
// Reusable metric/stat tile. Replaces the recurring "big number + small uppercase label"
// card pattern that appears in DashboardView, RelayView, HistoryView, BypassView with
// near-identical Tailwind classes.
//
// `flashDirection` accepts the value from useAnimatedNumber so callers can keep the
// existing green-up / red-down flash animation when the underlying number ticks.

defineProps<{
  value: string | number
  label: string
  flashDirection?: 'up' | 'down' | null
  suffix?: string
  icon?: string
  /** When true, renders a small pulsing blue dot next to the value (used by the "Active"
   *  tile in RelayView while requests are in flight). */
  pulsing?: boolean
}>()

function flashClass(dir?: 'up' | 'down' | null): string {
  if (dir === 'up') return 'flash-up'
  if (dir === 'down') return 'flash-down'
  return ''
}
</script>

<template>
  <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.08)]">
    <div class="text-2xl font-black italic tabular-nums flex items-center gap-2" :class="flashClass(flashDirection)">
      <i v-if="icon" :class="['bi text-base text-white/55', icon]"></i>
      <span>{{ value }}<span v-if="suffix" class="text-sm text-white/40">{{ suffix }}</span></span>
      <span v-if="pulsing" class="w-1.5 h-1.5 bg-blue-400 rounded-full animate-pulse inline-block"></span>
    </div>
    <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">{{ label }}</div>
  </div>
</template>

<style scoped>
@keyframes flash-up   { 0% { color: rgb(52,211,153); text-shadow: 0 0 12px rgba(52,211,153,0.6); } 100% {} }
@keyframes flash-down { 0% { color: rgb(248,113,113); text-shadow: 0 0 12px rgba(248,113,113,0.6); } 100% {} }
.flash-up   { animation: flash-up   900ms ease-out; }
.flash-down { animation: flash-down 900ms ease-out; }
</style>
