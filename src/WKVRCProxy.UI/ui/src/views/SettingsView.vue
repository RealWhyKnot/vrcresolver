<script setup lang="ts">
import { ref, computed } from 'vue'
import { useAppStore, TIER_DISPLAY, STRATEGY_PRIORITY_DEFAULTS } from '../stores/appStore'

const appStore = useAppStore()

function confirmUninstall() {
  const ok = window.confirm(
    'Uninstall WKVRCProxy?\n\n' +
    'This will:\n' +
    "  • Restore VRChat's original yt-dlp.exe\n" +
    '  • Delete saved settings, cookies, and bypass memory\n' +
    '  • Remove the install folder\n\n' +
    'WKVRCProxy will close and the uninstaller will take over.'
  )
  if (ok) appStore.launchUninstaller()
}

const DEFAULT_NATIVE_AVPRO_UA_HOSTS = ['vr-m.net']

// Chip editor for the deny-list. Users type one host into `hostInputDraft`, then press Enter
// or click + to commit it. Backspace on an empty input pops the last chip — standard chip-list
// keyboard shortcut. Trims/lowercases on add and de-dupes case-insensitively. Strips a leading
// http(s):// in case the user pastes a URL instead of a bare host.
const hostInputDraft = ref('')

function normaliseHost(raw: string): string {
  let h = raw.trim().toLowerCase()
  // Tolerate full URLs pasted in — extract the host.
  const proto = h.match(/^https?:\/\//)
  if (proto) h = h.slice(proto[0].length)
  // Trim path/query/port — only the bare host belongs in the deny-list.
  h = h.split('/')[0].split('?')[0].split(':')[0]
  return h
}

function addNativeUaHost(raw: string) {
  const host = normaliseHost(raw)
  if (!host) return
  const list = appStore.config.nativeAvProUaHosts ?? []
  if (list.some(h => h.toLowerCase() === host)) return
  appStore.config.nativeAvProUaHosts = [...list, host]
  hostInputDraft.value = ''
  appStore.markOverridden('nativeAvProUaHosts')
  appStore.saveConfig()
}

function removeNativeUaHost(host: string) {
  const list = appStore.config.nativeAvProUaHosts ?? []
  appStore.config.nativeAvProUaHosts = list.filter(h => h !== host)
  appStore.markOverridden('nativeAvProUaHosts')
  appStore.saveConfig()
}

function onHostInputKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter' || e.key === ',') {
    e.preventDefault()
    addNativeUaHost(hostInputDraft.value)
  } else if (e.key === 'Backspace' && hostInputDraft.value === '') {
    const list = appStore.config.nativeAvProUaHosts ?? []
    if (list.length > 0) removeNativeUaHost(list[list.length - 1])
  }
}

function onHostInputPaste(e: ClipboardEvent) {
  // Allow pasting "vr-m.net, foo.bar, baz.com" — split and add each.
  const text = e.clipboardData?.getData('text') ?? ''
  if (!text.includes(',') && !/\s/.test(text)) return
  e.preventDefault()
  for (const part of text.split(/[,\s]+/)) addNativeUaHost(part)
}

