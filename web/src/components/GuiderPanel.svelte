<script lang="ts">
  import { mainView } from '../lib/inbox.svelte'
  import { api, settleOf } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import NovaGuiderControls from './NovaGuiderControls.svelte'
  import OpticsEditor from './OpticsEditor.svelte'
  import Panel from './Panel.svelte'
  import { guideGraph } from '../lib/guideGraph'
  import { guideFloat } from '../lib/floats.svelte'

  const guider = $derived(nova.status?.guider)
  // PHD2, or NOVA's own guider: Guide, Stop, Dither, the graph and RMS work the same for both.
  const isNova = $derived(guider?.backend === 'nova')
  const connected = $derived(guider?.connected ?? false)
  const active = $derived(guider ? ['Guiding', 'Calibrating', 'Looping', 'LostLock', 'Selected'].includes(guider.state) : false)
  const stats = $derived(guider?.stats)
  const unit = $derived(stats?.units === 'arcsec' ? '″' : ' px')
  const drift = $derived(guider?.drift)
  const polar = (arcmin: number) => (arcmin >= 60 ? `${(arcmin / 60).toFixed(1)}°` : `${arcmin.toFixed(0)}′`)

  // The guide camera: a camera other than the imaging one (connected ones first). Its optics go to its driver when
  // PHD2 connects it, for its FITS headers (and so INDI stops warning that the focal length is missing).
  const guideCamera = $derived.by(() => {
    if (isNova && guider?.nova?.camera) return guider.nova.camera
    const others = (nova.status?.optics ?? []).filter((o) => o.camera !== nova.status?.camera?.name)
    return (others.find((o) => o.connected) ?? others[0])?.camera ?? null
  })

  let ditherPixels = $state(5)
  let busy = $state(false)
  // The dither amount starts at the mount's default (smaller for a ZWO AM) until changed here.
  let ditherTouched = false
  $effect(() => {
    const d = guider?.defaults?.ditherPixels
    if (d != null && !ditherTouched) ditherPixels = d
  })

  const badgeClass = (state: string | undefined, settling: boolean | undefined) => {
    if (settling) return 'warn'
    if (state === 'Guiding') return 'ok'
    if (state === 'LostLock') return 'danger'
    if (state === 'Calibrating' || state === 'Looping' || state === 'Selected') return 'warn'
    return 'muted'
  }

  async function setBackend(backend: 'phd2' | 'nova') {
    // Each guider has its own tab (PHD2's window, or NOVA's Guiding tab): show the one that stays.
    if (backend === 'nova' && mainView.view === 'phd2') mainView.view = 'guiding'
    if (backend === 'phd2' && mainView.view === 'guiding') mainView.view = 'image'
    await act(() => api.guiderBackend(backend))
  }

  async function toggleConnection() {
    busy = true
    await act(connected ? api.guiderDisconnect : api.guiderConnect)
    busy = false
  }

  // Guiding graph: the last 100 steps (see guideGraph: dithers marked, their settling not drawn or scaled to).
  const steps = 100
  const chart = $derived(guideGraph(guider?.history ?? [], steps, 100, 40))

  const fmt = (n: number | null | undefined, digits = 2) => (n == null ? '—' : n.toFixed(digits))

  // PHD2 runs headless on the mini PC; its screen shows in NOVA's PHD2 tab.
  const hasScreen = $derived(!!guider?.screenPort)
</script>

