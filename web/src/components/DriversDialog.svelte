<script lang="ts">
  import { portal } from '../lib/portal'
  // Which INDI drivers indiserver runs: tick the ones for your equipment and Apply. Saved to indi.env for the next
  // start, and started or stopped now (stopping one disconnects its devices).
  import { api } from '../lib/api'
  import { act } from '../lib/state.svelte'
  import type { DriverEntry, DriverSetup } from '../lib/types'

  let { onclose }: { onclose: () => void } = $props()

  let setup = $state<DriverSetup | null>(null)
  let loadError = $state<string | null>(null)
  let chosen = $state<Set<string>>(new Set())
  let query = $state('')
  // Which drivers to list: every installed one, the ones ticked (your equipment's), or those running a device now.
  let show = $state<'all' | 'ticked' | 'running'>('all')
  const counts = $derived({
    all: setup?.drivers.filter((d) => d.installed || chosen.has(d.exec)).length ?? 0,
    ticked: chosen.size,
    running: setup?.drivers.filter((d) => d.devices.length > 0).length ?? 0,
  })
  let applying = $state(false)
  let result = $state<string | null>(null)

  async function load() {
    try {
      setup = await api.drivers()
      chosen = new Set(setup.drivers.filter((d) => d.selected).map((d) => d.exec))
      loadError = null
    } catch (e) {
      loadError = e instanceof Error ? e.message : String(e)
    }
  }
  load()

  const name = (d: DriverEntry) => (d.labels.length ? d.labels.join(', ') : d.exec)
  const groupNames: Record<string, string> = {
    Telescopes: 'Mounts',
    CCDs: 'Cameras',
    Focusers: 'Focusers',
    'Filter Wheels': 'Filter wheels',
    Rotators: 'Rotators',
    Auxiliary: 'Auxiliary',
    Domes: 'Domes',
    Weather: 'Weather',
    Other: 'Other',
  }
  // Installed drivers (and chosen ones that aren't), by kind; a search looks at names, makers and programs.
  const groups = $derived.by(() => {
    if (!setup) return []
    const q = query.trim().toLowerCase()
    const shown = setup.drivers.filter(
      (d) =>
        (d.installed || chosen.has(d.exec)) &&
        // Ticked: also those ticked before (until Apply), so unticking one doesn't make it vanish.
        (show === 'all' || (show === 'ticked' ? chosen.has(d.exec) || d.selected : d.devices.length > 0)) &&
        (!q || [d.exec, d.manufacturer ?? '', ...d.labels].some((s) => s.toLowerCase().includes(q))),
    )
    // One heading per kind, in the order the kinds first come (the server sorts them).
    const byGroup = new Map<string, DriverEntry[]>()
    for (const d of shown) {
      const list = byGroup.get(d.group)
      if (list) list.push(d)
      else byGroup.set(d.group, [d])
    }
    return [...byGroup].map(([group, drivers]) => ({ group, drivers }))
  })
  const changed = $derived(!!setup && setup.drivers.some((d) => d.selected !== chosen.has(d.exec)))

  function toggle(exec: string, on: boolean) {
    const next = new Set(chosen)
    if (on) next.add(exec)
    else next.delete(exec)
    chosen = next
  }

  async function apply() {
    if (!setup) return
    applying = true
    result = null
    try {
      await act(async () => {
        // Keep the order of what was there, new ones after.
        const before = setup!.drivers.filter((d) => d.selected && chosen.has(d.exec)).map((d) => d.exec)
        const order = [...before, ...[...chosen].filter((e) => !before.includes(e))]
        const change = await api.setDrivers(order)
        const parts = [
          change.started.length ? `started ${change.started.join(', ')}` : '',
          change.stopped.length ? `stopped ${change.stopped.join(', ')}` : '',
          change.missing.length ? `not installed: ${change.missing.join(', ')}` : '',
        ].filter(Boolean)
        result = change.note ?? (parts.length ? `Saved: ${parts.join('; ')}.` : 'Saved.')
        await load()
      })
    } finally {
      applying = false
    }
  }

  function keydown(e: KeyboardEvent) {
    if (e.key === 'Escape') onclose()
  }
</script>

<svelte:window onkeydown={keydown} />

