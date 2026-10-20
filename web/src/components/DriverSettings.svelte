<script lang="ts">
  import { api } from '../lib/api'
  import { act, nick } from '../lib/state.svelte'
  import type { DeviceProperty } from '../lib/types'

  let { device, onclose }: { device: string; onclose: () => void } = $props()

  const RefreshMs = 1500

  let properties = $state<DeviceProperty[]>([])
  let loadError = $state<string | null>(null)
  let loaded = $state(false)
  let group = $state<string | null>(null)
  // Values being typed, keyed "PROPERTY.ELEMENT". Kept apart from the live values so refreshes don't clobber typing.
  let drafts = $state<Record<string, string>>({})

  const groups = $derived([...new Set(properties.map((p) => p.group))])
  const shown = $derived(properties.filter((p) => p.group === group && p.kind !== 'Blob'))

  // Poll while open. Driver values (temperatures, positions, states) change constantly.
  $effect(() => {
    const name = device
    let cancelled = false
    const load = async () => {
      try {
        const result = await api.deviceProperties(name)
        if (cancelled) return
        properties = result
        loadError = null
        loaded = true
        if (group === null || !result.some((p) => p.group === group)) group = result[0]?.group ?? null
      } catch (e) {
        if (!cancelled) loadError = e instanceof Error ? e.message : String(e)
      }
    }
    load()
    const timer = setInterval(load, RefreshMs)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  })

  const key = (p: DeviceProperty, element: string) => `${p.name}.${element}`
  const writable = (p: DeviceProperty) => p.permission !== 'ro'

  // Sends the whole property: typed values where given, the current ones elsewhere. Pressing Set without
  // editing re-sends the property as it is (e.g. to push a remembered location to a mount that lost it),
  // and drivers that expect every element get them all.
  async function submit(p: DeviceProperty) {
    const values: Record<string, string> = {}
    for (const e of p.elements) {
      const draft = drafts[key(p, e.name)]
      values[e.name] = draft !== undefined && draft.trim() !== '' ? draft.trim() : e.value
    }
    if (await act(() => api.setDeviceProperty(device, p.name, values))) {
      for (const e of p.elements) delete drafts[key(p, e.name)]
    }
  }

  function toggle(p: DeviceProperty, element: string, isOn: boolean) {
    // One-of-many: just turn the chosen one on; the driver turns the others off.
    const on = p.rule === 'OneOfMany' ? true : !isOn
    act(() => api.setDeviceProperty(device, p.name, { [element]: on ? 'On' : 'Off' }))
  }

  // INDI's CCD Simulator: ZWO's cooled cameras' sensors (width × height in pixels, pixel size in µm), so the
  // simulated frames come out the size the real camera's would.
  const SimSettings = 'SIMULATOR_SETTINGS'
  const sensorPresets: { name: string; x: number; y: number; pixel: number }[] = [
    { name: 'ASI2600MC/MM Pro (APS-C)', x: 6248, y: 4176, pixel: 3.76 },
    { name: 'ASI2400MC Pro (full frame)', x: 6072, y: 4042, pixel: 5.94 },
    { name: 'ASI6200MC/MM Pro (full frame)', x: 9576, y: 6388, pixel: 3.76 },
    { name: 'ASI071MC Pro (APS-C)', x: 4944, y: 3284, pixel: 4.78 },
    { name: 'ASI294MC Pro (4/3")', x: 4144, y: 2822, pixel: 4.63 },
    { name: 'ASI1600MM Pro (4/3")', x: 4656, y: 3520, pixel: 3.8 },
    { name: 'ASI183MC/MM Pro (1")', x: 5496, y: 3672, pixel: 2.4 },
    { name: 'ASI533MC/MM Pro (1", square)', x: 3008, y: 3008, pixel: 3.76 },
    { name: 'ASI585MC Pro (1/1.2")', x: 3840, y: 2160, pixel: 2.9 },
  ]
  const sizeElements = ['SIM_XRES', 'SIM_YRES', 'SIM_XSIZE', 'SIM_YSIZE']
  const hasSensorSize = (p: DeviceProperty) =>
    p.name === SimSettings && sizeElements.every((n) => p.elements.some((e) => e.name === n))
  const currentPreset = (p: DeviceProperty) => {
    const value = (n: string) => Number(p.elements.find((e) => e.name === n)?.value)
    return sensorPresets.findIndex(
      (s) => value('SIM_XRES') === s.x && value('SIM_YRES') === s.y && Math.abs(value('SIM_XSIZE') - s.pixel) < 0.005,
    )
  }

  function applyPreset(p: DeviceProperty, index: number) {
    const preset = sensorPresets[index]
    if (!preset) return
    drafts[key(p, 'SIM_XRES')] = String(preset.x)
    drafts[key(p, 'SIM_YRES')] = String(preset.y)
    drafts[key(p, 'SIM_XSIZE')] = String(preset.pixel)
    drafts[key(p, 'SIM_YSIZE')] = String(preset.pixel)
    submit(p)
  }

  function onkeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') onclose()
  }
</script>

<svelte:window {onkeydown} />

