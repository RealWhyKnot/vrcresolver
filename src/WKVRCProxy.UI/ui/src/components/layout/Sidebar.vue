<script setup lang="ts">
import { ref, watch, nextTick, onMounted } from 'vue'
import { useAppStore } from '../../stores/appStore'

const appStore = useAppStore()

const tabs = [
  { id: 'dashboard', label: 'Dashboard', icon: 'bi-grid-1x2-fill' },
  { id: 'history', label: 'History', icon: 'bi-collection-play-fill' },
  { id: 'bypass', label: 'Bypass', icon: 'bi-lightning-charge-fill' },
  { id: 'share', label: 'Share', icon: 'bi-share-fill' },
  { id: 'relay', label: 'Traffic', icon: 'bi-arrow-left-right' },
  { id: 'logs', label: 'Logs', icon: 'bi-terminal-fill' },
  { id: 'settings', label: 'Settings', icon: 'bi-sliders' }
]

const navRoot = ref<HTMLElement | null>(null)
const indicator = ref<{ top: number; height: number; visible: boolean }>({ top: 0, height: 0, visible: false })

function measure() {
  const root = navRoot.value
  if (!root) return
  const active = root.querySelector<HTMLElement>(`[data-tab-id="${appStore.activeTab}"]`)
  if (!active) return
  indicator.value = {
    top: active.offsetTop,
    height: active.offsetHeight,
    visible: true
  }
}

watch(() => appStore.activeTab, () => nextTick(measure))

onMounted(() => {
  nextTick(measure)
  // Re-measure on resize just in case the font metrics shift.
  window.addEventListener('resize', measure)
})
</script>

<template>
  <aside class="w-64 shrink-0 flex flex-col border-r border-white/5 bg-black/40 backdrop-blur-3xl p-6 z-20 relative overflow-hidden">
    <!-- Accent Line -->
    <div class="absolute inset-y-0 right-0 w-[1px] bg-gradient-to-b from-transparent via-blue-500/20 to-transparent"></div>

    <div class="mb-10 px-2 relative group cursor-pointer" @click="appStore.activeTab = 'dashboard'">
      <div class="flex items-center gap-3 transition-transform duration-700 group-hover:scale-105">
        <div class="relative">
          <img src="/favicon.png" class="w-10 h-10 object-contain drop-shadow-[0_0_15px_rgba(59,130,246,0.4)]" />
          <div class="absolute -inset-2 bg-blue-500/10 rounded-full blur-2xl opacity-0 group-hover:opacity-100 transition-opacity"></div>
          <!-- Connection Status Dot -->
          <div class="absolute -top-0.5 -right-0.5 w-2.5 h-2.5 rounded-full border border-black/50 transition-colors duration-300"
               :class="appStore.isBridgeReady ? 'bg-green-500 shadow-[0_0_6px_rgba(34,197,94,0.8)] animate-pulse' : 'bg-red-500 shadow-[0_0_6px_rgba(239,68,68,0.8)]'"></div>
        </div>
        <div>
          <h1 class="text-xl font-black text-white uppercase tracking-tighter leading-none italic">Why<span class="text-blue-500">Knot</span></h1>
        </div>
      </div>
    </div>

    <nav ref="navRoot" class="flex-grow space-y-1 relative">
      <!-- Sliding active indicator pill -->
      <div v-show="indicator.visible"
           class="absolute left-0 right-0 bg-white/5 border border-white/10 rounded-2xl shadow-xl pointer-events-none tab-indicator"
           :style="{ top: indicator.top + 'px', height: indicator.height + 'px' }">
        <div class="absolute left-0 top-0 bottom-0 w-[2px] bg-blue-500 shadow-[0_0_8px_rgba(59,130,246,0.5)]"></div>
      </div>

      <button v-for="tab in tabs" :key="tab.id"
              :data-tab-id="tab.id"
              @click="appStore.activeTab = tab.id"
              class="w-full flex items-center gap-3.5 px-5 py-3 rounded-2xl transition-colors duration-300 group relative active:scale-[0.98]"
              :class="appStore.activeTab === tab.id ? 'text-white' : 'text-white/50 hover:text-white/70'">

        <i class="bi text-lg relative z-10 transition-transform duration-500 group-hover:scale-110 group-hover:text-blue-400"
           :class="[tab.icon, appStore.activeTab === tab.id ? 'text-blue-400' : '']"></i>

        <span class="text-[10px] font-black uppercase tracking-[0.15em] relative z-10 italic">{{ tab.label }}</span>

        <!-- Badge Counts -->
        <span v-if="tab.id === 'logs' && appStore.logs.length > 0" class="relative z-10 ml-auto bg-blue-500/20 text-blue-400 text-[7px] font-black px-1.5 py-0.5 rounded-full">{{ appStore.logs.length }}</span>
        <span v-else-if="tab.id === 'relay' && appStore.relayEvents.length > 0" class="relative z-10 ml-auto bg-blue-500/20 text-blue-400 text-[7px] font-black px-1.5 py-0.5 rounded-full">{{ appStore.relayEvents.length }}</span>
        <span v-else-if="tab.id === 'history' && appStore.config.history.length > 0" class="relative z-10 ml-auto bg-blue-500/20 text-blue-400 text-[7px] font-black px-1.5 py-0.5 rounded-full">{{ appStore.config.history.length }}</span>
        <!-- Share badge: signals an active P2P stream (indigo, pulsing) or a ready cloud URL
             (green) so the user notices state they may have left behind on another tab. -->
        <span v-else-if="tab.id === 'share' && appStore.p2pShareStatus === 'active'"
              class="relative z-10 ml-auto flex items-center gap-1 bg-indigo-500/20 text-indigo-300 text-[7px] font-black px-1.5 py-0.5 rounded-full"
              title="Stream active">
          <span class="w-1 h-1 rounded-full bg-indigo-300 animate-pulse"></span>LIVE
        </span>
        <span v-else-if="tab.id === 'share' && appStore.cloudResolveStatus === 'ready'"
              class="relative z-10 ml-auto bg-emerald-500/20 text-emerald-300 text-[7px] font-black px-1.5 py-0.5 rounded-full"
              title="Resolved URL waiting to copy">URL</span>

        <!-- Active Indicator Dot -->
        <div v-if="appStore.activeTab === tab.id" class="absolute right-4 w-1 h-1 bg-blue-500 rounded-full shadow-[0_0_10px_rgba(59,130,246,1)] z-10"></div>
      </button>
    </nav>

    <div class="mt-auto pt-6 border-t border-white/5 space-y-4">
      <button @click="appStore.terminate()" class="w-full py-4 rounded-xl bg-white/5 border border-white/5 text-white/30 hover:bg-white/10 hover:text-white transition-all font-black text-[9px] uppercase tracking-[0.2em] group italic active:scale-95">
        <i class="bi bi-power mr-2 transition-transform group-hover:rotate-90 inline-block"></i>
        Exit Program
      </button>
      <p class="text-[7px] font-mono text-white/15 text-center mt-2 uppercase tracking-widest">v{{ appStore.version }}</p>
    </div>
  </aside>
</template>

<style scoped>
.tab-indicator {
  transition: top 400ms cubic-bezier(0.34, 1.56, 0.64, 1),
              height 300ms ease-out;
}
</style>
