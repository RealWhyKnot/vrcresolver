<script setup lang="ts">
// Reusable settings toggle row. Replaces ~13 hand-rolled clones in SettingsView.vue
// (each was 8 lines of identical Tailwind boilerplate that diverged only in the bound
// config field, label, and description). Visual is byte-identical to the legacy markup
// — switching SettingsView over to this component is a pure refactor.
//
// Usage:
//   <SettingToggle v-model="appStore.config.forceIPv4"
//                  label="Force IPv4"
//                  description="Use only IPv4 when resolving video URLs."
//                  @update:modelValue="appStore.saveConfig()" />

defineProps<{
  // Accept `undefined` so callers can bind optional config fields without a `?? false`
  // coalesce at every site. The toggle treats undefined as "off".
  modelValue: boolean | undefined
  label: string
  description?: string
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: boolean): void
}>()

function toggle(current: boolean | undefined) {
  emit('update:modelValue', !current)
}
</script>

<template>
  <div @click="toggle(modelValue)"
       class="bg-white/[0.03] border border-white/5 p-8 rounded-[32px] cursor-pointer hover:bg-white/[0.05] transition-all duration-500 group backdrop-blur-3xl">
    <div class="flex justify-between items-start mb-4">
      <h4 class="text-lg font-black uppercase tracking-tighter italic">{{ label }}</h4>
      <div :class="['w-10 h-5 rounded-full relative transition-all duration-700',
                    modelValue ? 'bg-blue-600 shadow-[0_0_15px_rgba(37,99,235,0.4)]'
                               : 'bg-white/10 border border-white/10']">
        <div :class="['absolute top-1 w-3 h-3 bg-white rounded-full transition-all duration-700',
                      modelValue ? 'left-6' : 'left-1']"></div>
      </div>
    </div>
    <p v-if="description" class="text-[9px] text-white/50 font-black uppercase tracking-widest leading-relaxed">{{ description }}</p>
  </div>
</template>
