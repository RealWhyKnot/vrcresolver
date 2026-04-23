<script setup lang="ts">
import { ref, watch, nextTick, onMounted } from 'vue'
import { useAppStore } from '../stores/appStore'

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

const scrollToBottom = () => {
  if (scrollContainer.value) {
    scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight
  }
}

// Only auto-scroll when the visible list grows (respects active filter)
watch(() => appStore.filteredLogs.length, () => {
  nextTick(() => scrollToBottom())
})

onMounted(() => {
  nextTick(() => scrollToBottom())
})

function setLevelFilter(level: number | null) {
  appStore.logLevelFilter = appStore.logLevelFilter === level ? null : level
}
</script>

<template>
  <div class="p-8 h-full flex flex-col space-y-6">
    <div class="space-y-2 shrink-0">
      <h2 class="text-3xl font-black uppercase tracking-tighter italic">System <span class="text-blue-500">Logs</span></h2>
      <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">Event stream</p>
    </div>

    <!-- Filter Toolbar -->
    <div class="shrink-0 flex items-center gap-3 flex-wrap">
      <!-- Level filter pills -->
      <div class="flex gap-1.5 flex-wrap">
        <button
          @click="appStore.logLevelFilter = null"
          :class="['px-3 py-1.5 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic border',
                   appStore.logLevelFilter === null
                     ? 'bg-blue-600/80 text-white border-blue-500/50 shadow-[0_0_10px_rgba(37,99,235,0.25)]'
                     : 'bg-white/[0.03] text-white/40 hover:text-white/70 border-white/5']">
          All
        </button>
        <button v-for="(name, idx) in logLevelNames" :key="idx"
          @click="setLevelFilter(idx)"
          :class="['px-3 py-1.5 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic border',
                   appStore.logLevelFilter === idx
                     ? 'bg-blue-600/80 text-white border-blue-500/50 shadow-[0_0_10px_rgba(37,99,235,0.25)]'
                     : 'bg-white/[0.03] text-white/40 hover:text-white/70 border-white/5']">
          {{ name }}
        </button>
      </div>

      <!-- Source filter input -->
      <input
        v-model="appStore.logSourceFilter"
        type="text"
        placeholder="Filter source..."
        class="bg-white/[0.03] border border-white/10 rounded-xl px-4 py-1.5 text-[9px] font-mono text-white/70 focus:outline-none focus:border-blue-500/50 placeholder:text-white/20 transition-all w-36" />

      <!-- Filtered / total count -->
      <span class="text-[8px] font-black uppercase tracking-widest text-white/25 ml-auto italic tabular-nums">
        {{ appStore.filteredLogs.length }} / {{ appStore.logs.length }} events
      </span>

      <!-- Clear Logs button -->
      <button @click="appStore.logs = []" class="text-[9px] font-black uppercase tracking-widest text-white/30 hover:text-red-400 transition-colors italic">
        Clear
      </button>
    </div>

    <!-- Log count by level -->
    <div class="flex gap-4 text-[8px] font-mono text-white/25 shrink-0">
      <span v-for="(name, idx) in logLevelNames" :key="idx" :class="logLevelClasses[idx]">
        {{ name }}: {{ appStore.logs.filter(l => l.Level === idx).length }}
      </span>
    </div>

    <!-- Playback-feedback demotion chips: emitted when a strategy's resolved URL is rejected by
         AVPro or the pre-flight probe. Click × to dismiss; all cleared by the button at the end. -->
    <div v-if="appStore.demotions.length > 0" class="shrink-0 flex items-start gap-2 flex-wrap">
      <span class="text-[8px] font-black uppercase tracking-[0.3em] text-yellow-400/70 italic mt-2 shrink-0">Demoted</span>
      <div class="flex gap-2 flex-wrap">
        <div v-for="d in appStore.demotions" :key="d.id"
             class="group bg-yellow-500/[0.06] border border-yellow-500/20 rounded-xl px-3 py-1.5 flex items-center gap-2 hover:bg-yellow-500/[0.12] transition-all"
             :title="`${d.reason}${d.correlationId ? ' · ' + d.correlationId : ''}`">
          <span class="text-[8px] font-black text-yellow-400/90 uppercase tracking-widest italic">{{ d.strategyName }}</span>
          <span class="text-[8px] text-white/35 font-mono">@ {{ d.memKey }}</span>
          <span class="text-[8px] text-white/40 italic">{{ d.reason }}</span>
          <button @click="appStore.dismissDemotion(d.id)"
                  class="text-white/30 hover:text-red-400 text-[10px] leading-none ml-1">×</button>
        </div>
      </div>
      <button @click="appStore.clearDemotions()" class="ml-auto text-[8px] font-black uppercase tracking-widest text-white/30 hover:text-red-400 transition-colors italic mt-2">
        Clear demotions
      </button>
    </div>

    <div class="flex-grow bg-white/[0.02] border border-white/5 rounded-[32px] overflow-hidden backdrop-blur-3xl shadow-2xl flex flex-col font-mono text-[9px]">
      <!-- Column headers -->
      <div class="px-8 py-4 bg-white/[0.01] border-b border-white/5 grid grid-cols-[7rem_6rem_8rem_1fr] gap-4 text-white/30 font-black uppercase tracking-[0.3em] text-[8px] italic shrink-0 shadow-[0_4px_12px_rgba(0,0,0,0.3)]">
        <span>Time</span>
        <span>Level</span>
        <span>Source</span>
        <span>Message</span>
      </div>
      <div ref="scrollContainer" class="flex-grow overflow-y-auto p-8 space-y-1.5 no-scrollbar leading-relaxed scroll-smooth">
        <div v-for="(log, i) in appStore.filteredLogs" :key="i"
             class="grid grid-cols-[7rem_6rem_8rem_1fr] gap-4 border-l border-transparent hover:border-blue-500/40 hover:bg-white/[0.03] px-4 transition-all py-0.5 rounded-r-lg group/item">
          <span class="text-white/35 shrink-0 group-hover/item:text-white/55 font-bold tabular-nums">
            {{ log.Timestamp.split('T')[1]?.split('.')[0] }}
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
        <div v-if="appStore.filteredLogs.length === 0" class="text-white/25 italic py-20 text-center uppercase tracking-[0.8em] font-black">
          {{ appStore.logs.length === 0 ? 'No logs yet' : 'No logs match filter' }}
        </div>
      </div>
    </div>
  </div>
</template>
