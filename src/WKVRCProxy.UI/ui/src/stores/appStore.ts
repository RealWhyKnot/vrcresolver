import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

export const TIER_DISPLAY: Record<string, { short: string; long: string; color: string }> = {
  tier1: { short: 'Local',        long: 'Fastest, offline-capable local extraction.',            color: 'bg-blue-500'   },
  tier2: { short: 'Cloud',        long: 'Reliable cloud-based resolution via WhyKnot.dev.',      color: 'bg-purple-500' },
  tier3: { short: 'VRChat Tools', long: 'Original VRChat yt-dlp behavior.',                      color: 'bg-amber-500'  },
  tier4: { short: 'Passthrough',  long: 'No resolution, returns raw original URL.',              color: 'bg-white/20'   },
}

export interface RelayEvent {
  id: string;
  timestamp: string;
  targetUrl: string;
  method: string;
  statusCode: number;
  bytesTransferred: number;
}

export interface LogEntry {
  Timestamp: string;
  Level: number;
  Message: string;
  Source: string;
}

export interface HistoryEntry {
  Timestamp: string;
  OriginalUrl: string;
  ResolvedUrl: string;
  Tier: string;
  Player: string;
  Success: boolean;
  IsLive: boolean;
  StreamType: string; // "live" | "vod" | "unknown"
  ResolutionHeight?: number | null;
  ResolutionWidth?: number | null;
  Vcodec?: string | null;
  // Playback verification set by the feedback loop. `Success` only means "we returned a URL";
  // this is the post-hoc truth of whether AVPro accepted it.
  //   null  — pending or tier4 passthrough (no way to verify)
  //   true  — no AVPro "Loading failed" observed within verify window
  //   false — AVPro rejected the URL, or pre-flight probe rejected it
  PlaybackVerified?: boolean | null;
}

export interface AppConfig {
  debugMode: boolean;
  preferredResolution: string;
  forceIPv4: boolean;
  autoPatchOnStart: boolean;
  preferredTier: string;
  history: HistoryEntry[];
  userAgent: string;
  customVrcPath?: string;
  bypassHostsSetupDeclined?: boolean;
  enableRelayBypass: boolean;
  disabledTiers: string[];
  autoUpdateYtDlp?: boolean;
  enablePreflightProbe: boolean;
  nativeAvProUaHosts: string[];
}

export interface BypassMemoryEntry {
  strategy: string;
  successCount: number;
  failureCount: number;
  consecutiveFailures: number;
  netScore: number;
  lastSuccess: string;
  lastFailure: string | null;
  firstSeen: string;
}

export interface BypassMemoryRow {
  key: string;
  entries: BypassMemoryEntry[];
}

export interface YtDlpUpdateStatus {
  status: 'Idle' | 'Checking' | 'UpToDate' | 'UpdateAvailable' | 'Downloading' | 'Updated' | 'Failed' | 'Disabled';
  detail: string;
  localVersion: string;
  remoteVersion: string;
}

export interface AppStatus {
  message: string;
  stats: {
    activeCount: number;
    tierStats: Record<string, number>;
    node: string;
    player: string;
  }
}

// Emitted when the playback-feedback loop demotes a strategy — either because AVPro rejected the
// resolved URL (Opening → Error: Loading failed) or because the pre-flight probe returned a bad
// status. Surfaced to the Logs view as a dismissable chip so the user can see the feedback loop
// acting without parsing log lines.
export interface DemotionNotification {
  id: string;
  timestamp: string;
  strategyName: string;
  memKey: string;
  reason: string;
  correlationId?: string;
}

export type ToastVariant = 'info' | 'success' | 'warning' | 'error'

export interface Toast {
  id: string;
  variant: ToastVariant;
  title: string;
  message?: string;
  /** ms until auto-dismiss; 0 or less = sticky */
  timeoutMs?: number;
  createdAt: number;
}

