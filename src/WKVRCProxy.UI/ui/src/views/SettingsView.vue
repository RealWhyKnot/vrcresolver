<script setup lang="ts">
import { ref, computed } from 'vue'
import { useAppStore, TIER_DISPLAY, STRATEGY_PRIORITY_DEFAULTS, STRATEGY_PRIORITY_DEFAULTS_VERSION } from '../stores/appStore'

const appStore = useAppStore()

const DEFAULT_NATIVE_AVPRO_UA_HOSTS = ['vr-m.net']

// The config stores the deny-list as a string[]; the UI edits a comma-separated string for
// simplicity. `saveNativeAvProUaHosts` parses it back and persists, trimming empty entries.
const nativeAvProUaHostsText = computed<string>({
  get: () => (appStore.config.nativeAvProUaHosts ?? []).join(', '),
  set: (v: string) => {
    appStore.config.nativeAvProUaHosts = v.split(',').map(s => s.trim()).filter(s => s.length > 0)
  }
})

function saveNativeAvProUaHosts() {
  appStore.saveConfig()
}

function resetNativeAvProUaHosts() {
  appStore.config.nativeAvProUaHosts = [...DEFAULT_NATIVE_AVPRO_UA_HOSTS]
  appStore.saveConfig()
}

function isTierEnabled(tierId: string): boolean {
  return !(appStore.config.disabledTiers ?? []).includes(tierId)
}

function toggleTier(tierId: string) {
  if (!appStore.config.disabledTiers) appStore.config.disabledTiers = []
  const idx = appStore.config.disabledTiers.indexOf(tierId)
  if (idx >= 0) {
    appStore.config.disabledTiers.splice(idx, 1)
  } else {
    appStore.config.disabledTiers.push(tierId)
  }
  appStore.saveConfig()
}

// Individual strategy variants that can be toggled off without disabling a whole tier.
// Mirrors ResolutionEngine.BuildColdRaceStrategies — keep in sync when adding variants there.
// Using the same disabledTiers list on the backend (it accepts both "tier1" group names and
// "tier1:browser-extract" full strategy names).
interface StrategyDescriptor {
  name: string          // full name, e.g. "tier1:browser-extract" — what gets put in disabledTiers
  label: string         // UI label
  description: string   // short "when it helps" blurb
  youtubeOnly?: boolean // only meaningful for YouTube URLs
}
const STRATEGY_CATALOG: Record<string, StrategyDescriptor[]> = {
  'Local (Tier 1)': [
    { name: 'tier1:yt-combo',         label: 'YouTube combo',    description: 'One yt-dlp call that tries every YouTube player_client (tv_simply, tv_embedded, tv, web_safari, web, mweb, ios, ios_music, android, android_vr, android_music) internally, stopping at the first that works. Low-burst — the primary YouTube strategy.', youtubeOnly: true },
    { name: 'tier1:ipv6',             label: 'IPv6 forced',      description: 'Same as default but forces v6 egress. Routes around residential/CGNAT rate flags that target v4 only. No-op on networks without v6.' },
    { name: 'tier1:default',          label: 'Default',          description: 'yt-dlp with auto PO token + curl-impersonate. First pick for non-YouTube hosts.' },
    { name: 'tier1:vrchat-ua',        label: 'VRChat UA',        description: 'UnityPlayer User-Agent for hosts that need traffic to look like it came from VRChat itself.' },
    { name: 'tier1:impersonate-only', label: 'Impersonate only', description: 'curl-impersonate TLS fingerprint, no PO token — for sites where PO fetch itself flags us.' },
    { name: 'tier1:plain',            label: 'Plain',            description: 'Bare yt-dlp, no extras. Last-resort variant.' },
    { name: 'tier1:browser-extract',  label: 'Browser extract',  description: 'Headless Edge/Chrome for JS-gated sites. Can serve decoy URLs on YouTube when detected.' },
  ],
  'Cloud (Tier 2)': [
    { name: 'tier2:cloud-whyknot',    label: 'WhyKnot.dev cloud', description: 'Cloud resolver fallback. Hits YouTube from a different IP so it works when your IP is rate-flagged.' },
  ],
  'Original yt-dlp (Tier 3)': [
    { name: 'tier3:plain',            label: 'Plain yt-dlp-og',   description: 'VRChat-pinned yt-dlp.exe, no extras. Sequential fallback only.' },
  ],
}

