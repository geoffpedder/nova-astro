<script lang="ts">
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import type { BluetoothMount } from '../lib/types'

  // ZWO AM mounts: USB cable or Bluetooth. Bluetooth: pick the mount once (Find mounts), then Connect equipment
  // connects it that way; NOVA keeps the link up and lets go on Disconnect, so the phone app can have it.
  let { disabled = false }: { disabled?: boolean } = $props()

  const bt = $derived(nova.status?.mountBluetooth)
  const equipmentConnected = $derived(nova.status?.equipmentConnected ?? false)
  let picking = $state(false)
  let busy = $state(false)
  let searched = $state(false)

  const found = $derived(bt?.found ?? [])

  async function scan() {
    searched = true
    await act(api.scanMountBluetooth)
  }

  async function useUsb() {
    picking = false
    if (!bt?.enabled) return
    busy = true
    await act(() => api.setMountBluetooth(false))
    busy = false
  }

  function useBluetooth() {
    picking = true
    if (!bt?.scanning) scan()
  }

  async function choose(mount: BluetoothMount) {
    busy = true
    if (await act(() => api.setMountBluetooth(true, mount.name, mount.address))) picking = false
    busy = false
  }

  // Bars, roughly: -55 dBm or better is next to it, -85 or worse is about to drop.
  const bars = (rssi: number | null) => (rssi == null ? 0 : rssi >= -60 ? 3 : rssi >= -75 ? 2 : 1)

  const stateText = $derived.by(() => {
    if (!bt) return ''
    switch (bt.state) {
      case 'Idle':
        return equipmentConnected ? 'not connected' : 'connects with Connect equipment'
      case 'Searching':
        return 'looking for it…'
      case 'Connecting':
        return 'connecting…'
      case 'Connected':
        return 'connected'
      case 'Error':
        return bt.detail ?? 'failed'
      default:
        return ''
    }
  })
  const stateClass = $derived(bt?.state === 'Connected' ? 'ok' : bt?.state === 'Error' ? 'danger' : bt?.state === 'Idle' ? '' : 'warn')
</script>

{#if bt?.applicable}
  <div class="connection">
    <span class="muted label">Connection</span>
    <div class="segmented" role="group" aria-label="How the mount is connected">
      <button class:on={!bt.enabled && !picking} disabled={disabled || busy} onclick={useUsb} title="The mount's USB cable">USB cable</button>
      <button class:on={bt.enabled || picking} disabled={disabled || busy} onclick={useBluetooth} title="The mount's own Bluetooth (ZWO AM mounts)"
        >Bluetooth</button
      >
    </div>
  </div>

  {#if bt.enabled && !picking}
    <div class="link">
      <span class="dot {stateClass}" aria-hidden="true"></span>
      <span class="name">{bt.mount ?? 'First ZWO mount found'}</span>
      <span class="muted state" title={bt.detail ?? ''}>{stateText}</span>
      <button class="secondary small" disabled={disabled || busy} onclick={useBluetooth} title="Look for mounts and choose another">Change</button>
    </div>
  {/if}

  {#if picking}
    <div class="picker">
      {#if bt.scanning}
        <p class="muted small">Looking for ZWO mounts…</p>
      {/if}
      {#each found as mount (mount.address)}
        <button class="secondary found" class:current={bt.enabled && mount.address === bt.address} disabled={busy} onclick={() => choose(mount)}>
          <span class="signal" aria-label="Signal {mount.rssi ?? '?'} dBm" title="Signal {mount.rssi ?? '?'} dBm">
            {#each [1, 2, 3] as bar (bar)}<i class:lit={bars(mount.rssi) >= bar} style:height="{bar * 4}px"></i>{/each}
          </span>
          <span class="name">{mount.name}</span>
        </button>
      {/each}
      {#if searched && !bt.scanning && found.length === 0}
        <p class="muted small">
          No ZWO mount found. Is it switched on and within a few metres? Close ZWO's app on your phone: a mount
          connected to something else doesn't show.
        </p>
      {/if}
      <div class="actions">
        <button class="secondary small" disabled={bt.scanning} onclick={scan}>{bt.scanning ? 'Searching…' : 'Search again'}</button>
        <button class="secondary small" onclick={() => (picking = false)}>Cancel</button>
      </div>
    </div>
  {/if}
{/if}

<style>
  .connection {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-top: 8px;
  }
  .label {
    font-size: 12px;
  }
  .segmented {
    display: flex;
    flex: 1;
  }
  .segmented button {
    flex: 1;
    border-radius: 0;
    color: var(--muted);
    background: transparent;
    border: 1px solid var(--panel-border);
    padding: 3px 8px;
    font-size: 12px;
  }
  .segmented button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }
  .segmented button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  .segmented button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .link {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 6px;
    font-size: 13px;
    min-width: 0;
  }
  .link .state {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-size: 12px;
  }
  .dot {
    flex: none;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--muted);
  }
  .dot.ok {
    background: var(--ok);
  }
  .dot.warn {
    background: var(--warn);
  }
  .dot.danger {
    background: var(--danger);
  }
  .picker {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-top: 6px;
  }
  .found {
    display: flex;
    align-items: center;
    gap: 8px;
    text-align: left;
    font-size: 13px;
  }
  .found.current {
    border-color: var(--accent);
  }
  .signal {
    display: inline-flex;
    align-items: flex-end;
    gap: 2px;
    height: 12px;
  }
  .signal i {
    width: 3px;
    background: var(--panel-border);
  }
  .signal i.lit {
    background: var(--ok);
  }
  .actions {
    display: flex;
    gap: 6px;
  }
  .small {
    font-size: 12px;
    margin: 0;
  }
  button.small {
    padding: 2px 8px;
  }
</style>
