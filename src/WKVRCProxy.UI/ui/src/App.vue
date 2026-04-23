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