<div class="backdrop" role="presentation" onclick={onclose}></div>
<div class="drawer panel" role="dialog" aria-modal="true" aria-label="Driver settings for {device}">
  <header>
    <div>
      <div class="muted small">Driver settings</div>
      <div class="device">{nick(device)}</div>
      {#if nick(device) !== device}<div class="muted small">{device}</div>{/if}
    </div>
    <button class="secondary" onclick={onclose} aria-label="Close">✕</button>
  </header>

  {#if loadError}
    <p class="error">Couldn't load settings: {loadError}</p>
  {:else if !loaded}
    <p class="muted">Loading…</p>
  {:else if properties.length === 0}
    <p class="muted">This device has no settings (yet). Some drivers only show most settings after connecting.</p>
  {/if}

  {#if groups.length > 0}
    <nav class="tabs">
      {#each groups as g (g)}
        <button class="tab" class:active={g === group} onclick={() => (group = g)}>{g}</button>
      {/each}
    </nav>

    <div class="props">
      {#each shown as p (p.name)}
        <section class="prop">
          <div class="prop-head">
            <span class="state {p.state}" title={p.state}></span>
            <span class="label" title={p.name}>{p.label}</span>
            {#if !writable(p)}<span class="muted small">read-only</span>{/if}
          </div>

          {#if p.kind === 'Switch'}
            <div class="switches">
              {#each p.elements as e (e.name)}
                {@const on = e.value === 'On'}
                <button
                  class="switch"
                  class:on
                  disabled={!writable(p)}
                  onclick={() => toggle(p, e.name, on)}
                  title={e.name}
                >
                  {e.label}
                </button>
              {/each}
            </div>
          {:else if p.kind === 'Light'}
            <div class="switches">
              {#each p.elements as e (e.name)}
                <span class="light"><span class="state {e.value}"></span>{e.label}</span>
              {/each}
            </div>
          {:else}
            {#if hasSensorSize(p) && writable(p)}
              {@const chosen = currentPreset(p)}
              <label class="preset">
                <span class="muted">Sensor like</span>
                <select
                  value={chosen}
                  onchange={(ev) => applyPreset(p, Number((ev.currentTarget as HTMLSelectElement).value))}
                >
                  <option value={-1} disabled>{chosen < 0 ? 'Choose a camera…' : ''}</option>
                  {#each sensorPresets as s, i (s.name)}
                    <option value={i}>{s.name}: {s.x}×{s.y}, {s.pixel} µm</option>
                  {/each}
                </select>
              </label>
              <p class="muted small hint">
                Sets the resolution and pixel size below. To keep it after the driver restarts: Options → Configuration →
                Save.
              </p>
            {/if}
            <div class="fields">
              {#each p.elements as e (e.name)}
                <span class="muted el" title={e.name}>{e.label}</span>
                <span class="mono value" title={e.value}>{e.display}</span>
                {#if writable(p)}
                  <input
                    class="mono"
                    placeholder={e.display}
                    title={e.min != null && e.max != null && e.max > e.min ? `${e.min} to ${e.max}` : ''}
                    bind:value={drafts[key(p, e.name)]}
                    onkeydown={(ev) => ev.key === 'Enter' && submit(p)}
                  />
                {/if}
              {/each}
            </div>
            {#if writable(p)}
              <div class="set"><button class="secondary" onclick={() => submit(p)}>Set</button></div>
            {/if}
          {/if}
        </section>
      {/each}
    </div>
  {/if}
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 40;
  }
  .drawer {
    position: fixed;
    top: 0;
    right: 0;
    bottom: 0;
    width: min(560px, 100vw);
    border-radius: 0;
    z-index: 41;
    display: flex;
    flex-direction: column;
    gap: 10px;
    overflow: hidden;
  }
  header {
    display: flex;
    justify-content: space-between;
    align-items: start;
    gap: 8px;
  }
  .device {
    font-weight: 600;
    font-size: 16px;
  }
  .small {
    font-size: 12px;
  }
  .error {
    color: var(--danger);
  }
  .tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .tab {
    background: transparent;
    color: var(--muted);
    border: 1px solid var(--panel-border);
    font-weight: 500;
    padding: 3px 10px;
  }
  .tab.active {
    color: var(--text);
    border-color: var(--accent);
  }
  .props {
    overflow-y: auto;
    display: grid;
    gap: 8px;
    align-content: start;
    padding-right: 4px;
  }
  .prop {
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 8px 10px;
  }
  .prop-head {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 6px;
  }
  .label {
    font-weight: 600;
    flex: 1;
  }
  .state {
    flex: none;
    display: inline-block;
    width: 9px;
    height: 9px;
    border-radius: 50%;
    background: var(--muted);
  }
  .state.Ok {
    background: var(--ok);
  }
  .state.Busy {
    background: var(--warn);
  }
  .state.Alert {
    background: var(--danger);
  }
  .switches {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .switch {
    background: transparent;
    color: var(--text);
    border: 1px solid var(--panel-border);
    font-weight: 500;
  }
  .switch.on {
    background: var(--accent);
    color: var(--accent-text);
    border-color: var(--accent);
  }
  .light {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    font-size: 13px;
  }
  .preset {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .preset select {
    flex: 1;
    min-width: 0;
  }
  .hint {
    margin: 4px 0 8px;
  }
  .fields {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1.2fr);
    gap: 4px 8px;
    align-items: center;
  }
  .fields:not(:has(input)) {
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  }
  .value,
  .el {
    overflow-wrap: anywhere;
    font-size: 13px;
  }
  .fields input {
    width: 100%;
  }
  .set {
    display: flex;
    justify-content: flex-end;
    margin-top: 6px;
  }
</style>
