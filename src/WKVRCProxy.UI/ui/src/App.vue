<script setup lang="ts">
import { onMounted } from 'vue'
import { useAppStore } from './stores/appStore'
import ThreeBackground from './components/ThreeBackground.vue'
import Sidebar from './components/layout/Sidebar.vue'
import ToastContainer from './components/ToastContainer.vue'
import ActivityBar from './components/ActivityBar.vue'
import DashboardView from './views/DashboardView.vue'
import HistoryView from './views/HistoryView.vue'
import BypassView from './views/BypassView.vue'
import ShareView from './views/ShareView.vue'
import LogsView from './views/LogsView.vue'
import SettingsView from './views/SettingsView.vue'
import RelayView from './views/RelayView.vue'

const appStore = useAppStore()

const acceptHostsPrompt = () => {
  appStore.sendMessage('HOSTS_SETUP_ACCEPTED')
  appStore.showHostsPrompt = false
}

const declineHostsPrompt = (neverAskAgain: boolean) => {
  if (neverAskAgain) {
    appStore.config.bypassHostsSetupDeclined = true
    appStore.saveConfig()
  }
  appStore.sendMessage('HOSTS_SETUP_DECLINED')
  appStore.showHostsPrompt = false
}

const acceptAppUpdate = () => {
  appStore.dismissAppUpdatePrompt(false)
  appStore.launchUpdater()
}

const skipAppUpdate = () => appStore.dismissAppUpdatePrompt(true)
const laterAppUpdate = () => appStore.dismissAppUpdatePrompt(false)

onMounted(() => {
  if (!appStore.initBridge()) {
    window.addEventListener('photino-ready', () => {
      appStore.initBridge();
    });
  }
})
</script>

