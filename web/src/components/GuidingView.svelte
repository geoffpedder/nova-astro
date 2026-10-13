<script lang="ts">
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import { guideView, setGuideStretch, type GuideStretch } from '../lib/guideView.svelte'
  import CalibrationGraph from './CalibrationGraph.svelte'
  import { guideGraph } from '../lib/guideGraph'

  // NOVA's own guider, full size: the whole guide frame with the stars it follows (click one to guide on it), the
  // lock star close up, the buttons, and a large graph of the last 10 minutes or so.
  const guider = $derived(nova.status?.guider)
  const n = $derived(guider?.nova)
  const s = $derived(n?.settings)
  const phase = $derived(guider?.state ?? 'Stopped')
  const running = $derived(['Guiding', 'Calibrating', 'Looping', 'LostLock'].includes(phase))
  const stats = $derived(guider?.stats)
  const unit = $derived(stats?.units === 'arcsec' ? '″' : ' px')
  const calibration = $derived(n?.calibration)
  const drift = $derived(guider?.drift)
  const fmt = (v: number | null | undefined, digits = 2) => (v == null ? '—' : v.toFixed(digits))
  const polar = (arcmin: number) => (arcmin >= 60 ? `${(arcmin / 60).toFixed(1)}°` : `${arcmin.toFixed(0)}′`)

  let ditherPixels = $state(5)

  const badgeClass = $derived(
    guider?.settling ? 'warn' : phase === 'Guiding' ? 'ok' : phase === 'LostLock' ? 'danger' : running ? 'warn' : 'muted',
  )

  // The guide frame, swapped in only once the next has loaded, so it doesn't flash blank every frame.
  let shown = $state<string | null>(null)
  $effect(() => {
    const frame = n?.frame ?? 0
    if (!frame) return
    const url = api.guideFrameUrl(frame, guideView.stretch)
    const img = new Image()
    img.onload = () => (shown = url)
    img.src = url
  })

  const w = $derived(n?.frameWidth ?? 0)
  const h = $derived(n?.frameHeight ?? 0)
  const mark = $derived(Math.max(w, h) / 60) // marker size in frame pixels
  const lock = $derived(n?.stars.find((m) => m.lock))
  const star = $derived(n?.star)

  // Click (or tap) a star to guide on it: the click converted into frame pixels.
  let frameSvg = $state<SVGSVGElement>()
  function pick(e: MouseEvent) {
    if (!frameSvg || !running || phase === 'Calibrating') return
    const matrix = frameSvg.getScreenCTM()
    if (!matrix) return
    const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(matrix.inverse())
    act(() => api.guideSelectStar(p.x, p.y))
  }

  function saveExposure(e: Event) {
    if (!s) return
    const seconds = Number((e.currentTarget as HTMLInputElement).value)
    act(() => api.novaGuiderSettings({ ...s, exposureSeconds: seconds }))
  }

  // The graph: the steps kept (up to 300), with lines at half and full scale. Dithers are marked and their settling
  // isn't drawn or scaled to (see guideGraph), so one dither doesn't squash the rest.
  const steps = 300
  const chart = $derived(guideGraph(guider?.history ?? [], steps, 300, 100))

  const when = (utc: string) => new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  const rate = (px: number, arcsec: number | null) => (arcsec != null ? `${arcsec.toFixed(1)}″/s` : `${px.toFixed(1)} px/s`)
</script>

