<script lang="ts">
  import { api } from '../lib/api'
  import { act, mountLock, nova } from '../lib/state.svelte'
  import DevicePicker from './DevicePicker.svelte'
  import Panel from './Panel.svelte'
  import SiteEditor from './SiteEditor.svelte'
  import ManualMotion from './ManualMotion.svelte'
  import { formatDec, formatRa, fromStellarium, type PlanetariumTarget } from '../lib/stellarium'
  import { saveStellariumSettings, stellariumSettings } from '../lib/stellariumSettings.svelte'
  import { planner, stellariumPick } from '../lib/inbox.svelte'

  const mount = $derived(nova.status?.mount)
  const ready = $derived(mount?.connected ?? false)
  const moving = $derived(mount?.state === 'Slewing' || mount?.state === 'Parking')
  const solver = $derived(nova.status?.plateSolve)
  const centering = $derived(solver?.centering)
  const cameraReady = $derived(nova.status?.camera?.connected ?? false)

  // J2000; NOVA converts to the mount's JNow.
  let ra = $state('')
  let dec = $state('')

  // The Targets tab's frame centre fills the boxes whenever it moves (a new target, or the frame dragged), so it can
  // be slewed to and centred from here too. Typing your own coordinates sticks until the frame moves again.
  let fromTargets = $state<string | null>(null)
  let lastFrame = ''
  $effect(() => {
    const frame = planner.frame
    if (!frame) return
    const key = `${frame.centre.ra} ${frame.centre.dec}`
    if (key === lastFrame) return
    lastFrame = key
    ra = frame.centre.ra
    dec = frame.centre.dec
    fromTargets = frame.name
    picked = null
  })

  // From Stellarium: the object selected there fills in the target. Settings remembered per browser.
  let showPort = $state(false)
  let fetching = $state(false)
  let picked = $state<PlanetariumTarget | null>(null)
  async function grabFromStellarium() {
    fetching = true
    try {
      await act(async () => {
        const target = await fromStellarium(stellariumSettings.port)
        ra = formatRa(target.raHours)
        dec = formatDec(target.decDegrees)
        picked = target
        fromTargets = null
        // And frame it in the Targets tab (its frame then fills these boxes as usual).
        stellariumPick.request = { name: target.name, ra: target.raHours, dec: target.decDegrees }
      })
    } finally {
      fetching = false
    }
  }
  function setPort(value: number) {
    if (!(value >= 1 && value <= 65535)) return
    stellariumSettings.port = value
    saveStellariumSettings()
  }
  // Typing in the boxes means it's no longer Stellarium's object, or the Targets tab's frame.
  $effect(() => {
    if (picked && (ra !== formatRa(picked.raHours) || dec !== formatDec(picked.decDegrees))) picked = null
    if (fromTargets && planner.frame && (ra !== planner.frame.centre.ra || dec !== planner.frame.centre.dec)) fromTargets = null
  })

  // Meridian: "in 1 h 12 min" before it, "23 min past" after; plus the pier side if the mount reports it.
  function meridianText(hourAngle: number): string {
    const minutes = Math.round(Math.abs(hourAngle) * 60 * 0.99727)
    const span = minutes >= 60 ? `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min` : `${minutes} min`
    return hourAngle < 0 ? `meridian in ${span}` : `${span} past the meridian`
  }

  function stateClass(state: string | undefined) {
    if (state === 'Tracking') return 'ok'
    if (state === 'Slewing' || state === 'Parking') return 'warn'
    if (state === 'Error') return 'danger'
    return 'muted'
  }
</script>

