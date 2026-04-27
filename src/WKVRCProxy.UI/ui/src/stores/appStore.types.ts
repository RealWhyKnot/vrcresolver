// Type definitions + constants shared across the UI. Extracted from appStore.ts to keep the
// store body focused on state + actions; types live here so other components can import them
// without pulling in the store's runtime dependencies.

export const TIER_DISPLAY: Record<string, { short: string; long: string; color: string }> = {
  tier1: { short: 'Local',        long: 'Resolves URLs locally with yt-dlp. The default first stop.',          color: 'bg-blue-500'   },
  tier2: { short: 'Cloud',        long: "Hands off to the WhyKnot.dev resolver from a different IP.",          color: 'bg-purple-500' },
  tier3: { short: 'VRChat Tools', long: "Last resort: runs the yt-dlp.exe that VRChat itself ships.",          color: 'bg-amber-500'  },
  tier4: { short: 'Passthrough',  long: "No resolution. The original URL is handed straight to VRChat.",       color: 'bg-white/20'   },
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
  strategyPriority: string[];
  enableWaveRace: boolean;
  // When true, every origin-facing strategy egresses through Cloudflare WARP. Tier 2 (whyknot.dev)
  // stays direct. Backend gates yt-dlp/browser-extract/yt-dlp-og on this; UI just sets the bool
  // (plus calls EnsureRunningAsync via SAVE_CONFIG echo).
  maskIp: boolean;
  // Anonymous failure reporting opt-in. When enabled, end-of-cascade failures POST a sanitized
  // summary to whyknot.dev/api/report (forwards to a private Discord channel). Strict client-side
  // sanitization strips PII before transmission. The first cascade failure after install surfaces
  // a modal asking the user to opt in or decline; that decision sets enableAnonymousReporting and
  // anonymousReportingPromptAnswered.
  enableAnonymousReporting: boolean;
  anonymousReportingPromptAnswered: boolean;
  // Names of fields the user has explicitly customized via the UI. Backend re-pulls the current
  // code default for any default-tracked field NOT in this set on next load. Helpers
  // markOverridden / clearOverridden manage entries.
  userOverriddenKeys: string[];
  waveSize: number;
  waveStageDeadlineSeconds: number;
  perHostRequestBudget: number;
  perHostRequestWindowSeconds: number;
  // Newly surfaced — backend supported these all along, the UI just didn't expose them.
  enableTierMemory: boolean;
  tier2TimeoutSeconds: number;
  enableBrowserExtract: boolean;
  downloadBundledChromium: boolean;
  streamlinkDisableTwitchAds: boolean;
  enableRelaySmoothnessDebug: boolean;
  // PoC flag: shows a "Website" tab that iframes https://whyknot.dev. Dark by default;
  // flip to true in app_config.json. Design lives in docs/embed-website/.
  enableWebsiteTab: boolean;
}

// Canonical default StrategyPriority list, mirrored from StrategyDefaults.PriorityDefaults
// in AppConfig.cs / StrategyDefaults.cs. The backend re-pulls this on load for any user whose
// userOverriddenKeys does NOT contain "strategyPriority" — so editing this constant flows out
// to non-customized users automatically. Customized lists are preserved verbatim.
export const STRATEGY_PRIORITY_DEFAULTS: string[] = [
  'tier1:yt-combo',        // one subprocess tries every YouTube player_client internally
  'tier2:cloud-whyknot',   // cross-IP fallback
  'tier1:ipv6',            // route around v4-only rate flags
  'tier1:default',         // non-YouTube hosts (auto PO + impersonate)
  'tier1:vrchat-ua',       // VRChat-looking traffic for hosts that gate on it
  'tier1:impersonate-only',// TLS-fingerprint-sensitive origins
  'tier1:plain',           // bare yt-dlp last-resort
  'tier1:browser-extract', // JS-gated sites
  'tier1:warp+default',    // WARP-egress mirror of Default
  'tier1:warp+vrchat-ua',  // WARP-egress mirror of VRChat UA
  'tier3:plain',           // VRChat's pinned yt-dlp-og
]

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

export interface AppUpdateStatus {
  status: 'Idle' | 'Checking' | 'UpToDate' | 'UpdateAvailable' | 'Failed';
  detail: string;
  localVersion: string;
  remoteVersion: string;
  releaseUrl: string;
  downloadUrl: string;
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
