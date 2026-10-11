<script lang="ts">
  import type { HfrPoint } from '../lib/types'

  /** Recent frames' HFR: smaller is sharper. What to watch while focusing in Loop mode. */
  let { points }: { points: HfrPoint[] } = $props()

  const W = 160
  const H = 34
  const measured = $derived(points.filter((p) => p.hfr != null).slice(-30) as (HfrPoint & { hfr: number })[])
  const chart = $derived.by(() => {
    if (measured.length < 2) return null
    const values = measured.map((p) => p.hfr)
    const lo = Math.min(...values)
    const hi = Math.max(...values)
    const span = Math.max(hi - lo, 0.05)
    const x = (i: number) => 2 + (i / (measured.length - 1)) * (W - 4)
    const y = (v: number) => 3 + (1 - (v - lo) / span) * (H - 6)
    const line = measured.map((p, i) => `${x(i).toFixed(1)},${y(p.hfr).toFixed(1)}`).join(' ')
    const best = values.indexOf(lo)
    return { line, last: { x: x(measured.length - 1), y: y(values.at(-1)!) }, best: { x: x(best), y: y(lo) }, lo, hi }
  })
</script>

{#if chart}
  <span class="trend" title="HFR of the last {measured.length} frames: best {chart.lo.toFixed(2)}, worst {chart.hi.toFixed(2)} px">
    <svg width={W} height={H} viewBox="0 0 {W} {H}" role="img" aria-label="HFR trend">
      <polyline points={chart.line} />
      <circle class="best" cx={chart.best.x} cy={chart.best.y} r="2.5" />
      <circle class="last" cx={chart.last.x} cy={chart.last.y} r="2.5" />
    </svg>
  </span>
{/if}

<style>
  .trend {
    display: inline-flex;
    align-items: center;
    vertical-align: middle;
  }
  svg {
    background: var(--input);
    border-radius: 4px;
  }
  polyline {
    fill: none;
    stroke: var(--accent);
    stroke-width: 1.5;
  }
  .best {
    fill: var(--ok);
  }
  .last {
    fill: var(--text);
  }
</style>