<Panel id="mount" title="Mount" kind="mount">
  {#snippet badge()}
    {#if mount}<span class="badge {stateClass(mount.state)}">{mount.state}</span>{/if}
  {/snippet}

  <DevicePicker kind="Mount" disabled={moving} />

  {#if !mount}
    <p class="muted">No mount.</p>
  {:else}
    <div class="coords mono" title="JNow (what the mount uses): {mount.jNowRaText} {mount.jNowDecText}">
      <span class="muted">RA</span><span>{mount.raText}</span>
      <span class="muted">Dec</span><span>{mount.decText}</span>
      <span></span><span class="muted epoch">J2000</span>
    </div>
    {#if mount.connected && (mount.hourAngle != null || mount.sideOfPier)}
      <p
        class="pier muted"
        class:due={mount.sideOfPier === 'West' && mount.hourAngle != null && mount.hourAngle > 0}
        title="Pier side as the mount reports it. West while looking east of the meridian; still West after crossing it means a flip is due."
      >
        {#if mount.hourAngle != null}{meridianText(mount.hourAngle)}{/if}{#if mount.sideOfPier}{mount.hourAngle != null
            ? ', '
            : ''}pier {mount.sideOfPier}{/if}
      </p>
    {/if}

    <ManualMotion />

    <div class="pick">
      <button
        class="secondary"
        disabled={!ready || fetching}
        title="Use the object selected in Stellarium on this PC (Remote Control plugin, port {stellariumSettings.port})"
        onclick={grabFromStellarium}>{fetching ? 'Asking…' : 'From Stellarium'}</button
      >
      <button class="secondary gear" aria-expanded={showPort} title="Stellarium settings" onclick={() => (showPort = !showPort)}
        >⚙</button
      >
    </div>
    {#if showPort}
      <div class="stellarium">
        <label>
          Remote Control port
          <input
            type="number"
            min="1"
            max="65535"
            value={stellariumSettings.port}
            onchange={(e) => setPort(Number(e.currentTarget.value))}
          />
        </label>
        <label class="check" title="After each plate solve, turn Stellarium's Oculars sensor frame to the camera's real angle">
          <input type="checkbox" bind:checked={stellariumSettings.autoRotation} onchange={saveStellariumSettings} />
          Send camera angle to Oculars after each solve
        </label>
        <div class="calibrate">
          <label title="Added to the solved angle: adjust once until Stellarium's frame matches a real image">
            Angle offset (°)
            <input type="number" step="90" bind:value={stellariumSettings.offset} onchange={saveStellariumSettings} />
          </label>
          <label class="check" title="Reverse the direction of rotation if Stellarium turns the frame the wrong way">
            <input type="checkbox" bind:checked={stellariumSettings.mirror} onchange={saveStellariumSettings} /> Reverse
          </label>
        </div>
      </div>
    {/if}
    {#if fromTargets}
      <p class="picked" title="The frame centre from the Targets tab">✦ {fromTargets} (Targets tab)</p>
    {/if}
    {#if picked}
      <p class="picked">
        ✦ {[picked.name, picked.type, picked.magnitude != null ? `mag ${picked.magnitude.toFixed(1)}` : null]
          .filter(Boolean)
          .join(', ')}
      </p>
    {/if}

    <div class="inputs">
      <label>RA <input class="mono" bind:value={ra} placeholder="hh:mm:ss" disabled={!ready} /></label>
      <label>Dec <input class="mono" bind:value={dec} placeholder="±dd:mm:ss" disabled={!ready} /></label>
    </div>

    <div class="row">
      <button
        disabled={!ready || moving || mount.state === 'Parked' || !!mountLock()}
        title={mountLock() ?? 'GoTo these coordinates'}
        onclick={() => act(() => api.slew(ra, dec))}
      >
        Slew
      </button>
      <button
        class="secondary"
        disabled={!ready || moving || mount.state === 'Parked' || !cameraReady || !solver?.available || centering?.running || !!mountLock()}
        title={mountLock() ?? (!solver?.available ? (solver?.problem ?? 'No plate solver') : !cameraReady ? 'Connect a camera to centre' : 'Slew, then plate solve and correct until centred')}
        onclick={() => act(() => api.center(ra, dec))}>Slew & center</button
      >
      <button class="danger" disabled={!ready} onclick={() => act(api.abortMount)}>Stop</button>
      <div class="spacer"></div>
      {#if mount.state === 'Parked'}
        <button class="secondary" disabled={!ready} onclick={() => act(api.unpark)}>Unpark</button>
      {:else}
        <button class="secondary" disabled={!ready || moving} onclick={() => act(api.park)}>Park</button>
      {/if}
    </div>
  {/if}

  {#if nova.status?.stellarium}
    {@const link = nova.status.stellarium}
    <p
      class="link muted"
      class:live={link.clients > 0}
      title="In Stellarium: Telescope Control → add → External software or a remote computer, host {location.hostname}, port {link.port}. Ctrl+1 then slews here (and plate solves to centre)."
    >
      Stellarium link on port {link.port}: {link.clients > 0 ? `${link.clients} connected` : 'not connected'}
    </p>
  {/if}

  {#if centering}
    <p class="centering mono" class:done={!centering.running && centering.message.startsWith('Centred')}>
      {#if centering.running}⟳ Centring {centering.attempt}/{centering.maxAttempts}: {centering.message}{:else}{centering.message}{/if}
      {#if centering.errorArcmin != null && centering.running}({centering.errorArcmin.toFixed(1)}′ off){/if}
    </p>
  {/if}

  <SiteEditor />
</Panel>

<style>
  .epoch {
    font-size: 11px;
  }
  .pier {
    font-size: 12px;
    margin: -4px 0 10px;
  }
  .pier.due {
    color: var(--warn);
  }
  .centering {
    font-size: 12px;
    margin: 8px 0 0;
  }
  .centering.done {
    color: var(--ok);
  }
  .link {
    font-size: 11px;
    margin: 8px 0 0;
  }
  .link.live {
    color: var(--ok);
  }
  .coords {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 2px 12px;
    font-size: 18px;
    margin-bottom: 10px;
  }
  .pick {
    display: flex;
    gap: 6px;
    margin-bottom: 8px;
  }
  .pick > button:first-child {
    flex: 1;
  }
  .gear {
    padding: 4px 8px;
  }
  .stellarium {
    display: grid;
    gap: 6px;
    margin-bottom: 8px;
    padding: 8px;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
  }
  .stellarium input[type='number'] {
    width: 100px;
  }
  .calibrate {
    display: flex;
    align-items: end;
    gap: 12px;
  }
  label.check {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .picked {
    margin: 0 0 8px;
    font-size: 12px;
    color: var(--kind-mount);
  }
  .inputs {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
    margin-bottom: 10px;
  }
  label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  label input {
    color: var(--text);
  }
  .spacer {
    flex: 1;
  }
</style>
