<script setup lang="ts">
import { computed } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const active = computed(() => {
  if (appStore.status.stats.activeCount > 0) return true
  const yt = appStore.ytDlpUpdate.status
  if (yt === 'Checking' || yt === 'Downloading') return true
  return false
})
</script>

<template>
  <Transition name="activitybar">
    <div v-if="active" class="fixed top-0 left-0 right-0 h-[2px] z-[150] overflow-hidden pointer-events-none">
      <div class="absolute inset-0 bg-blue-500/20"></div>
      <div class="absolute inset-y-0 w-1/3 bg-gradient-to-r from-transparent via-blue-400 to-transparent shadow-[0_0_10px_rgba(59,130,246,0.8)] activity-shimmer"></div>
    </div>
  </Transition>
</template>

<style scoped>
@keyframes activity-shimmer {
  0%   { transform: translateX(-100%); }
  100% { transform: translateX(400%); }
}
.activity-shimmer { animation: activity-shimmer 1.4s ease-in-out infinite; }

.activitybar-enter-active,
.activitybar-leave-active { transition: opacity 200ms ease-out; }
.activitybar-enter-from,
.activitybar-leave-to { opacity: 0; }
</style>
