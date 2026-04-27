<script setup lang="ts">
import { ref, watch, nextTick, onMounted, computed } from 'vue'
import { useAppStore } from '../stores/appStore'
import SkeletonRow from '../components/SkeletonRow.vue'

const appStore = useAppStore()
const scrollContainer = ref<HTMLElement | null>(null)

const logLevelNames = ['Trace', 'Debug', 'Info', 'Success', 'Warning', 'Error', 'Fatal']
const logLevelClasses = [
  'text-white/30',
  'text-blue-400/70',
  'text-white/60',
  'text-emerald-400 font-black italic',
  'text-yellow-400/75',
  'text-red-400/80',
  'text-red-500 font-black italic'
]

// Auto-scroll toggle. When off, user keeps their scroll position (e.g. reviewing old errors)
// even as new logs arrive. Default on — matches the previous behavior.
const autoScroll = ref(true)

const scrollToBottom = () => {
  if (!autoScroll.value) return
  if (scrollContainer.value) {
    scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight
  }
}

watch(() => appStore.filteredLogs.length, () => {
  nextTick(() => scrollToBottom())
})

onMounted(() => {
  nextTick(() => scrollToBottom())
})

function setLevelFilter(level: number | null) {
  appStore.logLevelFilter = appStore.logLevelFilter === level ? null : level
}

const isInitialLoad = computed(() => !appStore.isBridgeReady && appStore.logs.length === 0)

function isErrorLevel(level: number): boolean {
  return level >= 5  // 5 = Error, 6 = Fatal
}

// Track the newest error to trigger a one-shot pulse.
const newestErrorIndex = ref<number>(-1)
watch(() => appStore.filteredLogs.length, () => {
  const last = appStore.filteredLogs[appStore.filteredLogs.length - 1]
  if (last && isErrorLevel(last.Level)) {
    newestErrorIndex.value = appStore.filteredLogs.length - 1
    setTimeout(() => { newestErrorIndex.value = -1 }, 1500)
  }
})
</script>

