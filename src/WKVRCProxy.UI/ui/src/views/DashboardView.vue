<script setup lang="ts">
import { computed } from 'vue'
import { useAppStore, TIER_DISPLAY } from '../stores/appStore'
import { useAnimatedNumber } from '../composables/useAnimatedNumber'
import { formatUptime } from '../utils/format'
import SuccessDonut from '../components/charts/SuccessDonut.vue'
import ActivitySparkline from '../components/charts/ActivitySparkline.vue'
import RequestsOverTime from '../components/charts/RequestsOverTime.vue'
import TierStackedBar from '../components/charts/TierStackedBar.vue'

const appStore = useAppStore()

const totalResolutions = computed(() => {
  return Object.values(appStore.status.stats.tierStats).reduce((a, b) => a + b, 0)
})

const tierPercentages = computed(() => {
  const total = totalResolutions.value
  if (total === 0) return {} as Record<string, number>
  const res: Record<string, number> = {}
  for (const [tier, count] of Object.entries(appStore.status.stats.tierStats)) {
    res[tier] = (count / total) * 100
  }
  return res
})

const recentHistory = computed(() => appStore.config.history.slice(0, 5))

// Animated counters — shared composable with direction flash.
const total = useAnimatedNumber(() => totalResolutions.value)
const tier1 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier1'] ?? 0)
const tier2 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier2'] ?? 0)
const tier3 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier3'] ?? 0)
const tier4 = useAnimatedNumber(() => appStore.status.stats.tierStats['tier4'] ?? 0)
const tierAnim: Record<string, { display: any; flashDirection: any }> = {
  tier1, tier2, tier3, tier4
}

const successRateAnim = useAnimatedNumber(() => appStore.successRate)

// Counts for the donut.
const successCount = computed(() => appStore.config.history.filter(h => h.Success).length)
const failedCount = computed(() => appStore.config.history.filter(h => !h.Success).length)

// Sparkline points — success/fail as 1/0 over last 20 entries, oldest first.
const sparkPoints = computed(() => {
  const recent = appStore.config.history.slice(0, 20).reverse()
  return recent.map(e => ({
    value: e.Success ? 1 : 0,
    success: e.Success,
    timestamp: e.Timestamp,
    label: e.OriginalUrl
  }))
})

function flashClass(dir: 'up' | 'down' | null): string {
  if (dir === 'up') return 'flash-up'
  if (dir === 'down') return 'flash-down'
  return ''
}

// --- Uptime ---
// Reads from a store-owned ticker so the value persists across view switches; previously
// each Dashboard mount restarted a local timer at 00:00:00.
const uptime = computed(() => formatUptime(appStore.uptimeMs))

const isResolving = computed(() => appStore.status.stats.activeCount > 0)

// Some status fields ship as `null` until the backend fully populates the first STATUS push;
// guard the .split() so a transient null doesn't blow up the template.
const cloudNodeShort = computed(() => appStore.status.stats.node?.split('.')[0] ?? '—')
</script>

