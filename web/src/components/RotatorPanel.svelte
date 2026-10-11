<script lang="ts">
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import DevicePicker from './DevicePicker.svelte'
  import Panel from './Panel.svelte'

  const rotator = $derived(nova.status?.rotator)
  const ready = $derived(rotator?.connected ?? false)
  const moving = $derived(rotator?.isMoving ?? false)

  let target = $state<number | null>(null)

  const nudge = (delta: number) => rotator && act(() => api.rotatorMove((((rotator.angle + delta) % 360) + 360) % 360))
</script>

<Panel id="rotator" title="Rotator" kind="rotator">
  {#snippet badge()}
    {#if rotator}
      <span class="badge {moving ? 'warn' : ready ? 'ok' : 'muted'}">
        {moving ? 'Moving' : ready ? 'Ready' : 'Not connected'}
      </span>
    {/if}
  {/snippet}

  <DevicePicker kind="Rotator" disabled={moving} />

  {#if !rotator}
    <p class="muted">No rotator found.</p>
  {:else}
    <div class="readout mono">
      <span class="angle">{rotator.angle.toFixed(2)}°</span>
      <svg viewBox="-12 -12 24 24" class="dial" aria-hidden="true">
        <circle r="10" />
        <line x1="0" y1="0" x2="0" y2="-9" style:transform="rotate({rotator.angle}deg)" />
      </svg>
    </div>

    <div class="row nudges">
      {#each [-10, -1, 1, 10] as delta (delta)}
        <button class="secondary" disabled={!ready || moving} onclick={() => nudge(delta)}>
          {delta > 0 ? '+' : '−'}{Math.abs(delta)}°
        </button>
      {/each}
    </div>

    <div class="row">
      <input
        class="mono target"
        type="number"
        min="0"
        max="360"
        step="0.1"
        placeholder={rotator.angle.toFixed(1)}
        bind:value={target}
        disabled={!ready}
        aria-label="Target angle (degrees)"
        onkeydown={(e) => e.key === 'Enter' && target !== null && act(() => api.rotatorMove(target!))}
      />
      <button class="secondary" disabled={!ready || moving || target === null} onclick={() => act(() => api.rotatorMove(target!))}>
        Go to
      </button>
      <div class="spacer"></div>
      <button class="danger" disabled={!ready} onclick={() => act(api.rotatorHalt)}>Halt</button>
    </div>
  {/if}
</Panel>

<style>
  .readout {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 8px;
  }
  .angle {
    font-size: 22px;
  }
  .dial {
    width: 34px;
    height: 34px;
  }
  .dial circle {
    fill: none;
    stroke: color-mix(in srgb, var(--kind) 45%, transparent);
    stroke-width: 1.5;
  }
  .dial line {
    stroke: var(--kind);
    stroke-width: 2;
    stroke-linecap: round;
    transition: transform 0.2s linear;
  }
  .nudges {
    margin-bottom: 8px;
  }
  .nudges button {
    flex: 1;
    padding: 6px 0;
  }
  .target {
    width: 90px;
  }
  .spacer {
    flex: 1;
  }
</style>
