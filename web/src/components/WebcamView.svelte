<script lang="ts">
  import { api } from '../lib/api'
  import { act } from '../lib/state.svelte'
  import { liveStream, saveWebcamLayout, webcamLayout, webcamStreamUrl } from '../lib/webcam.svelte'
  import type { WebcamControl, WebcamControls, WebcamSettings, WebcamStatus } from '../lib/types'

  // The webcam on the mini PC, full size: for watching the rig from indoors. It streams only while this tab shows
  // (or the popped-out window does); NOVA stops the webcam a little after nobody watches.
  let { active }: { active: boolean } = $props()

  let status = $state<WebcamStatus | null>(null)
  let src = $state<string | null>(null)

  // The webcam's own controls (brightness, exposure, white balance…), beside the picture when opened.
  let showControls = $state(false)
  let controls = $state<WebcamControls | null>(null)
  let dragging = $state<Record<string, number>>({}) // a slider's value while it's being moved
  async function loadControls() {
    try {
      controls = await api.webcamControls()
    } catch {
      controls = null
    }
  }
  $effect(() => {
    if (active && showControls) loadControls()
  })
  async function setControl(c: WebcamControl, value: number) {
    await act(() => api.setWebcamControl(c.name, value))
    delete dragging[c.name]
    await loadControls() // switching an automatic mode changes what else can be set
  }
  async function resetControls() {
    await act(api.resetWebcamControls)
    await loadControls()
  }
  const groups = $derived.by(() => {
    const byGroup = new Map<string, WebcamControl[]>()
    for (const c of controls?.controls ?? []) byGroup.set(c.group, [...(byGroup.get(c.group) ?? []), c])
    return [...byGroup]
  })

  async function refresh() {
    try {
      status = await api.webcam()
    } catch {
      status = null
    }
  }
  $effect(() => {
    if (!active) {
      src = null
      return
    }
    src = webcamStreamUrl()
    refresh()
    const timer = setInterval(refresh, 3000)
    return () => clearInterval(timer)
  })

  const s = $derived(status?.settings)
  async function save(patch: Partial<WebcamSettings>) {
    if (!s) return
    if (await act(() => api.webcamSettings({ ...s, ...patch }))) {
      src = webcamStreamUrl() // reconnect with the new settings
      refresh()
    }
  }
  const sizes = [
    [320, 240],
    [640, 480],
    [1280, 720],
  ]

  // Downloads a picture (the server names it by the time).
  function snapshot() {
    const a = document.createElement('a')
    a.href = '/api/webcam/snapshot'
    a.download = ''
    a.click()
  }

  // A floating window over the tabs, as well as this view; pressed again it closes.
  function popOut() {
    webcamLayout.popped = !webcamLayout.popped
    saveWebcamLayout()
  }
</script>

