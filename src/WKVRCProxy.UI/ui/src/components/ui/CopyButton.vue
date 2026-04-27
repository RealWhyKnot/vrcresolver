<script setup lang="ts">
import { ref } from 'vue'

// Reusable "copy to clipboard" button with green-confirmed flash on success.
// Replaces the duplicated copy logic in ShareView (Cloud + P2P modes both had
// their own ref / setTimeout / styling). Future settings/share/links can reuse
// this without re-implementing the pattern.

const props = withDefaults(defineProps<{
  value: string
  label?: string
  /** Accent color for the idle state. Defaults to blue (the project's primary). */
  accent?: 'blue' | 'indigo' | 'emerald'
  disabled?: boolean
}>(), {
  label: 'Copy',
  accent: 'blue',
  disabled: false,
})

const emit = defineEmits<{
  (e: 'copied'): void
  (e: 'error', err: unknown): void
}>()

const copied = ref(false)
let resetTimer = 0

const ACCENT_CLASSES: Record<NonNullable<typeof props.accent>, string> = {
  blue:    'bg-blue-600 hover:bg-blue-500 shadow-blue-600/20',
  indigo:  'bg-indigo-600 hover:bg-indigo-500 shadow-indigo-600/20',
  emerald: 'bg-emerald-600 hover:bg-emerald-500 shadow-emerald-600/20',
}

async function copy() {
  if (props.disabled || !props.value) return
  try {
    await navigator.clipboard.writeText(props.value)
    copied.value = true
    if (resetTimer) window.clearTimeout(resetTimer)
    resetTimer = window.setTimeout(() => { copied.value = false }, 2000)
    emit('copied')
  } catch (err) {
    emit('error', err)
  }
}
</script>

<template>
  <button @click="copy"
          :disabled="disabled || !value"
          :class="['flex-1 py-3 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic active:scale-95 shadow-lg disabled:opacity-30 disabled:cursor-not-allowed',
                   copied ? 'bg-green-600/80 text-white shadow-green-600/20' : ACCENT_CLASSES[accent] + ' text-white']">
    <i :class="['bi mr-1.5', copied ? 'bi-check-lg' : 'bi-clipboard']"></i>{{ copied ? 'Copied!' : label }}
  </button>
</template>
