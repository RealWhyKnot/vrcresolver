<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { useAppStore } from '../stores/appStore'
import CopyButton from '../components/ui/CopyButton.vue'

const appStore = useAppStore()

const mode = ref<'cloud' | 'p2p'>('cloud')

const modes = [
  { id: 'cloud', label: 'Cloud Link', icon: 'bi-link-45deg', accent: 'blue' as const },
  { id: 'p2p',   label: 'P2P Stream', icon: 'bi-broadcast',  accent: 'indigo' as const }
]

// Most recent successful resolution from history — provides a sensible default
const lastEntry = computed(() =>
  appStore.config.history.find(h => h.Success && h.OriginalUrl)
)

// User-editable URL to share. Pre-filled with last-played OriginalUrl.
const shareUrl = ref('')

onMounted(() => {
  if (!shareUrl.value && lastEntry.value?.OriginalUrl) {
    shareUrl.value = lastEntry.value.OriginalUrl
  }
})

watch(() => lastEntry.value?.OriginalUrl, (newUrl) => {
  if (!shareUrl.value && newUrl) shareUrl.value = newUrl
})

function useLastVideo() {
  if (lastEntry.value?.OriginalUrl) shareUrl.value = lastEntry.value.OriginalUrl
}

// --- Cloud Link (Mode B) ----------------------------------------------------
function startCloudResolve() {
  if (!shareUrl.value.trim()) return
  appStore.requestCloudResolve(shareUrl.value.trim())
}

function resetCloud() {
  appStore.resetCloudResolve()
}

// If user edits the URL after a resolve, reset the resolved state so they don't copy a stale one.
watch(shareUrl, () => {
  if (appStore.cloudResolveStatus === 'ready' || appStore.cloudResolveStatus === 'error') {
    appStore.resetCloudResolve()
  }
})

// --- P2P Stream (Mode A) ----------------------------------------------------
function startP2PStream() {
  if (!shareUrl.value.trim()) return
  appStore.startP2PShare(shareUrl.value.trim())
}

function stopP2PStream() {
  appStore.stopP2PShare()
}

// --- State progression indicator -------------------------------------------
// Drives the 4-step bar above each panel so users see clearly which phase they're in.
const cloudSteps = [
  { id: 'idle',      label: 'Paste URL' },
  { id: 'resolving', label: 'Resolving' },
  { id: 'ready',     label: 'Ready' }
]
const p2pSteps = [
  { id: 'idle',       label: 'Paste URL' },
  { id: 'connecting', label: 'Connecting' },
  { id: 'active',     label: 'Streaming' }
]

const cloudCurrentStep = computed(() => {
  const s = appStore.cloudResolveStatus
  return s === 'error' ? 'idle' : s
})
const p2pCurrentStep = computed(() => {
  const s = appStore.p2pShareStatus
  return s === 'error' ? 'idle' : s
})

function stepIndex(steps: { id: string }[], current: string) {
  return Math.max(0, steps.findIndex(s => s.id === current))
}
</script>

