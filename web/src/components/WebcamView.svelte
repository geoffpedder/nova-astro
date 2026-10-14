<script lang="ts">
  import { api } from '../lib/api'
  import { act } from '../lib/state.svelte'
  import { mainView } from '../lib/inbox.svelte'
  import { liveStream, saveWebcamLayout, webcamLayout, webcamStreamUrl } from '../lib/webcam.svelte'
  import type { WebcamSettings, WebcamStatus } from '../lib/types'

  // The webcam on the mini PC, full size: for watching the rig from indoors. It streams only while this tab shows
  // (or the popped-out window does); NOVA stops the webcam a little after nobody watches.
  let { active }: { active: boolean } = $props()

  let status = $state<WebcamStatus | null>(null)
  let src = $state<string | null>(null)

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

  function popOut() {
    webcamLayout.popped = true
    saveWebcamLayout()
    mainView.view = 'image'
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
    <button class="secondary" onclick={popOut} title="A small floating view that stays on screen over the other tabs">Pop out</button>
  </div>

  <div class="view">
    <!-- The stream stays open while waiting or after a failure: NOVA keeps trying (a webcam plugged in starts by itself). -->
    {#if src}{#key src}<img class="frame" class:hidden={status?.state === 'Error' || status?.state === 'Waiting'} {src} alt="The webcam's live view" use:liveStream />{/key}{/if}
    {#if status?.state === 'Error'}
      <p class="bad">{status.error}<br /><span class="muted small">Trying again every couple of seconds.</span></p>
    {:else if status?.state === 'Waiting'}
      <p class="muted">Waiting for a webcam: plug one into the mini PC (a different USB port from the cameras). It starts by itself.</p>
    {/if}
  </div>
  {#if status?.note}<p class="warn small">{status.note}</p>{/if}
  <p class="muted small">
    Plug the webcam into a different USB port from the cameras (a USB 2 guide camera on the same hub can lose frames),
    and tape over its light. It only runs while this view, or the popped-out one, is open.
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
