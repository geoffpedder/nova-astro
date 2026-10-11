<script lang="ts">
  // Starting flats by hand: what they're for (the folder name) and how to take them. Once started, FlatsPrompt
  // walks through it (panel on → exposure → flats → cover → dark flats).
  import { untrack } from 'svelte'
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import type { FlatsSettings } from '../lib/types'

  let { name: initialName, onclose }: { name: string; onclose: () => void } = $props()

  const saved = nova.status?.flats?.settings
  // A flat panel NOVA can drive.
  const panel = nova.status?.flatPanel?.connected && nova.status.flatPanel.hasLight ? nova.status.flatPanel.name : null
  let name = $state(untrack(() => initialName)) // a suggestion, then yours to edit
  let settings = $state<FlatsSettings>(
    saved ? { ...saved, usePanel: saved.usePanel ?? true } : { targetPercent: 50, count: 25, darkFlats: true, minExposure: 0.1, maxExposure: 30, usePanel: true },
  )

  async function start() {
    if (await act(() => api.flatsStart(name.trim(), $state.snapshot(settings)))) onclose()
  }
  function keydown(e: KeyboardEvent) {
    if (e.key === 'Escape') onclose()
  }
</script>

<svelte:window onkeydown={keydown} />

<div class="backdrop" role="presentation" onclick={onclose}></div>
<div class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="flats-title">
  <h3 id="flats-title">Flats</h3>
  <p class="muted">
    With the camera exactly as it was for your lights (same angle and focus). NOVA asks you to put the panel on, finds the exposure,
    takes the flats, then asks you to cover the scope for the dark flats.
  </p>
  <label>
    For
    <input bind:value={name} placeholder="Target name (the folder: Flats/…)" />
  </label>
  <div class="grid">
    <label title="Median brightness to aim for: about half of full scale suits most cameras">
      Brightness (%)
      <input type="number" min="10" max="90" step="5" bind:value={settings.targetPercent} />
    </label>
    <label>
      Count
      <input type="number" min="1" max="500" bind:value={settings.count} />
    </label>
    <label title="Shortest exposure: shorter ones can show banding from the shutter or a flickering panel">
      Min exposure (s)
      <input type="number" min="0.001" step="0.01" bind:value={settings.minExposure} />
    </label>
    <label>
      Max exposure (s)
      <input type="number" min="0.01" step="1" bind:value={settings.maxExposure} />
    </label>
  </div>
  <label class="check">
    <input type="checkbox" bind:checked={settings.darkFlats} /> Dark flats too (as many, same exposure, scope covered)
  </label>
  {#if panel}
    <label class="check" title="NOVA switches it on and off, sets its brightness (dimming it if even the shortest exposure is too bright) and closes its cover">
      <input type="checkbox" bind:checked={settings.usePanel} /> Use {panel} (no need to be at the scope)
    </label>
  {/if}
  <div class="actions">
    <button class="secondary" onclick={onclose}>Cancel</button>
    <button onclick={start}>Start</button>
  </div>
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 60;
  }
  .dialog {
    position: fixed;
    z-index: 61;
    left: 50%;
    top: 30%;
    transform: translate(-50%, -30%);
    width: min(460px, calc(100vw - 32px));
    display: grid;
    gap: 10px;
    padding: 16px;
    box-shadow: 0 12px 32px rgb(0 0 0 / 0.45);
  }
  h3 {
    margin: 0;
    font-size: 16px;
  }
  p {
    margin: 0;
    font-size: 13px;
  }
  label {
    display: grid;
    gap: 4px;
    font-size: 12px;
    color: var(--muted);
  }
  label input {
    color: var(--text);
  }
  label.check {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 13px;
    color: var(--text);
  }
  .grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
  }
  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
  }
</style>
