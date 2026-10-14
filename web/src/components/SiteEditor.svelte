<script lang="ts">
  import { ask } from '../lib/confirm.svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import MapPicker from './MapPicker.svelte'
  import { reveal } from '../lib/reveal.svelte'

  const NewSite = '\u0000new'
  const ClockTolerance = 60 // seconds

  let open = $state(false)
  // The setup guide's "Set the site" button.
  $effect(() => {
    if (reveal.part !== 'site') return
    reveal.part = null
    open = true
  })
  let editing = $state<string | null>(null)
  let name = $state('')
  let latitude = $state<number | null>(null)
  let longitude = $state<number | null>(null)
  let elevation = $state<number | null>(null)
  let loadedKey = $state<string | null>(null)
  let showMap = $state(false)

  const site = $derived(nova.status?.site)

  // Sun protection: how far from the Sun the telescope is kept while the Sun is up.
  const sunAvoidance = $derived(site?.sunAvoidance ?? 30)
  const sunUp = $derived(site?.sunAltitude == null ? null : site.sunAltitude > -3)
  const sunOn = $derived(site?.sunProtection ?? true)
  async function setSun(value: string) {
    const degrees = Number(value)
    if (!Number.isFinite(degrees) || degrees < 5 || degrees > 90) return
    act(() => api.setSunAvoidance(degrees))
  }
  async function toggleSun(on: boolean) {
    if (
      !on &&
      !(await ask('Turn Sun protection off?', {
        message:
          'For testing indoors. NOVA will then point the telescope anywhere, even at the Sun: pointing at the Sun can blind you and destroy the camera. Turn it back on before using the scope outside.',
        confirm: 'Turn it off',
        danger: true,
      }))
    )
      return
    act(() => api.setSunProtection(on))
  }
  // Settle: how long the mount is left after every GoTo before NOVA exposes, solves or moves on.
  const settle = $derived(site?.settleSeconds ?? 3)
  function setSettle(value: string) {
    const seconds = Number(value)
    if (!Number.isFinite(seconds) || seconds < 0 || seconds > 60) return
    act(() => api.setSettleSeconds(seconds))
  }
  const sites = $derived(site?.sites ?? [])
  const target = $derived(editing ?? site?.active ?? (sites.length === 0 ? NewSite : null))
  const existing = $derived(sites.find((s) => s.name === target) ?? null)
  const active = $derived(sites.find((s) => s.name === site?.active) ?? null)

  $effect(() => {
    const key = `${target}`
    if (key === loadedKey) return
    loadedKey = key
    name = existing?.name ?? ''
    latitude = existing?.latitude ?? null
    longitude = existing?.longitude ?? null
    elevation = existing?.elevation ?? null
  })

  const dms = (v: number, pos: string, neg: string) => `${Math.abs(v).toFixed(4)}°${v >= 0 ? pos : neg}`
  const describe = (lat: number, lon: number) => `${dms(lat, 'N', 'S')} ${dms(lon, 'E', 'W')}`

  // The mini PC's clock vs this browser's: the mount gets the mini PC's time.
  const clockSkew = $derived(site ? (Date.parse(site.serverUtc) - Date.now()) / 1000 : 0)
  const valid = $derived(
    !!name.trim() &&
      latitude !== null && latitude >= -90 && latitude <= 90 &&
      longitude !== null && longitude >= -180 && longitude <= 180 &&
      elevation !== null,
  )
  const unsaved = $derived(
    !existing ||
      name.trim() !== existing.name ||
      latitude !== existing.latitude ||
      longitude !== existing.longitude ||
      elevation !== existing.elevation,
  )

  function choose(value: string) {
    if (value === NewSite) {
      editing = NewSite
      return
    }
    editing = null
    act(() => api.selectSite(value)) // picking a site makes it the one in use
  }

  async function save() {
    if (!valid) return
    if (await act(() => api.saveSite(name.trim(), latitude!, longitude!, elevation!, existing?.name))) editing = null
  }

  async function remove() {
    if (!existing || !(await ask(`Delete the site "${existing.name}"?`, { confirm: 'Delete', danger: true }))) return
    if (await act(() => api.deleteSite(existing!.name))) editing = null
  }

  function picked(lat: number, lon: number) {
    latitude = lat
    longitude = lon
    if (elevation === null) elevation = 0
    showMap = false
  }
</script>