<div use:portal class="backdrop" role="presentation" onclick={onclose}></div>
<div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="drivers-title">
  <header>
    <h3 id="drivers-title">Drivers</h3>
    <input type="search" placeholder="Search: celestron, eaf, qhy…" bind:value={query} aria-label="Search drivers" />
    <button class="secondary" onclick={onclose} aria-label="Close">✕</button>
  </header>
  <p class="muted small">
    Tick the INDI drivers for your equipment. Apply saves them to {setup?.envFile ?? 'indi.env'} for the next start{setup?.liveControl
      ? ', and starts or stops them now (stopping one disconnects its devices).'
      : '. Restart indiserver to use them.'}
  </p>

  {#if loadError}
    <p class="bad">{loadError}</p>
  {:else if !setup}
    <p class="muted">Looking at the installed drivers…</p>
  {:else if !setup.applicable}
    <p class="muted">{setup.problem}</p>
  {:else}
    {#if !setup.writable}
      <p class="bad small">
        NOVA can't save {setup.envFile}. Re-run deploy/install.sh (it lets NOVA save it), or: <code>sudo chown $USER {setup.envFile}</code>
      </p>
    {/if}
    {#if setup.problem}<p class="muted small">{setup.problem}</p>{/if}
    <div class="segmented" role="group" aria-label="Which drivers to list">
      <button class:on={show === 'all'} onclick={() => (show = 'all')} title="Every driver installed on the mini PC">All installed ({counts.all})</button>
      <button class:on={show === 'ticked'} onclick={() => (show = 'ticked')} title="The drivers ticked for your equipment">Ticked ({counts.ticked})</button>
      <button class:on={show === 'running'} onclick={() => (show = 'running')} title="Drivers with a device connected right now">Running ({counts.running})</button>
    </div>
    <div class="list">
      {#each groups as g (g.group)}
        <h4>{groupNames[g.group] ?? g.group}</h4>
        {#each g.drivers as d (d.exec)}
          <label class="driver" class:missing={!d.installed}>
            <input type="checkbox" checked={chosen.has(d.exec)} onchange={(e) => toggle(d.exec, e.currentTarget.checked)} />
            <span class="what">
              <span class="name">{name(d)}{#if d.manufacturer}<span class="muted"> ({d.manufacturer})</span>{/if}</span>
              {#if d.labels.length}<span class="exec mono muted">{d.exec}</span>{/if}
            </span>
            <span class="state">
              {#if !d.installed}
                <span class="bad" title="Install it, or untick it">not installed{#if d.package}: {d.package}{/if}</span>
              {:else if d.devices.length}
                <span class="ok" title={d.devices.join(', ')}>running: {d.devices.join(', ')}</span>
              {:else if d.selected}
                <span class="muted" title="Running with nothing plugged in, or not started yet">no device</span>
              {/if}
            </span>
          </label>
        {/each}
      {:else}
        <p class="muted">
          {query ? 'No driver matches that.' : show === 'ticked' ? 'No drivers ticked yet.' : show === 'running' ? 'No driver has a device connected.' : `No INDI drivers found in ${'/usr/bin'}: install libindi.`}
        </p>
      {/each}
    </div>
  {/if}

  <footer>
    {#if result}<span class="result">{result}</span>{/if}
    <span class="spacer"></span>
    <button class="secondary" onclick={onclose}>Close</button>
    <button disabled={!setup?.applicable || !setup.writable || !changed || applying} onclick={apply}>{applying ? 'Applying…' : 'Apply'}</button>
  </footer>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.55);
    z-index: 60;
  }
  .dialog {
    position: fixed;
    z-index: 61;
    inset: 6vh max(12px, calc(50vw - 330px));
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 14px 16px;
    box-shadow: 0 12px 32px rgb(0 0 0 / 0.45);
  }
  header,
  footer {
    display: flex;
    align-items: center;
    gap: 10px;
  }
  h3 {
    margin: 0;
    font-size: 16px;
  }
  header input {
    flex: 1;
  }
  .segmented {
    display: flex;
  }
  .segmented button {
    flex: 1;
    border-radius: 0;
    color: var(--muted);
    background: transparent;
    border: 1px solid var(--panel-border);
    padding: 4px 8px;
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
  .small {
    font-size: 12px;
    margin: 0;
  }
  .list {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  h4 {
    margin: 10px 0 4px;
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--muted);
  }
  .driver {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    align-items: center;
    gap: 10px;
    padding: 4px 6px;
    border-radius: var(--radius);
    cursor: pointer;
  }
  .driver:hover {
    background: var(--input);
  }
  .what {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .name {
    font-size: 13px;
  }
  .exec {
    font-size: 11px;
  }
  .state {
    font-size: 12px;
    text-align: right;
    max-width: 220px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ok {
    color: var(--ok);
  }
  .bad {
    color: var(--danger);
  }
  .result {
    font-size: 12px;
  }
  .spacer {
    flex: 1;
  }
</style>
