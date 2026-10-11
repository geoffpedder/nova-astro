<script lang="ts">
  import { api } from '../lib/api'
  import type { FitsHeaderCard } from '../lib/types'

  let { imageId, onclose }: { imageId: number; onclose: () => void } = $props()

  let cards = $state<FitsHeaderCard[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let filter = $state('')

  // Reload when a new frame arrives while the header is open.
  $effect(() => {
    void imageId
    let cancelled = false
    loading = true
    api
      .fitsHeader(imageId)
      .then((result) => {
        if (cancelled) return
        cards = result
        error = null
      })
      .catch((e) => {
        if (!cancelled) error = e instanceof Error ? e.message : String(e)
      })
      .finally(() => {
        if (!cancelled) loading = false
      })
    return () => {
      cancelled = true
    }
  })

  const shown = $derived.by(() => {
    const q = filter.trim().toLowerCase()
    if (!q) return cards
    return cards.filter((c) => [c.key, c.value, c.comment ?? ''].some((s) => s.toLowerCase().includes(q)))
  })

  function onkeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') onclose()
  }
</script>

<svelte:window {onkeydown} />

<div class="backdrop" role="presentation" onclick={onclose}></div>
<div class="dialog panel" role="dialog" aria-modal="true" aria-label="FITS header">
  <header>
    <div>
      <div class="muted small">FITS header of image #{imageId}</div>
      <div class="title">{cards.length} cards</div>
    </div>
    <button class="secondary" onclick={onclose} aria-label="Close">✕</button>
  </header>

  <!-- svelte-ignore a11y_autofocus -->
  <input class="search" type="search" placeholder="Filter, e.g. FILTER, TEMP, RA" bind:value={filter} autofocus />

  {#if error}
    <p class="error">Couldn't load the header: {error}</p>
  {:else if loading && cards.length === 0}
    <p class="muted">Loading…</p>
  {:else}
    <div class="table-wrap">
      <table class="mono">
        <thead>
          <tr><th>Keyword</th><th>Value</th><th>Comment</th></tr>
        </thead>
        <tbody>
          {#each shown as card, i (i)}
            <tr><td class="key">{card.key}</td><td>{card.value}</td><td class="muted">{card.comment ?? ''}</td></tr>
          {:else}
            <tr><td colspan="3" class="muted">Nothing matches “{filter}”.</td></tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 10;
  }
  .dialog {
    position: fixed;
    z-index: 11;
    inset: 5vh max(16px, calc(50vw - 380px)) auto;
    max-height: 90vh;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  header {
    display: flex;
    justify-content: space-between;
    align-items: start;
  }
  .title {
    font-weight: 600;
  }
  .small {
    font-size: 12px;
  }
  .search {
    width: 100%;
  }
  .error {
    color: var(--danger);
  }
  .table-wrap {
    overflow: auto;
    min-height: 0;
  }
  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 12px;
  }
  th {
    position: sticky;
    top: 0;
    background: var(--panel);
    text-align: left;
    color: var(--muted);
    font-weight: 600;
  }
  th,
  td {
    padding: 3px 8px;
    border-bottom: 1px solid var(--panel-border);
    vertical-align: top;
    overflow-wrap: anywhere;
  }
  .key {
    color: var(--accent);
    white-space: nowrap;
  }
</style>