{#if Math.abs(clockSkew) > ClockTolerance}
  <p class="warning">
    ⚠ The mini PC's clock is {Math.round(Math.abs(clockSkew) / 60)} min {clockSkew > 0 ? 'ahead of' : 'behind'} this browser.
    The mount uses the mini PC's time, so check it (e.g. <span class="mono">timedatectl</span>).
  </p>
{/if}
{#if site?.mountMismatch && site.mountLocation && active}
  <p class="warning">
    ⚠ The mount has a different location from “{active.name}”.
    <button class="link" onclick={() => act(() => api.selectSite(active.name))}>Send “{active.name}”</button>
  </p>
{/if}

<div class="sun" class:off={!sunOn}>
  <label class="switch" title="Sun protection: NOVA refuses GoTos near the Sun while it's up, routes slews round it and halts moves heading into it">
    <input type="checkbox" role="switch" checked={sunOn} onchange={(e) => toggleSun(e.currentTarget.checked)} />
    <span class="track" aria-hidden="true"><span class="knob"></span></span>
    ☀ Sun protection
  </label>
  {#if sunOn}
    <span class="inline">
      <input
        class="mono"
        type="number"
        min="5"
        max="90"
        step="5"
        value={sunAvoidance}
        aria-label="Degrees from the Sun"
        onchange={(e) => setSun(e.currentTarget.value)}
      />°
    </span>
    <span class="muted state">
      {#if sunUp === null}no site: Sun assumed up{:else if sunUp}Sun up: active{:else}Sun down: resting{/if}
    </span>
  {:else}
    <span class="bad state">Off: nothing stops the telescope pointing at the Sun</span>
  {/if}
</div>

<div class="sun" title="After every GoTo (Slew, centring, a sequence's next target, a flip) the mount is left this long before anything else: a moving mount trails the stars in a solve frame">
  <span>Settle after slews</span>
  <span class="inline">
    <input class="mono" type="number" min="0" max="60" step="1" value={settle} aria-label="Settle after slews (seconds)" onchange={(e) => setSettle(e.currentTarget.value)} /> s
  </span>
</div>

<div class="site">
  <button class="toggle" aria-expanded={open} onclick={() => (open = !open)}>
    <span class="chevron" class:closed={!open} aria-hidden="true">▾</span>
    Site
    <span class="muted summary">
      {active ? active.name : 'not set'}
    </span>
  </button>

  {#if open}
    <label class="wide">
      Site
      <select value={target ?? ''} onchange={(e) => choose(e.currentTarget.value)} title="Picking a site sends it to the mount">
        {#each sites as s (s.name)}
          <option value={s.name}>{s.name}{s.name === site?.active ? ' ✓' : ''}</option>
        {/each}
        <option value={NewSite}>+ New site…</option>
      </select>
    </label>

    <div class="fields">
      <label class="full">Name<input type="text" maxlength="60" placeholder="e.g. Home, Dark site" bind:value={name} /></label>
      <label>Latitude (°, +N)<input class="mono" type="number" min="-90" max="90" step="0.0001" bind:value={latitude} /></label>
      <label>Longitude (°, +E)<input class="mono" type="number" min="-180" max="180" step="0.0001" bind:value={longitude} /></label>
      <label>Elevation (m)<input class="mono" type="number" min="-500" max="10000" step="1" bind:value={elevation} /></label>
      <button class="secondary map-button" onclick={() => (showMap = true)}>🗺 Pick on map…</button>
    </div>

    {#if latitude !== null && longitude !== null}
      <p class="muted mono readout">{describe(latitude, longitude)}</p>
    {/if}

    <div class="row actions">
      <button disabled={!valid || !unsaved} onclick={save}>{existing ? 'Save changes' : 'Add site'}</button>
      {#if existing}<button class="secondary" onclick={remove}>Delete</button>{/if}
    </div>
    <p class="muted hint">The site marked ✓ and the mini PC's time are sent to the mount whenever it connects.</p>


  {/if}
</div>

{#if showMap}
  <MapPicker
    initial={latitude !== null && longitude !== null ? { lat: latitude, lon: longitude } : null}
    onpick={picked}
    onclose={() => (showMap = false)}
  />
{/if}

<style>
  .sun {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 6px 10px;
    margin-top: 10px;
    font-size: 12px;
  }
  .sun .inline {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  .sun .inline input {
    width: 4.5em;
    padding: 2px 6px;
  }
  .sun .state {
    font-size: 11px;
  }
  .switch {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    cursor: pointer;
    font-weight: 600;
  }
  .switch input {
    position: absolute;
    opacity: 0;
    width: 1px;
    height: 1px;
  }
  .track {
    width: 30px;
    height: 16px;
    border-radius: 999px;
    background: var(--input);
    border: 1px solid var(--panel-border);
    position: relative;
  }
  .knob {
    position: absolute;
    top: 2px;
    left: 2px;
    width: 10px;
    height: 10px;
    border-radius: 50%;
    background: var(--muted);
    transition: transform 0.15s;
  }
  .switch input:checked + .track {
    background: color-mix(in srgb, var(--warn) 40%, var(--input));
  }
  .switch input:checked + .track .knob {
    transform: translateX(14px);
    background: var(--warn);
  }
  .switch input:focus-visible + .track {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
  .bad {
    color: var(--danger);
  }
  .site {
    border-top: 1px solid var(--panel-border);
    padding-top: 8px;
    margin-top: 10px;
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
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label input,
  label select {
    width: 100%;
    color: var(--text);
  }
  .wide {
    margin-top: 8px;
  }
  .fields {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
    margin-top: 8px;
    align-items: end;
  }
  .full {
    grid-column: 1 / -1;
  }
  .readout {
    margin: 6px 0 0;
    font-size: 12px;
  }
  .actions {
    margin-top: 8px;
  }
  .hint {
    margin: 6px 0 0;
    font-size: 11px;
  }
  .warning {
    margin: 8px 0 0;
    font-size: 12px;
    color: var(--warn);
  }
  .link {
    all: unset;
    cursor: pointer;
    text-decoration: underline;
  }
</style>