export const useAppStore = defineStore('app', () => {
  const activeTab = ref('dashboard')
  const logs = ref<LogEntry[]>([])
  const logLevelFilter = ref<number | null>(null) // null = show all levels
  const logSourceFilter = ref<string>('')          // '' = show all sources

  const filteredLogs = computed(() => {
    return logs.value.filter(entry => {
      if (logLevelFilter.value !== null && entry.Level !== logLevelFilter.value) return false
      if (logSourceFilter.value && !entry.Source.toLowerCase().includes(logSourceFilter.value.toLowerCase())) return false
      return true
    })
  })
  
  const status = ref<AppStatus>({
    message: 'Ready',
    stats: {
      activeCount: 0,
      tierStats: { tier1: 0, tier2: 0, tier3: 0, tier4: 0 },
      node: 'None',
      player: 'None'
    }
  })

  const config = ref<AppConfig>({
    debugMode: true,
    preferredResolution: '1080p',
    forceIPv4: false,
    autoPatchOnStart: true,
    preferredTier: 'tier1',
    history: [],
    userAgent: '',
    bypassHostsSetupDeclined: false,
    enableRelayBypass: true,
    disabledTiers: [],
    enablePreflightProbe: true,
    nativeAvProUaHosts: ['vr-m.net']
  })
  
  const showHostsPrompt = ref(false)
  const relayEvents = ref<RelayEvent[]>([])

  // P2P Share state
  const p2pShareStatus = ref<'idle' | 'connecting' | 'active' | 'error'>('idle')
  const p2pSharePublicUrl = ref('')
  const p2pShareError = ref('')

  // Cloud Resolve state (for Share panel — resolves user-pasted URLs to direct CDN URL)
  const cloudResolveStatus = ref<'idle' | 'resolving' | 'ready' | 'error'>('idle')
  const cloudResolvedUrl = ref('')
  const cloudResolveTier = ref('')
  const cloudResolveHeight = ref<number | null>(null)
  const cloudResolveError = ref('')
  
  const isBridgeReady = ref(false)
  const version = ref('2026.4.23.5-C792')

  const demotions = ref<DemotionNotification[]>([])
  const DEMOTION_CAP = 20

  const bypassMemory = ref<BypassMemoryRow[]>([])
  const ytDlpUpdate = ref<YtDlpUpdateStatus>({
    status: 'Idle',
    detail: '',
    localVersion: '',
    remoteVersion: ''
  })
  // Previous status used to detect transitions (e.g. Checking -> Updated) for one-shot toasts.
  let _prevYtDlpStatus: YtDlpUpdateStatus['status'] = 'Idle'
  // Set when user clicks Save; cleared when the CONFIG echo returns. Lets us confirm persistence
  // on the round-trip rather than trusting the optimistic local mutation.
  let _pendingSaveToast = false

  const toasts = ref<Toast[]>([])
  const _toastTimers = new Map<string, number>()
  const TOAST_CAP = 6

  function enqueueToast(t: { variant: ToastVariant; title: string; message?: string; id?: string; timeoutMs?: number }) {
    const id = t.id ?? `toast-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
    const timeoutMs = t.timeoutMs ?? 4000
    const toast: Toast = {
      id,
      variant: t.variant,
      title: t.title,
      message: t.message,
      timeoutMs,
      createdAt: Date.now()
    }
    toasts.value.unshift(toast)
    if (toasts.value.length > TOAST_CAP) {
      const dropped = toasts.value.pop()
      if (dropped) {
        const timer = _toastTimers.get(dropped.id)
        if (timer) window.clearTimeout(timer)
        _toastTimers.delete(dropped.id)
      }
    }
    if (timeoutMs > 0) {
      const handle = window.setTimeout(() => dismissToast(id), timeoutMs)
      _toastTimers.set(id, handle)
    }
  }

  function dismissToast(id: string) {
    const timer = _toastTimers.get(id)
    if (timer) window.clearTimeout(timer)
    _toastTimers.delete(id)
    toasts.value = toasts.value.filter(t => t.id !== id)
  }

  function pauseToast(id: string) {
    const timer = _toastTimers.get(id)
    if (timer) window.clearTimeout(timer)
    _toastTimers.delete(id)
  }

  function resumeToast(id: string) {
    const t = toasts.value.find(x => x.id === id)
    if (!t || !t.timeoutMs || t.timeoutMs <= 0) return
    const handle = window.setTimeout(() => dismissToast(id), t.timeoutMs)
    _toastTimers.set(id, handle)
  }

  function handleMessage(message: string) {
    try {
      const parsed = JSON.parse(message)
      if (parsed.type === 'LOG') {
        const entry = parsed.data as LogEntry
        if (!logs.value.some(l => l.Message === entry.Message && l.Timestamp === entry.Timestamp)) {
          logs.value.push(entry)
          if (logs.value.length > 1000) logs.value.shift()
        }
      } else if (parsed.type === 'CONFIG') {
        config.value = parsed.data
        if (_pendingSaveToast) {
          enqueueToast({ variant: 'success', title: 'Settings saved' })
          _pendingSaveToast = false
        }
      } else if (parsed.type === 'STATUS') {
        status.value = parsed.data
      } else if (parsed.type === 'PROMPT_HOSTS_SETUP') {
        showHostsPrompt.value = true
      } else if (parsed.type === 'P2P_SHARE_STARTED') {
        p2pShareStatus.value = 'active'
        p2pSharePublicUrl.value = parsed.data?.publicUrl ?? ''
        enqueueToast({ variant: 'success', title: 'Stream active', message: 'Share link ready to copy.' })
      } else if (parsed.type === 'P2P_SHARE_STOPPED') {
        p2pShareStatus.value = 'idle'
        p2pSharePublicUrl.value = ''
      } else if (parsed.type === 'P2P_SHARE_ERROR') {
        p2pShareStatus.value = 'error'
        p2pShareError.value = parsed.data?.message ?? 'Unknown error'
        enqueueToast({ variant: 'error', title: 'Stream failed', message: p2pShareError.value, timeoutMs: 6000 })
      } else if (parsed.type === 'CLOUD_RESOLVE_RESULT') {
        if (parsed.data?.success) {
          cloudResolveStatus.value = 'ready'
          cloudResolvedUrl.value = parsed.data.url ?? ''
          cloudResolveTier.value = parsed.data.tier ?? ''
          cloudResolveHeight.value = parsed.data.height ?? null
          cloudResolveError.value = ''
          const height = cloudResolveHeight.value
          const tier = cloudResolveTier.value
          enqueueToast({
            variant: 'success',
            title: 'Resolved',
            message: [tier && tier.toUpperCase(), height && `${height}p`].filter(Boolean).join(' · ') || undefined
          })
        } else {
          cloudResolveStatus.value = 'error'
          cloudResolveError.value = parsed.data?.message ?? 'Resolve failed'
          enqueueToast({ variant: 'error', title: 'Resolve failed', message: cloudResolveError.value, timeoutMs: 6000 })
        }
      } else if (parsed.type === 'RELAY_EVENT') {
        const e = parsed.data as RelayEvent;
        const idx = relayEvents.value.findIndex(x => x.id === e.id);
        if (idx >= 0) {
          relayEvents.value[idx] = e;
        } else {
          relayEvents.value.unshift(e);
          if (relayEvents.value.length > 100) relayEvents.value.pop();
        }
      } else if (parsed.type === 'BYPASS_MEMORY') {
        bypassMemory.value = (parsed.data ?? []) as BypassMemoryRow[]
      } else if (parsed.type === 'YTDLP_UPDATE') {
        const next = parsed.data as YtDlpUpdateStatus
        const prev = _prevYtDlpStatus
        ytDlpUpdate.value = next
        if (prev !== next.status) {
          if (next.status === 'Updated') {
            enqueueToast({
              variant: 'success',
              title: 'yt-dlp updated',
              message: next.localVersion ? `Now running ${next.localVersion}` : undefined
            })
          } else if (next.status === 'Failed' && prev !== 'Idle') {
            enqueueToast({ variant: 'error', title: 'yt-dlp update failed', message: next.detail || undefined, timeoutMs: 6000 })
          }
          _prevYtDlpStatus = next.status
        }
      } else if (parsed.type === 'STRATEGY_DEMOTED') {
        const p = parsed.data ?? {}
        const entry: DemotionNotification = {
          id: (parsed.correlationId ?? '') + '-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8),
          timestamp: new Date().toISOString(),
          strategyName: p.strategyName ?? 'unknown',
          memKey: p.memKey ?? '',
          reason: p.reason ?? 'playback feedback',
          correlationId: parsed.correlationId ?? undefined
        }
        demotions.value.unshift(entry)
        if (demotions.value.length > DEMOTION_CAP) demotions.value.pop()
        enqueueToast({
          variant: 'warning',
          title: `Demoted: ${entry.strategyName}`,
          message: entry.reason
        })
      }
    } catch (e) { }
  }

  function sendMessage(type: string, data?: any) {
    // @ts-ignore
    if (window.photino) {
      // @ts-ignore
      window.photino.sendMessage(JSON.stringify({ type, data }))
    }
  }

  function initBridge() {
    // @ts-ignore
    if (window.photino && window.photino.receiveMessage) {
      // @ts-ignore
      window.photino.receiveMessage(handleMessage)
      sendMessage('SYNC_LOGS')
      sendMessage('GET_CONFIG')
      sendMessage('GET_BYPASS_MEMORY')
      sendMessage('GET_YTDLP_UPDATE')
      isBridgeReady.value = true
      return true
    }
    return false
  }

  function saveConfig() {
    _pendingSaveToast = true
    sendMessage('SAVE_CONFIG', config.value)
  }

  function pickVrcPath() {
    sendMessage('PICK_VRC_PATH')
  }

  function wipeTools() {
    sendMessage('WIPE_TOOLS')
  }

  function terminate() {
    sendMessage('EXIT')
  }

  const successRate = computed(() => {
    const history = config.value.history
    if (history.length === 0) return 0
    const successes = history.filter(h => h.Success).length
    return Math.round((successes / history.length) * 100)
  })

  const liveStreamCount = computed(() => {
    return config.value.history.filter(h => h.IsLive).length
  })

  const totalBytesTransferred = computed(() => {
    return relayEvents.value.reduce((sum, e) => sum + e.bytesTransferred, 0)
  })

  function clearHistory() {
    config.value.history = []
    saveConfig()
  }

  function clearLogs() {
    logs.value = []
  }

  function startP2PShare(url: string) {
    p2pShareStatus.value = 'connecting'
    p2pShareError.value = ''
    sendMessage('START_P2P_SHARE', { url })
  }

  function stopP2PShare() {
    sendMessage('STOP_P2P_SHARE')
  }

  function requestCloudResolve(url: string) {
    cloudResolveStatus.value = 'resolving'
    cloudResolvedUrl.value = ''
    cloudResolveTier.value = ''
    cloudResolveHeight.value = null
    cloudResolveError.value = ''
    sendMessage('REQUEST_CLOUD_RESOLVE', { url })
  }

  function resetCloudResolve() {
    cloudResolveStatus.value = 'idle'
    cloudResolvedUrl.value = ''
    cloudResolveTier.value = ''
    cloudResolveHeight.value = null
    cloudResolveError.value = ''
  }

  function refreshBypassMemory() {
    sendMessage('GET_BYPASS_MEMORY')
  }

  function forgetBypassKey(key: string) {
    sendMessage('FORGET_BYPASS_KEY', { key })
  }

  function refreshYtDlpUpdate() {
    sendMessage('GET_YTDLP_UPDATE')
  }

  function dismissDemotion(id: string) {
    demotions.value = demotions.value.filter(d => d.id !== id)
  }

  function clearDemotions() {
    demotions.value = []
  }

  return {
    activeTab,
    logs,
    filteredLogs,
    logLevelFilter,
    logSourceFilter,
    config,
    status,
    isBridgeReady,
    version,
    showHostsPrompt,
    initBridge,
    sendMessage,
    saveConfig,
    pickVrcPath,
    wipeTools,
    terminate,
    relayEvents,
    successRate,
    liveStreamCount,
    totalBytesTransferred,
    clearHistory,
    clearLogs,
    p2pShareStatus,
    p2pSharePublicUrl,
    p2pShareError,
    startP2PShare,
    stopP2PShare,
    cloudResolveStatus,
    cloudResolvedUrl,
    cloudResolveTier,
    cloudResolveHeight,
    cloudResolveError,
    requestCloudResolve,
    resetCloudResolve,
    bypassMemory,
    ytDlpUpdate,
    refreshBypassMemory,
    forgetBypassKey,
    refreshYtDlpUpdate,
    demotions,
    dismissDemotion,
    clearDemotions,
    toasts,
    enqueueToast,
    dismissToast,
    pauseToast,
    resumeToast
  }
})


