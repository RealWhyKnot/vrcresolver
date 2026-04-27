<script setup lang="ts">
// Small colored badge. Replaces inline pill markup across BypassView, DashboardView,
// HistoryView, etc. — each previously inlined its own color logic and Tailwind class blob.

const props = defineProps<{
  tone?: 'success' | 'warn' | 'danger' | 'neutral' | 'info' | 'live'
  label: string
  pulsing?: boolean
  icon?: string
}>()

const TONE_CLASSES: Record<NonNullable<typeof props.tone>, string> = {
  success: 'bg-emerald-500/15 text-emerald-300 border-emerald-500/25',
  warn:    'bg-amber-500/15 text-amber-300 border-amber-500/25',
  danger:  'bg-red-500/15 text-red-300 border-red-500/25',
  neutral: 'bg-white/5 text-white/55 border-white/10',
  info:    'bg-blue-500/15 text-blue-300 border-blue-500/25',
  live:    'bg-green-500/20 text-green-400 border-green-500/30',
}

function classFor(tone?: typeof props.tone) {
  return TONE_CLASSES[tone ?? 'neutral']
}
</script>

<template>
  <span :class="['inline-flex items-center gap-1 px-2.5 py-0.5 rounded-lg text-[8px] font-black uppercase tracking-widest border', classFor(tone)]">
    <span v-if="pulsing" :class="['w-1 h-1 rounded-full animate-pulse inline-block', tone === 'live' ? 'bg-green-400' : 'bg-current']"></span>
    <i v-if="icon" :class="['bi', icon]"></i>
    {{ label }}
  </span>
</template>
