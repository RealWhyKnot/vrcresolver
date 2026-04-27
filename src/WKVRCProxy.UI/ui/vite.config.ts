import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    tailwindcss(),
  ],
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    // Manual vendor chunking. The default single-bundle output crossed Vite's 500 KB warning
    // threshold and meant the changelog viewer's `marked` dep was paid for on every cold load
    // even though most users never open the modal. Splitting:
    //   - three:    ~515 KB → lazy-loaded via defineAsyncComponent in App.vue; arrives after
    //               first paint instead of blocking it.
    //   - charts:   chart.js + vue-chartjs, only Dashboard/Relay chart components import them.
    //   - marked:   only the changelog modal renders markdown.
    //   - vue:      framework runtime (vue + pinia + @vueuse/motion) — separate cache so a
    //               chart-only edit doesn't re-download the framework.
    // index.<hash>.js is now small (~170 KB), each vendor chunk caches independently.
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: 'three',  test: /[\\/]node_modules[\\/]three[\\/]/ },
            { name: 'charts', test: /[\\/]node_modules[\\/](chart\.js|vue-chartjs)[\\/]/ },
            { name: 'marked', test: /[\\/]node_modules[\\/]marked[\\/]/ },
            { name: 'vue',    test: /[\\/]node_modules[\\/](vue|pinia|@vueuse[\\/]motion)[\\/]/ },
          ],
        },
      },
    },
    // three.js minifies to ~515 KB as a single package — there's no further split available.
    // Since it's lazy-loaded and parallel-downloaded with the rest of the UI, the default
    // 500 KB warning is now noise. Bump the threshold to 600 KB so genuine bundle bloat
    // still surfaces but `three` doesn't trip the warning every build.
    chunkSizeWarningLimit: 600,
  },
})
