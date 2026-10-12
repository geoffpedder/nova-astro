<script lang="ts">
  import type { GuideCalibrationInfo } from '../lib/types'

  // The calibration as PHD2 draws it: where the star went after each West and East step (blue) and each North and
  // South step (red), in guide camera pixels, with the RA and Dec directions NOVA fitted through them. Two straight
  // lines crossing at a right angle is a good calibration; a bent or wandering Dec line usually means backlash or
  // polar drift during the Dec steps.
  let { calibration }: { calibration: GuideCalibrationInfo } = $props()

  const points = $derived(calibration.points ?? [])
  // A square view around the points (and where they started), with a margin.
  const box = $derived.by(() => {
    const xs = [0, ...points.map((p) => p.x)]
    const ys = [0, ...points.map((p) => p.y)]
    const size = Math.max(10, Math.max(...xs) - Math.min(...xs), Math.max(...ys) - Math.min(...ys)) * 1.25
    const cx = (Math.max(...xs) + Math.min(...xs)) / 2
    const cy = (Math.max(...ys) + Math.min(...ys)) / 2
    return { x: cx - size / 2, y: cy - size / 2, size }
  })
  const extent = $derived(box.size)
  const r = $derived(extent / 80)
  const northStart = $derived(points.find((p) => p.phase === 'North') ?? { x: 0, y: 0 })
  const axis = (angle: number, through: { x: number; y: number }) => {
    const a = (angle * Math.PI) / 180
    return { x1: through.x - Math.cos(a) * extent * 2, y1: through.y - Math.sin(a) * extent * 2, x2: through.x + Math.cos(a) * extent * 2, y2: through.y + Math.sin(a) * extent * 2 }
  }
  const ra = $derived(axis(calibration.raAngle, { x: 0, y: 0 }))
  const dec = $derived(axis(calibration.decAngle, northStart))
</script>

{#if points.length}
  <svg class="cal" viewBox="{box.x} {box.y} {box.size} {box.size}" role="img"
    aria-label="Calibration graph: {calibration.orthogonality.toFixed(1)} degrees from square">
    <line class="zero" x1={box.x} y1="0" x2={box.x + box.size} y2="0" />
    <line class="zero" x1="0" y1={box.y} x2="0" y2={box.y + box.size} />
    <line class="ra fit" {...ra} />
    <line class="dec fit" {...dec} />
    {#each points as p, i (i)}
      <circle class={p.phase === 'West' || p.phase === 'East' ? 'ra' : 'dec'} class:back={p.phase === 'East' || p.phase === 'South'}
        cx={p.x} cy={p.y} r={r} stroke-width={r / 2} />
    {/each}
  </svg>
  <div class="legend muted">
    <span class="ra-key">● West ○ East</span><span class="dec-key">● North ○ South</span>
    <span class:warn={calibration.orthogonality > 10}>{calibration.orthogonality.toFixed(1)}° from square</span>
  </div>
{:else}
  <p class="muted small">No graph for this calibration (it was made before NOVA kept them): calibrate again to see one.</p>
{/if}

<style>
  .cal {
    width: 100%;
    aspect-ratio: 1;
    background: var(--input);
    border-radius: 4px;
    display: block;
  }
  .zero {
    stroke: var(--panel-border);
    stroke-width: 1;
    vector-effect: non-scaling-stroke;
  }
  .fit {
    stroke-width: 1;
    stroke-dasharray: 4 3;
    vector-effect: non-scaling-stroke;
    opacity: 0.7;
  }
  circle.ra {
    fill: #60a5fa;
    stroke: #60a5fa;
  }
  circle.dec {
    fill: #f87171;
    stroke: #f87171;
  }
  circle.back {
    fill: none;
  }
  line.ra {
    stroke: #60a5fa;
  }
  line.dec {
    stroke: #f87171;
  }
  .ra-key {
    color: #60a5fa;
  }
  .dec-key {
    color: #f87171;
  }
  :global(:root[data-theme='night']) circle.ra,
  :global(:root[data-theme='night']) line.ra {
    fill: #a01010;
    stroke: #a01010;
  }
  :global(:root[data-theme='night']) circle.dec,
  :global(:root[data-theme='night']) line.dec {
    fill: #ff3030;
    stroke: #ff3030;
  }
  :global(:root[data-theme='night']) circle.back {
    fill: none;
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
    gap: 4px 10px;
    font-size: 11px;
    margin-top: 4px;
  }
  .warn {
    color: var(--warn);
  }
  .small {
    font-size: 12px;
    margin: 0;
  }
</style>