<div class="panel guiding">
  <div class="head">
    <h2>Guiding</h2>
    <span class="badge {badgeClass}">{guider?.settling ? 'Settling' : phase}</span>
    <span class="muted activity">{n?.darkProgress ? `Taking darks: ${n.darkProgress}` : (n?.activity ?? 'Stopped')}{n?.camera ? `, ${n.camera}` : ''}</span>
  </div>

  <div class="toolbar">
    <button class="secondary" disabled={phase === 'Looping'} onclick={() => act(api.guiderLoop)}
      title="Expose the guide camera in a loop and pick a guide star, without correcting">Loop</button>
    <button class="secondary" disabled={phase === 'Calibrating'} onclick={() => act(api.guiderCalibrate)}
      title="Measure how guide pulses move the star (near the meridian and Dec 0 is best)">Calibrate</button>
    <button disabled={guider?.settling || !guider?.connected} onclick={() => act(() => api.guide())}>
      {phase === 'Guiding' || phase === 'LostLock' ? 'Restart' : 'Guide'}
    </button>
    <button class="danger" disabled={!running && !n?.darkProgress} onclick={() => act(api.guiderStop)}>Stop</button>
    <span class="gap"></span>
    <div class="segmented" role="group" aria-label="Stretch" title="How bright the guide frame is shown: Low is just the stars, High shows faint ones (and the noise)">
      {#each ['low', 'medium', 'high'] as level (level)}
        <button class:on={guideView.stretch === level} onclick={() => setGuideStretch(level as GuideStretch)}>{level === 'medium' ? 'Med' : level[0].toUpperCase() + level.slice(1)}</button>
      {/each}
    </div>
    <label class="field">Exposure
      <input class="mono" type="number" min="0.05" max="30" step="0.5" value={s?.exposureSeconds ?? 2} onchange={saveExposure} /> s
    </label>
    <label class="field">
      <input class="mono" type="number" min="0.5" max="50" step="0.5" bind:value={ditherPixels} aria-label="Dither amount (pixels)" /> px
    </label>
    <button class="secondary" disabled={phase !== 'Guiding' || guider?.settling} onclick={() => act(() => api.dither(ditherPixels))}>Dither</button>
  </div>

  <div class="main">
    <div class="frame">
      {#if w && h}
        <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
        <svg bind:this={frameSvg} viewBox="0 0 {w} {h}" class:lost={phase === 'LostLock'} role="img" aria-label="Guide frame: click a star to guide on it" onclick={pick}>
          {#if shown}<image href={shown} x="0" y="0" width={w} height={h} preserveAspectRatio="none" />{/if}
          {#each n?.stars ?? [] as m, i (i)}
            {#if !m.lock}
              <circle class="ref" class:missing={!m.found} cx={m.x} cy={m.y} r={mark * 0.8} stroke-width={mark / 8} />
            {/if}
          {/each}
          {#if star}
            <line class="cross" x1={star.lockX - mark * 2} y1={star.lockY} x2={star.lockX + mark * 2} y2={star.lockY} stroke-width={mark / 8} />
            <line class="cross" x1={star.lockX} y1={star.lockY - mark * 2} x2={star.lockX} y2={star.lockY + mark * 2} stroke-width={mark / 8} />
          {/if}
          {#if lock}
            <rect class="lock" class:missing={!lock.found} x={lock.x - mark} y={lock.y - mark} width={mark * 2} height={mark * 2} stroke-width={mark / 8} />
          {/if}
        </svg>
      {:else}
        <p class="muted empty">
          {#if guider?.backend !== 'nova'}Choose NOVA in the Guider panel to guide with NOVA's own guider.
          {:else if !guider?.connected}No guide camera: choose one in the Guider panel.
          {:else}Press Loop to see the guide camera.{/if}
        </p>
      {/if}
    </div>

    <aside>
      <svg class="zoom" viewBox={star ? `${star.lockX - 32} ${star.lockY - 32} 64 64` : '0 0 64 64'} role="img" aria-label="The guide star close up">
        {#if shown && star}
          <image href={shown} x="0" y="0" width={w} height={h} preserveAspectRatio="none" />
          <line class="cross" x1={star.lockX - 32} y1={star.lockY} x2={star.lockX + 32} y2={star.lockY} stroke-width="0.3" />
          <line class="cross" x1={star.lockX} y1={star.lockY - 32} x2={star.lockX} y2={star.lockY + 32} stroke-width="0.3" />
          <circle class="ring" cx={star.x} cy={star.y} r="4" stroke-width="0.4" />
        {/if}
      </svg>
      <div class="facts mono">
        <span class="muted">Star</span><span>SNR {fmt(star?.snr, 0)}</span>
        <span></span><span>HFD {fmt(guider?.hfd, 1)} px</span>
        <span></span><span>{star ? `${star.found} of ${star.tracked} stars` : '—'}</span>
        <span class="muted">RMS</span><span>RA {fmt(stats?.rmsRa)}{unit}</span>
        <span></span><span>Dec {fmt(stats?.rmsDec)}{unit}</span>
        <span></span><span class="total">Total {fmt(stats?.rmsTotal)}{unit}</span>
        <span class="muted">Peak</span><span>RA {fmt(stats?.peakRa)}{unit}</span>
        <span></span><span>Dec {fmt(stats?.peakDec)}{unit}</span>
        <span></span><span class="muted">{stats?.samples ?? 0} steps</span>
      </div>
      {#if calibration}
        <div class="calibration mono">
          <span class="muted">Calibrated {when(calibration.utc)}</span>
          <span class="muted">Dec {calibration.decDegrees.toFixed(0)}°{calibration.pierSide ? `, pier ${calibration.pierSide.toLowerCase()}` : ''}</span>
          <span>RA {calibration.raAngle.toFixed(0)}° at {rate(calibration.raRate, calibration.raArcsecPerSecond)}</span>
          <span>Dec {calibration.decAngle.toFixed(0)}° at {rate(calibration.decRate, calibration.decArcsecPerSecond)}</span>
        </div>
        <CalibrationGraph {calibration} />
        {#if calibration.warning}<p class="small warn">{calibration.warning}</p>{/if}
      {:else}
        <p class="muted small">Not calibrated yet: Guide calibrates first.</p>
      {/if}
      {#if n?.darks}
        <p class="small mono" class:warn={!n.darks.inUse}>
          Darks {n.darks.exposures.join(', ')} s{n.darks.inUse ? ', in use' : n.darks.gainMatches ? ', off' : ', other gain: not used'}
        </p>
      {:else if n}
        <p class="small warn">No dark library: hot pixels can be taken for stars (Guider panel, Dark library).</p>
      {/if}
      <p class="muted small">Click a star in the frame to guide on it. Green square: the guide star; yellow rings: the stars averaged with it.</p>
    </aside>
  </div>

  {#if drift?.significant}
    <p class="drift">Dec drifting {drift.arcsecPerMinute.toFixed(0)}″/min: polar alignment at least {polar(drift.polarErrorArcmin)} off</p>
  {/if}
  <div class="graph">
    <svg viewBox="0 0 300 100" preserveAspectRatio="none" role="img" aria-label="Guiding error graph: RA and Dec">
      {#if chart}
        {#each chart.settling as [from, to], i (i)}<rect class="settle-band" x={from} y="0" width={Math.max(0.5, to - from)} height="100" />{/each}
        <line class="grid" x1="0" y1={chart.y(chart.limit / 2)} x2="300" y2={chart.y(chart.limit / 2)} />
        <line class="grid" x1="0" y1={chart.y(-chart.limit / 2)} x2="300" y2={chart.y(-chart.limit / 2)} />
        <line class="zero" x1="0" y1="50" x2="300" y2="50" />
        {#each chart.dithers as x, i (i)}<line class="dither-mark" x1={x} y1="0" x2={x} y2="100" />{/each}
        {#each chart.ra as points, i (i)}<polyline class="ra" {points} />{/each}
        {#each chart.dec as points, i (i)}<polyline class="dec" {points} />{/each}
      {:else}
        <line class="zero" x1="0" y1="50" x2="300" y2="50" />
      {/if}
    </svg>
    <div class="scale mono muted">
      <span>+{chart?.limit ?? 2}{unit}</span><span>0</span><span>−{chart?.limit ?? 2}{unit}</span>
    </div>
  </div>
  <div class="legend muted">
    <span class="ra-key">— RA</span><span class="dec-key">— Dec</span><span class="dither-key">┆ dither (settling shaded, left out of the RMS)</span><span>last {steps} steps, newest on the right</span>
  </div>
</div>

<style>
  .guiding {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-height: 0;
    overflow-y: auto;
    overflow-x: hidden;
  }
  .head {
    display: flex;
    align-items: baseline;
    gap: 12px;
  }
  .head h2 {
    margin: 0;
  }
  .activity {
    font-size: 13px;
  }
  .toolbar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
  }
  .gap {
    flex: 1;
  }
  .segmented {
    display: flex;
  }
  .segmented button {
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
  .field {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 13px;
  }
  .field input {
    width: 64px;
  }
  /* The frame gets what the window has left after the header, tabs, toolbar and graph (at least 260 px); in a short
     window the tab scrolls rather than squeezing the frame under the graph. */
  .main {
    display: flex;
    gap: 12px;
    flex: none;
    height: clamp(260px, calc(100vh - 460px), 75vh);
  }
  .frame {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #000;
    border: 1px solid var(--panel-border);
    min-height: 240px;
    overflow: hidden; /* never over the toolbar or the graph */
  }
  /* Fits inside the box whatever its shape (letterboxed; clicks still map to the right pixels). */
  .frame svg {
    width: 100%;
    height: 100%;
    cursor: crosshair;
    display: block;
  }
  .frame svg.lost {
    outline: 2px solid var(--danger);
  }
  .empty {
    padding: 24px;
    text-align: center;
  }
  aside {
    width: 240px;
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
    overflow-y: auto;
  }
  aside > :global(*) {
    flex-shrink: 0;
  }
  .zoom {
    width: 100%;
    aspect-ratio: 1;
    background: #000;
    border: 1px solid var(--panel-border);
  }
  .facts {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 1px 8px;
    font-size: 13px;
  }
  .total {
    font-weight: 600;
  }
  .calibration {
    display: flex;
    flex-direction: column;
    font-size: 12px;
  }
  .small {
    font-size: 12px;
    margin: 0;
  }
  .warn,
  .drift {
    color: var(--warn);
  }
  .drift {
    margin: 0;
    font-size: 13px;
  }
  .ref {
    fill: none;
    stroke: var(--warn);
  }
  .lock {
    fill: none;
    stroke: var(--ok);
  }
  .missing {
    stroke-dasharray: 2 2;
    opacity: 0.6;
  }
  .cross {
    stroke: var(--ok);
    opacity: 0.7;
  }
  .ring {
    fill: none;
    stroke: var(--warn);
  }
  .graph {
    display: flex;
    gap: 6px;
  }
  .graph svg {
    flex: 1;
    height: clamp(100px, 20vh, 180px);
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
    stroke-width: 1.2;
    vector-effect: non-scaling-stroke;
  }
  .zero,
  .grid {
    stroke: var(--panel-border);
    vector-effect: non-scaling-stroke;
  }
  .grid {
    stroke-dasharray: 3 3;
  }
  .scale {
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    font-size: 11px;
    padding: 2px 0;
  }
  .ra,
  .ra-key {
    stroke: #60a5fa;
    color: #60a5fa;
  }
  .dec,
  .dec-key {
    stroke: #f87171;
    color: #f87171;
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
  }
  @media (max-width: 900px) {
    .main {
      flex-direction: column;
      flex: none;
      height: auto;
    }
    .frame {
      min-height: 0;
    }
    .frame svg {
      height: auto;
      max-height: 60vh;
    }
    aside {
      width: auto;
      display: grid;
      grid-template-columns: 160px 1fr;
      align-items: start;
      column-gap: 12px;
    }
    .zoom {
      grid-row: span 4;
    }
    .graph svg {
      height: 140px;
    }
  }
</style>