<template>
  <div class="h-full flex flex-col p-8 overflow-y-auto no-scrollbar">

    <!-- Header -->
    <div class="mb-6"
         v-motion :initial="{ opacity: 0, y: -8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 40 } }">
      <h2 class="text-2xl font-black uppercase tracking-tighter italic text-white/90">Share</h2>
      <p class="text-white/35 text-[9px] font-bold uppercase tracking-[0.2em] mt-1">Broadcast your stream to a friend via WhyKnot.dev</p>
    </div>

    <!-- URL input -->
    <div class="mb-4"
         v-motion :initial="{ opacity: 0, y: 8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 100 } }">
      <label class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30 mb-1.5 block">Video URL</label>
      <div class="flex gap-2">
        <input v-model="shareUrl" type="text" placeholder="Paste a YouTube / Twitch / direct video URL..."
               class="flex-1 px-3 py-2.5 bg-white/[0.04] border border-white/10 rounded-xl text-white/85 text-[10px] font-mono placeholder-white/20 focus:outline-none focus:border-blue-500/50 focus:bg-white/[0.06] transition-all" />
        <button @click="useLastVideo" :disabled="!lastEntry"
                class="px-3 py-2.5 bg-white/5 hover:bg-white/10 text-white/55 hover:text-white/80 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-25 disabled:cursor-not-allowed whitespace-nowrap">
          <i class="bi bi-clock-history mr-1"></i>Use Last
        </button>
      </div>
      <p v-if="lastEntry" class="text-white/20 text-[7px] mt-1.5 font-mono truncate">
        Last: {{ lastEntry.OriginalUrl }}
      </p>
      <p v-else class="text-white/20 text-[7px] mt-1.5 font-mono">
        Try: <span class="text-white/30">youtube.com/watch?v=…</span> · <span class="text-white/30">twitch.tv/…</span> · <span class="text-white/30">https://example.com/video.mp4</span>
      </p>
    </div>

    <!-- Mode selector — distinct accent per mode (Cloud = blue, P2P = indigo) so the two
         panels read as different features instead of a single styled-twice surface. -->
    <div class="flex gap-2 mb-5"
         v-motion :initial="{ opacity: 0, y: 8 }" :enter="{ opacity: 1, y: 0, transition: { duration: 450, delay: 160 } }">
      <button v-for="m in modes" :key="m.id" @click="mode = (m.id as 'cloud' | 'p2p')"
              class="flex-1 py-3 rounded-2xl text-[8px] font-black uppercase tracking-widest transition-all duration-300 italic active:scale-95"
              :class="mode === m.id
                ? (m.accent === 'indigo' ? 'bg-indigo-600 text-white shadow-lg shadow-indigo-600/20'
                                         : 'bg-blue-600 text-white shadow-lg shadow-blue-600/20')
                : 'bg-white/5 text-white/45 hover:bg-white/8 hover:text-white/65'">
        <i :class="'bi ' + m.icon + ' mr-1.5'"></i>{{ m.label }}
      </button>
    </div>

    <!-- ── Mode B: Cloud Link ── -->
    <template v-if="mode === 'cloud'">

      <!-- Step indicator -->
      <div class="flex items-center gap-2 mb-3 px-1">
        <template v-for="(s, idx) in cloudSteps" :key="s.id">
          <div class="flex items-center gap-1.5">
            <div :class="['w-1.5 h-1.5 rounded-full transition-all duration-500',
                          idx <= stepIndex(cloudSteps, cloudCurrentStep)
                            ? 'bg-blue-400 shadow-[0_0_8px_rgba(96,165,250,0.6)]'
                            : 'bg-white/15']"></div>
            <span :class="['text-[7px] font-black uppercase tracking-widest',
                           idx === stepIndex(cloudSteps, cloudCurrentStep)
                             ? 'text-blue-300'
                             : idx < stepIndex(cloudSteps, cloudCurrentStep)
                               ? 'text-white/45'
                               : 'text-white/20']">{{ s.label }}</span>
          </div>
          <div v-if="idx < cloudSteps.length - 1"
               :class="['flex-grow h-px transition-colors duration-500',
                        idx < stepIndex(cloudSteps, cloudCurrentStep) ? 'bg-blue-400/40' : 'bg-white/8']"></div>
        </template>
      </div>

    <Transition name="state-swap" mode="out-in">
      <div :key="appStore.cloudResolveStatus">

      <!-- Idle / no resolve yet -->
      <div v-if="appStore.cloudResolveStatus === 'idle'" class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Resolve to Direct CDN URL</p>
        <p class="text-white/45 text-[8px] leading-relaxed">
          Resolves your URL through the tier cascade (whyknot.dev cloud preferred) and returns a direct CDN link
          your friend can paste into VRChat or any browser-based video player — no localhost redirect.
        </p>
        <button @click="startCloudResolve" :disabled="!shareUrl.trim()"
                class="w-full py-3.5 bg-blue-600 hover:bg-blue-500 text-white rounded-xl font-black text-[8px] uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30 disabled:cursor-not-allowed shadow-lg shadow-blue-600/20">
          <i class="bi bi-cloud-arrow-down mr-1.5"></i>Resolve &amp; Copy
        </button>
      </div>

      <!-- Resolving -->
      <div v-else-if="appStore.cloudResolveStatus === 'resolving'"
           class="p-5 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3 text-center">
        <div class="flex justify-center gap-1.5 py-3">
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce"></div>
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce [animation-delay:0.15s]"></div>
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce [animation-delay:0.3s]"></div>
        </div>
        <p class="text-white/45 text-[8px] font-bold uppercase tracking-widest">Resolving through tier cascade...</p>
      </div>

      <!-- Ready -->
      <div v-else-if="appStore.cloudResolveStatus === 'ready'"
           class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-2">
            <div class="w-2 h-2 bg-green-500 rounded-full shadow-[0_0_6px_rgba(34,197,94,0.8)]"></div>
            <p class="text-green-400 text-[8px] font-black uppercase tracking-widest">Resolved</p>
          </div>
          <div class="flex items-center gap-1.5">
            <span v-if="appStore.cloudResolveTier" class="text-[7px] font-black uppercase tracking-widest px-2 py-0.5 rounded-full bg-blue-500/20 text-blue-400">{{ appStore.cloudResolveTier.toUpperCase() }}</span>
            <span v-if="appStore.cloudResolveHeight" class="text-[7px] font-black uppercase tracking-widest px-2 py-0.5 rounded-full bg-white/10 text-white/65">{{ appStore.cloudResolveHeight }}p</span>
          </div>
        </div>
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Direct Stream URL</p>
        <p class="text-white/80 text-[8px] font-mono break-all leading-relaxed">{{ appStore.cloudResolvedUrl }}</p>
        <div class="flex gap-2">
          <CopyButton :value="appStore.cloudResolvedUrl" label="Copy URL" accent="blue" />
          <button @click="resetCloud"
                  class="px-4 py-3 bg-white/5 hover:bg-white/10 text-white/45 hover:text-white/75 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all">
            <i class="bi bi-arrow-clockwise"></i>
          </button>
        </div>
        <!-- Promoted from a soft gray hint to an amber pill — signed CDN URLs expire and the
             user genuinely needs to paste promptly. -->
        <div class="flex items-center gap-2 pt-1">
          <span class="px-2.5 py-1 rounded-lg text-[8px] font-black uppercase tracking-widest border bg-amber-500/10 text-amber-300 border-amber-500/25">
            <i class="bi bi-clock mr-1"></i>Expires soon
          </span>
          <span class="text-white/35 text-[7px] leading-relaxed">CDN URLs are signed — paste immediately.</span>
        </div>
      </div>

      <!-- Error -->
      <div v-else-if="appStore.cloudResolveStatus === 'error'"
           class="p-4 bg-red-500/10 border border-red-500/20 rounded-2xl space-y-3">
        <p class="text-red-400 text-[8px] font-black uppercase tracking-widest">
          <i class="bi bi-exclamation-triangle mr-1.5"></i>Resolve Failed
        </p>
        <p class="text-red-400/65 text-[8px] leading-relaxed">{{ appStore.cloudResolveError }}</p>
        <button @click="startCloudResolve" :disabled="!shareUrl.trim()"
                class="w-full py-3 bg-white/5 hover:bg-white/8 text-white/65 rounded-xl font-black text-[8px] uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30">
          <i class="bi bi-arrow-clockwise mr-1.5"></i>Retry
        </button>
      </div>

      </div>
    </Transition>
    </template>

    <!-- ── Mode A: P2P Stream — accent: indigo ── -->
    <template v-if="mode === 'p2p'">

      <!-- Step indicator -->
      <div class="flex items-center gap-2 mb-3 px-1">
        <template v-for="(s, idx) in p2pSteps" :key="s.id">
          <div class="flex items-center gap-1.5">
            <div :class="['w-1.5 h-1.5 rounded-full transition-all duration-500',
                          idx <= stepIndex(p2pSteps, p2pCurrentStep)
                            ? 'bg-indigo-400 shadow-[0_0_8px_rgba(129,140,248,0.6)]'
                            : 'bg-white/15']"></div>
            <span :class="['text-[7px] font-black uppercase tracking-widest',
                           idx === stepIndex(p2pSteps, p2pCurrentStep)
                             ? 'text-indigo-300'
                             : idx < stepIndex(p2pSteps, p2pCurrentStep)
                               ? 'text-white/45'
                               : 'text-white/20']">{{ s.label }}</span>
          </div>
          <div v-if="idx < p2pSteps.length - 1"
               :class="['flex-grow h-px transition-colors duration-500',
                        idx < stepIndex(p2pSteps, p2pCurrentStep) ? 'bg-indigo-400/40' : 'bg-white/8']"></div>
        </template>
      </div>

    <Transition name="state-swap" mode="out-in">
      <div :key="appStore.p2pShareStatus">

      <!-- Idle -->
      <div v-if="appStore.p2pShareStatus === 'idle'" class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Stream via WhyKnot.dev</p>
        <p class="text-white/45 text-[8px] leading-relaxed">
          The program resolves your URL, connects to WhyKnot.dev, and relays chunks so a friend can watch from their
          browser or VRChat world — no account required.
        </p>
        <p class="text-amber-400/60 text-[8px] leading-relaxed">
          <i class="bi bi-exclamation-triangle mr-1"></i>Works best with direct video URLs (MP4/WebM). Live HLS streams may not relay correctly.
        </p>
        <button @click="startP2PStream" :disabled="!shareUrl.trim()"
                class="w-full py-3.5 bg-indigo-600 hover:bg-indigo-500 text-white rounded-xl font-black text-[8px] uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30 disabled:cursor-not-allowed shadow-lg shadow-indigo-600/20">
          <i class="bi bi-broadcast mr-1.5"></i>Start Streaming
        </button>
      </div>

      <!-- Connecting -->
      <div v-else-if="appStore.p2pShareStatus === 'connecting'"
           class="p-5 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3 text-center">
        <div class="flex justify-center gap-1.5 py-3">
          <div class="w-1.5 h-1.5 bg-indigo-400 rounded-full animate-bounce"></div>
          <div class="w-1.5 h-1.5 bg-indigo-400 rounded-full animate-bounce [animation-delay:0.15s]"></div>
          <div class="w-1.5 h-1.5 bg-indigo-400 rounded-full animate-bounce [animation-delay:0.3s]"></div>
        </div>
        <p class="text-white/45 text-[8px] font-bold uppercase tracking-widest">Resolving &amp; connecting to WhyKnot.dev...</p>
      </div>

      <!-- Active -->
      <div v-else-if="appStore.p2pShareStatus === 'active'"
           class="p-4 bg-white/[0.03] border border-white/5 rounded-2xl space-y-3">
        <div class="flex items-center gap-2">
          <div class="w-2 h-2 bg-indigo-400 rounded-full animate-pulse shadow-[0_0_6px_rgba(129,140,248,0.8)]"></div>
          <p class="text-indigo-300 text-[8px] font-black uppercase tracking-widest">Streaming Active</p>
        </div>
        <p class="text-[7px] font-bold uppercase tracking-[0.2em] text-white/30">Share this link with your friend:</p>
        <p class="text-white/80 text-[8px] font-mono break-all leading-relaxed">{{ appStore.p2pSharePublicUrl }}</p>
        <div class="flex gap-2">
          <CopyButton :value="appStore.p2pSharePublicUrl" label="Copy Link" accent="indigo" />
          <button @click="stopP2PStream"
                  class="px-4 py-3 bg-white/5 hover:bg-red-500/20 hover:text-red-400 text-white/45 rounded-xl text-[8px] font-black uppercase tracking-widest transition-all">
            <i class="bi bi-stop-fill"></i>
          </button>
        </div>
      </div>

      <!-- Error -->
      <div v-else-if="appStore.p2pShareStatus === 'error'"
           class="p-4 bg-red-500/10 border border-red-500/20 rounded-2xl space-y-3">
        <p class="text-red-400 text-[8px] font-black uppercase tracking-widest">
          <i class="bi bi-exclamation-triangle mr-1.5"></i>Stream Failed
        </p>
        <p class="text-red-400/65 text-[8px] leading-relaxed">{{ appStore.p2pShareError }}</p>
        <button @click="startP2PStream" :disabled="!shareUrl.trim()"
                class="w-full py-3 bg-white/5 hover:bg-white/8 text-white/65 rounded-xl font-black text-[8px] uppercase tracking-widest transition-all italic active:scale-95 disabled:opacity-30">
          <i class="bi bi-arrow-clockwise mr-1.5"></i>Retry
        </button>
      </div>

      </div>
    </Transition>
    </template>

  </div>
</template>

<style scoped>
.state-swap-enter-active { transition: all 350ms cubic-bezier(0.34, 1.56, 0.64, 1); }
.state-swap-leave-active { transition: all 200ms ease-in; }
.state-swap-enter-from   { opacity: 0; transform: translateY(8px) scale(0.97); }
.state-swap-leave-to     { opacity: 0; transform: translateY(-8px) scale(0.97); }
</style>
