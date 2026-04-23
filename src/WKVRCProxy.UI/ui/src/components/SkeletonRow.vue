<script setup lang="ts">
defineProps<{ count?: number; height?: string }>()
</script>

<template>
  <div class="space-y-2">
    <div v-for="i in (count ?? 4)" :key="i"
         :class="['rounded-xl bg-white/[0.03] border border-white/5 overflow-hidden relative skeleton-row', height ?? 'h-10']">
      <div class="absolute inset-0 skeleton-shimmer"></div>
    </div>
  </div>
</template>

<style scoped>
@keyframes skeleton-shimmer {
  0%   { transform: translateX(-100%); }
  100% { transform: translateX(100%); }
}
.skeleton-shimmer {
  background: linear-gradient(
    90deg,
    transparent 0%,
    rgba(255, 255, 255, 0.04) 50%,
    transparent 100%
  );
  animation: skeleton-shimmer 1.6s ease-in-out infinite;
}
</style>