function resetNativeAvProUaHosts() {
  appStore.config.nativeAvProUaHosts = [...DEFAULT_NATIVE_AVPRO_UA_HOSTS]
  appStore.clearOverridden('nativeAvProUaHosts')
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
}
const STRATEGY_CATALOG: Record<string, StrategyDescriptor[]> = {
  'Local (Tier 1)': [
    { name: 'tier1:yt-combo',         label: 'YouTube combo',    description: 'A single yt-dlp call that walks through every YouTube client (web, mobile, TV, iOS, Android, etc.) and uses whichever one responds first. The default pick for YouTube URLs.' },
    { name: 'tier1:ipv6',             label: 'IPv6 forced',      description: 'Forces requests over IPv6. Useful when your IPv4 address is rate-limited (common on shared or CGNAT connections). Has no effect on networks without IPv6.' },
    { name: 'tier1:default',          label: 'Default',          description: 'Standard yt-dlp call with a real-browser TLS fingerprint. First pick for non-YouTube hosts.' },
    { name: 'tier1:vrchat-ua',        label: 'VRChat UA',        description: "Sends requests that look like they're coming from VRChat itself. Helps for hosts that only respond to in-game traffic." },
    { name: 'tier1:impersonate-only', label: 'Impersonate only', description: "Real-browser TLS fingerprint with no extra YouTube tokens. For sites that flag the standard token fetch but accept normal-looking browser traffic." },
    { name: 'tier1:plain',            label: 'Plain',            description: 'Bare yt-dlp, no impersonation, no tokens. Kept as a last-resort sanity check.' },
    { name: 'tier1:browser-extract',  label: 'Browser extract',  description: "Spins up a headless Edge/Chrome to handle sites that need full JavaScript. Used when yt-dlp can't crack the page on its own." },
    { name: 'tier1:warp+default',     label: 'Default via WARP',  description: 'Same as Default, but routes the request through Cloudflare WARP first so the host sees a different IP. The WireGuard helper starts on first use.' },
    { name: 'tier1:warp+vrchat-ua',   label: 'VRChat UA via WARP',description: "Same as VRChat UA, but via Cloudflare WARP. Combines the in-game User-Agent with a different egress IP for hosts that gate on both." },
  ],
  'Cloud (Tier 2)': [
    { name: 'tier2:cloud-whyknot',    label: 'WhyKnot.dev cloud', description: 'Hands resolution off to the WhyKnot.dev resolver. Useful when your local IP is rate-limited and you need to hit YouTube from somewhere else.' },
  ],
  'Original yt-dlp (Tier 3)': [
    { name: 'tier3:plain',            label: 'Plain yt-dlp-og',   description: "VRChat's bundled yt-dlp.exe with no extras. Final fallback when everything else has failed." },
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
    appStore.markOverridden('strategyPriority')
    appStore.saveConfig()
    return
  }
  const target = idx + delta
  if (target < 0 || target >= list.length) return
  list.splice(idx, 1)
  list.splice(target, 0, name)
  appStore.config.strategyPriority = list
  appStore.markOverridden('strategyPriority')
  appStore.saveConfig()
}

function addToPriority(name: string) {
  const list = [...(appStore.config.strategyPriority ?? [])]
  if (!list.includes(name)) {
    list.push(name)
    appStore.config.strategyPriority = list
    appStore.markOverridden('strategyPriority')
    appStore.saveConfig()
  }
}

function removeFromPriority(name: string) {
  const list = (appStore.config.strategyPriority ?? []).filter(n => n !== name)
  appStore.config.strategyPriority = list
  appStore.markOverridden('strategyPriority')
  appStore.saveConfig()
}

function resetStrategyPriority() {
  appStore.config.strategyPriority = [...STRATEGY_PRIORITY_DEFAULTS]
  appStore.clearOverridden('strategyPriority')
  appStore.saveConfig()
}

const showSystemInfo = ref(false)
const showRaceTuning = ref(false)

