<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useAppStore } from '../stores/appStore'
import { useAnimatedNumber } from '../composables/useAnimatedNumber'
import RelayBandwidthChart from '../components/charts/RelayBandwidthChart.vue'
import StatusCodeDonut from '../components/charts/StatusCodeDonut.vue'

const appStore = useAppStore()

const clearEvents = () => {
  appStore.relayEvents = []
}

const formatBytes = (bytes: number) => {
  if (bytes === 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i]
}

const totalRequests = computed(() => appStore.relayEvents.length)

const successRate = computed(() => {
  const total = appStore.relayEvents.length
  if (total === 0) return 0
  const successes = appStore.relayEvents.filter(e => e.statusCode >= 200 && e.statusCode < 300).length
  return Math.round((successes / total) * 1000) / 10
})

const totalBytes = computed(() =>
  appStore.relayEvents.reduce((acc, e) => acc + (e.bytesTransferred || 0), 0)
)

const activeCount = computed(() => appStore.relayEvents.filter(e => e.statusCode === 0).length)

const totalAnim = useAnimatedNumber(() => totalRequests.value)
const activeAnim = useAnimatedNumber(() => activeCount.value)

function flashClass(dir: 'up' | 'down' | null) {
  if (dir === 'up') return 'flash-up'
  if (dir === 'down') return 'flash-down'
  return ''
}

const methodClasses = (method: string) => {
  switch (method?.toUpperCase()) {
    case 'GET':  return 'bg-blue-500/10 text-blue-300 border-blue-500/20'
    case 'POST': return 'bg-purple-500/10 text-purple-300 border-purple-500/20'
    default:     return 'bg-white/5 text-white/70 border-white/10'
  }
}

// Freshness: the most recently added event id gets a 1.5s gold highlight.
const freshId = ref<string | null>(null)
let freshTimer = 0
watch(() => appStore.relayEvents[0]?.id, (id) => {
  if (!id) return
  freshId.value = id
  if (freshTimer) window.clearTimeout(freshTimer)
  freshTimer = window.setTimeout(() => { freshId.value = null }, 1500)
})
</script>

