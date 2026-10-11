<script lang="ts">
  // The Polar alignment tab: three-point (as N.I.N.A.'s TPPA) or with the camera on the pole (as SharpCap), the error
  // live in degrees, minutes and seconds, a target chart that zooms in as the error shrinks, and the latest frame
  // (with the mount's axis and the pole marked, on the pole).
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import type { PolarAlignmentSettings } from '../lib/types'

  const pa = $derived(nova.status?.polarAlignment)
  const run = $derived(pa?.status ?? null)
  const running = $derived(pa?.running ?? false)
  const error = $derived(run?.error ?? null)
  const settings = $derived(pa?.settings)
  const method = $derived(running && run ? run.method : (settings?.method ?? 'ThreePoint'))
  const mountReady = $derived(nova.status?.mount?.connected ?? false)
  const cameraReady = $derived(nova.status?.camera?.connected ?? false)
  const north = $derived(run?.northernHemisphere ?? true)

  // ---- Numbers: degrees, minutes and seconds, as N.I.N.A. shows them ----
  function dms(arcmin: number): string {
    const total = Math.round(Math.abs(arcmin) * 60)
    const pad = (n: number) => String(n).padStart(2, '0')
    return `${Math.floor(total / 3600)}° ${pad(Math.floor(total / 60) % 60)}′ ${pad(total % 60)}″`
  }
  // Facing the pole: + azimuth error means the axis is to the right, so move it left (west in the north).
  const azimuthMove = (arcmin: number) => {
    const left = arcmin > 0
    const side = left === north ? 'west' : 'east'
    return { arrow: left ? '←' : '→', word: left ? 'Left' : 'Right', side }
  }
  const quality = (total: number) => (total < 1 ? 'good' : total < 5 ? 'close' : 'far')

  // ---- Settings ----
  let editing = $state(false)
  let draft = $state<PolarAlignmentSettings | null>(null)
  function editSettings() {
    if (settings) draft = { ...settings }
    editing = !editing
  }
  async function saveSettings() {
    if (draft && (await act(() => api.polarSettings({ ...draft! })))) editing = false
  }
  function chooseMethod(m: 'ThreePoint' | 'Pole') {
    if (!settings || running || settings.method === m) return
    act(() => api.polarSettings({ ...settings, method: m }))
    if (draft) draft.method = m
  }

  // ---- Target chart: zooms in as the error shrinks, with rings at 30″, 1′ and 5′ (and wider ones while far off) ----
  const scales = [1, 2, 5, 10, 20, 30, 60, 120, 300, 600]
  let scale = $state(10)
  $effect(() => {
    if (!error) return
    const need = Math.max(error.totalArcmin * 1.25, 0.8)
    const fits = scales.find((s) => s >= need) ?? scales[scales.length - 1]
    // Zoom out straight away; zoom in only once it's clearly smaller (no jumping back and forth).
    if (fits > scale || fits <= scale / 2.5) scale = fits
  })
  const R = 140
  const at = (az: number, alt: number) => ({ x: 160 + (az / scale) * R, y: 160 - (alt / scale) * R })
  const rings = $derived([0.5, 1, 5, 10, 30, 60, 120].filter((r) => r <= scale * 1.01))
  const ringLabel = (r: number) => (r < 1 ? `${r * 60}″` : r >= 60 ? `${r / 60}°` : `${r}′`)
  const trail = $derived((run?.history ?? []).slice(-60).map((h) => at(h.azimuthArcmin, h.altitudeArcmin)))
  const dot = $derived(error ? at(error.azimuthArcmin, error.altitudeArcmin) : null)
  // Off the chart (shouldn't be, as it zooms out): clamp to the edge.
  const clamp = (p: { x: number; y: number }) => {
    const dx = p.x - 160
    const dy = p.y - 160
    const d = Math.hypot(dx, dy)
    return d > R + 6 ? { x: 160 + (dx / d) * (R + 6), y: 160 + (dy / d) * (R + 6) } : p
  }

  // ---- History: the total error frame by frame ----
  const history = $derived.by(() => {
    const h = run?.history ?? []
    if (h.length < 2) return null
    const max = Math.max(...h.map((p) => p.totalArcmin), 1)
    const w = 300
    const ht = 60
    return {
      max,
      points: h.map((p, i) => `${(i / (h.length - 1)) * w},${ht - (p.totalArcmin / max) * ht}`).join(' '),
    }
  })

  // ---- The latest frame, with the axis and pole marked (pole method) ----
  // Only polar alignment's own frame: not an image opened from the computer, or taken elsewhere, since.
  const image = $derived(nova.image && run?.imageId != null && nova.image.id === run.imageId ? nova.image : null)
  const overlay = $derived(run?.overlay ?? null)
  const imageUrl = $derived(image ? api.previewUrl(image, 'auto', true, 1600) : null)
  const px = (arcsec: number) => (overlay ? arcsec / overlay.arcsecPerPixel : 0)
  const unit = $derived(overlay ? overlay.width / 320 : 1)
  const upArrow = $derived.by(() => {
    if (!overlay?.axis || !overlay.upFromAxis) return null
    const [ax, ay] = overlay.axis
    const dx = overlay.upFromAxis[0] - ax
    const dy = overlay.upFromAxis[1] - ay
    const d = Math.hypot(dx, dy) || 1
    const len = unit * 22
    return { x1: ax, y1: ay, x2: ax + (dx / d) * len, y2: ay + (dy / d) * len }
  })

  // Markers outside the frame: an arrow at the edge pointing their way, so you know where they are.
  function offFrame(p: [number, number] | null) {
    if (!overlay || !p) return null
    const [x, y] = p
    const margin = unit * 10
    if (x >= 0 && x <= overlay.width && y >= 0 && y <= overlay.height) return null
    const cx = overlay.width / 2
    const cy = overlay.height / 2
    const dx = x - cx
    const dy = y - cy
    // Where the line from the centre leaves the frame, pulled in by the margin.
    const t = Math.min((cx - margin) / Math.abs(dx || 1e-9), (cy - margin) / Math.abs(dy || 1e-9))
    const ex = cx + dx * t
    const ey = cy + dy * t
    const angle = (Math.atan2(dy, dx) * 180) / Math.PI
    const distance = Math.hypot(x - ex, y - ey) * overlay.arcsecPerPixel / 60
    return { x: ex, y: ey, angle, distance }
  }
  const poleOff = $derived(offFrame(overlay?.pole ?? null))
  const axisOff = $derived(offFrame(overlay?.axis ?? null))
  const polarisOff = $derived(offFrame(overlay?.polaris ?? null))
  const offText = (arcmin: number) => (arcmin >= 60 ? `${(arcmin / 60).toFixed(1)}°` : `${Math.round(arcmin)}′`)
  // The arrows to show; the axis and the pole off the same edge (close together, as they should be) share one.
  const edgeMarks = $derived.by(() => {
    const marks: { name: string; cls: string; off: NonNullable<ReturnType<typeof offFrame>> }[] = []
    const together = axisOff && poleOff && Math.hypot(axisOff.x - poleOff.x, axisOff.y - poleOff.y) < unit * 40
    if (together) marks.push({ name: 'axis and pole', cls: 'to-pole', off: poleOff! })
    else {
      if (axisOff) marks.push({ name: 'axis', cls: 'axis-cross', off: axisOff })
      if (poleOff) marks.push({ name: 'pole', cls: 'to-pole', off: poleOff })
    }
    if (polarisOff) marks.push({ name: 'Polaris', cls: 'up', off: polarisOff })
    return marks
  })

  // ---- Simulator: virtual knobs ----
  let knobStep = $state(1)
  const turn = (altitude: number, azimuth: number) => act(() => api.turnPolarKnobs(altitude, azimuth))
