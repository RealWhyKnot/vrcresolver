import { ref, watch, onUnmounted } from 'vue'

export type FlashDirection = 'up' | 'down' | null

export interface AnimatedNumberOptions {
  duration?: number
  /** Milliseconds to hold the direction flash after the animation finishes. */
  flashMs?: number
}

export function useAnimatedNumber(
  source: () => number,
  options: AnimatedNumberOptions = {}
) {
  const { duration = 600, flashMs = 400 } = options
  const display = ref(0)
  const flashDirection = ref<FlashDirection>(null)

  let animId = 0
  let flashTimeout: number | undefined

  function animate(from: number, to: number) {
    cancelAnimationFrame(animId)
    if (to !== from) {
      flashDirection.value = to > from ? 'up' : 'down'
      if (flashTimeout) window.clearTimeout(flashTimeout)
      flashTimeout = window.setTimeout(() => {
        flashDirection.value = null
      }, duration + flashMs)
    }
    const start = performance.now()
    const step = (now: number) => {
      const elapsed = Math.min((now - start) / duration, 1)
      const t = 1 - Math.pow(1 - elapsed, 3)
      display.value = Math.round(from + (to - from) * t)
      if (elapsed < 1) {
        animId = requestAnimationFrame(step)
      }
    }
    animId = requestAnimationFrame(step)
  }

  watch(source, (newVal, oldVal) => {
    animate(oldVal ?? 0, newVal)
  }, { immediate: true })

  onUnmounted(() => {
    cancelAnimationFrame(animId)
    if (flashTimeout) window.clearTimeout(flashTimeout)
  })

  return { display, flashDirection }
}
