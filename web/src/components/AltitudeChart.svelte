<script lang="ts">
  import type { AltitudeCurve } from '../lib/types'

  // A target's altitude through the night: daylight and twilight shaded, the Moon dashed, the
  // minimum altitude as a line, and "now".
  let {
    curve,
    minAltitude = 30,
    label = '',
    fill = false,
  }: { curve: AltitudeCurve; minAltitude?: number; label?: string; /** As tall as its box, rather than a fixed height. */ fill?: boolean } = $props()

  // Drawn at the size it's given (no stretched text): its width, and a fixed height unless it fills its box.
  let width = $state(600)
  let height = $state(120)
  const W = $derived(Math.max(200, width))
  const H = $derived(fill ? Math.max(80, height) : 120)
  const PAD = { left: 28, right: 8, top: 8, bottom: 20 }

  const t0 = $derived(new Date(curve.points[0]?.utc ?? 0).getTime())
  const t1 = $derived(new Date(curve.points.at(-1)?.utc ?? 0).getTime())
  const x = (ms: number) => PAD.left + ((ms - t0) / Math.max(1, t1 - t0)) * (W - PAD.left - PAD.right)
  const y = (alt: number) => PAD.top + (1 - Math.max(0, Math.min(90, alt)) / 90) * (H - PAD.top - PAD.bottom)

  const path = (key: 'altitude' | 'moonAltitude') =>
    curve.points.map((p, i) => `${i ? 'L' : 'M'}${x(new Date(p.utc).getTime()).toFixed(1)},${y(p[key]).toFixed(1)}`).join('')

  // Sky brightness bands from the Sun's altitude: day, twilight, dark.
  const bands = $derived(
    curve.points.slice(1).map((p, i) => {
      const a = curve.points[i]
      const sun = (a.sunAltitude + p.sunAltitude) / 2
      return {
        x: x(new Date(a.utc).getTime()),
        w: x(new Date(p.utc).getTime()) - x(new Date(a.utc).getTime()) + 0.5,
        shade: sun > -0.8 ? 'day' : sun > -6 ? 'civil' : sun > -12 ? 'nautical' : sun > -18 ? 'astro' : 'dark',
      }
    }),
  )

  // Hour labels every 2 hours, or every 4, 6 or 12 when the chart is too narrow for them not to touch.
  const LabelSpace = 34 // px a "00:00" label needs (about 28) and a gap
  const hours = $derived.by(() => {
    const ticks: { x: number; text: string }[] = []
    const perHour = (W - PAD.left - PAD.right) / Math.max(1, (t1 - t0) / 3600_000)
    const every = [2, 4, 6, 12].find((h) => h * perHour >= LabelSpace) ?? 12
    const start = new Date(t0)
    start.setMinutes(0, 0, 0)
    for (let t = start.getTime() + 3600_000; t < t1; t += 3600_000) {
      const d = new Date(t)
      if (d.getHours() % every === 0) ticks.push({ x: x(t), text: d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) })
    }
    return ticks
  })

  let now = $state(Date.now())
  $effect(() => {
    const timer = setInterval(() => (now = Date.now()), 60_000)
    return () => clearInterval(timer)
  })

  const peak = $derived(curve.points.reduce((best, p) => (p.altitude > best.altitude ? p : best), curve.points[0]))
</script>

<div class="wrap" class:fill bind:clientWidth={width} bind:clientHeight={height}>
<svg viewBox="0 0 {W} {H}" width={W} height={H} class="chart" role="img" aria-label="Altitude of {label} through the night">
  {#each bands as b, i (i)}<rect class={b.shade} x={b.x} y={PAD.top} width={b.w} height={H - PAD.top - PAD.bottom} />{/each}
  {#each [0, 30, 60, 90] as a (a)}
    <line class="grid" x1={PAD.left} x2={W - PAD.right} y1={y(a)} y2={y(a)} />
    <text class="axis" x={PAD.left - 4} y={y(a) + 3} text-anchor="end">{a}°</text>
  {/each}
  <line class="limit" x1={PAD.left} x2={W - PAD.right} y1={y(minAltitude)} y2={y(minAltitude)} />
  {#each hours as h (h.x)}<text class="axis" x={h.x} y={H - 6} text-anchor="middle">{h.text}</text>{/each}
  <path class="moon" d={path('moonAltitude')} />
  <path class="target" d={path('altitude')} />
  {#if peak && peak.altitude > 0}
    <text class="peak" x={x(new Date(peak.utc).getTime())} y={y(peak.altitude) - 4} text-anchor="middle">{peak.altitude.toFixed(0)}°</text>
  {/if}
  {#if now > t0 && now < t1}<line class="now" x1={x(now)} x2={x(now)} y1={PAD.top} y2={H - PAD.bottom} />{/if}
</svg>
</div>

<style>
  .wrap {
    width: 100%;
  }
  .wrap.fill {
    flex: 1;
    min-height: 0;
    overflow: hidden;
  }
  .chart {
    display: block;
  }
  rect.day {
    fill: color-mix(in srgb, var(--accent) 18%, transparent);
  }
  rect.civil {
    fill: color-mix(in srgb, var(--accent) 12%, transparent);
  }
  rect.nautical {
    fill: color-mix(in srgb, var(--accent) 7%, transparent);
  }
  rect.astro {
    fill: color-mix(in srgb, var(--accent) 3%, transparent);
  }
  rect.dark {
    fill: transparent;
  }
  .grid {
    stroke: var(--panel-border);
    stroke-width: 1;
  }
  .limit {
    stroke: var(--warn);
    stroke-dasharray: 4 4;
    stroke-width: 1;
  }
  .axis {
    fill: var(--muted);
    font-size: 10px;
  }
  .target {
    fill: none;
    stroke: var(--ok);
    stroke-width: 2;
  }
  .moon {
    fill: none;
    stroke: var(--muted);
    stroke-width: 1.5;
    stroke-dasharray: 3 3;
  }
  .peak {
    fill: var(--ok);
    font-size: 10px;
  }
  .now {
    stroke: var(--danger);
    stroke-width: 1.5;
  }
</style>
