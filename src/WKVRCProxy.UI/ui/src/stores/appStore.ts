import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

// Types + constants live in appStore.types.ts so this file can stay focused on store logic.
// Re-export so existing `import { LogEntry, AppConfig, ... } from '../stores/appStore'` calls
// across the UI keep working without each consumer needing to update its import path.
export {
  TIER_DISPLAY,
  STRATEGY_PRIORITY_DEFAULTS,
} from './appStore.types'
export type {
  RelayEvent,
  LogEntry,
  HistoryEntry,
  AppConfig,
  BypassMemoryEntry,
  BypassMemoryRow,
  YtDlpUpdateStatus,
  AppUpdateStatus,
  AppStatus,
  DemotionNotification,
  ToastVariant,
  Toast,
} from './appStore.types'

// Internal: only the types/constants the store body actually references. Re-exports above
// surface the rest to consumers without needing the store to import them.
import { STRATEGY_PRIORITY_DEFAULTS } from './appStore.types'
import type {
  RelayEvent,
  LogEntry,
  AppConfig,
  BypassMemoryRow,
  YtDlpUpdateStatus,
  AppUpdateStatus,
  AppStatus,
  DemotionNotification,
  ToastVariant,
  Toast,
} from './appStore.types'

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
    nativeAvProUaHosts: ['vr-m.net'],
    strategyPriority: [...STRATEGY_PRIORITY_DEFAULTS],
    enableWaveRace: true,
    maskIp: false,
    enableAnonymousReporting: false,
    anonymousReportingPromptAnswered: false,
    userOverriddenKeys: [],
    waveSize: 2,
    waveStageDeadlineSeconds: 3,
    perHostRequestBudget: 3,
    perHostRequestWindowSeconds: 10,
    enableTierMemory: true,
    tier2TimeoutSeconds: 60,
    enableBrowserExtract: true,
    downloadBundledChromium: false,
    streamlinkDisableTwitchAds: false,
    enableRelaySmoothnessDebug: true,
    enableWebsiteTab: false,
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
  const version = ref('2026.4.27.14-A130')

  // Session uptime — store-owned ticker so the value survives DashboardView unmounts.
  // Previously DashboardView held its own setInterval + Date.now() baseline, which meant
  // every time the user navigated away and back the clock reset to 00:00:00.
  const sessionStartedAt = ref(Date.now())
  const uptimeMs = ref(0)
  window.setInterval(() => {
    uptimeMs.value = Date.now() - sessionStartedAt.value
  }, 1000)

  // Changelog viewer state. Content is fetched lazily on first open via GET_CHANGELOG IPC,
  // then cached for the rest of the session. The C# side reads CHANGELOG.md from the
  // embedded resources of WKVRCProxy.UI.exe.
  const changelog = ref<string>('')
  const changelogLoading = ref(false)
  const showChangelogModal = ref(false)

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

  const appUpdate = ref<AppUpdateStatus>({
    status: 'Idle',
    detail: '',
    localVersion: '',
    remoteVersion: '',
    releaseUrl: '',
    downloadUrl: ''
  })
  const showAppUpdatePrompt = ref(false)
  const dismissedAppUpdatePromptVersion = ref('')
  let _prevAppUpdateStatus: AppUpdateStatus['status'] = 'Idle'
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
        // Defensive fill for fields an older backend may not have sent; lets the UI render
        // immediately without waiting for the user to touch Settings. Keeps new config
        // surface area backward-compatible.
        const incoming = parsed.data as AppConfig
        if (!incoming.strategyPriority || incoming.strategyPriority.length === 0) {
          incoming.strategyPriority = [...STRATEGY_PRIORITY_DEFAULTS]
        }
        if (!Array.isArray(incoming.userOverriddenKeys)) {
          incoming.userOverriddenKeys = []
        }
        if (typeof incoming.maskIp !== 'boolean') {
          incoming.maskIp = false
        }
        if (typeof incoming.enableAnonymousReporting !== 'boolean') {
          incoming.enableAnonymousReporting = false
        }
        if (typeof incoming.anonymousReportingPromptAnswered !== 'boolean') {
          incoming.anonymousReportingPromptAnswered = false
        }
        if (incoming.enableWaveRace === undefined) incoming.enableWaveRace = true
        if (!incoming.waveSize || incoming.waveSize < 1) incoming.waveSize = 2
        if (!incoming.waveStageDeadlineSeconds || incoming.waveStageDeadlineSeconds < 1) incoming.waveStageDeadlineSeconds = 3
        if (!incoming.perHostRequestBudget || incoming.perHostRequestBudget < 1) incoming.perHostRequestBudget = 3
        if (!incoming.perHostRequestWindowSeconds || incoming.perHostRequestWindowSeconds < 1) incoming.perHostRequestWindowSeconds = 10
        if (incoming.enableTierMemory === undefined) incoming.enableTierMemory = true
        if (!incoming.tier2TimeoutSeconds || incoming.tier2TimeoutSeconds < 5) incoming.tier2TimeoutSeconds = 60
        if (incoming.enableBrowserExtract === undefined) incoming.enableBrowserExtract = true
        if (incoming.downloadBundledChromium === undefined) incoming.downloadBundledChromium = false
        if (incoming.streamlinkDisableTwitchAds === undefined) incoming.streamlinkDisableTwitchAds = false
        if (incoming.enableRelaySmoothnessDebug === undefined) incoming.enableRelaySmoothnessDebug = true
        config.value = incoming
        if (_pendingSaveToast) {
          enqueueToast({ variant: 'success', title: 'Settings saved' })
          _pendingSaveToast = false
        }
      } else if (parsed.type === 'STATUS') {
        status.value = parsed.data
      } else if (parsed.type === 'PROMPT_HOSTS_SETUP') {
        showHostsPrompt.value = true
      } else if (parsed.type === 'SIDECAR_ERROR') {
        sidecarError.value = {
          exe: parsed.data?.exe ?? '',
          message: parsed.data?.message ?? 'Unknown error',
          canForce: parsed.data?.canForce !== false,
        }
        enqueueToast({
          variant: 'error',
          title: 'Couldn\'t launch ' + (parsed.data?.exe ?? 'updater'),
          message: parsed.data?.message,
          timeoutMs: 8000,
        })
      } else if (parsed.type === 'PROMPT') {
        // Generic prompt envelope from SystemEventBus.PublishPrompt: { type, data }.
        const inner = parsed.data ?? {}
        const innerType = inner.type ?? ''
        if (innerType === 'anonymousReportingOptIn') {
          reportingOptInPreview.value = (inner.data?.preview ?? '') as string
          showReportingOptInPrompt.value = true
        }
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
      } else if (parsed.type === 'APP_UPDATE') {
        const next = parsed.data as AppUpdateStatus
        const prev = _prevAppUpdateStatus
        appUpdate.value = next
        // Surface the modal once per session per version. If the user has dismissed this exact
        // remote version already, only the persistent banner shows.
        if (next.status === 'UpdateAvailable' &&
            prev !== 'UpdateAvailable' &&
            next.remoteVersion !== dismissedAppUpdatePromptVersion.value) {
          showAppUpdatePrompt.value = true
        }
        _prevAppUpdateStatus = next.status
      } else if (parsed.type === 'CHANGELOG') {
        // C# replies to GET_CHANGELOG with the raw markdown of CHANGELOG.md (embedded resource).
        // We render it client-side in App.vue using `marked` so the C# side stays dumb.
        changelog.value = (parsed.data?.content ?? '') as string
        changelogLoading.value = false
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
      sendMessage('GET_APP_UPDATE')
      isBridgeReady.value = true
      return true
    }
    return false
  }

  function saveConfig() {
    _pendingSaveToast = true
    sendMessage('SAVE_CONFIG', config.value)
  }

  // Override-tracking helpers. Call markOverridden(key) right after mutating a default-tracked
  // field so the backend stops re-syncing it from source on subsequent loads. clearOverridden
  // pairs with a "reset to default" action: it removes the key, leaving the next backend load
  // to pull the current code default in.
  function markOverridden(key: string) {
    const list = config.value.userOverriddenKeys ?? []
    if (!list.includes(key)) {
      config.value.userOverriddenKeys = [...list, key]
    }
  }

  function clearOverridden(key: string) {
    const list = config.value.userOverriddenKeys ?? []
    if (list.includes(key)) {
      config.value.userOverriddenKeys = list.filter(k => k !== key)
    }
  }

  function isOverridden(key: string): boolean {
    return (config.value.userOverriddenKeys ?? []).includes(key)
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
    // Coalesce undefined bytes (in-flight events) to 0 — a single missing field would
    // otherwise NaN-poison the sum and the UI would render "NaN B" / "undefined".
    return relayEvents.value.reduce((sum, e) => sum + (e.bytesTransferred ?? 0), 0)
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

  function refreshAppUpdate() {
    sendMessage('APP_UPDATE_CHECK')
  }

  function launchUpdater() {
    sendMessage('LAUNCH_UPDATER')
  }

  // Force-update path — used after the normal launch returns ERROR_CANCELLED (UAC declined or
  // SmartScreen blocked). Backend strips Mark-of-the-Web via Unblock-File, then re-launches the
  // sidecar with Verb=runas so Windows shows an explicit admin elevation prompt.
  function launchUpdaterForce() {
    sendMessage('LAUNCH_UPDATER_FORCE')
  }

  function launchUninstaller() {
    sendMessage('LAUNCH_UNINSTALLER')
  }

  function launchUninstallerForce() {
    sendMessage('LAUNCH_UNINSTALLER_FORCE')
  }

  // Sidecar (updater/uninstall) launch failure surface. Backend emits SIDECAR_ERROR with
  // { exe, message, canForce }; UI surfaces a toast or banner offering the force-elevation
  // retry when canForce=true. canForce=false means the force path was already tried — no
  // point re-prompting UAC the user already declined.
  const sidecarError = ref<{ exe: string; message: string; canForce: boolean } | null>(null)
  function dismissSidecarError() { sidecarError.value = null }

  // Anonymous-reporting opt-in modal state. The backend publishes a "PROMPT" event with type
  // "anonymousReportingOptIn" and a sanitized JSON preview of what would be sent on the next
  // cascade failure. The UI surfaces a modal so the user can review the actual payload before
  // deciding. Once answered, the backend stops asking.
  const showReportingOptInPrompt = ref(false)
  const reportingOptInPreview = ref('')

  function answerAnonymousReporting(optIn: boolean) {
    showReportingOptInPrompt.value = false
    sendMessage('SET_ANONYMOUS_REPORTING', { optIn })
  }

  // Opens the changelog modal. First call sends GET_CHANGELOG so the C# side can stream
  // the embedded CHANGELOG.md back; subsequent opens reuse the cached content. The modal
  // shows immediately with a loading state so the user isn't staring at a blank screen
  // during the (typically <50 ms) round-trip.
  function openChangelog() {
    showChangelogModal.value = true
    if (!changelog.value && !changelogLoading.value) {
      changelogLoading.value = true
      sendMessage('GET_CHANGELOG')
    }
  }

  function closeChangelog() {
    showChangelogModal.value = false
  }

  function openReleasesPage() {
    sendMessage('OPEN_BROWSER', { url: 'https://github.com/RealWhyKnot/WKVRCProxy/releases' })
  }

  function dismissAppUpdatePrompt(skipThisVersion = false) {
    if (skipThisVersion) {
      dismissedAppUpdatePromptVersion.value = appUpdate.value.remoteVersion
    }
    showAppUpdatePrompt.value = false
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
    markOverridden,
    clearOverridden,
    isOverridden,
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
    appUpdate,
    showAppUpdatePrompt,
    showReportingOptInPrompt,
    reportingOptInPreview,
    answerAnonymousReporting,
    refreshBypassMemory,
    forgetBypassKey,
    refreshYtDlpUpdate,
    refreshAppUpdate,
    launchUpdater,
    launchUpdaterForce,
    launchUninstaller,
    launchUninstallerForce,
    sidecarError,
    dismissSidecarError,
    dismissAppUpdatePrompt,
    demotions,
    dismissDemotion,
    clearDemotions,
    toasts,
    enqueueToast,
    dismissToast,
    pauseToast,
    resumeToast,
    uptimeMs,
    sessionStartedAt,
    changelog,
    changelogLoading,
    showChangelogModal,
    openChangelog,
    closeChangelog,
    openReleasesPage
  }
})