<template>
  <div class="p-8 space-y-8">
    <!-- Header with Live Activity -->
    <div class="flex justify-between items-end"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <div class="space-y-2">
        <h2 class="text-3xl font-black uppercase tracking-tighter italic">Dashboard</h2>
        <div class="flex items-center gap-2 ml-1">
          <div class="flex items-center gap-1.5 px-2.5 py-1 bg-blue-500/10 border border-blue-500/20 rounded-full">
            <span class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-pulse"></span>
            <span class="text-[10px] font-black uppercase tracking-widest text-blue-400 italic">{{ appStore.status.message }}</span>
          </div>
          <p class="text-white/45 font-black uppercase tracking-[0.3em] text-[9px]">Live status</p>
        </div>
      </div>

      <div class="flex gap-6 items-center bg-white/[0.02] border border-white/5 px-6 py-4 rounded-2xl backdrop-blur-xl transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]">
        <div class="text-center relative">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Resolving</p>
          <p class="text-lg font-black italic relative z-10">{{ appStore.status.stats.activeCount }}</p>
          <span v-if="isResolving" class="absolute inset-0 rounded-xl bg-blue-500/20 animate-[pulse-glow_2s_ease-in-out_infinite] blur-md"></span>
        </div>
        <div class="w-[1px] h-6 bg-white/10"></div>
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Player</p>
          <p class="text-lg font-black italic text-blue-400">{{ appStore.status.stats.player }}</p>
        </div>
        <div class="w-[1px] h-6 bg-white/10"></div>
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Cloud</p>
          <p class="text-lg font-black italic text-purple-400 uppercase">{{ cloudNodeShort }}</p>
        </div>
        <div class="w-[1px] h-6 bg-white/10"></div>
        <div class="text-center">
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">Uptime</p>
          <p class="text-lg font-black italic text-emerald-400 tabular-nums">{{ uptime }}</p>
        </div>
      </div>
    </div>

    <!-- Tier Usage + Success Donut -->
    <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-8 relative overflow-hidden group transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]"
         v-motion :initial="{ opacity: 0, y: 16 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 100 } }">
      <div class="absolute -top-20 -right-20 w-64 h-64 bg-blue-500/5 blur-[100px] rounded-full group-hover:bg-blue-500/10 transition-all duration-1000"></div>

      <div class="flex justify-between items-center relative z-10">
        <div class="space-y-1">
          <h3 class="text-xl font-black uppercase tracking-tighter italic">Resolution Stats</h3>
          <p class="text-[9px] text-white/45 font-black uppercase tracking-widest">Requests handled per extraction tier</p>
        </div>
        <div class="text-right">
          <p class="text-3xl font-black italic text-white/90 tabular-nums" :class="flashClass(total.flashDirection.value)">{{ total.display.value }}</p>
          <p class="text-[8px] font-black uppercase tracking-widest text-white/45">Total Resolved</p>
        </div>
      </div>

      <div class="space-y-6 relative z-10">
        <template v-if="totalResolutions > 0">
          <!-- Chart.js stacked bar -->
          <div class="h-5 w-full">
            <TierStackedBar :tier-stats="appStore.status.stats.tierStats" />
          </div>

          <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
            <div v-for="(data, tier) in TIER_DISPLAY" :key="tier"
                 class="space-y-1 group/tier bg-white/[0.01] p-4 rounded-2xl border border-transparent hover:border-white/10 transition-all duration-500 hover:shadow-[0_0_20px_rgba(59,130,246,0.07)] hover:-translate-y-0.5">
              <div class="flex items-center gap-2">
                <div :class="[data.color, 'w-1.5 h-1.5 rounded-full']"></div>
                <span class="text-[10px] font-black uppercase tracking-widest text-white/55 italic group-hover/tier:text-white transition-colors">{{ data.short }}</span>
              </div>
              <div class="flex items-baseline gap-2">
                <p class="text-lg font-black italic text-white/90 tabular-nums"
                   :class="flashClass(tierAnim[tier]?.flashDirection.value ?? null)">
                  {{ tierAnim[tier]?.display.value ?? 0 }}
                </p>
                <p class="text-[9px] font-black uppercase tracking-widest text-white/35">{{ Math.round(tierPercentages[tier] || 0) }}%</p>
              </div>
            </div>
          </div>
        </template>
        <div v-else class="py-6 text-center uppercase tracking-[0.4em] text-white/20 text-[9px] font-black italic">No resolutions yet</div>

        <!-- Success Rate Donut (Chart.js) -->
        <div class="flex items-center gap-8 pt-2">
          <div class="relative w-[140px] h-[140px] shrink-0">
            <SuccessDonut :success="successCount" :failed="failedCount">
              <template #center>
                <div class="absolute inset-0 flex flex-col items-center justify-center pointer-events-none">
                  <span class="text-2xl font-black italic text-white/90 tabular-nums"
                        :class="flashClass(successRateAnim.flashDirection.value)">
                    {{ successRateAnim.display.value }}%
                  </span>
                  <span class="text-[8px] font-black uppercase tracking-widest text-white/35 italic">Success</span>
                </div>
              </template>
            </SuccessDonut>
          </div>
          <div class="space-y-2">
            <p class="text-sm font-black uppercase tracking-tighter italic text-white/80">Success Rate</p>
            <p class="text-[9px] text-white/40 font-black uppercase tracking-widest">Based on {{ appStore.config.history.length }} {{ appStore.config.history.length === 1 ? 'request' : 'requests' }}</p>
            <div class="flex items-center gap-3 mt-2">
              <div class="flex items-center gap-1.5">
                <span class="w-2 h-2 rounded-full bg-emerald-500"></span>
                <span class="text-[9px] font-black text-white/50 uppercase tracking-widest tabular-nums">{{ successCount }} OK</span>
              </div>
              <div class="flex items-center gap-1.5">
                <span class="w-2 h-2 rounded-full bg-red-500/70"></span>
                <span class="text-[9px] font-black text-white/50 uppercase tracking-widest tabular-nums">{{ failedCount }} Failed</span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Requests-over-time area chart -->
    <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-4 backdrop-blur-3xl transition-shadow duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)]"
         v-motion :initial="{ opacity: 0, y: 16 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 160 } }">
      <div class="flex justify-between items-start">
        <div class="space-y-1">
          <h3 class="text-lg font-black uppercase tracking-tighter italic">Requests · last 60 min</h3>
          <p class="text-[9px] text-white/45 font-black uppercase tracking-widest">Per-minute resolutions by tier</p>
        </div>
        <div class="flex items-center gap-3">
          <div v-for="(data, tier) in TIER_DISPLAY" :key="tier" class="flex items-center gap-1.5">
            <span :class="[data.color, 'w-2 h-2 rounded-full']"></span>
            <span class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">{{ data.short }}</span>
          </div>
        </div>
      </div>
      <div class="h-44 w-full">
        <RequestsOverTime :history="appStore.config.history" />
      </div>
    </div>

    <!-- Recent History & Logs -->
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-8"
         v-motion :initial="{ opacity: 0, y: 16 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 220 } }">
      <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-6 backdrop-blur-3xl group transition-all duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)] hover:-translate-y-1">
        <div class="flex justify-between items-center">
          <h3 class="text-lg font-black uppercase tracking-tighter italic">Recent Activity</h3>
          <button @click="appStore.activeTab = 'history'" class="text-[9px] font-black uppercase tracking-widest text-blue-400 hover:text-blue-300 transition-colors italic">View History</button>
        </div>

        <!-- Chart.js sparkline -->
        <div v-if="sparkPoints.length > 1" class="px-1">
          <div class="h-10 w-full">
            <ActivitySparkline :points="sparkPoints" />
          </div>
          <div class="flex justify-between mt-1">
            <span class="text-[7px] font-black uppercase tracking-widest text-white/25">Older</span>
            <span class="text-[7px] font-black uppercase tracking-widest text-white/25">Recent</span>
          </div>
        </div>

        <div class="space-y-3">
          <TransitionGroup name="list-row" tag="div" class="space-y-3">
            <div v-for="(entry, i) in recentHistory" :key="entry.Timestamp + i"
                 class="flex items-center gap-4 p-4 bg-white/[0.02] border border-white/5 rounded-2xl hover:bg-white/[0.04] hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.07)] transition-all duration-300 group/item">
              <div :class="[entry.Success ? 'bg-emerald-500/10 text-emerald-400' : 'bg-red-500/10 text-red-400', 'w-8 h-8 rounded-xl flex items-center justify-center shrink-0 border border-current/10']">
                <i :class="[entry.Success ? 'bi-check-lg' : 'bi-exclamation-triangle', 'text-sm']"></i>
              </div>
              <div class="min-w-0 flex-grow">
                <p class="text-[11px] font-black text-white/80 truncate italic group-hover/item:text-blue-400 transition-colors">{{ entry.OriginalUrl }}</p>
                <div class="flex items-center gap-2 mt-1">
                  <span class="text-[8px] font-black uppercase tracking-widest text-white/45 italic">{{ TIER_DISPLAY[entry.Tier?.split('-')[0] ?? '']?.short || entry.Tier || '—' }}</span>
                  <span class="w-0.5 h-0.5 bg-white/20 rounded-full"></span>
                  <span class="text-[8px] font-bold text-white/45 uppercase tabular-nums tracking-widest">{{ new Date(entry.Timestamp).toLocaleTimeString() }}</span>
                </div>
              </div>
              <div class="flex flex-col gap-1 items-end shrink-0">
                <span class="px-2 py-0.5 bg-white/5 rounded-lg text-[8px] font-black text-white/50 uppercase italic border border-white/5">{{ entry.Player }}</span>
                <span v-if="entry.IsLive" class="px-2 py-0.5 bg-green-500/20 rounded-lg text-[8px] font-black text-green-400 uppercase border border-green-500/30 flex items-center gap-1">
                  <span class="w-1 h-1 bg-green-400 rounded-full animate-pulse inline-block"></span>LIVE
                </span>
                <span v-else class="px-2 py-0.5 bg-white/5 rounded-lg text-[8px] font-black text-white/35 uppercase border border-white/5">VOD</span>
              </div>
            </div>
          </TransitionGroup>
          <div v-if="recentHistory.length === 0" class="py-10 text-center uppercase tracking-[0.4em] text-white/25 text-[9px] font-black italic">No activity yet</div>
        </div>
      </div>

      <div class="bg-white/[0.02] border border-white/5 rounded-[32px] p-8 space-y-6 backdrop-blur-3xl group transition-all duration-500 hover:shadow-[0_0_30px_rgba(59,130,246,0.1)] hover:-translate-y-1">
        <div class="flex justify-between items-center">
          <h3 class="text-lg font-black uppercase tracking-tighter italic">Recent Logs</h3>
          <button @click="appStore.activeTab = 'logs'" class="text-[9px] font-black uppercase tracking-widest text-white/45 hover:text-white/70 transition-colors italic">Full Logs</button>
        </div>

        <div class="space-y-2 font-mono text-[9px]">
          <div v-for="(log, i) in appStore.logs.slice(-10).reverse()"
               :key="(log.Timestamp ?? '') + '-' + (log.Source ?? '') + '-' + i"
               class="flex gap-3 px-3 py-1.5 border-l-2 border-white/5 hover:border-blue-500/40 hover:bg-white/[0.02] transition-all">
            <span class="text-white/35 shrink-0 font-bold tabular-nums">{{ log.Timestamp?.split('T')[1]?.split('.')[0] ?? '--:--:--' }}</span>
            <span class="text-white/65 break-all leading-normal">{{ log.Message }}</span>
          </div>
          <div v-if="appStore.logs.length === 0" class="py-6 text-center uppercase tracking-[0.4em] text-white/25 text-[8px] font-black italic">
            No logs yet · play a video in VRChat
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
@keyframes pulse-glow {
  0%, 100% { opacity: 0.3; transform: scale(1); }
  50%      { opacity: 0.7; transform: scale(1.15); }
}

/* Direction flash: value just ticked up → green flash; down → red flash.
   Kept short so it hints without distracting. */
@keyframes flash-up   { 0% { color: rgb(52,211,153); text-shadow: 0 0 12px rgba(52,211,153,0.6); } 100% {} }
@keyframes flash-down { 0% { color: rgb(248,113,113); text-shadow: 0 0 12px rgba(248,113,113,0.6); } 100% {} }
.flash-up   { animation: flash-up   900ms ease-out; }
.flash-down { animation: flash-down 900ms ease-out; }

.list-row-enter-active { transition: all 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }
.list-row-leave-active { transition: all 250ms ease-in; }
.list-row-enter-from   { opacity: 0; transform: translateY(-8px) scale(0.98); }
.list-row-leave-to     { opacity: 0; transform: translateX(16px); }
.list-row-move         { transition: transform 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }
</style>
