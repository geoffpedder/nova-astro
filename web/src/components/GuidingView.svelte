<script lang="ts">
  import { api, settleOf } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import { guideView, setGuideStretch, type GuideStretch } from '../lib/guideView.svelte'
  import CalibrationGraph from './CalibrationGraph.svelte'
  import GuideChart from './GuideChart.svelte'
  import { guideFloat } from '../lib/floats.svelte'
  import type { GuideLogFile } from '../lib/types'

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
  // The dither amount starts at the mount's default (smaller for a ZWO AM) until changed here.
  let ditherTouched = false
  $effect(() => {
    const d = guider?.defaults?.ditherPixels
    if (d != null && !ditherTouched) ditherPixels = d
  })

  // The guide logs (one per night): fetched when the tab opens and whenever guiding or calibration ends.
  let logs = $state<GuideLogFile[]>([])
  async function loadLogs() {
    try {
      logs = await api.guideLogs()
    } catch {
      logs = []
    }
  }
  let lastPhase = ''
  $effect(() => {
    const now = phase
    if (lastPhase === '' || (['Guiding', 'Calibrating', 'LostLock'].includes(lastPhase) && !['Guiding', 'Calibrating', 'LostLock'].includes(now))) loadLogs()
    lastPhase = now
  })
  const size = (bytes: number) => (bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`)

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

  // The graph: the steps kept (up to 300).
  const steps = 300

  // Guiding assistant: how long to follow the star unguided.
  let assistantSeconds = $state(120)
  const assistant = $derived(n?.assistant)
  const assistantRunning = $derived(!!n?.activity?.startsWith('Guiding assistant'))
  const au = $derived(assistant?.units === 'arcsec' ? '″' : ' px')

  // The verdict on recent guiding, and what guiding does to the stars: guiding blur combines with seeing (and
  // focus, optics) roughly in quadrature, so the latest frame's FWHM shows how big a part it plays.
  const diagnosis = $derived(guider?.diagnosis)
  const latest = $derived(nova.status?.latestImage)
  const frameFwhm = $derived(latest && !latest.openedFile ? latest.stars.fwhmArcsec : null)
  const withoutGuiding = $derived(
    diagnosis?.blurArcsec != null && frameFwhm != null && frameFwhm > diagnosis.blurArcsec
      ? Math.sqrt(frameFwhm * frameFwhm - diagnosis.blurArcsec * diagnosis.blurArcsec)
      : null,
  )
  const diagnosisWarn = $derived(diagnosis?.axes.some((a) => a.verdict === 'chasing' || a.verdict === 'lagging') ?? false)

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
    <span class="assist">
      <button class="secondary" disabled={assistantRunning || phase === 'Calibrating'} onclick={() => act(() => api.guiderAssistant(assistantSeconds))}
        title="Follow the star with corrections off and measure the seeing, drift and periodic error; then recommends settings (guiding pauses)">Assistant</button>
      <select bind:value={assistantSeconds} aria-label="How long the assistant measures" title="How long the assistant measures">
        <option value={60}>1 min</option><option value={120}>2 min</option><option value={180}>3 min</option><option value={300}>5 min</option>
      </select>
    </span>
    <button disabled={guider?.settling || !guider?.connected} onclick={() => act(() => api.guide(false, settleOf(guider?.defaults)))}>
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
      <input class="mono" type="number" min="0.5" max="50" step="0.5" bind:value={ditherPixels} oninput={() => (ditherTouched = true)} aria-label="Dither amount (guide pixels)" /> px
    </label>
    <button class="secondary" disabled={phase !== 'Guiding' || guider?.settling} onclick={() => act(() => api.dither(ditherPixels, settleOf(guider?.defaults)))}>Dither</button>
  </div>

  <div class="main">
    <div class="frame">
      {#if w && h}
        <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
        <svg bind:this={frameSvg} viewBox="0 0 {w} {h}" class:lost={phase === 'LostLock'} role="img" aria-label="Guide frame: click a star to guide on it" onclick={pick}>
          {#if shown}<image class="frame" href={shown} x="0" y="0" width={w} height={h} preserveAspectRatio="none" />{/if}
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
          <image class="frame" href={shown} x="0" y="0" width={w} height={h} preserveAspectRatio="none" />
          <line class="cross" x1={star.lockX - 32} y1={star.lockY} x2={star.lockX + 32} y2={star.lockY} stroke-width="0.3" />
          <line class="cross" x1={star.lockX} y1={star.lockY - 32} x2={star.lockX} y2={star.lockY + 32} stroke-width="0.3" />
          <circle class="ring" cx={star.x} cy={star.y} r="4" stroke-width="0.4" />
        {/if}
      </svg>
      <div class="facts mono">
        <span class="muted">Star</span><span>SNR {fmt(star?.snr, 0)}</span>
        <span></span><span>HFD {fmt(guider?.hfd, 1)} px</span>
        <span></span><span>{star ? `${star.found} of ${star.tracked} stars` : '—'}</span>
      </div>
      {#if assistant}
        <div class="assistant-result mono">
          <span class="muted">Assistant {when(assistant.utc)}, {Math.round(assistant.seconds)} s</span>
          <span>Seeing {assistant.jitterRa.toFixed(2)}{au} {assistant.axes === 'RA/Dec' ? 'RA' : 'X'}, {assistant.jitterDec.toFixed(2)}{au} {assistant.axes === 'RA/Dec' ? 'Dec' : 'Y'}</span>
          <span>Drift {assistant.driftRa.toFixed(2)}, {assistant.driftDec.toFixed(2)}{au}/min</span>
          {#if assistant.axes === 'RA/Dec'}<span>RA wander {assistant.periodicRa.toFixed(1)}{au} p-p</span>{/if}
          {#if assistant.pePeriods?.length}<span>RA period {assistant.pePeriods.map((p) => `${p.toFixed(0)} s`).join(' + ')}</span>{/if}
          {#if assistant.polarErrorArcmin != null}<span>Polar error ≥ {assistant.polarErrorArcmin >= 60 ? `${(assistant.polarErrorArcmin / 60).toFixed(1)}°` : `${assistant.polarErrorArcmin.toFixed(0)}′`}</span>{/if}
          <span class="rec">Suggests min move RA {assistant.recommendedRaMinMove.toFixed(2)}, Dec {assistant.recommendedDecMinMove.toFixed(2)} px{assistant.recommendedExposure != null ? `, ${assistant.recommendedExposure} s exposures` : ''}</span>
          <button class="secondary apply" disabled={assistantRunning} onclick={() => act(api.guiderApplyAssistant)}>Apply</button>
        </div>
      {/if}
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
      {#if n?.pec}
        {@const pec = n.pec}
        <p class="small mono pec" class:predicting={pec.state === 'Predicting'}
          title="Predictive PEC: learns the mount's periodic error while guiding and corrects it ahead of time">
          {#if pec.state === 'Predicting'}
            PEC predicting: {pec.periods.map((p) => `${p.toFixed(0)} s`).join(' + ')}, {(pec.quality * 100).toFixed(0)}% explained, ±{guider?.pixelScale ? `${(pec.amplitudePixels * guider.pixelScale).toFixed(1)}″` : `${pec.amplitudePixels.toFixed(2)} px`}
          {:else}
            PEC learning: {pec.minutes.toFixed(0)}{pec.minutesNeeded != null ? ` of ~${Math.ceil(pec.minutesNeeded)}` : ''} min{pec.periods.length ? `, period so far ${pec.periods.map((p) => `${p.toFixed(0)} s`).join(' + ')}` : ''}
          {/if}
        </p>
      {/if}
      {#if n?.backlash}
        {@const b = n.backlash}
        <p class="small mono pec" class:predicting={b.enabled}
          title="Dec backlash: how far a reversed Dec move runs before the star moves, measured at calibration (South steps coming up short of the North ones). Compensation adds that much pulse whenever Dec reverses, and adjusts it as it guides">
          Dec backlash {guider?.pixelScale ? `${(b.measuredPixels * guider.pixelScale).toFixed(1)}″` : `${b.measuredPixels.toFixed(1)} px`} ({b.measuredMs} ms){b.enabled
            ? `: adding ${b.pulseMs} ms on reversals${b.applied ? `, ${b.applied} so far` : ''}`
            : ': compensation off'}
        </p>
      {/if}
      {#if n?.darks}
        <p class="small mono" class:warn={!n.darks.inUse}>
          Darks {n.darks.exposures.join(', ')} s{n.darks.inUse ? ', in use' : n.darks.gainMatches ? ', off' : ', other gain: not used'}
        </p>
      {:else if n}
        <p class="small warn">No dark library: hot pixels can be taken for stars (Guider panel, Dark library).</p>
      {/if}
      <p class="muted small">Click a star in the frame to guide on it. Green square: the guide star; yellow rings: the stars averaged with it.</p>
      <div class="logs">
        <span class="muted small">Guide logs{s && !s.guideLog ? ' (off in Guiding settings)' : ''}</span>
        {#each logs.slice(0, 5) as log (log.name + log.onDrive)}
          <a class="small mono" href={api.guideLogUrl(log)} download={log.name} title="PHD2's log format: opens in PHD2 log viewers">
            {log.night}{log.onDrive ? ' (drive)' : ''}, {size(log.bytes)}
          </a>
        {:else}
          <span class="muted small">None yet: one is written per night while calibrating and guiding.</span>
        {/each}
      </div>
    </aside>
  </div>

  {#if diagnosis}
    <p class="diagnosis" class:warn={diagnosisWarn}>
      {diagnosis.summary}.
      {#if diagnosis.blurArcsec != null}
        <span class="muted">Guiding adds about {diagnosis.blurArcsec.toFixed(1)}″ to star FWHM{#if frameFwhm != null}; the latest frame's stars are {frameFwhm.toFixed(1)}″{#if withoutGuiding != null && frameFwhm - withoutGuiding >= 0.1} (about {withoutGuiding.toFixed(1)}″ without it){/if}{/if}.</span>
      {/if}
    </p>
  {/if}
  {#if drift?.significant}
    <p class="drift">Dec drifting {drift.arcsecPerMinute.toFixed(0)}″/min: polar alignment at least {polar(drift.polarErrorArcmin)} off</p>
  {/if}
  <!-- The RMS beside the graph it describes. -->
  <div class="chart-row">
    <div class="chart"><GuideChart history={guider?.history ?? []} {unit} {steps} /></div>
    <div class="rms facts mono">
      <span class="muted">RMS</span><span>RA {fmt(stats?.rmsRa)}{unit}</span>
      <span></span><span>Dec {fmt(stats?.rmsDec)}{unit}</span>
      <span></span><span class="total">Total {fmt(stats?.rmsTotal)}{unit}</span>
      <span class="muted">Peak</span><span>RA {fmt(stats?.peakRa)}{unit}</span>
      <span></span><span>Dec {fmt(stats?.peakDec)}{unit}</span>
      <span></span><span class="muted">{stats?.samples ?? 0} steps</span>
    </div>
  </div>
  <div class="legend muted">
    <span class="ra-key">— RA</span><span class="dec-key">— Dec</span><span class="dither-key">┆ dither (settling shaded, left out of the RMS)</span><span>last {steps} steps, newest on the right</span>
    <span class="gap"></span>
    <button class="secondary small" class:on={guideFloat.layout.popped} aria-pressed={guideFloat.layout.popped}
      onclick={() => ((guideFloat.layout.popped = !guideFloat.layout.popped), guideFloat.save())}
      title="The graph and RMS in a small window that stays on screen whichever tab is showing">Pop out</button>
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
  .chart-row {
    display: flex;
    gap: 12px;
    align-items: center;
    min-width: 0;
  }
  .chart-row .chart {
    flex: 1;
    min-width: 0;
  }
  .chart-row .rms {
    flex: none;
    width: 11em; /* fixed: changing numbers mustn't resize the graph */
  }
  @media (max-width: 700px) {
    .chart-row {
      flex-direction: column;
      align-items: stretch;
    }
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
  /* The frame takes whatever height the rest leaves, so the tab fits the window without scrolling (it scrolls only
     when the window is too short for a 260 px frame). */
  .main {
    display: flex;
    gap: 12px;
    flex: 1 1 0;
    min-height: 260px;
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
  .assist {
    display: inline-flex;
    gap: 4px;
  }
  .assist select {
    font-size: 12px;
  }
  .assistant-result {
    display: flex;
    flex-direction: column;
    font-size: 12px;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 6px 8px;
  }
  .assistant-result .rec {
    margin-top: 2px;
  }
  .assistant-result .apply {
    align-self: flex-start;
    margin-top: 4px;
    font-size: 11px;
    padding: 1px 10px;
  }
  .diagnosis {
    margin: 0;
    font-size: 13px;
  }
  .pec {
    color: var(--muted);
  }
  .pec.predicting {
    color: var(--ok);
  }
  .logs {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .logs a {
    color: var(--accent);
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
  .dither-key {
    color: var(--warn);
  }
  .ra-key {
    color: var(--ra);
  }
  .dec-key {
    color: var(--dec);
  }
  :global(:root[data-theme='night']) .ra-key {
    color: #a01010;
  }
  :global(:root[data-theme='night']) .dec-key {
    color: #ff3030;
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 12px;
    font-size: 11px;
  }
  .legend button {
    font-size: 11px;
    padding: 2px 8px;
  }
  .legend button.on {
    color: var(--accent);
    border-color: var(--accent);
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
  }
</style>
