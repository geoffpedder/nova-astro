<script lang="ts">
  import DriversDialog from './DriversDialog.svelte'
  import EquipmentProfiles from './EquipmentProfiles.svelte'
  import { reveal } from '../lib/reveal.svelte'

  let showDrivers = $state(false)
  // The setup guide's "Choose drivers" button.
  $effect(() => {
    if (reveal.part !== 'drivers') return
    reveal.part = null
    showDrivers = true
  })
  // Drivers are INDI's: not for NOVA's own simulator backend.
  const indi = $derived((nova.status?.backend ?? '').toUpperCase().startsWith('INDI'))
  import { api } from '../lib/api'
  import { act, nick, nova } from '../lib/state.svelte'
  import Panel from './Panel.svelte'

  const devices = $derived(nova.status?.devices ?? [])

  // Same colours as the panels; a device's first kind decides (e.g. ASI120: Camera, Guider).
  const kindColor: Record<string, string> = {
    Mount: 'mount',
    Camera: 'camera',
    Focuser: 'focuser',
    FilterWheel: 'filterwheel',
    Rotator: 'rotator',
    Guider: 'guider',
    FlatPanel: 'flatpanel',
  }
  const colorOf = (kinds: string[]) => {
    const kind = kinds.map((k) => kindColor[k]).find(Boolean)
    return kind ? `var(--kind-${kind})` : 'var(--muted)'
  }
  const label = (kind: string) => (kind === 'FilterWheel' ? 'Filter wheel' : kind === 'FlatPanel' ? 'Flat panel' : kind)
  const hasSettings = $derived(nova.status?.hasDriverSettings ?? false)
  const simulators = $derived(nova.status?.simulators)
  let switching = $state(false)

  // Renaming: one device at a time, inline.
  let renaming = $state<string | null>(null)
  let draftName = $state('')
  function startRename(device: string) {
    renaming = device
    draftName = nova.status?.nicknames?.[device] ?? ''
  }
  async function saveRename() {
    const device = renaming
    if (!device) return
    if (await act(() => api.setNickname(device, draftName.trim()))) renaming = null
  }
  function renameKey(e: KeyboardEvent) {
    if (e.key === 'Enter') saveRename()
    else if (e.key === 'Escape') renaming = null
  }
  const focus = (node: HTMLInputElement) => node.focus()

  // Connecting the chosen devices (and disconnecting them) lives at the top of this panel.
  const connected = $derived(nova.status?.equipmentConnected ?? false)
  const serverConnected = $derived(nova.status?.serverConnected ?? false)
  let connecting = $state(false)
  async function toggleConnection() {
    connecting = true
    await act(connected ? api.disconnect : api.connect)
    connecting = false
  }

  async function toggleSimulators() {
    if (!simulators) return
    switching = true
    await act(() => api.setSimulators(!simulators.enabled))
    switching = false
  }
</script>

