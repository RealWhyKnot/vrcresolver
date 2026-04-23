<script setup lang="ts">
import { ref, computed } from 'vue'
import { useAppStore, TIER_DISPLAY } from '../stores/appStore'
import { useAnimatedNumber } from '../composables/useAnimatedNumber'
import SuccessDonut from '../components/charts/SuccessDonut.vue'
import ResolutionHistogram from '../components/charts/ResolutionHistogram.vue'
import SkeletonRow from '../components/SkeletonRow.vue'

const appStore = useAppStore()

const searchQuery = ref('')
const selectedTier = ref<string | null>(null)

const tierFilterOptions = [
  { key: null, label: 'All' },
  ...Object.entries(TIER_DISPLAY).map(([key, data]) => ({ key, label: data.short }))
]

const totalCount = computed(() => appStore.config.history.length)
const successCount = computed(() => appStore.config.history.filter(e => e.Success).length)
const failedCount = computed(() => appStore.config.history.filter(e => !e.Success).length)
const liveCount = computed(() => appStore.config.history.filter(e => e.IsLive).length)

const totalAnim = useAnimatedNumber(() => totalCount.value)
const successAnim = useAnimatedNumber(() => successCount.value)
const failedAnim = useAnimatedNumber(() => failedCount.value)
const liveAnim = useAnimatedNumber(() => liveCount.value)

function flashClass(dir: 'up' | 'down' | null) {
  if (dir === 'up') return 'flash-up'
  if (dir === 'down') return 'flash-down'
  return ''
}

const filteredHistory = computed(() => {
  let list = appStore.config.history
  if (selectedTier.value) {
    list = list.filter(e => e.Tier.split('-')[0] === selectedTier.value)
  }
  if (searchQuery.value.trim()) {
    const q = searchQuery.value.trim().toLowerCase()
    list = list.filter(e =>
      e.OriginalUrl.toLowerCase().includes(q) ||
      e.ResolvedUrl.toLowerCase().includes(q)
    )
  }
  return list
})

function setTierFilter(tier: string | null) {
  selectedTier.value = selectedTier.value === tier ? null : tier
}

function formatTime(ts: string) {
  return new Date(ts).toLocaleTimeString()
}

function truncate(str: string, len: number) {
  if (str.length <= len) return str
  return str.substring(0, len) + '...'
}

const isInitialLoad = computed(() => !appStore.isBridgeReady)
</script>