function isStrategyEnabled(name: string): boolean {
  const list = appStore.config.disabledTiers ?? []
  return !list.includes(name)
}

function toggleStrategy(name: string) {
  if (!appStore.config.disabledTiers) appStore.config.disabledTiers = []
  const idx = appStore.config.disabledTiers.indexOf(name)
  if (idx >= 0) appStore.config.disabledTiers.splice(idx, 1)
  else appStore.config.disabledTiers.push(name)
  appStore.saveConfig()
}

const showAdvancedStrategies = ref(false)

// Build a flat, catalog-ordered list of every known strategy. The priority panel below renders
// in the user's StrategyPriority order; this flat list is used for the "Any strategy not in
// priority list" section so users can see and include newly-added catalog entries.
const ALL_STRATEGIES: StrategyDescriptor[] = Object.values(STRATEGY_CATALOG).flat()

function findDescriptor(name: string): StrategyDescriptor | null {
  return ALL_STRATEGIES.find(s => s.name === name) ?? null
}

const priorityOrderedStrategies = computed(() => {
  const priority = appStore.config.strategyPriority ?? []
  const ordered = priority.map(findDescriptor).filter((d): d is StrategyDescriptor => d !== null)
  const present = new Set(ordered.map(d => d.name))
  const orphans = ALL_STRATEGIES.filter(s => !present.has(s.name))
  return { ordered, orphans }
})

function moveStrategy(name: string, delta: number) {
  const list = [...(appStore.config.strategyPriority ?? [])]
  const idx = list.indexOf(name)
  if (idx < 0) {
    // Not in list yet — append at the tail. Delta is ignored for first insertion.
    list.push(name)
    appStore.config.strategyPriority = list
    appStore.saveConfig()
    return
  }
  const target = idx + delta
  if (target < 0 || target >= list.length) return
  list.splice(idx, 1)
  list.splice(target, 0, name)
  appStore.config.strategyPriority = list
  appStore.saveConfig()
}

function addToPriority(name: string) {
  const list = [...(appStore.config.strategyPriority ?? [])]
  if (!list.includes(name)) {
    list.push(name)
    appStore.config.strategyPriority = list
    appStore.saveConfig()
  }
}

function removeFromPriority(name: string) {
  const list = (appStore.config.strategyPriority ?? []).filter(n => n !== name)
  appStore.config.strategyPriority = list
  appStore.saveConfig()
}

function resetStrategyPriority() {
  appStore.config.strategyPriority = [...STRATEGY_PRIORITY_DEFAULTS]
  appStore.config.strategyPriorityDefaultsVersion = STRATEGY_PRIORITY_DEFAULTS_VERSION
  appStore.saveConfig()
}

const showSystemInfo = ref(false)

const DEFAULT_USER_AGENT = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'

function truncate(str: string, len: number) {
  if (str.length <= len) return str
  return str.substring(0, len) + '...'
}

function clearCustomPath() {
  appStore.config.customVrcPath = undefined
  appStore.saveConfig()
}

function resetUserAgent() {
  appStore.config.userAgent = DEFAULT_USER_AGENT
  appStore.saveConfig()
}

function saveUserAgent() {
  appStore.saveConfig()
}

function clearHistory() {
  appStore.config.history = []
  appStore.saveConfig()
}
</script>