<div class="panel webcam">
  <div class="head">
    <h2>Webcam</h2>
    {#if status}
      <span class="badge {status.state === 'Streaming' ? 'ok' : status.state === 'Error' ? 'danger' : status.state === 'Waiting' ? 'warn' : 'muted'}">{status.state}</span>
      {#if status.fps != null}<span class="muted small mono">{status.fps.toFixed(1)} fps</span>{/if}
    {/if}
    <span class="gap"></span>
    {#if s && status}
      <select value={s.device ?? ''} onchange={(e) => save({ device: e.currentTarget.value || null })} aria-label="Webcam">
        <option value="">Automatic{status.device && !s.device ? ` (${status.devices.find((d) => d.path === status?.device)?.name ?? status.device})` : ''}</option>
        {#each status.devices as d (d.path)}<option value={d.path}>{d.name}</option>{/each}
      </select>
      <select value={`${s.width}x${s.height}`} aria-label="Size"
        onchange={(e) => { const [w, h] = e.currentTarget.value.split('x').map(Number); save({ width: w, height: h }) }}>
        {#each sizes as [w, h] (w)}<option value={`${w}x${h}`}>{w}×{h}</option>{/each}
      </select>
      <select value={s.fps} onchange={(e) => save({ fps: Number(e.currentTarget.value) })} aria-label="Frame rate">
        {#each [1, 2, 5, 10, 15] as f (f)}<option value={f}>{f} fps</option>{/each}
      </select>
    {/if}
    <button class="secondary" onclick={snapshot} title="Save a picture from the webcam">Snapshot</button>
    <button class="secondary" class:on={webcamLayout.popped} aria-pressed={webcamLayout.popped} onclick={popOut} title="A small floating view that stays on screen over the other tabs">Pop out</button>
    <button class="secondary" class:on={showControls} aria-pressed={showControls} onclick={() => (showControls = !showControls)}
      title="The webcam's own settings: brightness, exposure, white balance…">Controls</button>
  </div>

  <div class="body" class:with-controls={showControls}>
  <div class="view">
    <!-- The stream stays open while waiting or after a failure: NOVA keeps trying (a webcam plugged in starts by itself). -->
    {#if src}{#key src}<img class="frame" class:hidden={status?.state === 'Error' || status?.state === 'Waiting'} {src} alt="The webcam's live view" use:liveStream />{/key}{/if}
    {#if status?.state === 'Error'}
      <p class="bad">{status.error}<br /><span class="muted small">Trying again every couple of seconds.</span></p>
    {:else if status?.state === 'Waiting'}
      <p class="muted">Waiting for a webcam: plug one into the mini PC (a different USB port from the cameras). It starts by itself.</p>
    {/if}
  </div>
  {#if showControls}
    <aside class="controls">
      {#if controls?.error}
        <p class="bad small">{controls.error}</p>
      {:else if !controls}
        <p class="muted small">Reading the webcam's controls…</p>
      {:else if controls.controls.length === 0}
        <p class="muted small">This webcam has no settings to change.</p>
      {:else}
        {#each groups as [group, list] (group)}
          <h4>{group}</h4>
          {#each list as c (c.name)}
            <label class="control" class:inactive={c.inactive} title={c.inactive ? 'Set automatically now (turn the automatic mode off to set it)' : c.name}>
              <span class="name">{c.label}</span>
              {#if c.type === 'bool'}
                <input type="checkbox" checked={c.value === 1} disabled={c.inactive} onchange={(e) => setControl(c, e.currentTarget.checked ? 1 : 0)} />
              {:else if c.type === 'menu'}
                <select value={c.value} disabled={c.inactive} onchange={(e) => setControl(c, Number(e.currentTarget.value))}>
                  {#each c.menu as m (m.value)}<option value={m.value}>{m.label}</option>{/each}
                </select>
              {:else}
                <span class="slider">
                  <input type="range" min={c.min} max={c.max} step={c.step} value={dragging[c.name] ?? c.value} disabled={c.inactive}
                    oninput={(e) => (dragging[c.name] = Number(e.currentTarget.value))}
                    onchange={(e) => setControl(c, Number(e.currentTarget.value))} />
                  <span class="mono value">{dragging[c.name] ?? c.value}</span>
                </span>
              {/if}
            </label>
          {/each}
        {/each}
        <button class="secondary reset" onclick={resetControls} title="Every control back to the webcam's own default">Reset to defaults</button>
        <p class="muted small">Kept for this webcam and set again each time it starts.</p>
      {/if}
    </aside>
  {/if}
  </div>
  {#if status?.note}<p class="warn small">{status.note}</p>{/if}
  <p class="muted small">
    Plug the webcam into a different USB port from the cameras (a USB 2 guide camera on the same hub can lose frames).
    It only runs while this view, or the popped-out one, is open.
  </p>
</div>

<style>
  .webcam {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-height: 0;
  }
  .head {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
  }
  .head h2 {
    margin: 0;
  }
  .gap {
    flex: 1;
  }
  .head select {
    font-size: 12px;
  }
  .body {
    flex: 1;
    min-height: 0;
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    gap: 10px;
  }
  .body.with-controls {
    grid-template-columns: minmax(0, 1fr) 280px;
  }
  .controls {
    overflow-y: auto;
    overflow-x: hidden;
    padding-right: 4px;
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: 12px;
  }
  .controls h4 {
    margin: 6px 0 2px;
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--muted);
  }
  .control {
    display: grid;
    grid-template-columns: 100px minmax(0, 1fr);
    align-items: center;
    gap: 8px;
  }
  .control.inactive {
    opacity: 0.5;
  }
  .control select {
    min-width: 0;
    font-size: 12px;
  }
  .slider {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .slider input {
    flex: 1;
    min-width: 0;
  }
  .slider .value {
    flex-shrink: 0;
    min-width: 44px;
    text-align: right;
  }
  .reset {
    align-self: flex-start;
    margin-top: 8px;
    font-size: 12px;
  }
  button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  @media (max-width: 900px) {
    .body.with-controls {
      grid-template-columns: minmax(0, 1fr);
    }
  }
  .view {
    flex: 1;
    min-height: 200px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #000;
    border: 1px solid var(--panel-border);
    overflow: hidden;
  }
  .view img {
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
  }
  .view img.hidden {
    display: none;
  }
  .view p {
    padding: 16px;
    text-align: center;
  }
  .small {
    font-size: 12px;
    margin: 0;
  }
  .warn {
    color: var(--warn);
  }
  .bad {
    color: var(--danger);
    padding: 16px;
    text-align: center;
  }
</style>
