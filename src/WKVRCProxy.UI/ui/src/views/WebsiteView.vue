<script setup lang="ts">
// Frame whyknot.dev inside the program window. The Website tab is unconditionally rendered
// (no flag); the embedded site is the canonical surface for share/upload/browse flows.
//
// Native bridge: the iframe at https://whyknot.dev posts
//   { type: 'wkBridge', method, requestId, args }
// to its parent. We validate event.origin against EMBED_ORIGIN, then forward to the host
// via the existing photino IPC as `WEBSITE_BRIDGE`. The host responds with
// `WEBSITE_BRIDGE_RESPONSE` carrying { requestId, ok, data?, error? }; we route that back to
// the iframe via iframe.contentWindow.postMessage with EMBED_ORIGIN as the explicit
// targetOrigin.
//
// Allowlist (host-enforced too — see Program.WebsiteBridge.cs): PICK_FILE,
// COPY_TO_CLIPBOARD, OPEN_IN_BROWSER, GET_HISTORY, GET_VERSION, GET_LAST_LINK.
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useAppStore } from '../stores/appStore'

const appStore = useAppStore()

const WEBSITE_URL = 'https://whyknot.dev'
const EMBED_ORIGIN = 'https://whyknot.dev'
// Vite dev server origin — only honoured when the UI was built in dev mode (import.meta.env.DEV
// is replaced by the bundler at build time, so production builds reject this origin).
const DEV_ORIGIN = 'http://localhost:5173'
const ALLOWED_METHODS = new Set([
  'PICK_FILE',
  'COPY_TO_CLIPBOARD',
  'OPEN_IN_BROWSER',
  'GET_HISTORY',
  'GET_VERSION',
  'GET_LAST_LINK',
])

function isAllowedOrigin(origin: string): boolean {
  if (origin === EMBED_ORIGIN) return true
  if (import.meta.env.DEV && origin === DEV_ORIGIN) return true
  return false
}

const status = ref<'loading' | 'ready' | 'error'>('loading')
const iframeKey = ref(0)
const errorMessage = ref('')

let loadTimer: number | undefined

// In-flight bridge requests this view forwarded to the host. Used to filter
// WEBSITE_BRIDGE_RESPONSE messages back to the originating iframe contentWindow.
const pendingRequests = new Map<string, { source: Window; sourceOrigin: string }>()
// One-shot warning per (origin, method) so a misbehaving page can't flood the console.
const warnedRejections = new Set<string>()

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
  pendingRequests.clear()
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

function handleBridgeMessage(event: MessageEvent) {
  // Origin guard FIRST — anything outside the allowlist is dropped before we even look at
  // the payload shape, so a foreign window can't probe our protocol.
  if (!isAllowedOrigin(event.origin)) return
  const data = event.data
  if (!data || typeof data !== 'object') return
  if (data.type !== 'wkBridge') return

  const method = typeof data.method === 'string' ? data.method : ''
  const requestId = typeof data.requestId === 'string' ? data.requestId : ''
  const args = data.args ?? {}

  if (!ALLOWED_METHODS.has(method)) {
    const key = event.origin + '|' + method
    if (!warnedRejections.has(key)) {
      warnedRejections.add(key)
      console.warn('[wkBridge] rejected method', method, 'from', event.origin)
    }
    if (requestId && event.source) {
      try {
        ;(event.source as Window).postMessage(
          { type: 'wkBridge', requestId, ok: false, error: 'method not allowed' },
          event.origin as any,
        )
      } catch { /* best-effort reply */ }
    }
    return
  }

  if (requestId && event.source instanceof Window) {
    pendingRequests.set(requestId, { source: event.source, sourceOrigin: event.origin })
  }

  appStore.sendMessage('WEBSITE_BRIDGE', { method, requestId, args })
}

function handleHostResponse(event: Event) {
  const payload = (event as CustomEvent).detail
  if (!payload || typeof payload !== 'object') return
  const requestId = typeof payload.requestId === 'string' ? payload.requestId : ''
  if (!requestId) return
  const pending = pendingRequests.get(requestId)
  if (!pending) return
  pendingRequests.delete(requestId)
  try {
    pending.source.postMessage(
      {
        type: 'wkBridge',
        requestId,
        ok: !!payload.ok,
        data: payload.data,
        error: payload.error,
      },
      pending.sourceOrigin as any,
    )
  } catch (e) {
    console.warn('[wkBridge] failed to deliver response to iframe:', e)
  }
}

onMounted(() => {
  window.addEventListener('message', handleBridgeMessage)
  // appStore.handleMessage re-emits WEBSITE_BRIDGE_RESPONSE as a DOM event so this view
  // can route it back to the right iframe contentWindow without owning the photino bridge.
  window.addEventListener('wkBridgeResponse', handleHostResponse)
})

onBeforeUnmount(() => {
  window.removeEventListener('message', handleBridgeMessage)
  window.removeEventListener('wkBridgeResponse', handleHostResponse)
  if (loadTimer) window.clearTimeout(loadTimer)
  pendingRequests.clear()
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
