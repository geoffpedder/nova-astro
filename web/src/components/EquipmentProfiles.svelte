<script lang="ts">
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nick, nova } from '../lib/state.svelte'
  import type { EquipmentProfile } from '../lib/types'

  // Saved equipment setups, like the site and optics presets: save what's in use now, load one later (while
  // disconnected) to put the devices, drivers and mount connection back, then Connect equipment.
  const NewProfile = '\u0000new'

  let open = $state(false)
  let editing = $state<string | null>(null)
  let label = $state('')
  let loadedKey = $state<string | null>(null)
  let busy = $state(false)

  const status = $derived(nova.status?.equipmentProfiles ?? null)
  const profiles = $derived(status?.profiles ?? [])
  const connected = $derived(nova.status?.equipmentConnected ?? false)
  const target = $derived(editing ?? status?.active ?? (profiles.length === 0 ? NewProfile : null))
  const existing = $derived(profiles.find((p) => p.name === target) ?? null)

  $effect(() => {
    const key = `${target}`
    if (key === loadedKey) return
    loadedKey = key
    label = existing?.name ?? ''
  })

  const roles: [keyof EquipmentProfile['devices'], string][] = [
    ['camera', 'Camera'],
    ['mount', 'Mount'],
    ['focuser', 'Focuser'],
    ['filterWheel', 'Filter wheel'],
    ['rotator', 'Rotator'],
    ['flatPanel', 'Flat panel'],
  ]
  // "(none)": a role left empty, so nothing's connected for it.
  const contents = (p: EquipmentProfile) =>
    roles.filter(([key]) => p.devices[key] && p.devices[key] !== '(none)').map(([key, role]) => ({ role, name: p.devices[key]! }))

  async function choose(value: string) {
    if (value === NewProfile) {
      editing = NewProfile
      return
    }
    editing = null
    if (connected || value === status?.active) {
      editing = value
      return
    }
    busy = true
    await act(() => api.loadEquipmentProfile(value))
    busy = false
  }

  async function save() {
    if (!label.trim()) return
    busy = true
    if (await act(() => api.saveEquipmentProfile(label.trim(), existing?.name))) editing = null
    busy = false
  }

  async function remove() {
    if (!existing) return
    if (!(await ask(`Delete the equipment profile "${existing.name}"?`, { confirm: 'Delete', danger: true }))) return
    if (await act(() => api.deleteEquipmentProfile(existing!.name))) editing = null
  }

  async function load() {
    if (!existing) return
    busy = true
    if (await act(() => api.loadEquipmentProfile(existing!.name))) editing = null
    busy = false
  }
</script>

<div class="profiles">
  <button class="toggle" aria-expanded={open} onclick={() => (open = !open)}>
    <span class="chevron" class:closed={!open} aria-hidden="true">▾</span>
    Profile <span class="muted summary">{status?.active ?? 'none'}</span>
  </button>

  {#if open}
    <label class="wide">
      Profile
      <select value={target ?? ''} disabled={busy} onchange={(e) => choose(e.currentTarget.value)}
        title={connected ? 'Disconnect equipment to load another profile' : 'Picking a profile loads it'}>
        {#each profiles as p (p.name)}
          <option value={p.name}>{p.name}{p.name === status?.active ? ' ✓' : ''}</option>
        {/each}
        <option value={NewProfile}>+ New profile…</option>
      </select>
    </label>

    {#if existing}
      <ul class="contents">
        {#each contents(existing) as item (item.role)}
          <li><span class="muted">{item.role}</span><span>{nick(item.name)}</span></li>
        {/each}
        {#if existing.guider}
          <li>
            <span class="muted">Guiding</span><span
              >{existing.guider === 'nova' ? 'NOVA' : 'PHD2'}{existing.guider === 'nova' && existing.guideCamera ? `, ${nick(existing.guideCamera)}` : ''}</span
            >
          </li>
        {/if}
        {#if existing.simulators != null}
          <li><span class="muted">Simulators</span><span>{existing.simulators ? 'on' : 'off'}</span></li>
        {/if}
        {#if existing.mountConnection?.bluetooth}
          <li><span class="muted">Mount over</span><span>Bluetooth ({existing.mountConnection.mount ?? existing.mountConnection.address})</span></li>
        {/if}
        {#if existing.drivers}
          <li><span class="muted">Drivers</span><span class="mono drivers">{existing.drivers.length ? existing.drivers.join(' ') : 'none'}</span></li>
        {/if}
      </ul>
      {#if existing.name !== status?.active || connected}
        <button class="secondary small" disabled={busy || connected} onclick={load}
          title={connected ? 'Disconnect equipment first' : 'Put these devices, drivers and mount connection back'}>Load</button>
        {#if connected}<span class="muted note">Disconnect equipment to load it.</span>{/if}
      {/if}
    {/if}

    <div class="row actions">
      <input type="text" maxlength="60" placeholder="e.g. Askar 71F + ASI2600" bind:value={label} aria-label="Profile name" />
      <button disabled={busy || !label.trim()} onclick={save}
        title="Save the devices in use now, the drivers and the mount's connection under this name">
        {existing ? 'Save current' : 'Save'}
      </button>
      {#if existing}<button class="secondary" disabled={busy} onclick={remove}>Delete</button>{/if}
    </div>
    <p class="muted hint">
      A profile keeps the device for each role (None for an empty one), the INDI drivers, the Simulators switch, how
      the mount connects, and the guider and its camera. Load one while disconnected, then press Connect equipment.
    </p>
  {/if}
</div>

<style>
  .profiles {
    border-top: 1px solid var(--panel-border);
    padding: 8px 0;
    margin-bottom: 8px;
  }
  .toggle {
    all: unset;
    cursor: pointer;
    display: flex;
    align-items: baseline;
    gap: 6px;
    width: 100%;
    font-size: 13px;
    font-weight: 600;
  }
  .toggle:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
  .chevron {
    font-size: 10px;
    transition: transform 0.15s;
  }
  .chevron.closed {
    transform: rotate(-90deg);
  }
  .summary {
    font-weight: 400;
    font-size: 12px;
    margin-left: auto;
    text-align: right;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label select {
    width: 100%;
    color: var(--text);
  }
  .wide {
    margin-top: 8px;
  }
  .contents {
    list-style: none;
    margin: 8px 0 6px;
    padding: 0;
    display: grid;
    gap: 2px;
    font-size: 12px;
  }
  .contents li {
    display: grid;
    grid-template-columns: 90px minmax(0, 1fr);
    gap: 8px;
  }
  .drivers {
    overflow-wrap: anywhere;
    font-size: 11px;
  }
  .actions {
    margin-top: 8px;
    display: flex;
    gap: 6px;
  }
  .actions input {
    flex: 1;
    min-width: 0;
  }
  .small {
    padding: 2px 8px;
    font-size: 12px;
  }
  .note {
    font-size: 12px;
    margin-left: 6px;
  }
  .hint {
    margin: 6px 0 0;
    font-size: 11px;
  }
</style>