<template>
  <div class="p-8 max-w-4xl space-y-10">
    <div class="space-y-2"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <h2 class="text-3xl font-black uppercase tracking-tighter italic">Settings</h2>
      <p class="text-white/45 font-black uppercase tracking-[0.4em] text-[9px] ml-1">Configuration</p>
    </div>

    <div class="space-y-6">

      <!-- ═══════════════════════ RESOLUTION ═══════════════════════ -->
      <div class="flex items-center gap-4 pt-8 pb-2">
        <h3 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/30 italic">Resolution</h3>
        <div class="flex-grow h-px bg-white/5"></div>
      </div>

      <!-- VRChat Path Selection -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl group">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 bg-blue-500/10 rounded-2xl flex items-center justify-center text-blue-500 group-hover:scale-110 transition-transform">
            <i class="bi bi-folder-fill text-xl"></i>
          </div>
          <div>
            <h4 class="text-lg font-black uppercase tracking-tighter italic">VRChat Tools Path</h4>
            <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Where the game stores video tools</p>
          </div>
        </div>

        <div class="flex gap-3">
          <div class="flex-grow bg-white/[0.02] border border-white/5 rounded-2xl px-6 py-3 flex items-center overflow-hidden group-hover:bg-white/[0.04] transition-colors">
            <span class="text-[9px] font-mono text-white/60 truncate italic">
              {{ appStore.config.customVrcPath || 'Detecting automatically...' }}
            </span>
          </div>
          <button @click="appStore.pickVrcPath()" class="px-6 py-3 bg-blue-600 hover:bg-blue-500 text-white rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all italic active:scale-95 shadow-xl shadow-blue-600/20">
            Change Path
          </button>
          <button v-if="appStore.config.customVrcPath" @click="clearCustomPath()" class="px-4 bg-white/5 hover:bg-red-500/20 text-white/50 hover:text-red-400 rounded-2xl transition-all border border-white/5 active:scale-90">
            <i class="bi bi-x-lg"></i>
          </button>
        </div>
      </section>

      <!-- Tier Configuration -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-5 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 bg-blue-500/10 rounded-2xl flex items-center justify-center text-blue-500">
            <i class="bi bi-layers-fill text-xl"></i>
          </div>
          <div>
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Tier Configuration</h4>
            <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Enable or disable individual extraction methods</p>
          </div>
        </div>
        <div class="space-y-3">
          <div v-for="(data, tierId) in TIER_DISPLAY" :key="tierId"
               @click="toggleTier(tierId)"
               class="flex items-center gap-4 p-4 bg-white/[0.02] border border-white/5 rounded-2xl cursor-pointer hover:bg-white/[0.04] hover:border-white/10 transition-all group/tier"
               :class="isTierEnabled(tierId) ? '' : 'opacity-50'">
            <div :class="[data.color, 'w-2.5 h-2.5 rounded-full shrink-0']"></div>
            <div class="flex-grow min-w-0">
              <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80 group-hover/tier:text-white transition-colors">{{ data.short }}</p>
              <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5">{{ data.long }}</p>
            </div>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-500 shrink-0', isTierEnabled(tierId) ? 'bg-blue-600 shadow-[0_0_12px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-500', isTierEnabled(tierId) ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
        </div>
        <p class="text-[8px] text-white/25 font-bold uppercase tracking-widest italic">Passthrough always activates on unrecoverable error regardless of this setting.</p>
      </section>

      <!-- Advanced: per-strategy toggles. Users can disable a single variant without taking down
           the whole tier — handy when one path regresses on a specific host (e.g. browser-extract
           serving decoy URLs on YouTube) and you need to work around it while the fix lands. -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-5 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl">
        <div class="flex items-center justify-between cursor-pointer" @click="showAdvancedStrategies = !showAdvancedStrategies">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 bg-purple-500/10 rounded-2xl flex items-center justify-center text-purple-400">
              <i class="bi bi-sliders text-xl"></i>
            </div>
            <div>
              <h4 class="text-lg font-black uppercase tracking-tighter italic">Advanced — Individual Strategies</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Toggle specific variants inside each tier</p>
            </div>
          </div>
          <i :class="['bi transition-transform text-white/45 text-lg', showAdvancedStrategies ? 'bi-chevron-up rotate-0' : 'bi-chevron-down']"></i>
        </div>

        <div v-if="showAdvancedStrategies" class="space-y-5 pt-2">
          <p class="text-[8px] text-white/40 font-bold uppercase tracking-widest leading-relaxed italic">Use these to mute a single variant that's misbehaving for a host. Disabling all variants in a tier has the same effect as disabling the whole tier.</p>
          <div v-for="(strategies, groupLabel) in STRATEGY_CATALOG" :key="groupLabel" class="space-y-2">
            <h5 class="text-[9px] font-black uppercase tracking-[0.3em] text-white/45 italic">{{ groupLabel }}</h5>
            <div class="space-y-1.5">
              <div v-for="s in strategies" :key="s.name"
                   @click="toggleStrategy(s.name)"
                   class="flex items-center gap-3 p-3 bg-white/[0.015] border border-white/5 rounded-xl cursor-pointer hover:bg-white/[0.04] transition-all"
                   :class="isStrategyEnabled(s.name) ? '' : 'opacity-50'">
                <div class="flex-grow min-w-0">
                  <div class="flex items-center gap-2">
                    <span class="text-[10px] font-black uppercase tracking-widest italic text-white/75">{{ s.label }}</span>
                    <span v-if="s.youtubeOnly" class="px-1.5 py-0.5 rounded text-[7px] font-black uppercase tracking-widest bg-red-500/10 border border-red-500/20 text-red-400/75 italic">YouTube</span>
                    <span class="text-[8px] font-mono text-white/25">{{ s.name }}</span>
                  </div>
                  <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">{{ s.description }}</p>
                </div>
                <div :class="['w-8 h-4 rounded-full relative transition-all duration-500 shrink-0', isStrategyEnabled(s.name) ? 'bg-blue-600' : 'bg-white/10 border border-white/10']">
                  <div :class="['absolute top-0.5 w-3 h-3 bg-white rounded-full transition-all duration-500', isStrategyEnabled(s.name) ? 'left-[18px]' : 'left-0.5']"></div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      <!-- Strategy priority: ordered list of strategy names, drives wave-1 pick + ties broken by
           StrategyMemory net-score + built-in priority. Up/down arrows reorder; × removes from
           the priority list (strategies not in the list still run, just at the tail). -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-5 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 bg-emerald-500/10 rounded-2xl flex items-center justify-center text-emerald-400">
              <i class="bi bi-sort-down-alt text-xl"></i>
            </div>
            <div>
              <h4 class="text-lg font-black uppercase tracking-tighter italic">Strategy Priority</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Which strategies run first in the cold race (wave 1)</p>
            </div>
          </div>
          <button @click="resetStrategyPriority()" class="px-4 py-2 bg-white/5 hover:bg-emerald-500/10 text-white/50 hover:text-emerald-400 rounded-xl font-black text-[8px] uppercase tracking-[0.2em] transition-all italic active:scale-95 border border-white/5 hover:border-emerald-500/20">
            Reset default
          </button>
        </div>

        <div class="space-y-1.5">
          <div v-for="(s, index) in priorityOrderedStrategies.ordered" :key="s.name"
               class="flex items-center gap-3 p-2.5 bg-white/[0.015] border border-white/5 rounded-xl">
            <span class="text-[9px] font-black text-white/35 tabular-nums w-6 text-center">{{ index + 1 }}</span>
            <div class="flex-grow min-w-0">
              <div class="flex items-center gap-2">
                <span class="text-[10px] font-black uppercase tracking-widest italic text-white/75">{{ s.label }}</span>
                <span v-if="s.youtubeOnly" class="px-1.5 py-0.5 rounded text-[7px] font-black uppercase tracking-widest bg-red-500/10 border border-red-500/20 text-red-400/75 italic">YouTube</span>
                <span class="text-[8px] font-mono text-white/25">{{ s.name }}</span>
              </div>
              <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">{{ s.description }}</p>
            </div>
            <button @click="moveStrategy(s.name, -1)" :disabled="index === 0"
                    class="w-7 h-7 rounded-lg bg-white/5 hover:bg-blue-500/10 text-white/50 hover:text-blue-400 transition-all flex items-center justify-center disabled:opacity-30 disabled:cursor-not-allowed">
              <i class="bi bi-chevron-up text-xs"></i>
            </button>
            <button @click="moveStrategy(s.name, 1)" :disabled="index === priorityOrderedStrategies.ordered.length - 1"
                    class="w-7 h-7 rounded-lg bg-white/5 hover:bg-blue-500/10 text-white/50 hover:text-blue-400 transition-all flex items-center justify-center disabled:opacity-30 disabled:cursor-not-allowed">
              <i class="bi bi-chevron-down text-xs"></i>
            </button>
            <button @click="removeFromPriority(s.name)"
                    class="w-7 h-7 rounded-lg bg-white/5 hover:bg-red-500/10 text-white/40 hover:text-red-400 transition-all flex items-center justify-center">
              <i class="bi bi-x text-sm"></i>
            </button>
          </div>
        </div>

        <div v-if="priorityOrderedStrategies.orphans.length > 0" class="space-y-1.5 pt-3 border-t border-white/5">
          <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/30 italic">Not in priority list (run at the tail)</p>
          <div v-for="s in priorityOrderedStrategies.orphans" :key="s.name"
               class="flex items-center gap-3 p-2.5 bg-white/[0.01] border border-white/[0.03] rounded-xl opacity-70">
            <span class="text-[9px] font-black text-white/25 w-6 text-center">—</span>
            <div class="flex-grow min-w-0">
              <div class="flex items-center gap-2">
                <span class="text-[10px] font-black uppercase tracking-widest italic text-white/60">{{ s.label }}</span>
                <span v-if="s.youtubeOnly" class="px-1.5 py-0.5 rounded text-[7px] font-black uppercase tracking-widest bg-red-500/10 border border-red-500/20 text-red-400/60 italic">YouTube</span>
                <span class="text-[8px] font-mono text-white/20">{{ s.name }}</span>
              </div>
            </div>
            <button @click="addToPriority(s.name)"
                    class="px-3 py-1.5 rounded-lg bg-white/5 hover:bg-emerald-500/10 text-white/40 hover:text-emerald-400 text-[8px] font-black uppercase tracking-widest transition-all italic active:scale-95">
              Add to list
            </button>
          </div>
        </div>

        <p class="text-[8px] text-white/25 font-bold uppercase tracking-widest italic leading-relaxed">Wave 1 launches the top <span class="text-emerald-400/60">{{ appStore.config.waveSize ?? 2 }}</span> entries (skipping any disabled above). Each wave waits <span class="text-emerald-400/60">{{ appStore.config.waveStageDeadlineSeconds ?? 3 }}s</span> for a winner before launching the next.</p>
      </section>

      <!-- Preferred Tier -->
      <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] flex items-center justify-between group hover:border-blue-500/20 transition-all duration-500 backdrop-blur-3xl shadow-xl">
        <div class="max-w-md">
          <h4 class="text-lg font-black uppercase tracking-tighter mb-0.5 italic">Preferred Resolution Tier</h4>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest">Which extraction method to try first</p>
        </div>
        <select v-model="appStore.config.preferredTier" @change="appStore.saveConfig()" class="bg-[#010103] border border-white/10 rounded-xl px-6 py-3 text-[10px] font-black uppercase tracking-widest focus:outline-none focus:border-blue-500 transition-all cursor-pointer text-white/80 hover:text-white italic appearance-none shadow-2xl">
          <option v-for="(data, id) in TIER_DISPLAY" :key="id" :value="id">{{ data.short }} — {{ truncate(data.long, 35) }}</option>
        </select>
      </div>

      <!-- Max Quality -->
      <div class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] flex items-center justify-between group hover:border-blue-500/20 transition-all duration-500 backdrop-blur-3xl">
        <div class="max-w-md">
          <h4 class="text-lg font-black uppercase tracking-tighter mb-0.5 italic">Max Video Quality</h4>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest">Resolution cap for video streams</p>
        </div>
        <div class="flex gap-2">
          <button v-for="res in ['480p', '720p', '1080p', '1440p']" :key="res"
                  @click="appStore.config.preferredResolution = res; appStore.saveConfig()"
                  :class="['px-4 py-2 rounded-xl text-[9px] font-black uppercase tracking-widest transition-all italic', appStore.config.preferredResolution === res ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.3)] text-white' : 'bg-white/5 border border-white/10 text-white/55 hover:border-white/25 hover:text-white']">
            {{ res }}
          </button>
        </div>
      </div>

      <!-- ═══════════════════════ NETWORK ═══════════════════════ -->
      <div class="flex items-center gap-4 pt-8 pb-2">
        <h3 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/30 italic">Network</h3>
        <div class="flex-grow h-px bg-white/5"></div>
      </div>

      <!-- Toggles -->
      <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div @click="appStore.config.debugMode = !appStore.config.debugMode; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Detailed Logging</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.debugMode ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.debugMode ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Show technical detail in the logs panel.</p>
        </div>

        <div @click="appStore.config.forceIPv4 = !appStore.config.forceIPv4; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Force IPv4</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.forceIPv4 ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.forceIPv4 ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Use only IPv4 when resolving video URLs.</p>
        </div>

        <div @click="appStore.config.autoPatchOnStart = !appStore.config.autoPatchOnStart; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Auto-Patch on Start</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.autoPatchOnStart ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.autoPatchOnStart ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Automatically patch VRChat video tools on startup.</p>
        </div>

        <div @click="appStore.config.enableRelayBypass = !appStore.config.enableRelayBypass; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Enable Relay Bypass</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.enableRelayBypass ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.enableRelayBypass ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Route video URLs through a local proxy to bypass domain blocking in public VRChat worlds. Required for most public world video players.</p>
        </div>

        <div @click="appStore.config.enablePreflightProbe = !appStore.config.enablePreflightProbe; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Pre-Flight URL Probe</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.enablePreflightProbe ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.enablePreflightProbe ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Verify resolved URLs are playable before handing them to VRChat. Catches dead CDN URLs and cloud-resolver drift; adds up to 5s on cold resolve.</p>
        </div>
      </div>

      <!-- Direct-connect hosts (skipped by the relay wrap) -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl group">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 bg-blue-500/10 rounded-2xl flex items-center justify-center text-blue-500 group-hover:scale-110 transition-transform">
            <i class="bi bi-shield-lock text-xl"></i>
          </div>
          <div>
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Direct-Connect Hosts (relay skip-list)</h4>
            <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Comma-separated — hosts that need a direct connection from the player</p>
          </div>
        </div>
        <div class="flex gap-3">
          <div class="flex-grow">
            <input
              v-model="nativeAvProUaHostsText"
              @blur="saveNativeAvProUaHosts()"
              type="text"
              placeholder="vr-m.net, some-other-host.net"
              class="w-full bg-white/[0.02] border border-white/5 rounded-2xl px-6 py-3 text-[9px] font-mono text-white/60 italic placeholder:text-white/25 focus:outline-none focus:border-blue-500/40 transition-colors group-hover:bg-white/[0.04]"
            />
          </div>
          <button @click="resetNativeAvProUaHosts()" class="px-6 py-3 bg-white/5 hover:bg-blue-500/10 text-white/50 hover:text-blue-400 rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all italic active:scale-95 border border-white/5 hover:border-blue-500/20">
            Reset Default
          </button>
        </div>
        <p class="text-[9px] text-white/40 font-bold uppercase tracking-widest leading-relaxed">Hosts that need to see traffic coming directly from VRChat's player (e.g. vr-m.net). These are skipped by the relay; every other host is routed through it by default.</p>
      </section>

      <!-- Custom User Agent -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl group">
        <div class="flex items-center gap-4">
          <div class="w-10 h-10 bg-blue-500/10 rounded-2xl flex items-center justify-center text-blue-500 group-hover:scale-110 transition-transform">
            <i class="bi bi-globe2 text-xl"></i>
          </div>
          <div>
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Custom User Agent</h4>
            <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Browser identity string used for video extraction</p>
          </div>
        </div>

        <div class="flex gap-3">
          <div class="flex-grow">
            <input
              v-model="appStore.config.userAgent"
              @blur="saveUserAgent()"
              type="text"
              placeholder="Leave empty for default..."
              class="w-full bg-white/[0.02] border border-white/5 rounded-2xl px-6 py-3 text-[9px] font-mono text-white/60 italic placeholder:text-white/25 focus:outline-none focus:border-blue-500/40 transition-colors group-hover:bg-white/[0.04]"
            />
          </div>
          <button @click="resetUserAgent()" class="px-6 py-3 bg-white/5 hover:bg-blue-500/10 text-white/50 hover:text-blue-400 rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all italic active:scale-95 border border-white/5 hover:border-blue-500/20">
            Reset Default
          </button>
        </div>
      </section>

      <!-- Network Auth -->
      <div class="flex items-center justify-between group">
        <div>
          <h4 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/55 mb-1 italic group-hover:text-blue-400 transition-colors">Network Authorization</h4>
          <p class="text-[9px] text-white/40 font-bold uppercase tracking-widest">Re-prompt for hosts file bypass permission</p>
        </div>
        <button @click="appStore.sendMessage('REQUEST_HOSTS_SETUP')" class="px-8 py-4 rounded-2xl bg-white/5 border border-white/5 text-white/55 hover:bg-blue-500/10 hover:text-blue-400 hover:border-blue-500/20 transition-all text-[9px] font-black uppercase tracking-[0.2em] italic active:scale-95">
          Request Prompt
        </button>
      </div>

      <!-- Troubleshooting -->
      <div class="flex items-center justify-between group">
        <div>
          <h4 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/55 mb-1 italic group-hover:text-yellow-400 transition-colors">Troubleshooting</h4>
          <p class="text-[9px] text-white/40 font-bold uppercase tracking-widest">If videos fail immediately in public instances</p>
        </div>
        <button @click="appStore.sendMessage('ADD_FIREWALL_RULE')" class="px-8 py-4 rounded-2xl bg-white/5 border border-white/5 text-white/55 hover:bg-yellow-500/10 hover:text-yellow-400 hover:border-yellow-500/20 transition-all text-[9px] font-black uppercase tracking-[0.2em] italic active:scale-95">
          Add Firewall Exclusion
        </button>
      </div>

      <!-- ═══════════════════════ MAINTENANCE ═══════════════════════ -->
      <div class="flex items-center gap-4 pt-8 pb-2">
        <h3 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/30 italic">Maintenance</h3>
        <div class="flex-grow h-px bg-white/5"></div>
      </div>

      <!-- Reset Tools -->
      <div class="flex items-center justify-between">
        <div>
          <h4 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/40 mb-1 italic">Reset Tools</h4>
          <p class="text-[9px] text-white/35 font-bold uppercase tracking-widest">Wipe and reinstall local tools</p>
        </div>
        <button @click="appStore.wipeTools()" class="px-8 py-4 rounded-2xl bg-white/5 border border-white/5 text-white/40 hover:bg-red-500/10 hover:text-red-400 hover:border-red-500/20 transition-all text-[9px] font-black uppercase tracking-[0.2em] italic active:scale-95">
          Reset Tools
        </button>
      </div>

      <!-- Clear History -->
      <div class="flex items-center justify-between">
        <div>
          <h4 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/40 mb-1 italic">Clear History</h4>
          <p class="text-[9px] text-white/35 font-bold uppercase tracking-widest">Remove all resolution history entries</p>
        </div>
        <button @click="clearHistory()" class="px-8 py-4 rounded-2xl bg-white/5 border border-white/5 text-white/40 hover:bg-yellow-500/10 hover:text-yellow-400 hover:border-yellow-500/20 transition-all text-[9px] font-black uppercase tracking-[0.2em] italic active:scale-95">
          Clear All
        </button>
      </div>

      <!-- ═══════════════════════ SYSTEM ═══════════════════════ -->
      <div class="flex items-center gap-4 pt-8 pb-2">
        <h3 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/30 italic">System</h3>
        <div class="flex-grow h-px bg-white/5"></div>
      </div>

      <!-- System Information (collapsible) -->
      <div class="bg-white/[0.03] border border-white/5 rounded-[32px] overflow-hidden transition-all duration-500 backdrop-blur-3xl" :class="showSystemInfo ? 'shadow-2xl' : ''">
        <button @click="showSystemInfo = !showSystemInfo" class="w-full p-8 flex items-center justify-between group cursor-pointer hover:bg-white/[0.02] transition-colors">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 bg-white/5 rounded-2xl flex items-center justify-center text-white/40 group-hover:scale-110 transition-transform">
              <i class="bi bi-info-circle text-xl"></i>
            </div>
            <div class="text-left">
              <h4 class="text-lg font-black uppercase tracking-tighter italic">System Information</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Build, runtime, and session details</p>
            </div>
          </div>
          <i :class="['bi text-white/30 text-lg transition-transform duration-500', showSystemInfo ? 'bi-chevron-up rotate-0' : 'bi-chevron-down']"></i>
        </button>

        <div v-if="showSystemInfo" class="px-8 pb-8 space-y-4 animate-in slide-in-from-top-2 duration-300">
          <div class="grid grid-cols-2 md:grid-cols-3 gap-4">
            <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 space-y-2">
              <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/30 italic">Build Version</p>
              <p class="text-sm font-mono text-white/70 italic">{{ appStore.version }}</p>
            </div>
            <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 space-y-2">
              <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/30 italic">History Entries</p>
              <p class="text-sm font-mono text-white/70 italic">{{ appStore.config.history.length }}</p>
            </div>
            <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 space-y-2">
              <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/30 italic">Total Logs</p>
              <p class="text-sm font-mono text-white/70 italic">{{ appStore.logs.length }}</p>
            </div>
            <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 space-y-2">
              <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/30 italic">Cloud Node</p>
              <p class="text-sm font-mono text-white/70 italic">{{ appStore.status.stats.node }}</p>
            </div>
            <div class="bg-white/[0.03] border border-white/5 rounded-2xl p-5 space-y-2">
              <p class="text-[8px] font-black uppercase tracking-[0.3em] text-white/30 italic">Current Player</p>
              <p class="text-sm font-mono text-white/70 italic">{{ appStore.status.stats.player }}</p>
            </div>
          </div>
        </div>
      </div>

    </div>
  </div>
</template>