{#if showDrivers}<DriversDialog onclose={() => (showDrivers = false)} />{/if}

<Panel id="equipment" title="Equipment">
  <button
    class="connect"
    class:secondary={connected}
    disabled={connecting || nova.hub !== 'connected' || (!connected && !serverConnected)}
    onclick={toggleConnection}
  >
    {connecting ? 'Working…' : connected ? 'Disconnect equipment' : 'Connect equipment'}
  </button>
  <EquipmentProfiles />
  {#if simulators?.applicable}
    <div class="simulators">
      <label class="switch">
        <input type="checkbox" role="switch" checked={simulators.enabled} disabled={switching} onchange={toggleSimulators} />
        <span class="track" aria-hidden="true"><span class="knob"></span></span>
        Simulators
      </label>
      <span class="muted note">
        {#if simulators.enabled}
          INDI simulator drivers available
        {:else if simulators.driverControl}
          off: simulator drivers stopped
        {:else}
          off: hidden here, but still running (re-run install.sh to let NOVA stop them)
        {/if}
      </span>
    </div>
  {/if}
  {#if indi}
    <button class="secondary small drivers-button" onclick={() => (showDrivers = true)} title="Choose which INDI drivers run (for your mount, camera and so on)"
      >Drivers…</button
    >
  {/if}
  {#if devices.length === 0}
    <p class="muted">
      {#if nova.status?.serverConnected === false}
        Waiting for the driver server… (is indiserver running?)
      {:else if indi}
        No devices found. <button class="link" onclick={() => (showDrivers = true)}>Choose your drivers…</button>
      {:else}
        No devices found.
      {/if}
    </p>
  {:else}
    <ul>
      {#each devices as device (device.name)}
        <li style:--kind={colorOf(device.kinds)}>
          <span class="dot" class:on={device.isConnected} title={device.isConnected ? 'Connected' : 'Not connected'}></span>
          {#if renaming === device.name}
            <input
              class="rename"
              bind:value={draftName}
              maxlength="40"
              placeholder={device.name}
              aria-label="Nickname for {device.name}"
              onkeydown={renameKey}
              use:focus
            />
            <button class="secondary small" onclick={saveRename} title="Save the nickname (empty: use the driver's name)">Save</button>
            <button class="secondary small" onclick={() => (renaming = null)} title="Cancel">✕</button>
          {:else}
            <span class="name" title={device.name}>
              {nick(device.name)}
              {#if nick(device.name) !== device.name}<span class="real muted">{device.name}</span>{/if}
            </span>
            <span class="kinds">{device.kinds.map(label).join(', ')}</span>
            <button
              class="secondary gear"
              title="Nickname {device.name}"
              aria-label="Nickname {device.name}"
              onclick={() => startRename(device.name)}>✎</button
            >
          {/if}
          {#if hasSettings}
            <button
              class="secondary gear"
              title="All driver settings for {device.name}"
              aria-label="Driver settings for {device.name}"
              onclick={() => (nova.settingsDevice = device.name)}>⚙</button
            >
          {/if}
        </li>
      {/each}
    </ul>
    {#if !connected}<p class="muted hint">Choose your camera and mount in their panels below, then press <b>Connect equipment</b>.</p>{/if}
  {/if}
</Panel>

<style>
  .rename {
    flex: 1;
    min-width: 0;
    font-size: 13px;
    padding: 2px 6px;
  }
  .small {
    padding: 2px 8px;
    font-size: 12px;
  }
  .real {
    display: block;
    font-size: 10px;
    line-height: 1.2;
  }
  .connect {
    width: 100%;
    margin-bottom: 10px;
  }
  .drivers-button {
    align-self: flex-start;
    margin-top: 6px;
  }
  .simulators {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-bottom: 10px;
    flex-wrap: wrap;
  }
  .switch {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
    font-size: 13px;
  }
  .switch input {
    position: absolute;
    opacity: 0;
    width: 1px;
    height: 1px;
  }
  .track {
    width: 32px;
    height: 18px;
    border-radius: 999px;
    background: var(--input);
    border: 1px solid var(--panel-border);
    position: relative;
    transition: background 0.15s;
  }
  .knob {
    position: absolute;
    top: 2px;
    left: 2px;
    width: 12px;
    height: 12px;
    border-radius: 50%;
    background: var(--muted);
    transition: transform 0.15s, background 0.15s;
  }
  .switch input:checked + .track {
    background: color-mix(in srgb, var(--accent) 35%, var(--input));
  }
  .switch input:checked + .track .knob {
    transform: translateX(14px);
    background: var(--accent);
  }
  .switch input:focus-visible + .track {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
  .note {
    font-size: 12px;
  }
  ul {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 6px;
  }
  li {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 3px 6px;
    border-left: 3px solid var(--kind);
    border-radius: 4px;
    background: color-mix(in srgb, var(--panel) 88%, var(--kind) 12%);
  }
  .name {
    flex: 1;
    min-width: 0;
    overflow-wrap: anywhere;
  }
  .kinds {
    font-size: 12px;
    color: color-mix(in srgb, var(--kind) 70%, var(--text) 30%);
  }
  .dot {
    flex: none;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--muted);
  }
  .dot.on {
    background: var(--ok);
  }
  .gear {
    padding: 0 7px;
    font-size: 14px;
    line-height: 22px;
  }
  .hint {
    margin: 10px 0 0;
    font-size: 12px;
  }
</style>