<template>
  <div class="p-8 h-full flex flex-col space-y-6">
    <div class="space-y-2 shrink-0"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <h2 class="text-3xl font-black uppercase tracking-tighter italic">System <span class="text-blue-500">Logs</span></h2>
      <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">Event stream</p>
    </div>

    <!-- Filter Toolbar -->
    <div class="shrink-0 flex items-center gap-3 flex-wrap"
         v-motion :initial="{ opacity: 0, y: 8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 400, delay: 100 } }">
      <div class="flex gap-1.5 flex-wrap">
        <button
          @click="appStore.logLevelFilter = null"
          :class="['px-3 py-1.5 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic border active:scale-95',
                   appStore.logLevelFilter === null
                     ? 'bg-blue-600/80 text-white border-blue-500/50 shadow-[0_0_10px_rgba(37,99,235,0.25)]'
                     : 'bg-white/[0.03] text-white/40 hover:text-white/70 border-white/5']">
          All
        </button>
        <button v-for="(name, idx) in logLevelNames" :key="idx"
          @click="setLevelFilter(idx)"
          :class="['px-3 py-1.5 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic border active:scale-95',
                   appStore.logLevelFilter === idx
                     ? 'bg-blue-600/80 text-white border-blue-500/50 shadow-[0_0_10px_rgba(37,99,235,0.25)]'
                     : 'bg-white/[0.03] text-white/40 hover:text-white/70 border-white/5']">
          {{ name }}
        </button>
      </div>

      <input
        v-model="appStore.logSourceFilter"
        type="text"
        placeholder="Filter source..."
        class="bg-white/[0.03] border border-white/10 rounded-xl px-4 py-1.5 text-[9px] font-mono text-white/70 focus:outline-none focus:border-blue-500/50 placeholder:text-white/20 transition-all w-36" />

      <!-- Auto-scroll toggle -->
      <label class="flex items-center gap-2 cursor-pointer select-none group ml-2">
        <div :class="['w-8 h-4 rounded-full relative transition-all duration-300',
                      autoScroll ? 'bg-blue-600 shadow-[0_0_10px_rgba(37,99,235,0.3)]' : 'bg-white/10 border border-white/10']">
          <div :class="['absolute top-0.5 w-3 h-3 bg-white rounded-full transition-all duration-300',
                        autoScroll ? 'left-[18px]' : 'left-0.5']"></div>
        </div>
        <input v-model="autoScroll" type="checkbox" class="sr-only" />
        <span class="text-[8px] font-black uppercase tracking-widest text-white/45 group-hover:text-white/70 transition-colors italic">Auto-scroll</span>
      </label>

      <span class="text-[8px] font-black uppercase tracking-widest text-white/25 ml-auto italic tabular-nums">
        {{ appStore.filteredLogs.length }} / {{ appStore.logs.length }} events
      </span>

      <button @click="appStore.logs = []" class="text-[9px] font-black uppercase tracking-widest text-white/30 hover:text-red-400 transition-colors italic active:scale-95">
        Clear
      </button>
    </div>

    <!-- Log count by level -->
    <div class="flex gap-4 text-[8px] font-mono text-white/25 shrink-0"
         v-motion :initial="{ opacity: 0 }" :enter="{ opacity: 1, transition: { duration: 400, delay: 160 } }">
      <span v-for="(name, idx) in logLevelNames" :key="idx" :class="logLevelClasses[idx]">
        {{ name }}: {{ appStore.logs.filter(l => l.Level === idx).length }}
      </span>
    </div>

    <!-- Demotion chips -->
    <div v-if="appStore.demotions.length > 0" class="shrink-0 flex items-start gap-2 flex-wrap">
      <span class="text-[8px] font-black uppercase tracking-[0.3em] text-yellow-400/70 italic mt-2 shrink-0">Demoted</span>
      <div class="flex gap-2 flex-wrap">
        <div v-for="d in appStore.demotions" :key="d.id"
             class="group bg-yellow-500/[0.06] border border-yellow-500/20 rounded-xl px-3 py-1.5 flex items-center gap-2 hover:bg-yellow-500/[0.12] transition-all demotion-chip"
             :title="`${d.reason}${d.correlationId ? ' · ' + d.correlationId : ''}`">
          <span class="text-[8px] font-black text-yellow-400/90 uppercase tracking-widest italic">{{ d.strategyName }}</span>
          <span class="text-[8px] text-white/35 font-mono">@ {{ d.memKey }}</span>
          <span class="text-[8px] text-white/40 italic">{{ d.reason }}</span>
          <button @click="appStore.dismissDemotion(d.id)"
                  class="text-white/30 hover:text-red-400 text-[10px] leading-none ml-1">×</button>
        </div>
      </div>
      <button @click="appStore.clearDemotions()" class="ml-auto text-[8px] font-black uppercase tracking-widest text-white/30 hover:text-red-400 transition-colors italic mt-2 active:scale-95">
        Clear demotions
      </button>
    </div>

    <div class="flex-grow bg-white/[0.02] border border-white/5 rounded-[32px] overflow-hidden backdrop-blur-3xl shadow-2xl flex flex-col font-mono text-[9px]"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 220 } }">
      <div class="px-8 py-4 bg-white/[0.01] border-b border-white/5 grid grid-cols-[7rem_6rem_8rem_1fr] gap-4 text-white/30 font-black uppercase tracking-[0.3em] text-[8px] italic shrink-0 shadow-[0_4px_12px_rgba(0,0,0,0.3)]">
        <span>Time</span>
        <span>Level</span>
        <span>Source</span>
        <span>Message</span>
      </div>
      <div ref="scrollContainer" class="flex-grow overflow-y-auto p-8 space-y-1.5 no-scrollbar leading-relaxed scroll-smooth">
        <template v-if="isInitialLoad">
          <SkeletonRow :count="6" height="h-6" />
        </template>
        <template v-else>
          <div v-for="(log, i) in appStore.filteredLogs"
               :key="(log.Timestamp ?? '') + '|' + (log.Source ?? '') + '|' + i"
               :class="['grid grid-cols-[7rem_6rem_8rem_1fr] gap-4 border-l border-transparent hover:border-blue-500/40 hover:bg-white/[0.03] px-4 transition-all py-0.5 rounded-r-lg group/item',
                        isErrorLevel(log.Level) ? 'border-red-500/25' : '',
                        i === newestErrorIndex ? 'error-pulse' : '']">
            <span class="text-white/35 shrink-0 group-hover/item:text-white/55 font-bold tabular-nums">
              {{ log.Timestamp?.split('T')[1]?.split('.')[0] ?? '--:--:--' }}
            </span>
            <span :class="logLevelClasses[log.Level]" class="shrink-0 uppercase tracking-widest truncate">
              [{{ logLevelNames[log.Level] }}]
            </span>
            <span class="text-white/40 group-hover/item:text-white/60 transition-colors truncate font-bold">
              {{ log.Source }}
            </span>
            <span class="text-white/65 group-hover/item:text-white/85 transition-colors break-all">
              {{ log.Message }}
            </span>
          </div>
          <div v-if="appStore.filteredLogs.length === 0" class="text-white/25 italic py-20 text-center font-black">
            <p class="uppercase tracking-[0.8em]">{{ appStore.logs.length === 0 ? 'No logs yet' : 'No logs match filter' }}</p>
            <p v-if="appStore.logs.length === 0" class="mt-3 text-white/20 text-[10px] tracking-widest normal-case">
              Logs from VRChat and the proxy will appear here once a video is requested.
            </p>
            <p v-else class="mt-3 text-white/20 text-[10px] tracking-widest normal-case">
              Try clearing the level / source filter above.
            </p>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>

<style scoped>
@keyframes error-pulse {
  0%   { background-color: rgba(239,68,68,0.18); border-left-color: rgb(239,68,68); }
  100% { background-color: transparent; border-left-color: rgba(239,68,68,0.25); }
}
.error-pulse { animation: error-pulse 1500ms ease-out; }

@keyframes demotion-in {
  from { opacity: 0; transform: translateY(-4px) scale(0.95); }
  to   { opacity: 1; transform: translateY(0) scale(1); }
}
.demotion-chip { animation: demotion-in 350ms cubic-bezier(0.34, 1.56, 0.64, 1); }
</style>