<template>
  <div class="h-screen w-screen flex overflow-hidden bg-[#0a0e1a] text-white font-sans selection:bg-blue-500/30 antialiased">
    <!-- Critical Failure Overlay -->
    <div v-if="!appStore.isBridgeReady" class="fixed inset-0 z-[100] bg-black flex items-center justify-center backdrop-blur-3xl animate-in fade-in duration-500">
      <div class="text-center space-y-6 max-w-md p-10">
        <div class="w-16 h-16 bg-red-500/20 border border-red-500/40 rounded-3xl flex items-center justify-center mx-auto mb-6 animate-pulse shadow-2xl shadow-red-500/20">
          <i class="bi bi-exclamation-triangle-fill text-red-500 text-3xl"></i>
        </div>
        <h1 class="text-2xl font-black uppercase tracking-tighter italic">Link Failure</h1>
        <p class="text-white/65 text-[11px] font-bold leading-relaxed uppercase tracking-[0.2em]">Unable to connect to system core.</p>
        <div class="flex justify-center gap-2">
          <div class="w-1 h-1 bg-red-500 rounded-full animate-bounce"></div>
          <div class="w-1 h-1 bg-red-500 rounded-full animate-bounce [animation-delay:0.2s]"></div>
          <div class="w-1 h-1 bg-red-500 rounded-full animate-bounce [animation-delay:0.4s]"></div>
        </div>
      </div>
    </div>

    <!-- Hosts Setup Prompt Overlay -->
    <div v-if="appStore.showHostsPrompt" class="fixed inset-0 z-[90] bg-black/80 flex items-center justify-center backdrop-blur-xl animate-in fade-in duration-300">
      <div class="bg-[#0a0a0c] border border-white/10 rounded-3xl p-8 max-w-lg shadow-2xl relative overflow-hidden mx-4">
        <div class="absolute inset-0 bg-gradient-to-br from-blue-500/10 to-transparent pointer-events-none"></div>
        <div class="relative z-10 space-y-6">
          <div class="w-12 h-12 bg-blue-500/20 rounded-2xl flex items-center justify-center border border-blue-500/30">
            <i class="bi bi-shield-lock-fill text-blue-400 text-xl"></i>
          </div>
          <div>
            <h2 class="text-xl font-bold mb-2">Network Configuration Required</h2>
            <p class="text-white/60 text-sm leading-relaxed">
              To enable public world video proxying safely via <code class="bg-black/40 px-1 py-0.5 rounded text-blue-300">localhost.youtube.com</code>, we need to add a local route to your Windows hosts file.
            </p>
            <p class="text-white/60 text-xs mt-2 italic border-l-2 border-white/10 pl-3">
              This requires Administrator privileges to modify: <br/>C:\Windows\System32\drivers\etc\hosts
            </p>
          </div>
          <div class="flex flex-col gap-3 pt-4">
            <button @click="acceptHostsPrompt" class="bg-blue-600 hover:bg-blue-500 text-white font-bold py-3 px-6 rounded-xl transition-all shadow-lg shadow-blue-500/20 flex items-center justify-center gap-2">
              <i class="bi bi-shield-check"></i> Allow (Requires Admin)
            </button>
            <div class="flex gap-3">
              <button @click="declineHostsPrompt(false)" class="flex-1 bg-white/5 hover:bg-white/10 text-white/70 py-3 px-6 rounded-xl transition-all text-sm font-semibold">
                Not Now
              </button>
              <button @click="declineHostsPrompt(true)" class="flex-1 bg-white/5 hover:bg-red-500/20 hover:text-red-400 text-white/50 py-3 px-6 rounded-xl transition-all text-sm">
                Don't Ask Again
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- App Update Modal — shows once per session per remote version -->
    <div v-if="appStore.showAppUpdatePrompt" class="fixed inset-0 z-[95] bg-black/80 flex items-center justify-center backdrop-blur-xl animate-in fade-in duration-300">
      <div class="bg-[#0a0a0c] border border-white/10 rounded-3xl p-8 max-w-lg shadow-2xl relative overflow-hidden mx-4">
        <div class="absolute inset-0 bg-gradient-to-br from-emerald-500/10 to-transparent pointer-events-none"></div>
        <div class="relative z-10 space-y-6">
          <div class="w-12 h-12 bg-emerald-500/20 rounded-2xl flex items-center justify-center border border-emerald-500/30">
            <i class="bi bi-arrow-up-circle-fill text-emerald-400 text-xl"></i>
          </div>
          <div>
            <h2 class="text-xl font-bold mb-2">Update available</h2>
            <p class="text-white/60 text-sm leading-relaxed">
              <span class="text-white/40">{{ appStore.appUpdate.localVersion || 'this build' }}</span>
              <i class="bi bi-arrow-right mx-2 text-white/30"></i>
              <span class="text-emerald-300 font-mono text-xs">{{ appStore.appUpdate.remoteVersion }}</span>
            </p>
            <p class="text-white/50 text-xs mt-2 italic border-l-2 border-white/10 pl-3">
              The updater downloads the new build, swaps it in place, and relaunches WKVRCProxy. Your settings and bypass memory are preserved.
            </p>
          </div>
          <div class="flex flex-col gap-3 pt-4">
            <button @click="acceptAppUpdate" class="bg-emerald-600 hover:bg-emerald-500 text-white font-bold py-3 px-6 rounded-xl transition-all shadow-lg shadow-emerald-500/20 flex items-center justify-center gap-2">
              <i class="bi bi-download"></i> Update now
            </button>
            <div class="flex gap-3">
              <button @click="laterAppUpdate" class="flex-1 bg-white/5 hover:bg-white/10 text-white/70 py-3 px-6 rounded-xl transition-all text-sm font-semibold">
                Later
              </button>
              <button @click="skipAppUpdate" class="flex-1 bg-white/5 hover:bg-red-500/20 hover:text-red-400 text-white/50 py-3 px-6 rounded-xl transition-all text-sm">
                Skip this version
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Sidecar Launch Failure Modal — surfaces when updater.exe / uninstall.exe couldn't be
         launched (typically Windows installer-detection auto-UAC + user declined, or SmartScreen
         block). Offers a "force" retry that strips Mark-of-the-Web with Unblock-File and
         re-launches with Verb=runas for an explicit admin elevation prompt. -->
    <div v-if="appStore.sidecarError" class="fixed inset-0 z-[96] bg-black/80 flex items-center justify-center backdrop-blur-xl animate-in fade-in duration-300">
      <div class="bg-[#0a0a0c] border border-white/10 rounded-3xl p-8 max-w-lg shadow-2xl relative overflow-hidden mx-4">
        <div class="absolute inset-0 bg-gradient-to-br from-amber-500/10 to-transparent pointer-events-none"></div>
        <div class="relative z-10 space-y-6">
          <div class="w-12 h-12 bg-amber-500/20 rounded-2xl flex items-center justify-center border border-amber-500/30">
            <i class="bi bi-shield-exclamation text-amber-400 text-xl"></i>
          </div>
          <div>
            <h2 class="text-xl font-bold mb-2">Windows blocked the {{ appStore.sidecarError.exe }}</h2>
            <p class="text-white/60 text-sm leading-relaxed">
              Either SmartScreen flagged it as untrusted or the UAC prompt was dismissed. <span v-if="appStore.sidecarError.canForce">You can retry — we'll unblock the file (removes the SmartScreen mark) and trigger an explicit admin elevation prompt instead of Windows' installer-detection auto-prompt.</span>
            </p>
            <p class="text-white/40 text-xs mt-3 italic font-mono border-l-2 border-white/10 pl-3">{{ appStore.sidecarError.message }}</p>
          </div>
          <div class="flex flex-col gap-3 pt-2">
            <button v-if="appStore.sidecarError.canForce && appStore.sidecarError.exe.includes('updater')" @click="appStore.launchUpdaterForce(); appStore.dismissSidecarError()" class="bg-amber-600 hover:bg-amber-500 text-white font-bold py-3 px-6 rounded-xl transition-all shadow-lg shadow-amber-500/20 flex items-center justify-center gap-2">
              <i class="bi bi-shield-lock"></i> Retry with admin elevation
            </button>
            <button v-else-if="appStore.sidecarError.canForce && appStore.sidecarError.exe.includes('uninstall')" @click="appStore.launchUninstallerForce(); appStore.dismissSidecarError()" class="bg-amber-600 hover:bg-amber-500 text-white font-bold py-3 px-6 rounded-xl transition-all shadow-lg shadow-amber-500/20 flex items-center justify-center gap-2">
              <i class="bi bi-shield-lock"></i> Retry with admin elevation
            </button>
            <button @click="appStore.dismissSidecarError" class="bg-white/5 hover:bg-white/10 text-white/70 py-3 px-6 rounded-xl transition-all text-sm font-semibold">
              Close
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Anonymous Reporting Opt-In Modal — fires the first time end-of-cascade resolution
         fails. Shows the exact sanitized payload that would be sent so the user can verify
         what's transmitted before deciding. -->
    <div v-if="appStore.showReportingOptInPrompt" class="fixed inset-0 z-[95] bg-black/80 flex items-center justify-center backdrop-blur-xl animate-in fade-in duration-300">
      <div class="bg-[#0a0a0c] border border-white/10 rounded-3xl p-8 max-w-2xl shadow-2xl relative overflow-hidden mx-4 max-h-[85vh] flex flex-col">
        <div class="absolute inset-0 bg-gradient-to-br from-blue-500/10 to-transparent pointer-events-none"></div>
        <div class="relative z-10 space-y-6 flex-1 overflow-hidden flex flex-col">
          <div class="w-12 h-12 bg-blue-500/20 rounded-2xl flex items-center justify-center border border-blue-500/30 shrink-0">
            <i class="bi bi-shield-check text-blue-400 text-xl"></i>
          </div>
          <div class="shrink-0">
            <h2 class="text-xl font-bold mb-2">Help improve WKVRCProxy?</h2>
            <p class="text-white/60 text-sm leading-relaxed">
              A video just failed every resolution method. WKVRCProxy can send a sanitized summary of what went wrong so the project can spot patterns and fix recurring failures faster.
            </p>
            <p class="text-white/40 text-xs mt-2 italic border-l-2 border-white/10 pl-3">
              No usernames, no IPs, no full URLs, no file paths — only the host (e.g. <code class="text-blue-300">youtube.com</code>), a hashed identifier, and which strategies were tried. The payload below is exactly what gets sent.
            </p>
          </div>
          <div class="flex-1 overflow-auto bg-black/40 border border-white/5 rounded-2xl p-4 font-mono text-[10px] text-white/70 whitespace-pre-wrap leading-relaxed">{{ appStore.reportingOptInPreview }}</div>
          <div class="flex gap-3 pt-2 shrink-0">
            <button @click="appStore.answerAnonymousReporting(true)" class="flex-1 bg-blue-600 hover:bg-blue-500 text-white font-bold py-3 px-6 rounded-xl transition-all shadow-lg shadow-blue-500/20 flex items-center justify-center gap-2">
              <i class="bi bi-check-circle"></i> Send this and future reports
            </button>
            <button @click="appStore.answerAnonymousReporting(false)" class="flex-1 bg-white/5 hover:bg-white/10 text-white/70 py-3 px-6 rounded-xl transition-all text-sm font-semibold">
              No thanks
            </button>
          </div>
          <p class="text-white/30 text-[9px] text-center shrink-0">You can change this later in Settings → Network → Anonymous Reporting.</p>
        </div>
      </div>
    </div>

    <!-- Global activity bar + toasts (z-index above all content) -->
    <ActivityBar />
    <ToastContainer />

    <!-- 3D Background & Overlays -->
    <ThreeBackground :isReduced="appStore.activeTab !== 'dashboard'" />
    <div class="fixed inset-0 z-[1] pointer-events-none bg-[radial-gradient(circle_at_center,transparent_0%,#010103_85%)] opacity-60"></div>
    <div class="fixed inset-0 z-[1] pointer-events-none bg-gradient-to-b from-black/30 via-transparent to-black/20"></div>

    <!-- Sidebar -->
    <Sidebar class="z-20" />

    <!-- Main Content -->
    <main class="flex-grow flex flex-col relative z-10 h-full overflow-hidden">
      <!-- Persistent update banner — visible whenever an update is available, even after the modal is dismissed -->
      <button
        v-if="appStore.appUpdate.status === 'UpdateAvailable'"
        @click="appStore.launchUpdater"
        class="w-full px-6 py-2 bg-emerald-500/15 hover:bg-emerald-500/25 border-b border-emerald-500/30 flex items-center justify-center gap-3 text-xs font-semibold text-emerald-200 transition-all"
        :title="appStore.appUpdate.detail"
      >
        <i class="bi bi-arrow-up-circle-fill text-emerald-400"></i>
        <span>Update available:</span>
        <span class="font-mono text-emerald-300">{{ appStore.appUpdate.remoteVersion }}</span>
        <span class="text-emerald-400/70">— click to update</span>
      </button>

      <div class="flex-grow overflow-y-auto no-scrollbar">
        <transition mode="out-in"
                    enter-active-class="transition duration-500 ease-out"
                    enter-from-class="opacity-0 translate-y-4 scale-[0.98]"
                    enter-to-class="opacity-100 translate-y-0 scale-100"
                    leave-active-class="transition duration-200 ease-in"
                    leave-from-class="opacity-100 scale-100"
                    leave-to-class="opacity-0 scale-[1.02]">
          <div :key="appStore.activeTab" class="h-full">
            <DashboardView v-if="appStore.activeTab === 'dashboard'" />
            <HistoryView v-if="appStore.activeTab === 'history'" />
            <BypassView v-if="appStore.activeTab === 'bypass'" />
            <ShareView v-if="appStore.activeTab === 'share'" />
            <RelayView v-if="appStore.activeTab === 'relay'" />
            <LogsView v-if="appStore.activeTab === 'logs'" />
            <SettingsView v-if="appStore.activeTab === 'settings'" />
          </div>
        </transition>
      </div>

      <!-- Footer Area -->
      <footer class="px-8 py-4 border-t border-white/5 bg-black/20 backdrop-blur-xl flex items-center justify-between z-20">
        <span class="text-[8px] font-bold text-white/20 uppercase tracking-[0.2em]">&copy; {{ new Date().getFullYear() }} WhyKnot</span>
        <div class="flex items-center gap-2 font-mono text-[8px] uppercase tracking-widest text-white/20">
          <span class="w-1 h-1 bg-blue-500/40 rounded-full"></span>
          Build: <span class="text-white/40">{{ appStore.version }}</span>
        </div>
      </footer>
    </main>
  </div>
</template>

<style>
.no-scrollbar::-webkit-scrollbar { display: none; }
::-webkit-scrollbar { width: 4px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.1); border-radius: 10px; }
::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.2); }

@keyframes fade-in { from { opacity: 0; } to { opacity: 1; } }
@keyframes slide-in-from-bottom-4 { from { transform: translateY(1rem); opacity: 0; } to { transform: translateY(0); opacity: 1; } }
@keyframes zoom-in-95 { from { transform: scale(0.95); opacity: 0; } to { transform: scale(1); opacity: 1; } }
.animate-in { animation-fill-mode: both; }
</style>