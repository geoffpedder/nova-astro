<script lang="ts">
  // Flats in progress, over every tab (you're at the scope with the panel): what to do next, and how it's going.
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'

  const f = $derived(nova.status?.flats?.status ?? null)
  let dismissed = $state(false)
  let wasRunning = false
  $effect(() => {
    const running = f?.running ?? false
    if (running && !wasRunning) dismissed = false
    wasRunning = running
  })
  const visible = $derived(!!f && (f.running || !dismissed))

  const asks: Record<string, { done: string; skip: string }> = {
    PanelOn: { done: "Panel's on", skip: 'Skip flats' },
    Dimmer: { done: 'Dimmed it', skip: 'Skip flats' },
    Brighter: { done: 'Turned it up', skip: 'Skip flats' },
    Cover: { done: 'Covered', skip: 'Skip dark flats' },
  }
  const ask = $derived(f?.ask ? asks[f.ask] : null)
  const percent = (n: number) => `${Math.round((n / 65535) * 100)}%`
</script>

{#if visible && f}
  <div class="flats" role="status" aria-live="polite">
    <header>
      <strong>{f.name ? `Flats for ${f.name}` : 'Flats'}</strong>
      <span class="spacer"></span>
      {#if !f.running}
        <button class="close" title="Dismiss" aria-label="Dismiss" onclick={() => (dismissed = true)}>✕</button>
      {/if}
    </header>

    <p class:big={!!ask} class:bad={f.phase === 'Failed'}>
      {#if f.running && !ask}<span class="spinner" aria-hidden="true"></span>{/if}
      {f.message}
    </p>

    {#if f.exposure != null || f.flats > 0}
      <div class="numbers muted small">
        {#if f.exposure != null}<span>{f.exposure.toFixed(3)} s</span>{/if}
        {#if f.median != null}<span>median {Math.round(f.median)} ({percent(f.median)}, aiming for {percent(f.targetAdu)})</span>{/if}
        {#if f.tests.length}<span>{f.tests.length} test frame{f.tests.length === 1 ? '' : 's'}</span>{/if}
        {#if f.panel}<span>{f.panel}{#if f.panelBrightness != null} at {Math.round(f.panelBrightness)}{/if}</span>{/if}
      </div>
      <div class="bars">
        <div class="bar" title="Flats">
          <span>Flats {f.flats}/{f.count}</span>
          <progress max={f.count} value={f.flats}></progress>
        </div>
        {#if f.takeDarkFlats}
          <div class="bar" title="Dark flats">
            <span>Dark flats {f.darkFlats}/{f.count}</span>
            <progress max={f.count} value={f.darkFlats}></progress>
          </div>
        {/if}
      </div>
    {/if}

    {#if ask || (f.running && !f.fromSequence)}
      <div class="buttons">
        {#if ask}
          <button onclick={() => act(() => api.flatsContinue())}>{ask.done}</button>
          <button class="secondary" onclick={() => act(() => api.flatsSkip())}>{ask.skip}</button>
        {/if}
        {#if f.running && !f.fromSequence}<button class="secondary" onclick={() => act(() => api.flatsCancel())}>Cancel</button>{/if}
      </div>
    {/if}
    {#if ask && f.fromSequence && f.ask === 'PanelOn'}
      <p class="muted small">The sequence waits for you: between targets it carries on without flats after 20 minutes.</p>
    {/if}
  </div>
{/if}

<style>
  .flats {
    position: fixed;
    left: 50%;
    bottom: 24px;
    transform: translateX(-50%);
    z-index: 46;
    width: min(480px, calc(100vw - 32px));
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 12px 14px;
    background: var(--panel);
    border: 1px solid var(--accent);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
  }
  header {
    display: flex;
    align-items: baseline;
    gap: 8px;
  }
  .spacer {
    flex: 1;
  }
  .close {
    background: none;
    border: none;
    color: var(--muted);
    padding: 0 2px;
    cursor: pointer;
  }
  p {
    margin: 0;
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .big {
    font-size: 16px;
    font-weight: 600;
    color: var(--accent);
  }
  .bad {
    color: var(--danger);
  }
  .small {
    font-size: 12px;
  }
  .numbers {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
  }
  .bars {
    display: grid;
    gap: 4px;
  }
  .bar {
    display: grid;
    grid-template-columns: 110px 1fr;
    align-items: center;
    gap: 8px;
    font-size: 12px;
  }
  progress {
    width: 100%;
    height: 8px;
  }
  .buttons {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }
  .spinner {
    width: 12px;
    height: 12px;
    border: 2px solid var(--panel-border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.9s linear infinite;
    flex: none;
  }
  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }
</style>
