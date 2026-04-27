<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useAppStore } from '../stores/appStore'
import type { BypassMemoryRow, BypassMemoryEntry } from '../stores/appStore'
import { useAnimatedNumber } from '../composables/useAnimatedNumber'
import SkeletonRow from '../components/SkeletonRow.vue'

const appStore = useAppStore()

onMounted(() => {
  appStore.refreshBypassMemory()
  appStore.refreshYtDlpUpdate()
})

const hostCount = computed(() => appStore.bypassMemory.length)
const totalEntries = computed(() =>
  appStore.bypassMemory.reduce((sum: number, row: BypassMemoryRow) => sum + row.entries.length, 0)
)

const hostAnim = useAnimatedNumber(() => hostCount.value)
const entriesAnim = useAnimatedNumber(() => totalEntries.value)

function flashClass(dir: 'up' | 'down' | null) {
  if (dir === 'up') return 'flash-up'
  if (dir === 'down') return 'flash-down'
  return ''
}

const rows = computed(() => {
  return [...appStore.bypassMemory].sort((a: BypassMemoryRow, b: BypassMemoryRow) => {
    const aBest = bestEntry(a.entries)
    const bBest = bestEntry(b.entries)
    const aTime = aBest ? new Date(aBest.lastSuccess).getTime() : 0
    const bTime = bBest ? new Date(bBest.lastSuccess).getTime() : 0
    return bTime - aTime
  })
})

function bestEntry(entries: BypassMemoryEntry[]): BypassMemoryEntry | null {
  if (entries.length === 0) return null
  let best: BypassMemoryEntry = entries[0]
  for (const e of entries) {
    if (e.netScore > best.netScore) best = e
    else if (e.netScore === best.netScore &&
             new Date(e.lastSuccess).getTime() > new Date(best.lastSuccess).getTime()) best = e
  }
  return best
}

function otherEntries(entries: BypassMemoryEntry[]): BypassMemoryEntry[] {
  const best = bestEntry(entries)
  return best ? entries.filter(e => e.strategy !== best.strategy) : []
}

function formatHost(key: string): string {
  return key.split(':')[0] ?? key
}

function streamKind(key: string): string {
  return key.split(':')[1] ?? ''
}

function relative(iso: string | null | undefined): string {
  if (!iso) return '—'
  const t = new Date(iso).getTime()
  if (!Number.isFinite(t) || t <= 0) return '—'
  const diff = Date.now() - t
  if (diff < 0) return 'just now'
  const s = Math.floor(diff / 1000)
  if (s < 60) return s + 's ago'
  const m = Math.floor(s / 60)
  if (m < 60) return m + 'm ago'
  const h = Math.floor(m / 60)
  if (h < 24) return h + 'h ago'
  const d = Math.floor(h / 24)
  return d + 'd ago'
}

function isDemoted(entry: BypassMemoryEntry): boolean {
  return entry.consecutiveFailures >= 3
}

function isStale(entry: BypassMemoryEntry): boolean {
  const t = new Date(entry.lastSuccess).getTime()
  if (!Number.isFinite(t) || t <= 0) return true
  return Date.now() - t > 30 * 24 * 60 * 60 * 1000
}

function winRatio(entry: BypassMemoryEntry): number {
  const total = entry.successCount + entry.failureCount
  if (total === 0) return 0
  return entry.successCount / total
}

function forget(key: string) {
  appStore.forgetBypassKey(key)
}

function updateStatusColor(s: string): string {
  switch (s) {
    case 'Updated':
    case 'UpToDate': return 'text-emerald-400'
    case 'UpdateAvailable':
    case 'Downloading':
    case 'Checking': return 'text-blue-400'
    case 'Failed': return 'text-red-400'
    case 'Disabled': return 'text-white/30'
    default: return 'text-white/50'
  }
}

function updateStatusLabel(s: string): string {
  switch (s) {
    case 'UpToDate': return 'Up to date'
    case 'UpdateAvailable': return 'Update available'
    default: return s
  }
}

const isLoading = computed(() => !appStore.isBridgeReady && appStore.bypassMemory.length === 0)
const isDownloading = computed(() => appStore.ytDlpUpdate.status === 'Downloading' || appStore.ytDlpUpdate.status === 'Checking')
</script>

