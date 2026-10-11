<script lang="ts">
  import { act } from '../lib/state.svelte'

  // The stored sky survey: how much of it the mini PC has, and downloading whole levels for use offline.

  interface Status {
    storedTiles: number
    storedBytes: number
    completeOrder: number
    folder: string
    download: {
      state: 'Running' | 'Done' | 'Cancelled' | 'Failed'
      maxOrder: number
      total: number
      done: number
      failed: number
      bytes: number
      message: string | null
    } | null
  }

  let open = $state(false)
  let status = $state<Status | null>(null)
  let level = $state(5)
  const running = $derived(status?.download?.state === 'Running')

  // Levels, their detail and (measured on DSS colour tiles, ~55 KB each) roughly how much disk they take.
  const levels = [
    { order: 3, detail: '50″/px', tiles: 1020 },
    { order: 4, detail: '25″/px', tiles: 4092 },
    { order: 5, detail: '13″/px', tiles: 16380 },
    { order: 6, detail: '6″/px', tiles: 65532 },
  ]
  const size = (bytes: number) => (bytes >= 1e9 ? `${(bytes / 1e9).toFixed(2)} GB` : `${Math.round(bytes / 1e6)} MB`)

  async function refresh() {
    try {
      status = await (await fetch('/api/sky/tiles/status')).json()
    } catch {
      // keep the last
    }
  }
  $effect(() => {
    if (!open && !running) return
    refresh()
    const timer = setInterval(refresh, 2000)
    return () => clearInterval(timer)
  })

  async function post(path: string, body?: unknown) {
    const response = await fetch(path, {
      method: 'POST',
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      throw new Error(problem?.detail ?? `${response.status} ${response.statusText}`)
    }
    await refresh()
  }
</script>

<div class="wrap">
  <button class="secondary" class:busy={running} aria-expanded={open} onclick={() => (open = !open)} title="Keep the sky survey on the mini PC for use without internet">
    {running && status?.download ? `⤓ ${Math.floor((100 * status.download.done) / status.download.total)}%` : '⤓ Offline sky'}
  </button>
  {#if open}
    <div class="pop panel">
      <b>Offline sky survey (DSS colour)</b>
      <p class="muted small">
        The mini PC keeps every sky tile it shows. Download whole levels in advance (with internet) so the chart has imagery
        in the field.
      </p>
      {#if status}
        <p class="small">
          Stored: {status.storedTiles.toLocaleString()} tiles, {size(status.storedBytes)}{status.completeOrder >= 0
            ? `. The whole sky is stored to level ${status.completeOrder}.`
            : ''}
        </p>
        {#if status.download}
          {@const d = status.download}
          <div class="bar" title="{d.done} of {d.total} tiles"><div style:width="{(100 * d.done) / d.total}%"></div></div>
          <p class="small" class:bad={d.state === 'Failed'}>
            {d.state === 'Running' ? 'Downloading' : d.state} to level {d.maxOrder}: {d.done.toLocaleString()} / {d.total.toLocaleString()} tiles,
            {size(d.bytes)} fetched{d.failed ? `, ${d.failed} failed` : ''}{d.message ? `. ${d.message}` : ''}
          </p>
        {/if}
      {/if}
      <div class="row">
        <select bind:value={level} disabled={running} aria-label="Level to download">
          {#each levels as l (l.order)}
            <option value={l.order}>Level {l.order}: {l.detail}, about {size(l.tiles * 55_000)}</option>
          {/each}
        </select>
        {#if running}
          <button class="danger" onclick={() => act(() => post('/api/sky/tiles/cancel'))}>Stop</button>
        {:else}
          <button onclick={() => act(() => post('/api/sky/tiles/download', { maxOrder: level }))}>Download</button>
        {/if}
      </div>
      <p class="muted small">
        Already-stored tiles are skipped, so a stopped download carries on where it left off. Level 5 suits framing at a few
        hundred mm focal length; for close-ups use the high-res images (online). Survey: Digitized Sky Survey (STScI/NASA),
        coloured and tiled by CDS.
      </p>
    </div>
  {/if}
</div>

<style>
  .wrap {
    position: relative;
  }
  .busy {
    color: var(--warn);
  }
  .pop {
    position: absolute;
    top: calc(100% + 6px);
    left: 0;
    z-index: 30;
    width: min(380px, 80vw);
    display: grid;
    gap: 8px;
    padding: 12px;
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
  }
  p {
    margin: 0;
  }
  .small {
    font-size: 12px;
  }
  .bad {
    color: var(--danger);
  }
  .row {
    display: flex;
    gap: 6px;
  }
  .row select {
    flex: 1;
  }
  .bar {
    height: 6px;
    background: var(--input);
    border-radius: 3px;
    overflow: hidden;
  }
  .bar div {
    height: 100%;
    background: var(--accent);
  }
</style>