<template>
  <div class="h-full flex flex-col p-8 lg:p-12 max-w-7xl mx-auto w-full">
    <div class="flex justify-between items-end mb-8"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <div>
        <h2 class="text-4xl font-black uppercase tracking-tighter mb-2 italic">Traffic <span class="text-blue-500">Monitor</span></h2>
        <p class="text-xs text-white/40 font-bold tracking-[0.2em] uppercase">Live localhost relay telemetry</p>
      </div>

      <button @click="clearEvents" class="bg-white/5 border border-white/10 hover:bg-white/10 hover:border-blue-500/30 text-white/50 hover:text-white px-6 py-2.5 rounded-xl transition-all duration-300 font-black text-[10px] uppercase tracking-widest flex items-center gap-2 group backdrop-blur-xl italic active:scale-95">
        <i class="bi bi-trash-fill text-blue-500 group-hover:scale-110 transition-transform"></i>
        Clear
      </button>
    </div>

    <!-- Stats row -->
    <div class="grid grid-cols-4 gap-4 mb-6"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 80 } }">
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.08)]">
        <div class="text-2xl font-black italic tabular-nums" :class="flashClass(totalAnim.flashDirection.value)">{{ totalAnim.display.value }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Total Requests</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.08)]">
        <div class="text-2xl font-black italic tabular-nums">{{ successRate.toFixed(1) }}<span class="text-sm text-white/40">%</span></div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Success Rate</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.08)]">
        <div class="text-2xl font-black italic tabular-nums">{{ formatBytes(totalBytes) }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Total Transferred</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.08)]">
        <div class="text-2xl font-black italic tabular-nums flex items-center gap-2" :class="flashClass(activeAnim.flashDirection.value)">
          {{ activeAnim.display.value }}
          <span v-if="activeCount > 0" class="w-1.5 h-1.5 bg-blue-400 rounded-full animate-pulse inline-block"></span>
        </div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45 italic mt-1">Active</div>
      </div>
    </div>

    <!-- Bandwidth + status breakdown -->
    <div class="grid grid-cols-1 lg:grid-cols-3 gap-4 mb-6"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 140 } }">
      <div class="lg:col-span-2 bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <div class="flex justify-between items-center mb-3">
          <div>
            <p class="text-[10px] font-black uppercase tracking-widest text-white/65 italic">Bandwidth · last 60s</p>
            <p class="text-[8px] font-black uppercase tracking-widest text-white/30 italic">Bytes/sec · Requests/sec</p>
          </div>
          <div class="flex gap-3">
            <span class="flex items-center gap-1.5 text-[8px] font-black uppercase tracking-widest text-white/50 italic">
              <span class="w-2 h-0.5 bg-blue-500 inline-block"></span>Bytes
            </span>
            <span class="flex items-center gap-1.5 text-[8px] font-black uppercase tracking-widest text-white/50 italic">
              <span class="w-2 h-0.5 bg-purple-500 inline-block" style="border-top: 1.5px dashed rgb(168,85,247); background: transparent;"></span>Req
            </span>
          </div>
        </div>
        <div class="h-36 w-full">
          <RelayBandwidthChart :events="appStore.relayEvents" />
        </div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <p class="text-[10px] font-black uppercase tracking-widest text-white/65 italic mb-2">Status Codes</p>
        <div class="relative h-36 w-full flex items-center justify-center">
          <StatusCodeDonut :events="appStore.relayEvents" />
        </div>
        <div class="flex flex-wrap gap-x-3 gap-y-1 mt-3 text-[8px] font-black uppercase tracking-widest">
          <span class="flex items-center gap-1 text-white/55"><span class="w-1.5 h-1.5 rounded-full bg-emerald-500"></span>2xx</span>
          <span class="flex items-center gap-1 text-white/55"><span class="w-1.5 h-1.5 rounded-full bg-orange-500"></span>3xx</span>
          <span class="flex items-center gap-1 text-white/55"><span class="w-1.5 h-1.5 rounded-full bg-red-500"></span>4xx</span>
          <span class="flex items-center gap-1 text-white/55"><span class="w-1.5 h-1.5 rounded-full bg-purple-500"></span>5xx</span>
          <span class="flex items-center gap-1 text-white/55"><span class="w-1.5 h-1.5 rounded-full bg-blue-500"></span>Pending</span>
        </div>
      </div>
    </div>

    <!-- Live table -->
    <div class="flex-grow bg-[#0a0a0c]/80 backdrop-blur-3xl border border-white/5 rounded-[32px] overflow-hidden flex flex-col shadow-2xl relative group"
         v-motion :initial="{ opacity: 0, y: 16 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 200 } }">
      <div class="absolute inset-0 bg-gradient-to-b from-blue-500/5 to-transparent opacity-0 group-hover:opacity-100 transition-opacity duration-1000 pointer-events-none"></div>

      <div class="grid grid-cols-12 gap-4 px-8 py-5 border-b border-white/5 bg-black/40 backdrop-blur-md text-[9px] font-black uppercase tracking-[0.2em] text-white/55 sticky top-0 z-10 italic">
        <div class="col-span-2">Time</div>
        <div class="col-span-1">Method</div>
        <div class="col-span-6">Target</div>
        <div class="col-span-1 text-center">Status</div>
        <div class="col-span-2 text-right">Transferred</div>
      </div>

      <div class="flex-grow overflow-y-auto no-scrollbar relative z-0">
        <div v-if="appStore.relayEvents.length === 0" class="h-full flex flex-col items-center justify-center text-white/20 py-24">
          <div class="relative mb-6">
            <i class="bi bi-activity text-6xl opacity-30 animate-pulse"></i>
            <div class="absolute inset-0 bg-blue-500/10 blur-3xl rounded-full"></div>
          </div>
          <p class="text-xs font-black uppercase tracking-widest italic mb-2">Awaiting traffic...</p>
          <p class="text-[10px] text-white/25 font-medium max-w-xs text-center leading-relaxed">Relay requests will appear here in real time as they pass through the proxy.</p>
        </div>

        <TransitionGroup v-else name="relay-row" tag="div" class="flex flex-col">
          <div v-for="evt in appStore.relayEvents" :key="evt.id"
               :class="['grid grid-cols-12 gap-4 px-8 py-4 border-b border-white/5 hover:bg-white/[0.02] transition-colors items-center',
                        freshId === evt.id ? 'fresh-row' : '']">

            <div class="col-span-2 font-mono text-[10px] text-white/65">
              {{ new Date(evt.timestamp).toLocaleTimeString() }}
            </div>

            <div class="col-span-1">
              <span class="px-2 py-1 rounded text-[8px] font-black uppercase tracking-widest border" :class="methodClasses(evt.method)">
                {{ evt.method }}
              </span>
            </div>

            <div class="col-span-6 truncate font-mono text-[10px] text-white/85" :title="evt.targetUrl">
              {{ evt.targetUrl }}
            </div>

            <div class="col-span-1 flex justify-center">
              <div v-if="evt.statusCode === 0" class="w-2 h-2 rounded-full bg-blue-500 shadow-[0_0_10px_rgba(59,130,246,0.8)] animate-pulse" title="Pending"></div>
              <span v-else-if="evt.statusCode >= 200 && evt.statusCode < 300" class="bg-emerald-500/10 text-emerald-400 px-2 py-0.5 rounded-lg font-black text-[10px]" title="Success">{{ evt.statusCode }}</span>
              <span v-else-if="evt.statusCode >= 300 && evt.statusCode < 400" class="bg-orange-500/10 text-orange-400 px-2 py-0.5 rounded-lg font-black text-[10px]">{{ evt.statusCode }}</span>
              <span v-else class="bg-red-500/10 text-red-400 px-2 py-0.5 rounded-lg font-black text-[10px]" title="Error">{{ evt.statusCode }}</span>
            </div>

            <div class="col-span-2 text-right font-mono text-[10px] text-white/65">
              {{ formatBytes(evt.bytesTransferred) }}
            </div>
          </div>
        </TransitionGroup>
      </div>
    </div>
  </div>
</template>

<style scoped>
.relay-row-enter-active { transition: all 350ms cubic-bezier(0.34, 1.56, 0.64, 1); }
.relay-row-leave-active { transition: all 200ms ease-in; position: absolute; }
.relay-row-enter-from   { opacity: 0; transform: translateY(-12px); }
.relay-row-leave-to     { opacity: 0; transform: translateX(20px); }
.relay-row-move         { transition: transform 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }

/* Gold fade — signals "just arrived" for 1.5s without being loud. */
@keyframes fresh-row-fade {
  0%   { background-color: rgba(251, 191, 36, 0.12); box-shadow: inset 3px 0 0 rgba(251, 191, 36, 0.6); }
  100% { background-color: transparent; box-shadow: inset 3px 0 0 transparent; }
}
.fresh-row { animation: fresh-row-fade 1500ms ease-out; }

@keyframes flash-up   { 0% { color: rgb(52,211,153); text-shadow: 0 0 12px rgba(52,211,153,0.6); } 100% {} }
@keyframes flash-down { 0% { color: rgb(248,113,113); text-shadow: 0 0 12px rgba(248,113,113,0.6); } 100% {} }
.flash-up   { animation: flash-up   900ms ease-out; }
.flash-down { animation: flash-down 900ms ease-out; }
</style>