<template>
  <div class="p-8 space-y-8">
    <!-- Header row -->
    <div class="flex items-start justify-between"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <div class="space-y-2">
        <h2 class="text-3xl font-black uppercase tracking-tighter italic">Resolution <span class="text-blue-500">History</span></h2>
        <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">All resolved requests</p>
      </div>
      <span class="text-[8px] font-black uppercase tracking-widest text-white/25 italic tabular-nums pt-2">
        {{ filteredHistory.length }} of {{ totalCount }} entries
      </span>
    </div>

    <!-- Stats summary row -->
    <div class="grid grid-cols-4 gap-3"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 80 } }">
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.07)]">
        <div class="text-lg font-black italic tabular-nums text-white/80" :class="flashClass(totalAnim.flashDirection.value)">{{ totalAnim.display.value }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Total</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(16,185,129,0.07)]">
        <div class="text-lg font-black italic tabular-nums text-emerald-400" :class="flashClass(successAnim.flashDirection.value)">{{ successAnim.display.value }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Success</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(239,68,68,0.07)]">
        <div class="text-lg font-black italic tabular-nums text-red-400" :class="flashClass(failedAnim.flashDirection.value)">{{ failedAnim.display.value }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Failed</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(34,197,94,0.07)]">
        <div class="text-lg font-black italic tabular-nums text-green-400 flex items-center gap-2" :class="flashClass(liveAnim.flashDirection.value)">
          {{ liveAnim.display.value }}
          <span v-if="liveCount > 0" class="w-1.5 h-1.5 bg-green-400 rounded-full animate-pulse inline-block"></span>
        </div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Live</div>
      </div>
    </div>

    <!-- Charts row: success donut + resolution histogram -->
    <div v-if="totalCount > 0" class="grid grid-cols-1 lg:grid-cols-3 gap-4"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 140 } }">
      <div class="bg-white/[0.02] border border-white/5 rounded-2xl p-5 backdrop-blur-xl flex items-center gap-6">
        <div class="relative w-32 h-32 shrink-0">
          <SuccessDonut :success="successCount" :failed="failedCount">
            <template #center>
              <div class="absolute inset-0 flex flex-col items-center justify-center pointer-events-none">
                <span class="text-xl font-black italic text-white/90 tabular-nums">{{ appStore.successRate }}%</span>
                <span class="text-[7px] font-black uppercase tracking-widest text-white/35 italic">Success</span>
              </div>
            </template>
          </SuccessDonut>
        </div>
        <div class="space-y-1">
          <p class="text-[10px] font-black uppercase tracking-widest text-white/65 italic">Outcome</p>
          <p class="text-[8px] font-black uppercase tracking-widest text-white/35">{{ totalCount }} total</p>
        </div>
      </div>
      <div class="lg:col-span-2 bg-white/[0.02] border border-white/5 rounded-2xl p-5 backdrop-blur-xl">
        <div class="flex justify-between items-start mb-2">
          <div>
            <p class="text-[10px] font-black uppercase tracking-widest text-white/65 italic">Resolution Mix</p>
            <p class="text-[8px] font-black uppercase tracking-widest text-white/35">Distribution of video heights</p>
          </div>
        </div>
        <div class="h-32 w-full">
          <ResolutionHistogram :history="appStore.config.history" />
        </div>
      </div>
    </div>

    <!-- Filter toolbar -->
    <div class="flex items-center gap-3 flex-wrap"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 200 } }">
      <div class="flex gap-1.5 flex-wrap">
        <button
          v-for="opt in tierFilterOptions"
          :key="String(opt.key)"
          @click="setTierFilter(opt.key)"
          :class="['px-3 py-1.5 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic border active:scale-95',
                   selectedTier === opt.key || (opt.key === null && selectedTier === null)
                     ? 'bg-blue-600/80 text-white border-blue-500/50 shadow-[0_0_10px_rgba(37,99,235,0.25)]'
                     : 'bg-white/[0.03] text-white/40 hover:text-white/70 border-white/5']">
          {{ opt.label }}
        </button>
      </div>

      <input
        v-model="searchQuery"
        type="text"
        placeholder="Filter by URL..."
        class="bg-white/[0.03] border border-white/10 rounded-xl px-4 py-2 text-[10px] font-mono text-white/70 focus:outline-none focus:border-blue-500/50 placeholder:text-white/20 transition-all w-48" />
    </div>

    <!-- Table -->
    <div class="bg-white/[0.02] border border-white/5 rounded-3xl overflow-hidden backdrop-blur-3xl shadow-2xl"
         v-motion :initial="{ opacity: 0, y: 16 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 260 } }">
      <template v-if="isInitialLoad && totalCount === 0">
        <div class="p-8">
          <SkeletonRow :count="5" height="h-14" />
        </div>
      </template>
      <table v-else class="w-full text-left text-[10px]">
        <thead class="bg-white/[0.01] text-white/50 font-black uppercase tracking-[0.2em]">
          <tr>
            <th class="px-6 py-4">Time</th>
            <th class="px-6 py-4">Source URL</th>
            <th class="px-6 py-4">Tier</th>
            <th class="px-6 py-4">Type</th>
            <th class="px-6 py-4">Resolution</th>
            <th class="px-6 py-4 text-right">Result</th>
          </tr>
        </thead>
        <TransitionGroup tag="tbody" name="history-row" class="divide-y divide-white/5 font-bold">
          <tr v-for="(entry, i) in filteredHistory" :key="entry.Timestamp + i" class="hover:bg-white/[0.03] transition-all duration-300 group">
            <td class="px-6 py-4 text-white/50 font-mono tabular-nums">{{ formatTime(entry.Timestamp) }}</td>
            <td class="px-6 py-4">
              <div class="flex flex-col gap-1">
                <span class="text-white/80 group-hover:text-blue-400 transition-colors tracking-tight truncate max-w-md">{{ entry.OriginalUrl }}</span>
                <span class="text-[8px] text-white/35 font-mono italic group-hover:text-white/50 transition-colors">{{ truncate(entry.ResolvedUrl, 80) }}</span>
              </div>
            </td>
            <td class="px-6 py-4">
              <div class="flex items-center gap-2">
                <span :title="TIER_DISPLAY[entry.Tier.split('-')[0]]?.long" class="px-3 py-1 bg-white/5 rounded-lg text-[8px] font-black uppercase tracking-widest border border-white/5 group-hover:border-blue-500/20 transition-all italic">
                  {{ TIER_DISPLAY[entry.Tier.split('-')[0]]?.short || entry.Tier }}
                </span>
                <i :title="entry.Player" :class="entry.Player === 'AVPro' ? 'bi-camera-video-fill text-purple-400/70' : 'bi-play-circle-fill text-blue-400/70'" class="bi text-xs"></i>
              </div>
            </td>
            <td class="px-6 py-4">
              <span v-if="entry.IsLive" class="px-3 py-1 bg-green-500/15 rounded-lg text-[8px] font-black uppercase border border-green-500/25 text-green-400 flex items-center gap-1.5 w-fit">
                <span class="w-1 h-1 bg-green-400 rounded-full animate-pulse inline-block"></span>LIVE
              </span>
              <span v-else class="px-3 py-1 bg-white/5 rounded-lg text-[8px] font-black uppercase border border-white/5 text-white/45 italic">VOD</span>
            </td>
            <td class="px-6 py-4">
              <span v-if="entry.ResolutionHeight" :title="entry.ResolutionWidth && entry.ResolutionHeight ? (entry.ResolutionWidth + 'x' + entry.ResolutionHeight + (entry.Vcodec ? ' ' + entry.Vcodec : '')) : undefined"
                    class="px-3 py-1 rounded-lg text-[8px] font-black uppercase border italic tabular-nums"
                    :class="entry.ResolutionHeight >= 1080 ? 'bg-blue-500/15 border-blue-500/25 text-blue-400'
                          : entry.ResolutionHeight >= 720  ? 'bg-emerald-500/15 border-emerald-500/25 text-emerald-400'
                          : 'bg-amber-500/15 border-amber-500/25 text-amber-400'">
                {{ entry.ResolutionHeight }}p
              </span>
              <span v-else class="text-white/25 font-mono text-[9px]">—</span>
            </td>
            <td class="px-6 py-4 text-right">
              <div class="inline-flex items-center gap-2 justify-end">
                <span v-if="entry.Success"
                      :title="entry.PlaybackVerified === true ? 'Playback verified — AVPro accepted the URL'
                             : entry.PlaybackVerified === false ? 'Playback FAILED — AVPro rejected the URL after resolution'
                             : 'Playback pending or passthrough (not verified)'"
                      :class="['w-1.5 h-1.5 rounded-full shrink-0',
                               entry.PlaybackVerified === true ? 'bg-emerald-400 shadow-[0_0_6px_rgba(52,211,153,0.6)]'
                               : entry.PlaybackVerified === false ? 'bg-red-400 shadow-[0_0_6px_rgba(248,113,113,0.6)]'
                               : 'bg-white/20']"></span>
                <span :class="entry.Success ? (entry.PlaybackVerified === false ? 'text-red-400' : 'text-emerald-400') : 'text-red-400'"
                      class="font-black italic text-[9px]">
                  {{ entry.Success ? (entry.PlaybackVerified === false ? 'BAD URL' : 'OK') : 'FAILED' }}
                </span>
              </div>
            </td>
          </tr>
          <tr v-if="filteredHistory.length === 0 && totalCount === 0" key="empty-none">
            <td colspan="6" class="px-6 py-24 text-center">
              <div class="flex flex-col items-center gap-4 animate-pulse">
                <i class="bi bi-clock-history text-4xl text-white/10"></i>
                <div class="space-y-2">
                  <p class="text-white/25 font-black uppercase tracking-[0.5em] italic text-[10px]">No Records Yet</p>
                  <p class="text-white/15 text-[9px] font-mono italic max-w-xs mx-auto">Resolution history will appear here once VRChat requests are processed through the proxy.</p>
                </div>
              </div>
            </td>
          </tr>
          <tr v-else-if="filteredHistory.length === 0" key="empty-filtered">
            <td colspan="6" class="px-6 py-16 text-center">
              <div class="flex flex-col items-center gap-3">
                <i class="bi bi-funnel text-2xl text-white/10"></i>
                <p class="text-white/25 font-black uppercase tracking-[0.3em] italic text-[9px]">No entries match current filters</p>
              </div>
            </td>
          </tr>
        </TransitionGroup>
      </table>
    </div>
  </div>
</template>

<style scoped>
.history-row-enter-active { transition: all 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }
.history-row-leave-active { transition: all 200ms ease-in; }
.history-row-enter-from   { opacity: 0; transform: translateY(-8px); }
.history-row-leave-to     { opacity: 0; transform: translateX(16px); }
.history-row-move         { transition: transform 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }

@keyframes flash-up   { 0% { color: rgb(52,211,153); text-shadow: 0 0 12px rgba(52,211,153,0.6); } 100% {} }
@keyframes flash-down { 0% { color: rgb(248,113,113); text-shadow: 0 0 12px rgba(248,113,113,0.6); } 100% {} }
.flash-up   { animation: flash-up   900ms ease-out; }
.flash-down { animation: flash-down 900ms ease-out; }
</style>
