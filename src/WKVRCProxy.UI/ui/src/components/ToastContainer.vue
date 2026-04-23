<script setup lang="ts">
import { useAppStore } from '../stores/appStore'
import type { ToastVariant } from '../stores/appStore'

const appStore = useAppStore()

const variantStyles: Record<ToastVariant, { bg: string; border: string; icon: string; iconClass: string; accent: string }> = {
  info:    { bg: 'bg-blue-500/10',    border: 'border-blue-500/30',    icon: 'bi-info-circle-fill',        iconClass: 'text-blue-400',   accent: 'bg-blue-500' },
  success: { bg: 'bg-emerald-500/10', border: 'border-emerald-500/30', icon: 'bi-check-circle-fill',       iconClass: 'text-emerald-400', accent: 'bg-emerald-500' },
  warning: { bg: 'bg-amber-500/10',   border: 'border-amber-500/30',   icon: 'bi-exclamation-triangle-fill', iconClass: 'text-amber-400',  accent: 'bg-amber-500' },
  error:   { bg: 'bg-red-500/10',     border: 'border-red-500/30',     icon: 'bi-x-octagon-fill',          iconClass: 'text-red-400',    accent: 'bg-red-500' }
}
</script>

<template>
  <div class="fixed top-4 right-4 z-[200] flex flex-col gap-2 w-80 pointer-events-none">
    <TransitionGroup name="toast" tag="div" class="flex flex-col gap-2">
      <div v-for="t in appStore.toasts" :key="t.id"
           :class="['pointer-events-auto relative overflow-hidden rounded-2xl backdrop-blur-2xl border shadow-2xl px-4 py-3 flex items-start gap-3 cursor-pointer group',
                    variantStyles[t.variant].bg, variantStyles[t.variant].border]"
           @click="appStore.dismissToast(t.id)"
           @mouseenter="appStore.pauseToast(t.id)"
           @mouseleave="appStore.resumeToast(t.id)">
        <div :class="['absolute left-0 top-0 bottom-0 w-[3px]', variantStyles[t.variant].accent]"></div>
        <i :class="['bi shrink-0 mt-0.5 text-base', variantStyles[t.variant].icon, variantStyles[t.variant].iconClass]"></i>
        <div class="min-w-0 flex-grow space-y-0.5">
          <p class="text-[10px] font-black uppercase tracking-widest italic text-white/90 leading-tight">{{ t.title }}</p>
          <p v-if="t.message" class="text-[9px] font-mono text-white/55 break-words leading-snug">{{ t.message }}</p>
        </div>
        <button class="text-white/25 hover:text-white/80 transition-colors shrink-0 -mr-1 -mt-0.5 text-[11px] leading-none" @click.stop="appStore.dismissToast(t.id)">×</button>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.toast-enter-active { transition: all 300ms cubic-bezier(0.34, 1.56, 0.64, 1); }
.toast-leave-active { transition: all 200ms ease-in; }
.toast-enter-from { opacity: 0; transform: translateX(20px) scale(0.95); }
.toast-leave-to   { opacity: 0; transform: translateX(20px) scale(0.95); }
.toast-move       { transition: transform 300ms cubic-bezier(0.34, 1.56, 0.64, 1); }
</style>
