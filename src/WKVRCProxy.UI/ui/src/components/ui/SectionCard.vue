<script setup lang="ts">
// Wraps the recurring section card pattern: dark glassy panel, optional icon-circle
// header, hover-glow on a tunable accent color. The two slots — `header` for the
// title row and the default slot for body — keep callers flexible without paying
// for prop bloat.

const props = defineProps<{
  /** Accent color for icon background, hover glow, and border highlight. */
  accent?: 'blue' | 'emerald' | 'amber' | 'red' | 'indigo' | 'violet'
  /** Optional Bootstrap icon class (e.g. "bi-shield-lock") rendered in a circle on the left. */
  icon?: string
  title?: string
  subtitle?: string
}>()

const ACCENT_BORDER: Record<NonNullable<typeof props.accent>, string> = {
  blue:    'hover:border-blue-500/20',
  emerald: 'hover:border-emerald-500/20',
  amber:   'hover:border-amber-500/20',
  red:     'hover:border-red-500/20',
  indigo:  'hover:border-indigo-500/20',
  violet:  'hover:border-violet-500/20',
}
const ACCENT_ICON: Record<NonNullable<typeof props.accent>, string> = {
  blue:    'bg-blue-500/10 text-blue-500',
  emerald: 'bg-emerald-500/10 text-emerald-500',
  amber:   'bg-amber-500/10 text-amber-500',
  red:     'bg-red-500/10 text-red-500',
  indigo:  'bg-indigo-500/10 text-indigo-400',
  violet:  'bg-violet-500/10 text-violet-400',
}
</script>

<template>
  <section :class="['bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-5 transition-all duration-500 shadow-2xl backdrop-blur-3xl group',
                    accent ? ACCENT_BORDER[accent] : 'hover:border-white/15']">
    <div v-if="$slots.header || title" class="flex items-center gap-4">
      <slot name="header">
        <div v-if="icon" :class="['w-10 h-10 rounded-2xl flex items-center justify-center group-hover:scale-110 transition-transform',
                                  accent ? ACCENT_ICON[accent] : 'bg-white/5 text-white/55']">
          <i :class="['bi text-xl', icon]"></i>
        </div>
        <div>
          <h4 v-if="title" class="text-lg font-black uppercase tracking-tighter italic">{{ title }}</h4>
          <p v-if="subtitle" class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">{{ subtitle }}</p>
        </div>
      </slot>
    </div>
    <slot />
  </section>
</template>