<template>
  <div class="p-8 space-y-8">
    <!-- Header -->
    <div class="flex items-start justify-between"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <div class="space-y-2">
        <h2 class="text-3xl font-black uppercase tracking-tighter italic">Bypass <span class="text-blue-500">Health</span></h2>
        <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">What the resolver has learned</p>
      </div>
      <button
        @click="appStore.refreshBypassMemory()"
        class="px-4 py-2 rounded-xl text-[8px] font-black uppercase tracking-widest bg-white/[0.03] hover:bg-white/[0.06] border border-white/5 text-white/60 hover:text-white transition-all italic active:scale-95">
        <i class="bi bi-arrow-clockwise mr-1"></i> Refresh
      </button>
    </div>

    <!-- yt-dlp updater card -->
    <div class="bg-white/[0.02] border border-white/5 rounded-3xl p-6 backdrop-blur-3xl space-y-3 relative overflow-hidden"
         :class="isDownloading ? 'ytdlp-shimmer' : ''"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 100 } }">
      <div class="flex items-start justify-between gap-6 relative z-10">
        <div class="space-y-1.5">
          <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/40">yt-dlp Updater</p>
          <p class="font-mono text-[11px] text-white/80">
            local <span class="text-white/50">{{ appStore.ytDlpUpdate.localVersion || '—' }}</span>
            <span v-if="appStore.ytDlpUpdate.remoteVersion" class="text-white/30"> / latest </span>
            <span v-if="appStore.ytDlpUpdate.remoteVersion" class="text-white/70">{{ appStore.ytDlpUpdate.remoteVersion }}</span>
          </p>
          <p v-if="appStore.ytDlpUpdate.detail" class="text-[10px] text-white/40 italic">{{ appStore.ytDlpUpdate.detail }}</p>
        </div>
        <span :class="['text-[9px] font-black uppercase tracking-widest italic', updateStatusColor(appStore.ytDlpUpdate.status)]">
          <span v-if="isDownloading" class="inline-block w-1.5 h-1.5 rounded-full bg-current mr-1.5 animate-pulse"></span>
          {{ updateStatusLabel(appStore.ytDlpUpdate.status) }}
        </span>
      </div>
    </div>

    <!-- Stats row -->
    <div class="grid grid-cols-2 gap-3"
         v-motion :initial="{ opacity: 0, y: 12 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 160 } }">
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.07)]">
        <div class="text-lg font-black italic tabular-nums text-white/80" :class="flashClass(hostAnim.flashDirection.value)">{{ hostAnim.display.value }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Hosts Tracked</div>
      </div>
      <div class="bg-white/[0.03] border border-white/5 rounded-xl px-4 py-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-[0_0_20px_rgba(59,130,246,0.07)]">
        <div class="text-lg font-black italic tabular-nums text-blue-400" :class="flashClass(entriesAnim.flashDirection.value)">{{ entriesAnim.display.value }}</div>
        <div class="text-[8px] font-black uppercase tracking-widest text-white/45">Strategy Entries</div>
      </div>
    </div>

    <!-- Table -->
    <div class="bg-white/[0.02] border border-white/5 rounded-3xl overflow-hidden backdrop-blur-3xl shadow-2xl"
         v-motion :initial="{ opacity: 0, y: 16 }" :enter="{ opacity: 1, y: 0, transition: { duration: 500, delay: 220 } }">
      <template v-if="isLoading">
        <div class="p-8">
          <SkeletonRow :count="4" height="h-14" />
        </div>
      </template>
      <table v-else class="w-full text-left text-[10px]">
        <thead class="bg-white/[0.01] text-white/50 font-black uppercase tracking-[0.2em]">
          <tr>
            <th class="px-6 py-4">Host</th>
            <th class="px-6 py-4">Best Strategy</th>
            <th class="px-6 py-4">Win Ratio</th>
            <th class="px-6 py-4 text-right">W / L</th>
            <th class="px-6 py-4 text-right">Last Success</th>
            <th class="px-6 py-4 text-right">Action</th>
          </tr>
        </thead>
        <TransitionGroup tag="tbody" name="bypass-row" class="divide-y divide-white/5 font-bold">
          <template v-for="row in rows" :key="row.key">
            <tr class="hover:bg-white/[0.03] transition-all duration-300 group">
              <td class="px-6 py-4">
                <div class="flex flex-col gap-1">
                  <span class="text-white/80 tracking-tight">{{ formatHost(row.key) }}</span>
                  <span class="text-[8px] text-white/35 font-mono italic uppercase tracking-widest">{{ streamKind(row.key) }}</span>
                </div>
              </td>
              <td class="px-6 py-4">
                <template v-if="bestEntry(row.entries)">
                  <div class="flex items-center gap-2 flex-wrap">
                    <span class="px-3 py-1 bg-white/5 rounded-lg text-[8px] font-black uppercase tracking-widest border border-white/5 italic font-mono">
                      {{ bestEntry(row.entries)!.strategy }}
                    </span>
                    <span v-if="isDemoted(bestEntry(row.entries)!)"
                          class="px-2 py-0.5 rounded-md text-[7px] font-black uppercase tracking-widest bg-amber-500/15 border border-amber-500/25 text-amber-400">
                      Demoted
                    </span>
                    <span v-else-if="isStale(bestEntry(row.entries)!)"
                          class="px-2 py-0.5 rounded-md text-[7px] font-black uppercase tracking-widest bg-white/[0.04] border border-white/15 text-white/55">
                      Stale
                    </span>
                  </div>
                </template>
                <span v-else class="text-white/25 font-mono text-[9px]">—</span>
              </td>
              <td class="px-6 py-4">
                <template v-if="bestEntry(row.entries)">
                  <div class="flex items-center gap-2">
                    <div class="flex-grow h-1.5 bg-white/5 rounded-full overflow-hidden border border-white/5 w-24 max-w-[100px]">
                      <div class="h-full bg-gradient-to-r from-emerald-500 to-emerald-400 transition-all duration-700 ease-out"
                           :style="{ width: (winRatio(bestEntry(row.entries)!) * 100) + '%' }"></div>
                    </div>
                    <span class="text-[9px] font-black tabular-nums text-white/55 italic">{{ Math.round(winRatio(bestEntry(row.entries)!) * 100) }}%</span>
                  </div>
                </template>
                <span v-else class="text-white/25 font-mono text-[9px]">—</span>
              </td>
              <td class="px-6 py-4 text-right tabular-nums font-mono">
                <template v-if="bestEntry(row.entries)">
                  <span class="text-emerald-400">{{ bestEntry(row.entries)!.successCount }}</span>
                  <span class="text-white/25"> / </span>
                  <span class="text-red-400">{{ bestEntry(row.entries)!.failureCount }}</span>
                </template>
                <span v-else class="text-white/25">—</span>
              </td>
              <td class="px-6 py-4 text-right text-white/50 font-mono tabular-nums">
                {{ bestEntry(row.entries) ? relative(bestEntry(row.entries)!.lastSuccess) : '—' }}
              </td>
              <td class="px-6 py-4 text-right">
                <button
                  @click="forget(row.key)"
                  class="px-3 py-1.5 rounded-lg text-[8px] font-black uppercase tracking-widest bg-white/[0.03] hover:bg-red-500/20 hover:text-red-400 border border-white/5 text-white/50 transition-all italic active:scale-95">
                  <i class="bi bi-trash mr-1"></i> Forget
                </button>
              </td>
            </tr>
            <tr v-if="row.entries.length > 1" class="bg-black/20">
              <td colspan="6" class="px-6 py-3">
                <div class="flex flex-wrap gap-2 text-[9px]">
                  <span class="text-white/30 font-black uppercase tracking-[0.2em] mr-1">Also tried:</span>
                  <span v-for="entry in otherEntries(row.entries)" :key="entry.strategy"
                        :class="['font-mono px-2 py-0.5 rounded border',
                                 isDemoted(entry) ? 'text-amber-400/80 border-amber-500/20 bg-amber-500/5' : 'text-white/50 border-white/5 bg-white/[0.02]']">
                    {{ entry.strategy }}
                    <span class="text-white/30 ml-1">{{ entry.successCount }}W / {{ entry.failureCount }}L</span>
                  </span>
                </div>
              </td>
            </tr>
          </template>
          <tr v-if="rows.length === 0" key="empty-state">
            <td colspan="6" class="px-6 py-24 text-center">
              <div class="flex flex-col items-center gap-4 animate-pulse">
                <i class="bi bi-lightning-charge text-4xl text-white/10"></i>
                <div class="space-y-2">
                  <p class="text-white/25 font-black uppercase tracking-[0.5em] italic text-[10px]">No Memory Yet</p>
                  <p class="text-white/15 text-[9px] font-mono italic max-w-xs mx-auto">
                    The resolver will learn per-site bypass strategies as you play videos in VRChat.
                  </p>
                </div>
              </div>
            </td>
          </tr>
        </TransitionGroup>
      </table>
    </div>
  </div>
</template>

<style scoped>
.bypass-row-enter-active { transition: all 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }
.bypass-row-leave-active { transition: all 200ms ease-in; }
.bypass-row-enter-from   { opacity: 0; transform: translateY(-8px); }
.bypass-row-leave-to     { opacity: 0; transform: translateX(16px); }
.bypass-row-move         { transition: transform 400ms cubic-bezier(0.34, 1.56, 0.64, 1); }

@keyframes ytdlp-shimmer {
  0%   { transform: translateX(-100%); }
  100% { transform: translateX(100%); }
}
.ytdlp-shimmer::before {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(90deg, transparent 0%, rgba(59,130,246,0.08) 50%, transparent 100%);
  animation: ytdlp-shimmer 2s ease-in-out infinite;
  pointer-events: none;
}

@keyframes flash-up   { 0% { color: rgb(52,211,153); text-shadow: 0 0 12px rgba(52,211,153,0.6); } 100% {} }
@keyframes flash-down { 0% { color: rgb(248,113,113); text-shadow: 0 0 12px rgba(248,113,113,0.6); } 100% {} }
.flash-up   { animation: flash-up   900ms ease-out; }
.flash-down { animation: flash-down 900ms ease-out; }
</style>
