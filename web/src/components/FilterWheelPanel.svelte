<script lang="ts">
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'
  import DevicePicker from './DevicePicker.svelte'
  import Panel from './Panel.svelte'

  const wheel = $derived(nova.status?.filterWheel)
  const ready = $derived(wheel?.connected ?? false)
  const moving = $derived(wheel?.isMoving ?? false)
  const current = $derived(wheel && wheel.position > 0 ? wheel.names[wheel.position - 1] : null)
</script>

<Panel id="filterwheel" title="Filter wheel" kind="filterwheel">
  {#snippet badge()}
    {#if wheel}
      <span class="badge {moving ? 'warn' : ready ? 'ok' : 'muted'}">
        {moving ? 'Moving' : ready ? (current ?? 'Ready') : 'Not connected'}
      </span>
    {/if}
  {/snippet}

  <DevicePicker kind="FilterWheel" disabled={moving} />

  {#if !wheel}
    <p class="muted">No filter wheel found.</p>
  {:else}
    <div class="filters">
      {#each wheel.names as name, i (i)}
        <button
          class="filter"
          class:active={wheel.position === i + 1}
          disabled={!ready || moving}
          onclick={() => act(() => api.filterMove(i + 1))}
          title="{name}, slot {i + 1}"
        >
          {name}
        </button>
      {/each}
    </div>
    <p class="muted hint">Rename filters in the wheel's ⚙ driver settings (Filter tab).</p>
  {/if}
</Panel>

<style>
  /* Each button as wide as its name (Luminance, H_Alpha…), rows filled out evenly. */
  .filters {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .filter {
    flex: 1 1 auto;
    min-width: 56px;
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    background: transparent;
    color: var(--text);
    border: 1px solid var(--panel-border);
    font-weight: 500;
    padding: 6px 8px;
  }
  .filter.active {
    background: var(--kind);
    border-color: var(--kind);
    color: #04140d;
    font-weight: 700;
  }
  .hint {
    margin: 8px 0 0;
    font-size: 12px;
  }
</style>
