<script lang="ts">
  import { onMount } from 'svelte'
  import { api } from '../lib/api'
  import type { FolderListing } from '../lib/types'

  // Open… → From the mini PC: the image folders on the mini PC (and a mounted USB drive), to open a frame saved there,
  // e.g. from an earlier night not yet copied off. Folders open on click; a file opens in the viewer and closes this.
  let { onclose }: { onclose: () => void } = $props()

  let listing = $state<FolderListing | null>(null)
  let error = $state<string | null>(null)
  let opening = $state<string | null>(null)

  async function browse(root: string, path: string) {
    error = null
    try {
      listing = await api.browseFiles(root, path)
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
  }
  onMount(() => browse('local', ''))

  const crumbs = $derived(listing?.path ? listing.path.split('/') : [])
  const join = (...parts: string[]) => parts.filter(Boolean).join('/')

  async function open(name: string) {
    if (!listing) return
    opening = name
    error = null
    try {
      await api.openMiniPcFile(listing.root, join(listing.path, name))
      onclose()
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      opening = null
    }
  }

  const size = (bytes: number) => (bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`)
  const when = (utc: string) =>
    new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  function onkeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') onclose()
  }
</script>

<svelte:window {onkeydown} />
<div class="backdrop" role="presentation" onclick={() => !opening && onclose()}></div>
<div class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="files-title">
  <div class="head">
    <h3 id="files-title">Open from the mini PC</h3>
    <button class="secondary small" onclick={onclose} aria-label="Close">✕</button>
  </div>

  {#if listing && listing.roots.length > 1}
    <div class="segmented" role="group" aria-label="Where">
      {#each listing.roots as r (r.id)}
        <button class:on={listing.root === r.id} onclick={() => browse(r.id, '')}>{r.label}</button>
      {/each}
    </div>
  {/if}

  {#if listing}
    <nav class="crumbs" aria-label="Folder">
      <button class="link" onclick={() => browse(listing!.root, '')}>{listing.roots.find((r) => r.id === listing!.root)?.label ?? 'Top'}</button>
      {#each crumbs as c, i (i)}
        <span class="muted">/</span>
        <button class="link" onclick={() => browse(listing!.root, crumbs.slice(0, i + 1).join('/'))}>{c}</button>
      {/each}
    </nav>

    <div class="list">
      {#if listing.path}
        <button class="entry" onclick={() => browse(listing!.root, crumbs.slice(0, -1).join('/'))}>
          <span class="icon" aria-hidden="true">↰</span><span class="name muted">Up</span>
        </button>
      {/if}
      {#each listing.entries as e (e.name)}
        {#if e.folder}
          <button class="entry" onclick={() => browse(listing!.root, join(listing!.path, e.name))}>
            <span class="icon" aria-hidden="true">▸</span><span class="name">{e.name}</span>
          </button>
        {:else}
          <button class="entry file" disabled={opening !== null} onclick={() => open(e.name)} title="Open in the Image tab">
            <span class="icon" aria-hidden="true">◦</span>
            <span class="name">{e.name}</span>
            <span class="muted meta mono">{opening === e.name ? 'Opening…' : `${e.bytes != null ? size(e.bytes) : ''} ${e.modifiedUtc ? when(e.modifiedUtc) : ''}`}</span>
          </button>
        {/if}
      {:else}
        <p class="muted empty">No folders or FITS files here.</p>
      {/each}
    </div>
  {/if}

  {#if error}<p class="error">{error}</p>{:else if !listing}<p class="muted">Loading…</p>{/if}
</div>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 40;
  }
  .dialog {
    position: fixed;
    z-index: 41;
    left: 50%;
    top: 10%;
    transform: translateX(-50%);
    width: min(620px, calc(100vw - 32px));
    max-height: 80vh;
    display: grid;
    grid-template-rows: auto auto auto minmax(0, 1fr) auto;
    gap: 10px;
    padding: 16px;
  }
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  h3 {
    margin: 0;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
  }
  .segmented {
    display: flex;
  }
  .segmented button {
    flex: 1;
    border-radius: 0;
    color: var(--muted);
    background: transparent;
    border: 1px solid var(--panel-border);
    padding: 4px 8px;
    font-size: 12px;
  }
  .segmented button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }
  .segmented button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  .segmented button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .crumbs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    align-items: baseline;
    font-size: 13px;
  }
  .link {
    all: unset;
    cursor: pointer;
    color: var(--accent);
  }
  .link:hover {
    text-decoration: underline;
  }
  .list {
    overflow: auto;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 4px;
    min-height: 120px;
  }
  .entry {
    all: unset;
    box-sizing: border-box;
    display: flex;
    align-items: baseline;
    gap: 8px;
    width: 100%;
    padding: 5px 6px;
    border-radius: 4px;
    font-size: 13px;
    cursor: pointer;
  }
  .entry:hover,
  .entry:focus-visible {
    background: var(--input);
  }
  .entry[disabled] {
    cursor: wait;
  }
  .icon {
    width: 12px;
    color: var(--muted);
  }
  .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .meta {
    font-size: 11px;
    white-space: nowrap;
  }
  .empty {
    margin: 8px;
    font-size: 13px;
  }
  .error {
    margin: 0;
    color: var(--danger);
    font-size: 13px;
  }
</style>
