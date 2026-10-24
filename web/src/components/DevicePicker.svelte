<script lang="ts">
  import { api } from '../lib/api'
  import { act, nick, nova } from '../lib/state.svelte'
  import type { DeviceKind } from '../lib/types'

  type PickerKind = 'Camera' | 'Mount' | 'Focuser' | 'FilterWheel' | 'Rotator' | 'FlatPanel'
  let { kind, disabled = false }: { kind: PickerKind; disabled?: boolean } = $props()

  const selectionKey = {
    Camera: 'camera',
    Mount: 'mount',
    Focuser: 'focuser',
    FilterWheel: 'filterWheel',
    Rotator: 'rotator',
    FlatPanel: 'flatPanel',
  } as const
  const label = $derived(kind === 'FilterWheel' ? 'filter wheel' : kind === 'FlatPanel' ? 'flat panel' : kind.toLowerCase())

  const Automatic = ''
  // Leaves the role empty: nothing is connected for it (no simulated rotator just because a simulator driver is running).
  const None = '(none)'

  const options = $derived((nova.status?.devices ?? []).filter((d) => d.kinds.includes(kind as DeviceKind)))
  const selected = $derived(nova.status?.selection[selectionKey[kind]] ?? Automatic)
  // A saved choice whose device is unplugged or whose driver isn't running.
  const missing = $derived(selected !== Automatic && selected !== None && !options.some((d) => d.name === selected))

  function choose(event: Event) {
    const value = (event.currentTarget as HTMLSelectElement).value
    act(() => api.select(kind, value === Automatic ? null : value))
  }
</script>

<select value={selected} onchange={choose} {disabled} aria-label="{kind} device" class:missing>
  <option value={Automatic}>Automatic (first {label} found)</option>
  <option value={None}>None</option>
  {#each options as device (device.name)}
    <option value={device.name}>{nick(device.name)}{device.isConnected ? '' : ' (not connected)'}</option>
  {/each}
  {#if missing}
    <option value={selected}>{selected} (not found)</option>
  {/if}
</select>

<style>
  select {
    width: 100%;
    margin-bottom: 8px;
  }
  select.missing {
    border-color: var(--danger);
    color: var(--danger);
  }
</style>