</script>

<section class="polar">
  <div class="top">
    <div class="methods" role="group" aria-label="Method">
      <button class:active={method === 'ThreePoint'} aria-pressed={method === 'ThreePoint'} disabled={running}
        title="Three plate solves, turning RA between them: works almost anywhere in the sky" onclick={() => chooseMethod('ThreePoint')}>Three-point</button
      >
      <button class:active={method === 'Pole'} aria-pressed={method === 'Pole'} disabled={running}
        title="The camera on the pole with Polaris in view: two solves, turning RA about 90°" onclick={() => chooseMethod('Pole')}>On the pole</button
      >
    </div>
    {#if running}
      {#if run?.waitingForRotation}
        <button onclick={() => act(api.polarContinue)}>Continue</button>
      {/if}
      <button class={run?.phase === 'Adjusting' ? '' : 'danger'} onclick={() => act(api.polarStop)}>{run?.phase === 'Adjusting' ? 'Done' : 'Cancel'}</button>
    {:else}
      <button disabled={!mountReady || !cameraReady}
        title={mountReady && cameraReady ? 'Start polar alignment' : 'Connect a mount and camera first'}
        onclick={() => act(api.polarStart)}>{run ? 'Start again' : 'Start'}</button
      >
    {/if}
    <button class="secondary" disabled={running} aria-expanded={editing} onclick={editSettings}>⚙ Settings</button>
    {#if run}
      <span class="badge {running ? 'warn' : run.phase === 'Done' ? 'ok' : run.phase === 'Failed' ? 'danger' : 'muted'}">{run.phase}</span>
    {/if}
  </div>

  {#if editing && draft}
    <div class="settings panel">
      {#if draft.method === 'ThreePoint'}
        <label title="How far RA turns between the three points">Turn between points (°)
          <input type="number" min="2" max="60" bind:value={draft.stepDegrees} /></label>
        <label title="Degrees above the pole for the automatic start (N.I.N.A.'s default: 2)">Start above the pole (°)
          <input type="number" min="-30" max="30" step="0.5" bind:value={draft.altitudeOffset} disabled={!draft.startAutomatically || draft.manual} /></label>
        <label title="Degrees east of the pole for the automatic start (negative: west)">Start east of the pole (°)
          <input type="number" min="-90" max="90" step="0.5" bind:value={draft.azimuthOffset} disabled={!draft.startAutomatically || draft.manual} /></label>
      {:else}
        <label title="How far RA turns between the two frames: 60–90° is best">Turn between frames (°)
          <input type="number" min="30" max="120" step="5" bind:value={draft.poleTurnDegrees} /></label>
      {/if}
      <label title="Which way RA turns">Direction
        <select bind:value={draft.direction}>
          <option value="East">East</option>
          <option value="West">West</option>
        </select></label
      >
      <label title="Exposure per plate solve">Exposure (s)
        <input type="number" min="0" max="60" step="0.5" bind:value={draft.exposureSeconds} /></label>
      <div class="checks">
        <label class="check" title="Slew to the start first, assuming the mount points roughly at the pole (its home position is fine). Off: start wherever the scope points">
          <input type="checkbox" bind:checked={draft.startAutomatically} disabled={draft.manual} />
          {draft.method === 'Pole' ? 'Slew to the pole first' : 'Slew to the start first'}</label
        >
        <label class="check" title="For mounts without GoTo or a drivable RA axis: you turn RA and press Continue">
          <input type="checkbox" bind:checked={draft.manual} /> Turn RA by hand</label
        >
        <label class="check" title="Aim at the refracted pole (where it appears) rather than the true pole">
          <input type="checkbox" bind:checked={draft.refraction} /> Refraction</label
        >
      </div>
      <div class="row">
        <span class="spacer"></span>
        <button class="secondary" onclick={() => (editing = false)}>Cancel</button>
        <button onclick={saveSettings}>Save</button>
      </div>
    </div>
  {/if}

  <div class="body">
    <div class="side">
      {#if !run && settings}
        <p class="muted hint">
          {#if method === 'Pole'}
            Point at the pole with Polaris in the frame{settings.startAutomatically && !settings.manual ? ' (NOVA slews there from the home position)' : ''}
            and press Start. NOVA solves, turns RA {settings.poleTurnDegrees}° {settings.direction.toLowerCase()}, solves again, then marks the
            mount's axis on the live image: turn the altitude and azimuth knobs until the pole sits in the axis rings.
          {:else if settings.startAutomatically && !settings.manual}
            Set the mount up roughly at the pole (the home position is fine) and press Start. NOVA slews to {settings.altitudeOffset}° above the
            pole, plate solves three times turning RA {settings.stepDegrees}° {settings.direction.toLowerCase()} between them, then shows the error
            live while you turn the altitude and azimuth knobs.
          {:else}
            Point almost anywhere above 15° (near the pole is easiest; avoid straight overhead and due east or west) and press Start. NOVA plate
            solves three times turning RA {settings.stepDegrees}° {settings.direction.toLowerCase()} between them, then shows the error live.
          {/if}
        </p>
      {/if}

      {#if run}
        {#if run.phase === 'Measuring' || (run.phase !== 'Adjusting' && !error)}
          <div class="steps">
            {#each Array.from({ length: run.points }, (_, i) => i + 1) as p (p)}
              <span class="step" class:done={p < run.point || (p === run.point && run.message.includes('solved'))} class:now={p === run.point}>{p}</span>
            {/each}
          </div>
        {/if}
        <p class="message" class:bad={run.phase === 'Failed'}>{run.message}</p>
        {#if error?.warning}<p class="warning">⚠ {error.warning}</p>{/if}
      {/if}

      {#if error}
        <div class="numbers panel">
          <div class="total {quality(error.totalArcmin)}">{dms(error.totalArcmin)}</div>
          <div class="muted small">total error{#if run?.initialError}{' '}(started at {dms(run.initialError.totalArcmin)}, {run.frames} frames){/if}</div>
          <div class="dir">
            <span class="arrow">{error.altitudeArcmin > 0 ? '↓' : '↑'}</span>
            <span class="what">Altitude: {error.altitudeArcmin > 0 ? 'lower' : 'raise'}</span>
            <span class="value mono">{dms(error.altitudeArcmin)}</span>
          </div>
          <div class="dir">
            <span class="arrow">{azimuthMove(error.azimuthArcmin).arrow}</span>
            <span class="what">Azimuth: {azimuthMove(error.azimuthArcmin).word.toLowerCase()} ({azimuthMove(error.azimuthArcmin).side})</span>
            <span class="value mono">{dms(error.azimuthArcmin)}</span>
          </div>
        </div>

        <svg class="chart" viewBox="0 0 320 320" role="img" aria-label="Where the mount's axis is, relative to the pole">
          <line class="axis" x1="10" y1="160" x2="310" y2="160" />
          <line class="axis" x1="160" y1="10" x2="160" y2="310" />
          {#each rings as r (r)}
            <circle class="ring" class:goal={r === 1} class:fine={r === 0.5} cx="160" cy="160" r={(r / scale) * R} />
            <text class="ring-label" x={160 + (r / scale) * R * 0.71 + 3} y={160 - (r / scale) * R * 0.71 - 3}>{ringLabel(r)}</text>
          {/each}
          <text class="label" x="160" y="12" text-anchor="middle">up</text>
          <text class="label" x="314" y="156" text-anchor="end">right</text>
          <text class="label" x="6" y="156">left</text>
          {#if trail.length > 1}
            <polyline class="trail" points={trail.map((p) => { const c = clamp(p); return `${c.x},${c.y}` }).join(' ')} />
          {/if}
          {#if dot}
            {@const c = clamp(dot)}
            <circle class="now {quality(error.totalArcmin)}" cx={c.x} cy={c.y} r="6" />
          {/if}
        </svg>
        <p class="muted small">
          As seen facing the {north ? 'north' : 'south'} pole, {scale >= 60 ? `${scale / 60}°` : `${scale}′`} to the edge: move the dot into the
          middle; the chart zooms in as it gets close.
          {#if run?.phase === 'Done'}The mount moved: plate solve and sync before going to a target.{/if}
        </p>

        {#if history}
          <div class="history">
            <div class="muted small">Total error, frame by frame (top: {dms(history.max)})</div>
            <svg viewBox="0 0 300 60" preserveAspectRatio="none" role="img" aria-label="Total error over time">
              <polyline points={history.points} />
            </svg>
          </div>
        {/if}
      {/if}

      {#if pa?.knobs}
        <div class="knobs">
          <div class="muted small">
            Simulator knobs (true error: altitude {dms(pa.knobs.altitudeErrorArcmin)}, azimuth {dms(pa.knobs.azimuthErrorArcmin)})
          </div>
          <div class="row">
            <button class="secondary small" onclick={() => turn(knobStep, 0)} title="Raise the axis">▲</button>
            <button class="secondary small" onclick={() => turn(-knobStep, 0)} title="Lower the axis">▼</button>
            <button class="secondary small" onclick={() => turn(0, -knobStep)} title="Move the axis left">◀</button>
            <button class="secondary small" onclick={() => turn(0, knobStep)} title="Move the axis right">▶</button>
            <select bind:value={knobStep} aria-label="Knob step">
              <option value={0.5}>0.5′</option>
              <option value={1}>1′</option>
              <option value={5}>5′</option>
            </select>
          </div>
        </div>
      {/if}
    </div>

    <div class="frame">
      {#if image && imageUrl}
        <div class="picture" style:aspect-ratio="{image.width} / {image.height}">
          <img src={imageUrl} alt="The latest frame" />
          {#if overlay && method === 'Pole'}
            <svg viewBox="0 0 {overlay.width} {overlay.height}" role="img" aria-label="The mount's axis and the pole in the frame">
              {#if overlay.axis}
                {@const [ax, ay] = overlay.axis}
                <circle class="axis-ring" cx={ax} cy={ay} r={px(300)} stroke-width={unit} />
                <circle class="axis-ring goal" cx={ax} cy={ay} r={px(60)} stroke-width={unit} />
                <circle class="axis-ring fine" cx={ax} cy={ay} r={px(30)} stroke-width={unit * 0.8} />
                <line class="axis-cross" x1={ax - unit * 8} y1={ay} x2={ax + unit * 8} y2={ay} stroke-width={unit * 0.8} />
                <line class="axis-cross" x1={ax} y1={ay - unit * 8} x2={ax} y2={ay + unit * 8} stroke-width={unit * 0.8} />
                <text class="tag" x={ax + px(300) + unit * 3} y={ay} font-size={unit * 9}>axis</text>
                {#if overlay.pole}
                  <line class="to-pole" x1={overlay.pole[0]} y1={overlay.pole[1]} x2={ax} y2={ay} stroke-width={unit * 0.8} />
                {/if}
              {/if}
              {#if overlay.pole}
                <circle class="pole" cx={overlay.pole[0]} cy={overlay.pole[1]} r={unit * 4} />
                <text class="tag pole-tag" x={overlay.pole[0] + unit * 6} y={overlay.pole[1] - unit * 6} font-size={unit * 9}>pole</text>
              {/if}
              {#if overlay.polaris}
                <circle class="polaris" cx={overlay.polaris[0]} cy={overlay.polaris[1]} r={unit * 7} stroke-width={unit * 0.8} />
                <text class="tag" x={overlay.polaris[0] + unit * 9} y={overlay.polaris[1] + unit * 3} font-size={unit * 9}>Polaris</text>
              {/if}
              {#each edgeMarks as m (m.name)}
                <g transform="translate({m.off.x} {m.off.y}) rotate({m.off.angle})">
                  <path class="edge {m.cls}" d="M {unit * 6} 0 L {-unit * 4} {-unit * 4} L {-unit * 4} {unit * 4} Z" />
                </g>
                <text class="tag" x={m.off.x} y={m.off.y + unit * (m.off.y > (overlay?.height ?? 0) / 2 ? -9 : 14)} font-size={unit * 9}
                  text-anchor={m.off.x > (overlay?.width ?? 0) * 0.8 ? 'end' : m.off.x < (overlay?.width ?? 0) * 0.2 ? 'start' : 'middle'}
                  >{m.name} {offText(m.off.distance)} out</text
                >
              {/each}
              {#if upArrow}
                <line class="up" x1={upArrow.x1} y1={upArrow.y1} x2={upArrow.x2} y2={upArrow.y2} stroke-width={unit} />
                <text class="tag" x={upArrow.x2 + unit * 3} y={upArrow.y2} font-size={unit * 8}>up</text>
              {/if}
            </svg>
          {/if}
        </div>
        {#if method === 'Pole' && overlay}
          <p class="muted small">Turn the knobs to bring the pole (the filled dot) into the axis rings: 5′, 1′ and 30″.</p>
        {/if}
      {:else}
        <p class="muted empty">
          {run?.imageId != null && nova.image ? "The latest image isn't from polar alignment: its frames appear here as they're taken." : "The frames appear here as they're taken."}
        </p>
      {/if}
    </div>
  </div>
</section>

<style>
  .polar {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 12px;
    height: 100%;
    overflow: auto;
  }
  .top {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
  }
  .methods {
    display: inline-flex;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    overflow: hidden;
  }
  .methods button {
    border: 0;
    border-radius: 0;
    background: transparent;
    color: var(--muted);
  }
  .methods button.active {
    background: var(--accent);
    color: var(--accent-text);
  }
  .settings {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
    gap: 8px;
    padding: 12px;
  }
  .settings label {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  .checks {
    display: grid;
    gap: 4px;
    grid-column: 1 / -1;
  }
  .check {
    display: flex !important;
    align-items: center;
    gap: 6px;
  }
  .settings .row {
    grid-column: 1 / -1;
  }
  .row {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .spacer {
    flex: 1;
  }
  .body {
    display: grid;
    grid-template-columns: minmax(300px, 380px) 1fr;
    gap: 16px;
    align-items: start;
  }
  @media (max-width: 900px) {
    .body {
      grid-template-columns: 1fr;
    }
  }
  .side {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .hint,
  .message {
    margin: 0;
    font-size: 13px;
  }
  .message.bad {
    color: var(--danger);
  }
  .warning {
    margin: 0;
    font-size: 12px;
    color: var(--warn);
  }
  .steps {
    display: flex;
    gap: 6px;
  }
  .step {
    width: 24px;
    height: 24px;
    border-radius: 50%;
    border: 1px solid var(--panel-border);
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 12px;
    color: var(--muted);
  }
  .step.now {
    border-color: var(--accent);
    color: var(--text);
  }
  .step.done {
    border-color: var(--ok);
    color: var(--ok);
  }
  .numbers {
    padding: 12px;
    display: grid;
    gap: 4px;
  }
  .total {
    font-family: var(--font-mono);
    font-size: 28px;
    font-weight: 600;
  }
  .total.good {
    color: var(--ok);
  }
  .total.close {
    color: var(--warn);
  }
  .total.far {
    color: var(--danger);
  }
  .dir {
    display: grid;
    grid-template-columns: 22px 1fr auto;
    align-items: baseline;
    gap: 6px;
    font-size: 14px;
  }
  .arrow {
    font-size: 18px;
  }
  .value {
    font-size: 15px;
  }
  .small {
    font-size: 12px;
  }
  .chart {
    width: 100%;
    max-width: 380px;
  }
  .chart .axis {
    stroke: var(--panel-border);
  }
  .chart .ring {
    fill: none;
    stroke: var(--muted);
    stroke-opacity: 0.45;
  }
  .chart .ring.goal {
    stroke: var(--ok);
    stroke-opacity: 0.9;
  }
  .chart .ring.fine {
    stroke: var(--ok);
    stroke-dasharray: 3 3;
  }
  .ring-label,
  .label {
    fill: var(--muted);
    font-size: 10px;
  }
  .trail {
    fill: none;
    stroke: var(--accent);
    stroke-opacity: 0.5;
  }
  .now {
    stroke: var(--bg);
    stroke-width: 1.5;
  }
  .now.good {
    fill: var(--ok);
  }
  .now.close {
    fill: var(--warn);
  }
  .now.far {
    fill: var(--danger);
  }
  .history svg {
    width: 100%;
    height: 60px;
    display: block;
  }
  .history polyline {
    fill: none;
    stroke: var(--accent);
    stroke-width: 1.5;
    vector-effect: non-scaling-stroke;
  }
  .knobs {
    display: grid;
    gap: 6px;
  }
  .frame {
    min-width: 0;
  }
  .picture {
    position: relative;
    width: 100%;
    background: #000;
  }
  .picture img,
  .picture svg {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
  }
  .picture {
    overflow: hidden;
  }
  :global(:root[data-theme='night']) .picture img {
    filter: grayscale(1) sepia(1) saturate(6) hue-rotate(-50deg) brightness(0.55);
  }
  .axis-ring {
    fill: none;
    stroke: #7fb6de;
  }
  .axis-ring.goal,
  .axis-ring.fine {
    stroke: #6fcf7f;
  }
  .axis-ring.fine {
    stroke-dasharray: 4 4;
  }
  .axis-cross {
    stroke: #7fb6de;
  }
  .to-pole {
    stroke: #f0c060;
    stroke-dasharray: 6 4;
  }
  .pole {
    fill: #f0c060;
  }
  .polaris {
    fill: none;
    stroke: #f0f0f0;
  }
  .up {
    stroke: #cfd6e0;
  }
  .edge {
    fill: #f0c060;
    stroke: none;
  }
  .edge.axis-cross {
    fill: #7fb6de;
  }
  .edge.up {
    fill: #e8edf3;
  }
  .tag {
    fill: #e8edf3;
    paint-order: stroke;
    stroke: #000;
    stroke-width: 0.4em;
  }
  .pole-tag {
    fill: #f0c060;
  }
  .empty {
    padding: 40px 0;
    text-align: center;
  }
</style>