<Panel id="guider" title="Guider" kind="guider">
  {#snippet badge()}
    <span class="badge {badgeClass(guider?.state, guider?.settling)}">
      {guider?.settling ? 'Settling' : (guider?.state ?? 'Disconnected')}
    </span>
  {/snippet}

  <div class="segmented" role="group" aria-label="Guider">
    <button class:on={!isNova} disabled={active} onclick={() => setBackend('phd2')} title="Guide with PHD2 (its own window in the PHD2 tab)">PHD2</button>
    <button class:on={isNova} disabled={active} onclick={() => setBackend('nova')} title="Guide with NOVA's own guider">NOVA</button>
  </div>

  {#if isNova}
    <button class="link screen open-tab" onclick={() => (mainView.view = 'guiding')} title="The guide frame, its stars and the graph, full size">Guiding tab</button>
    <NovaGuiderControls />
    {#if !connected}
      <p class="muted hint">No guide camera: plug one in and choose it above.</p>
    {/if}
  {:else}
  <div class="row head">
    <span class="muted host" title={guider?.version ? `PHD2 ${guider.version}` : ''}>
      {guider?.host ?? 'localhost:4400'}
    </span>
    {#if hasScreen}
      <button class="link screen" onclick={() => (mainView.view = 'phd2')} title="PHD2's own window, in the PHD2 tab">PHD2 screen</button>
    {/if}
    <div class="spacer"></div>
    <button class={connected ? 'secondary' : ''} disabled={busy} onclick={toggleConnection}>
      {busy ? 'Working…' : connected ? 'Disconnect' : 'Connect'}
    </button>
  </div>

  {#if !connected}
    <p class="muted hint">
      Waiting for PHD2… It starts automatically on the mini PC (phd2 service); NOVA connects as soon as it's up.
    </p>
  {:else}
    <div class="row gear">
      <select
        value={guider?.profile?.id ?? ''}
        onchange={(e) => act(() => api.guiderProfile(Number(e.currentTarget.value)))}
        disabled={active || guider?.settling}
        aria-label="PHD2 equipment profile"
        title="PHD2 equipment profile (create or edit profiles on the PHD2 screen)"
      >
        {#each guider?.profiles ?? [] as p (p.id)}<option value={p.id}>{p.name}</option>{/each}
      </select>
      <button
        class="secondary"
        disabled={active}
        onclick={() => act(() => api.guiderEquipment(!guider?.equipmentConnected))}
        title="PHD2's camera and mount (Connect equipment does this too)"
      >
        {guider?.equipmentConnected ? 'Disconnect gear' : 'Connect gear'}
      </button>
    </div>
    {#if !guider?.equipmentConnected}
      <p class="muted hint">PHD2's camera and mount aren't connected. <b>Connect equipment</b> (top right) connects them too.</p>
    {/if}
  {/if}
  {/if}

  {#if connected}
    <div class="stats mono">
      <span class="muted">RMS</span>
      <span>RA {fmt(stats?.rmsRa)}{unit}</span>
      <span>Dec {fmt(stats?.rmsDec)}{unit}</span>
      <span class="total">Tot {fmt(stats?.rmsTotal)}{unit}</span>
      <span class="muted">Star</span>
      <span>SNR {fmt(guider?.snr, 0)}</span>
      <span>HFD {fmt(guider?.hfd, 1)}</span>
      <span class="muted">{stats?.samples ?? 0} steps</span>
    </div>
    {#if drift?.significant}
      <p class="drift" title="Over the last {drift.minutes.toFixed(0)} minutes {Math.round(drift.oneWay * 100)}% of the Dec corrections went {drift.direction.toLowerCase()}: the star keeps drifting, which is what polar error does">
        Dec drifting {drift.arcsecPerMinute.toFixed(0)}″/min: polar alignment at least {polar(drift.polarErrorArcmin)} off
      </p>
    {/if}

    {#if chart}
      <svg class="graph" viewBox="0 0 100 40" preserveAspectRatio="none" role="img" aria-label="Guiding error graph: RA and Dec">
        {#each chart.settling as [from, to], i (i)}<rect class="settle-band" x={from} y="0" width={Math.max(0.5, to - from)} height="40" />{/each}
        <line class="zero" x1="0" y1="20" x2="100" y2="20" />
        {#each chart.dithers as x, i (i)}<line class="dither-mark" x1={x} y1="0" x2={x} y2="40" />{/each}
        {#each chart.ra as points, i (i)}<polyline class="ra" {points} />{/each}
        {#each chart.dec as points, i (i)}<polyline class="dec" {points} />{/each}
      </svg>
      <div class="legend muted">
        <span class="ra-key">— RA</span><span class="dec-key">— Dec</span>{#if chart.dithers.length}<span class="dither-key">┆ dither</span>{/if}<span>±{fmt(chart.limit, 1)}{unit}</span>
        <button class="link pop" onclick={() => ((guideFloat.layout.popped = !guideFloat.layout.popped), guideFloat.save())}
          title="The graph in a small window that stays on screen whichever tab is showing">{guideFloat.layout.popped ? 'Close pop-out' : 'Pop out'}</button>
      </div>
      {#if guider?.diagnosis}
        <p class="diagnosis" class:warn={guider.diagnosis.axes.some((a) => a.verdict === 'chasing' || a.verdict === 'lagging')}>{guider.diagnosis.summary}</p>
      {/if}
    {/if}

    <div class="row">
      <button disabled={guider?.settling} onclick={() => act(() => api.guide(false, settleOf(guider?.defaults)))}>{active ? 'Restart' : 'Guide'}</button>
      <button class="danger" disabled={!active && !guider?.settling && !guider?.nova?.darkProgress} onclick={() => act(api.guiderStop)}>Stop</button>
      <div class="spacer"></div>
      <input class="mono dither" type="number" min="0.5" max="50" step="0.5" bind:value={ditherPixels} oninput={() => (ditherTouched = true)} aria-label="Dither amount (guide pixels)" />
      <button class="secondary" disabled={guider?.state !== 'Guiding' || guider?.settling} onclick={() => act(() => api.dither(ditherPixels, settleOf(guider?.defaults)))}>
        Dither
      </button>
    </div>
  {/if}
  {#if guideCamera}
    <div class="guide-optics"><OpticsEditor defaultCamera={guideCamera} title="Guide scope" imaging={false} /></div>
  {/if}
</Panel>

<style>
  .guide-optics {
    margin-top: 10px;
  }
  .segmented {
    display: flex;
    margin-bottom: 8px;
  }
  .link {
    background: transparent;
    border: none;
    padding: 0;
    cursor: pointer;
  }
  .open-tab {
    display: block;
    margin: -2px 0 6px auto;
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
  .head {
    margin-bottom: 8px;
  }
  .host,
  .screen {
    font-size: 12px;
  }
  .screen {
    color: var(--kind);
  }
  .gear {
    margin-bottom: 8px;
  }
  .gear select {
    flex: 1;
    min-width: 0;
  }
  .hint {
    margin: 0 0 8px;
    font-size: 12px;
  }
  .stats {
    display: grid;
    grid-template-columns: auto 1fr 1fr 1fr;
    gap: 2px 8px;
    font-size: 13px;
    margin-bottom: 8px;
  }
  .total {
    font-weight: 600;
  }
  .drift {
    margin: 0 0 8px;
    font-size: 12px;
    color: var(--warn);
  }
  .graph {
    width: 100%;
    height: 70px;
    background: var(--input);
    border-radius: 4px;
  }
  .settle-band {
    fill: var(--warn);
    opacity: 0.08;
  }
  .dither-mark {
    stroke: var(--warn);
    stroke-width: 1;
    stroke-dasharray: 3 3;
    vector-effect: non-scaling-stroke;
  }
  .dither-key {
    color: var(--warn);
  }
  .graph polyline {
    fill: none;
    stroke-width: 1;
    vector-effect: non-scaling-stroke;
  }
  .zero {
    stroke: var(--panel-border);
    vector-effect: non-scaling-stroke;
  }
  .ra,
  .ra-key {
    stroke: var(--ra);
    color: var(--ra);
  }
  .dec,
  .dec-key {
    stroke: var(--dec);
    color: var(--dec);
  }
  :global(:root[data-theme='night']) .ra,
  :global(:root[data-theme='night']) .ra-key {
    stroke: #a01010;
    color: #a01010;
  }
  :global(:root[data-theme='night']) .dec,
  :global(:root[data-theme='night']) .dec-key {
    stroke: #ff3030;
    color: #ff3030;
  }
  .legend {
    display: flex;
    gap: 12px;
    font-size: 11px;
    margin: 2px 0 8px;
  }
  .legend .pop {
    margin-left: auto;
    font-size: 11px;
    color: var(--accent);
  }
  .dither {
    width: 60px;
  }
  .diagnosis {
    margin: -4px 0 8px;
    font-size: 12px;
    color: var(--muted);
  }
  .diagnosis.warn {
    color: var(--warn);
  }
  .spacer {
    flex: 1;
  }
</style>
