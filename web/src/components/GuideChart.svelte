<script lang="ts">
  import { guideGraph, type GuideStep } from '../lib/guideGraph'

  // The guiding graph: RA and Dec error over the last steps, with lines at half and full scale. Dithers are marked and
  // their settling isn't drawn or scaled to (see guideGraph), so one dither doesn't squash the rest. `fill` makes it
  // as tall as its box (the pop-out) rather than a set height (the Guiding tab).
  let { history, unit, steps = 300, fill = false }: { history: GuideStep[]; unit: string; steps?: number; fill?: boolean } = $props()

  const chart = $derived(guideGraph(history, steps, 300, 100))
</script>

<div class="graph" class:fill>
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

<style>
  .graph {
    display: flex;
    gap: 6px;
  }
  .graph svg {
    flex: 1;
    min-width: 0;
    height: clamp(100px, 20vh, 180px);
    background: var(--input);
    border-radius: 4px;
  }
  .graph.fill {
    height: 100%;
  }
  .graph.fill svg {
    height: auto;
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
  polyline {
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
  .ra {
    stroke: #60a5fa;
  }
  .dec {
    stroke: #f87171;
  }
  :global(:root[data-theme='night']) .ra {
    stroke: #a01010;
  }
  :global(:root[data-theme='night']) .dec {
    stroke: #ff3030;
  }
  @media (max-width: 900px) {
    .graph:not(.fill) svg {
      height: 140px;
    }
  }
</style>
