<script setup lang="ts">
// PoC: frame whyknot.dev inside the program window. Dark by default behind
// `enableWebsiteTab` in app_config.json. Phase 1 has no native bridge — the iframe
// is a passive embed validating that WebView2 will frame a remote HTTPS origin from
// a file:// parent and that the iframe's own /mesh WebSocket connects.
//
// See docs/embed-website/DESIGN.md for the full design and Phase 2 bridge spec.
import { ref, computed } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const WEBSITE_URL = 'https://whyknot.dev'

// Track loading state: WebView2 fires `load` when the frame finishes; if it errors at
// the network layer (offline, DNS failure, TLS), `load` never fires and we'd be stuck
// on the spinner. A short timeout flips us to an error state with a retry path.
const status = ref<'loading' | 'ready' | 'error'>('loading')
const iframeKey = ref(0)
const errorMessage = ref('')

let loadTimer: number | undefined

function armLoadTimer() {
  if (loadTimer) window.clearTimeout(loadTimer)
  loadTimer = window.setTimeout(() => {
    if (status.value === 'loading') {
      status.value = 'error'
      errorMessage.value = 'No response from whyknot.dev within 15s. Check your connection.'
    }
  }, 15000)
}

function onIframeLoad() {
  if (loadTimer) window.clearTimeout(loadTimer)
  status.value = 'ready'
  errorMessage.value = ''
}

function reload() {
  status.value = 'loading'
  errorMessage.value = ''
  iframeKey.value++
  armLoadTimer()
}

function openExternal() {
  appStore.sendMessage('OPEN_BROWSER', { url: WEBSITE_URL })
}

armLoadTimer()

const statusLabel = computed(() => {
  switch (status.value) {
    case 'loading': return 'Connecting'
    case 'ready':   return 'Live'
    case 'error':   return 'Offline'
  }
})
</script>

<template>
  <div class="h-full flex flex-col">
    <!-- Thin top bar: source + status + actions. Keeps the embed self-identifying so the
         user always knows they're inside an embedded page. -->
    <div class="shrink-0 flex items-center gap-3 px-6 py-3 border-b border-white/5 bg-black/30 backdrop-blur-xl">
      <i class="bi bi-globe2 text-blue-400 text-base"></i>
      <div class="flex-1 min-w-0">
        <div class="text-[10px] font-black uppercase tracking-[0.2em] italic text-white/85">Website</div>
        <div class="text-[8px] font-mono text-white/35 truncate">{{ WEBSITE_URL }}</div>
      </div>
      <div class="flex items-center gap-1.5 px-2 py-1 rounded-md bg-white/5 border border-white/5"
           :title="statusLabel">
        <span class="w-1.5 h-1.5 rounded-full"
              :class="status === 'ready' ? 'bg-emerald-400 shadow-[0_0_6px_rgba(52,211,153,0.6)]'
                    : status === 'error' ? 'bg-red-400'
                    : 'bg-amber-400 animate-pulse'"></span>
        <span class="text-[7px] font-black uppercase tracking-[0.2em] text-white/55">{{ statusLabel }}</span>
      </div>
      <button @click="reload"
              class="px-2.5 py-1 rounded-md bg-white/5 hover:bg-white/10 text-white/55 hover:text-white text-[8px] font-black uppercase tracking-widest italic transition-all active:scale-95"
              title="Reload">
        <i class="bi bi-arrow-clockwise"></i>
      </button>
      <button @click="openExternal"
              class="px-2.5 py-1 rounded-md bg-white/5 hover:bg-white/10 text-white/55 hover:text-white text-[8px] font-black uppercase tracking-widest italic transition-all active:scale-95"
              title="Open in default browser">
        <i class="bi bi-box-arrow-up-right"></i>
      </button>
    </div>

    <!-- Frame area -->
    <div class="flex-1 relative bg-black/20">
      <!-- Loading overlay — covers iframe until first `load` fires or the timer trips -->
      <div v-if="status === 'loading'"
           class="absolute inset-0 z-10 flex flex-col items-center justify-center text-white/40 bg-[#0a0e1a]/80 backdrop-blur-sm">
        <div class="flex gap-1.5 mb-3">
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce"></div>
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce [animation-delay:0.15s]"></div>
          <div class="w-1.5 h-1.5 bg-blue-500 rounded-full animate-bounce [animation-delay:0.3s]"></div>
        </div>
        <p class="text-[9px] font-black uppercase tracking-widest italic">Connecting to whyknot.dev</p>
      </div>

      <!-- Error overlay -->
      <div v-if="status === 'error'"
           class="absolute inset-0 z-10 flex flex-col items-center justify-center px-8 text-center bg-[#0a0e1a]/95 backdrop-blur-sm">
        <div class="w-12 h-12 bg-red-500/15 border border-red-500/30 rounded-2xl flex items-center justify-center mb-4">
          <i class="bi bi-cloud-slash text-red-400 text-xl"></i>
        </div>
        <h3 class="text-sm font-black uppercase tracking-tight italic text-white/85 mb-1">Embed unavailable</h3>
        <p class="text-[10px] text-white/45 max-w-sm mb-5 leading-relaxed">{{ errorMessage }}</p>
        <div class="flex gap-2">
          <button @click="reload"
                  class="px-4 py-2 rounded-xl bg-blue-600 hover:bg-blue-500 text-white text-[9px] font-black uppercase tracking-widest italic transition-all active:scale-95">
            <i class="bi bi-arrow-clockwise mr-1.5"></i>Retry
          </button>
          <button @click="openExternal"
                  class="px-4 py-2 rounded-xl bg-white/5 hover:bg-white/10 text-white/65 hover:text-white text-[9px] font-black uppercase tracking-widest italic transition-all active:scale-95">
            <i class="bi bi-box-arrow-up-right mr-1.5"></i>Open in browser
          </button>
        </div>
      </div>

      <!-- The frame itself. `:key` forces remount on retry so a stuck iframe is fully torn down.
           Sandbox: scripts (the SPA needs them), same-origin (so the iframe can talk to its own
           origin's WS/cookies/storage), forms (logins/inputs), popups (open-in-new-tab links),
           popups-to-escape-sandbox (so popups land in a real browser tab via WebView2's default
           handling), and downloads (file saves). NO `allow-top-navigation` — the embedded page
           should never be able to navigate the program window itself. -->
      <iframe :key="iframeKey"
              :src="WEBSITE_URL"
              class="w-full h-full border-0 bg-white"
              sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox allow-downloads"
              referrerpolicy="strict-origin-when-cross-origin"
              @load="onIframeLoad" />
    </div>
  </div>
</template>
