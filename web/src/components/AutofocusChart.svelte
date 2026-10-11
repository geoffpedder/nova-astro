<script lang="ts">
  import type { FocusFit, FocusPoint } from '../lib/types'

  /** The V curve: measured HFR per focuser position, the fitted hyperbola and its minimum. */
  let { points, fit, best }: { points: FocusPoint[]; fit: FocusFit | null; best: number | null } = $props()

  const W = 300
  const H = 150
  const PAD = { left: 30, right: 10, top: 8, bottom: 20 }

  /** A round step (1, 2 or 5 × 10ⁿ) giving about `count` intervals over `range`. */
  function niceStep(range: number, count: number): number {
    const raw = range / count
    const power = 10 ** Math.floor(Math.log10(raw))
    const unit = raw / power
    return (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10) * power
  }

  // The axes fit the data, not zero: the V fills the chart so its shape and minimum are easy to read.
  const chart = $derived.by(() => {
    if (points.length === 0) return null
    const xs = points.map((p) => p.position)
    const xSpan = Math.max(Math.max(...xs) - Math.min(...xs), 2)
    const xPad = xs.length > 1 ? xSpan / (xs.length - 1) / 2 : 1 // half a step either side
    const lo = Math.min(...xs) - xPad
    const hi = Math.max(...xs) + xPad

    const ys = points.map((p) => p.hfr)
    if (fit) ys.push(fit.bestHfr)
    const ySpan = Math.max(Math.max(...ys) - Math.min(...ys), 0.2)
    const yStep = niceStep(ySpan * 1.15, 4)
    const yMin = Math.max(0, Math.floor((Math.min(...ys) - ySpan * 0.08) / yStep) * yStep)
    const yMax = Math.ceil((Math.max(...ys) + ySpan * 0.08) / yStep) * yStep
    const ticks: number[] = []
    for (let t = yMin; t <= yMax + yStep / 2; t += yStep) ticks.push(t)
    const decimals = yStep < 0.1 ? 2 : yStep < 1 ? 1 : 0

    const x = (v: number) => PAD.left + ((v - lo) / (hi - lo)) * (W - PAD.left - PAD.right)
    const y = (v: number) => PAD.top + (1 - (v - yMin) / (yMax - yMin)) * (H - PAD.top - PAD.bottom)

    let curve: string | null = null
    if (fit) {
      const samples = 80
      curve = Array.from({ length: samples + 1 }, (_, i) => {
        const p = lo + ((hi - lo) * i) / samples
        const hfr = Math.sqrt(Math.max(fit.a + fit.b * (p - fit.c) ** 2, 0))
        return `${x(p).toFixed(1)},${y(hfr).toFixed(1)}`
      }).join(' ')
    }
    const marker = best ?? fit?.bestPosition ?? null
    return {
      x,
      y,
      first: Math.min(...xs),
      last: Math.max(...xs),
      ticks: ticks.map((t) => ({ y: y(t), label: t.toFixed(decimals) })),
      curve,
      marker: marker !== null && marker >= lo && marker <= hi ? marker : null,
    }
  })
</script>

{#if chart}
  <svg viewBox="0 0 {W} {H}" role="img" aria-label="Autofocus curve">
    <defs>
      <clipPath id="af-plot">
        <rect x={PAD.left} y={PAD.top} width={W - PAD.left - PAD.right} height={H - PAD.top - PAD.bottom} />
      </clipPath>
    </defs>
    {#each chart.ticks as t (t.label)}
      <line class="grid" x1={PAD.left} y1={t.y} x2={W - PAD.right} y2={t.y} />
      <text class="label" x={PAD.left - 4} y={t.y + 3} text-anchor="end">{t.label}</text>
    {/each}
    <line class="axis" x1={PAD.left} y1={H - PAD.bottom} x2={W - PAD.right} y2={H - PAD.bottom} />
    <line class="axis" x1={PAD.left} y1={PAD.top} x2={PAD.left} y2={H - PAD.bottom} />
    <text class="label" x={chart.x(chart.first)} y={H - 6} text-anchor="middle">{chart.first}</text>
    <text class="label" x={chart.x(chart.last)} y={H - 6} text-anchor="middle">{chart.last}</text>
    {#if chart.marker !== null}
      <line class="best" x1={chart.x(chart.marker)} y1={PAD.top} x2={chart.x(chart.marker)} y2={H - PAD.bottom} />
      <text class="label best-label" x={chart.x(chart.marker)} y={H - 6} text-anchor="middle">{chart.marker}</text>
    {/if}
    {#if chart.curve}<polyline class="fit" points={chart.curve} clip-path="url(#af-plot)" />{/if}
    {#each points as p (p.position)}
      <circle cx={chart.x(p.position)} cy={chart.y(p.hfr)} r="3">
        <title>{p.position}: HFR {p.hfr.toFixed(2)} px, {p.stars} stars</title>
      </circle>
    {/each}
  </svg>
{/if}

<style>
  svg {
    width: 100%;
    height: auto;
    display: block;
    background: var(--input);
    border-radius: var(--radius);
  }
  .axis {
    stroke: var(--muted);
    stroke-opacity: 0.5;
  }
  .grid {
    stroke: var(--panel-border);
  }
  .label {
    fill: var(--muted);
    font-size: 9px;
  }
  .best {
    stroke: var(--ok);
    stroke-dasharray: 3 3;
  }
  .best-label {
    fill: var(--ok);
  }
  .fit {
    fill: none;
    stroke: var(--accent);
    stroke-width: 1.5;
  }
  circle {
    fill: var(--kind-focuser);
  }
</style>
