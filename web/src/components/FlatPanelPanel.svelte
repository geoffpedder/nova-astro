<script lang="ts">
  // A flat panel: its light (on/off, brightness) and, on a flip-flat, the cover over the telescope. The flats
  // wizard drives it by itself; this is for doing it by hand.
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import DevicePicker from './DevicePicker.svelte'
  import Panel from './Panel.svelte'

  const panel = $derived(nova.status?.flatPanel)
  const ready = $derived(panel?.connected ?? false)
  const busy = $derived(panel?.coverMoving ?? false)
  // The slider shows the panel's brightness, and what you're choosing while you drag.
  let level = $state<number | null>(null)
  const shownLevel = $derived(level ?? panel?.brightness?.value ?? 0)

  function setLight(on: boolean, brightness?: number) {
    act(() => api.flatPanelLight(on, brightness ?? null))
  }
</script>

<Panel id="flatpanel" title="Flat panel" kind="flatpanel">
  {#snippet badge()}
    {#if panel}
      <span class="badge {busy ? 'warn' : !ready ? 'muted' : panel.lightOn ? 'ok' : 'muted'}">
        {busy ? 'Cover moving' : !ready ? 'Not connected' : panel.lightOn ? 'Lit' : 'Off'}
      </span>
    {/if}
  {/snippet}

  <DevicePicker kind="FlatPanel" disabled={busy} />

  {#if !panel}
    <p class="muted">No flat panel found.</p>
  {:else if ready}
    {#if panel.hasLight}
      <div class="row">
        <label class="check">
          <input type="checkbox" role="switch" checked={panel.lightOn} onchange={(e) => setLight(e.currentTarget.checked)} />
          Light
        </label>
        {#if panel.brightness}
          <input
            class="level"
            type="range"
            min={panel.brightness.min}
            max={panel.brightness.max}
            step={panel.brightness.step || 1}
            value={shownLevel}
            oninput={(e) => (level = Number(e.currentTarget.value))}
            onchange={(e) => {
              setLight(panel.lightOn, Number(e.currentTarget.value))
              level = null
            }}
            aria-label="Brightness"
            title="Brightness"
          />
          <span class="mono value">{Math.round(shownLevel)}</span>
        {/if}
      </div>
    {/if}
    {#if panel.hasCover}
      <div class="row">
        <span class="muted">Cover {busy ? 'moving…' : panel.coverClosed ? 'closed' : 'open'}</span>
        <span class="spacer"></span>
        <button class="secondary" disabled={busy || panel.coverClosed === true} onclick={() => act(() => api.flatPanelCover(true))}
          title="Close the cover over the telescope">Close</button
        >
        <button class="secondary" disabled={busy || panel.coverClosed === false} onclick={() => act(() => api.flatPanelCover(false))}
          title="Open the cover for imaging">Open</button
        >
      </div>
    {/if}
    <p class="muted hint">The flats wizard switches it on and off, sets the brightness and closes the cover by itself.</p>
  {/if}
</Panel>

<style>
  .row {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 8px;
  }
  .level {
    flex: 1;
    min-width: 0;
  }
  .value {
    width: 32px;
    text-align: right;
  }
  .spacer {
    flex: 1;
  }
  .check {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .hint {
    font-size: 11px;
    margin: 8px 0 0;
  }
</style>