// Numeric inputs save on blur and clamp to safe bounds. The backend defends itself too (defaults
// kick in for nonsense values), but clamping here keeps the UI from displaying obvious nonsense
// like waveSize=0 between blur events.
function saveNumberClamped(key: 'waveSize' | 'waveStageDeadlineSeconds' | 'perHostRequestBudget' | 'perHostRequestWindowSeconds' | 'tier2TimeoutSeconds', min: number, max: number) {
  const raw = appStore.config[key] as number | undefined
  let v = typeof raw === 'number' && !Number.isNaN(raw) ? Math.floor(raw) : min
  if (v < min) v = min
  if (v > max) v = max
  ;(appStore.config[key] as number) = v
  appStore.saveConfig()
}

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
        <p class="text-[8px] text-white/25 font-bold uppercase tracking-widest italic">Passthrough always runs as a last resort when everything else fails — that part can't be turned off.</p>
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
          <p class="text-[8px] text-white/40 font-bold uppercase tracking-widest leading-relaxed italic">Turn off a single strategy that's misbehaving without disabling its whole tier. Turning off every strategy in a tier is equivalent to disabling that tier above.</p>
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
              <div class="flex items-center gap-2">
                <h4 class="text-lg font-black uppercase tracking-tighter italic">Strategy Priority</h4>
                <span :class="['text-[8px] font-black uppercase tracking-[0.2em] italic px-2 py-0.5 rounded-md',
                               appStore.isOverridden('strategyPriority') ? 'text-amber-400/80 bg-amber-500/10' : 'text-emerald-400/70 bg-emerald-500/5']">
                  {{ appStore.isOverridden('strategyPriority') ? 'Custom' : 'Default' }}
                </span>
              </div>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Order strategies are tried in when nothing is cached yet</p>
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
                <span class="text-[8px] font-mono text-white/20">{{ s.name }}</span>
              </div>
            </div>
            <button @click="addToPriority(s.name)"
                    class="px-3 py-1.5 rounded-lg bg-white/5 hover:bg-emerald-500/10 text-white/40 hover:text-emerald-400 text-[8px] font-black uppercase tracking-widest transition-all italic active:scale-95">
              Add to list
            </button>
          </div>
        </div>

        <p class="text-[8px] text-white/25 font-bold uppercase tracking-widest italic leading-relaxed">Wave 1 launches the top <span class="text-emerald-400/60">{{ appStore.config.waveSize ?? 2 }}</span> entries (skipping any disabled above). Each wave waits <span class="text-emerald-400/60">{{ appStore.config.waveStageDeadlineSeconds ?? 3 }}s</span> for a winner before launching the next. Tune these in <span class="text-emerald-400/60">Race Tuning</span> below.</p>
      </section>

      <!-- Race Tuning: numeric knobs for the wave-race dispatcher. Collapsed by default — the
           defaults are good and tweaking them wrong (waveSize=8, deadline=1s) hammers upstream
           rate limits. Each input clamps to a safe range on blur. -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-5 hover:border-emerald-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl">
        <div class="flex items-center justify-between cursor-pointer" @click="showRaceTuning = !showRaceTuning">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 bg-emerald-500/10 rounded-2xl flex items-center justify-center text-emerald-400">
              <i class="bi bi-stopwatch text-xl"></i>
            </div>
            <div>
              <h4 class="text-lg font-black uppercase tracking-tighter italic">Race Tuning</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Wave dispatch + per-host rate limits + cloud timeout</p>
            </div>
          </div>
          <i :class="['bi transition-transform text-white/45 text-lg', showRaceTuning ? 'bi-chevron-up rotate-0' : 'bi-chevron-down']"></i>
        </div>

        <div v-if="showRaceTuning" class="space-y-5 pt-2">
          <p class="text-[8px] text-white/40 font-bold uppercase tracking-widest leading-relaxed italic">Defaults match yt-dlp's own 2–3 concurrent-request recommendation. Raise these only on a reliable connection; lower them if you keep hitting rate limits.</p>

          <!-- Wave race master toggle -->
          <div @click="appStore.config.enableWaveRace = !appStore.config.enableWaveRace; appStore.saveConfig()"
               class="flex items-center gap-4 p-4 bg-white/[0.02] border border-white/5 rounded-2xl cursor-pointer hover:bg-white/[0.04] hover:border-white/10 transition-all">
            <div class="flex-grow min-w-0">
              <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80">Wave Mode</p>
              <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">On (the default) launches strategies in small waves to avoid hammering rate limits. Off launches every strategy in parallel right away.</p>
            </div>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-500 shrink-0', appStore.config.enableWaveRace ? 'bg-emerald-600 shadow-[0_0_12px_rgba(16,185,129,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-500', appStore.config.enableWaveRace ? 'left-6' : 'left-1']"></div>
            </div>
          </div>

          <!-- Numeric knobs -->
          <div class="grid grid-cols-1 md:grid-cols-2 gap-3">
            <div class="p-4 bg-white/[0.02] border border-white/5 rounded-2xl flex items-center gap-3">
              <div class="flex-grow min-w-0">
                <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80">Wave Size</p>
                <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">Strategies per wave. 1–8.</p>
              </div>
              <input type="number" min="1" max="8" v-model.number="appStore.config.waveSize"
                     @blur="saveNumberClamped('waveSize', 1, 8)"
                     class="w-20 bg-white/[0.02] border border-white/10 rounded-xl px-3 py-2 text-[11px] font-mono text-white/80 italic text-center focus:outline-none focus:border-emerald-500/40 transition-colors" />
            </div>

            <div class="p-4 bg-white/[0.02] border border-white/5 rounded-2xl flex items-center gap-3">
              <div class="flex-grow min-w-0">
                <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80">Stage Deadline (s)</p>
                <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">How long each wave waits before launching the next. 1–30.</p>
              </div>
              <input type="number" min="1" max="30" v-model.number="appStore.config.waveStageDeadlineSeconds"
                     @blur="saveNumberClamped('waveStageDeadlineSeconds', 1, 30)"
                     class="w-20 bg-white/[0.02] border border-white/10 rounded-xl px-3 py-2 text-[11px] font-mono text-white/80 italic text-center focus:outline-none focus:border-emerald-500/40 transition-colors" />
            </div>

            <div class="p-4 bg-white/[0.02] border border-white/5 rounded-2xl flex items-center gap-3">
              <div class="flex-grow min-w-0">
                <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80">Per-Host Budget</p>
                <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">Max yt-dlp spawns per host per window before falling back to cloud. 1–10.</p>
              </div>
              <input type="number" min="1" max="10" v-model.number="appStore.config.perHostRequestBudget"
                     @blur="saveNumberClamped('perHostRequestBudget', 1, 10)"
                     class="w-20 bg-white/[0.02] border border-white/10 rounded-xl px-3 py-2 text-[11px] font-mono text-white/80 italic text-center focus:outline-none focus:border-emerald-500/40 transition-colors" />
            </div>

            <div class="p-4 bg-white/[0.02] border border-white/5 rounded-2xl flex items-center gap-3">
              <div class="flex-grow min-w-0">
                <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80">Per-Host Window (s)</p>
                <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">Sliding window for the budget above. 5–120.</p>
              </div>
              <input type="number" min="5" max="120" v-model.number="appStore.config.perHostRequestWindowSeconds"
                     @blur="saveNumberClamped('perHostRequestWindowSeconds', 5, 120)"
                     class="w-20 bg-white/[0.02] border border-white/10 rounded-xl px-3 py-2 text-[11px] font-mono text-white/80 italic text-center focus:outline-none focus:border-emerald-500/40 transition-colors" />
            </div>

            <div class="md:col-span-2 p-4 bg-white/[0.02] border border-white/5 rounded-2xl flex items-center gap-3">
              <div class="flex-grow min-w-0">
                <p class="text-[11px] font-black uppercase tracking-widest italic text-white/80">Cloud (Tier 2) Timeout (s)</p>
                <p class="text-[8px] font-bold uppercase tracking-widest text-white/35 mt-0.5 leading-relaxed">How long the cloud resolver gets before it's marked as a failure. YouTube can need 30–60 seconds the first time it sees a video. Allowed: 5–180.</p>
              </div>
              <input type="number" min="5" max="180" v-model.number="appStore.config.tier2TimeoutSeconds"
                     @blur="saveNumberClamped('tier2TimeoutSeconds', 5, 180)"
                     class="w-20 bg-white/[0.02] border border-white/10 rounded-xl px-3 py-2 text-[11px] font-mono text-white/80 italic text-center focus:outline-none focus:border-emerald-500/40 transition-colors" />
            </div>
          </div>
        </div>
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

        <div @click="appStore.config.maskIp = !appStore.config.maskIp; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Mask IP (always WARP)</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.maskIp ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.maskIp ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Routes every video resolution and probe through Cloudflare WARP, so the host sees a Cloudflare edge IP instead of yours. Adds latency. The cloud resolver (Tier 2) stays direct. Switching this on while a YouTube session is active may need a fresh resolve.</p>
        </div>

        <div @click="appStore.config.autoPatchOnStart = !appStore.config.autoPatchOnStart; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Auto-Patch on Start</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.autoPatchOnStart ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.autoPatchOnStart ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Refresh VRChat's bundled yt-dlp on launch so it matches the bypass tools this app ships with.</p>
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
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Test the resolved URL is reachable before handing it to VRChat. Catches dead CDN links and stale cloud results; adds up to 5 seconds on a cold start.</p>
        </div>

        <div @click="appStore.config.enableAnonymousReporting = !appStore.config.enableAnonymousReporting; appStore.config.anonymousReportingPromptAnswered = true; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Anonymous Reporting</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.enableAnonymousReporting ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.enableAnonymousReporting ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">When every resolution method fails for a video, send a sanitized summary (domain only, no full URL, no usernames, no IPs) so the project can spot patterns. Off by default; toggling on opts in for future failures.</p>
        </div>

        <div @click="appStore.config.enableTierMemory = !appStore.config.enableTierMemory; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Strategy Memory</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.enableTierMemory ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.enableTierMemory ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Remember which strategy worked per host so the next play skips the cold race. Off = every play runs the full race.</p>
        </div>

        <div @click="appStore.config.autoUpdateYtDlp = !appStore.config.autoUpdateYtDlp; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Auto-Update yt-dlp</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.autoUpdateYtDlp ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.autoUpdateYtDlp ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Check GitHub for a newer yt-dlp on every launch. The Tier 3 fallback (yt-dlp-og) always stays pinned to VRChat's own copy.</p>
        </div>

        <div @click="appStore.config.enableBrowserExtract = !appStore.config.enableBrowserExtract; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Browser Extract Strategy</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.enableBrowserExtract ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.enableBrowserExtract ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Headless Edge/Chrome fallback for sites that need JavaScript to load. Uses an already-installed browser; no extra download.</p>
        </div>

        <!-- Sub-toggle: only meaningful when browser-extract is on AND no system browser is found.
             Off keeps PuppeteerSharp from grabbing a ~180 MB Chromium build behind your back. -->
        <div v-if="appStore.config.enableBrowserExtract"
             @click="appStore.config.downloadBundledChromium = !appStore.config.downloadBundledChromium; appStore.saveConfig()"
             class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Bundled Chromium Fallback</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.downloadBundledChromium ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.downloadBundledChromium ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">If no Edge/Chrome/Brave is installed, this lets browser-extract download its own Chromium (about 180 MB) the first time it needs one.</p>
        </div>

        <div @click="appStore.config.streamlinkDisableTwitchAds = !appStore.config.streamlinkDisableTwitchAds; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Filter Twitch Ads</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.streamlinkDisableTwitchAds ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.streamlinkDisableTwitchAds ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Strip Twitch ad segments from the Streamlink (Tier 0) feed. The picture freezes on the last good frame for the ad's duration; off = ads play through normally.</p>
        </div>

        <div @click="appStore.config.enableRelaySmoothnessDebug = !appStore.config.enableRelaySmoothnessDebug; appStore.saveConfig()" class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
          <div class="flex justify-between items-start mb-4">
            <h4 class="text-lg font-black uppercase tracking-tighter italic">Relay Smoothness Debug</h4>
            <div :class="['w-10 h-5 rounded-full relative transition-all duration-700', appStore.config.enableRelaySmoothnessDebug ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]' : 'bg-white/10 border border-white/10']">
              <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700', appStore.config.enableRelaySmoothnessDebug ? 'left-6' : 'left-1']"></div>
            </div>
          </div>
          <p class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">Logs how fast each video segment arrives. Slow segments show up at info, stalls at warning. Useful for spotting stutter that isn't a full playback failure.</p>
        </div>
      </div>

      <!-- Direct-connect hosts (skipped by the relay wrap) -->
      <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-blue-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl group">
        <div class="flex items-center justify-between gap-4">
          <div class="flex items-center gap-4">
            <div class="w-10 h-10 bg-blue-500/10 rounded-2xl flex items-center justify-center text-blue-500 group-hover:scale-110 transition-transform">
              <i class="bi bi-shield-lock text-xl"></i>
            </div>
            <div>
              <h4 class="text-lg font-black uppercase tracking-tighter italic">Direct-Connect Hosts (relay skip-list)</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">Hosts that need a direct connection from the player</p>
            </div>
          </div>
          <button @click="resetNativeAvProUaHosts()" class="shrink-0 px-4 py-2 bg-white/5 hover:bg-blue-500/10 text-white/50 hover:text-blue-400 rounded-xl font-black text-[8px] uppercase tracking-[0.2em] transition-all italic active:scale-95 border border-white/5 hover:border-blue-500/20">
            Reset Default
          </button>
        </div>

        <!-- Chip list. Click × on a chip to remove it. The input below adds new entries on Enter
             or comma; Backspace on empty input pops the last chip. Pasting a comma/space-delimited
             string splits and adds each entry. -->
        <div class="flex flex-wrap gap-2 items-center bg-white/[0.02] border border-white/5 rounded-2xl px-4 py-3 min-h-[58px] focus-within:border-blue-500/40 transition-colors group-hover:bg-white/[0.04]">
          <span v-for="host in (appStore.config.nativeAvProUaHosts ?? [])" :key="host"
                class="inline-flex items-center gap-2 pl-3 pr-1.5 py-1.5 rounded-xl bg-blue-500/10 border border-blue-500/25 text-[10px] font-mono text-blue-300/90 italic">
            {{ host }}
            <button @click="removeNativeUaHost(host)"
                    :title="`Remove ${host}`"
                    class="w-5 h-5 rounded-md flex items-center justify-center text-blue-200/60 hover:text-white hover:bg-red-500/40 transition-all">
              <i class="bi bi-x text-sm"></i>
            </button>
          </span>
          <input
            v-model="hostInputDraft"
            @keydown="onHostInputKeydown"
            @paste="onHostInputPaste"
            @blur="addNativeUaHost(hostInputDraft)"
            type="text"
            :placeholder="(appStore.config.nativeAvProUaHosts ?? []).length === 0 ? 'Add a host (e.g. vr-m.net) and press Enter' : 'Add another…'"
            class="flex-grow min-w-[140px] bg-transparent text-[10px] font-mono text-white/70 italic placeholder:text-white/25 focus:outline-none px-2 py-1"
          />
          <button v-if="hostInputDraft.trim().length > 0"
                  @click="addNativeUaHost(hostInputDraft)"
                  title="Add host"
                  class="shrink-0 w-7 h-7 rounded-lg bg-blue-600/80 hover:bg-blue-500 text-white flex items-center justify-center transition-all active:scale-90">
            <i class="bi bi-plus-lg text-xs"></i>
          </button>
        </div>

        <p class="text-[9px] text-white/40 font-bold uppercase tracking-widest leading-relaxed">Some hosts (like vr-m.net) only respond when they see traffic coming straight from VRChat's player. Hosts listed here are handed straight to VRChat untouched; every other host is routed through the relay by default.</p>
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

      <!-- Maintenance section: app updates + uninstall -->
      <div class="space-y-3">
        <div class="flex items-center gap-3 ml-2">
          <div class="w-1 h-6 bg-emerald-500/40 rounded-full"></div>
          <h3 class="text-[10px] font-black uppercase tracking-[0.3em] text-white/30 italic">Maintenance</h3>
        </div>

        <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-emerald-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl">
          <div class="flex items-center justify-between gap-4">
            <div>
              <h4 class="text-lg font-black uppercase tracking-tighter italic">Application updates</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">
                <template v-if="appStore.appUpdate.status === 'UpdateAvailable'">
                  <span class="text-emerald-300">{{ appStore.appUpdate.localVersion }} → {{ appStore.appUpdate.remoteVersion }}</span>
                </template>
                <template v-else-if="appStore.appUpdate.status === 'UpToDate'">
                  Up to date — {{ appStore.appUpdate.localVersion }}
                </template>
                <template v-else-if="appStore.appUpdate.status === 'Checking'">
                  Checking GitHub releases…
                </template>
                <template v-else-if="appStore.appUpdate.status === 'Failed'">
                  Last check failed: {{ appStore.appUpdate.detail }}
                </template>
                <template v-else>
                  Click "Check now" to query the latest release
                </template>
              </p>
            </div>
            <div class="flex gap-2">
              <button
                @click="appStore.refreshAppUpdate"
                class="px-5 py-2.5 bg-white/5 hover:bg-white/10 text-white/70 rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all border border-white/5 active:scale-95">
                Check now
              </button>
              <button
                v-if="appStore.appUpdate.status === 'UpdateAvailable'"
                @click="appStore.launchUpdater"
                class="px-5 py-2.5 bg-emerald-600 hover:bg-emerald-500 text-white rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all italic active:scale-95 shadow-xl shadow-emerald-600/20">
                Update now
              </button>
            </div>
          </div>
        </section>

        <section class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] space-y-6 hover:border-red-500/20 transition-all duration-500 shadow-2xl backdrop-blur-3xl">
          <div class="flex items-center justify-between gap-4">
            <div>
              <h4 class="text-lg font-black uppercase tracking-tighter italic">Uninstall WKVRCProxy</h4>
              <p class="text-[9px] text-white/50 font-black uppercase tracking-widest mt-0.5">
                Restores VRChat's original yt-dlp.exe and removes this install completely
              </p>
            </div>
            <button
              @click="confirmUninstall"
              class="px-5 py-2.5 bg-white/5 hover:bg-red-500/20 hover:text-red-400 text-white/50 rounded-2xl font-black text-[9px] uppercase tracking-[0.2em] transition-all border border-white/5 active:scale-95">
              Uninstall…
            </button>
          </div>
        </section>
      </div>

    </div>
  </div>
</template>
