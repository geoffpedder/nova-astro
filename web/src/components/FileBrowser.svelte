<script lang="ts">
  import { onMount } from 'svelte'
  import { api } from '../lib/api'
  import { mainView } from '../lib/inbox.svelte'
  import type { FileEntry, FolderListing } from '../lib/types'
  import Icon from './Icon.svelte'

  // The image folders on the mini PC (and a mounted USB drive). Two ways to use it, one browser:
  //   open:  Open… → From the mini PC, FITS files only: a file opens in the Image tab (then onopened).
  //   files: the Mini PC tab, every file: open a FITS file, view a picture, download anything.
  let { mode, onopened }: { mode: 'open' | 'files'; onopened?: () => void } = $props()

  const all = $derived(mode === 'files')
  let listing = $state<FolderListing | null>(null)
  let error = $state<string | null>(null)
  let opening = $state<string | null>(null)

  async function browse(root: string, path: string) {
    error = null
    try {
      listing = await api.browseFiles(root, path, all)
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    }
  }
  onMount(() => browse('local', ''))

  const crumbs = $derived(listing?.path ? listing.path.split('/') : [])
  const join = (...parts: string[]) => parts.filter(Boolean).join('/')
  const at = (e: FileEntry) => join(listing!.path, e.name)

  async function open(e: FileEntry) {
    if (!listing) return
    opening = e.name
    error = null
    try {
      await api.openMiniPcFile(listing.root, at(e))
      if (mode === 'files') mainView.view = 'image'
      onopened?.()
    } catch (err) {
      error = err instanceof Error ? err.message : String(err)
    } finally {
      opening = null
    }
  }

  const size = (bytes: number) =>
    bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} GB` : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
  const when = (utc: string) => new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  // What's in this folder, for the line under the list.
  const summary = $derived.by(() => {
    if (!listing) return ''
    const files = listing.entries.filter((e) => !e.folder)
    const folders = listing.entries.length - files.length
    const bytes = files.reduce((n, e) => n + (e.bytes ?? 0), 0)
    const parts = [folders ? `${folders} folder${folders === 1 ? '' : 's'}` : null, files.length ? `${files.length} file${files.length === 1 ? '' : 's'}, ${size(bytes)}` : null]
    return parts.filter(Boolean).join(' and ')
  })
</script>

<div class="browser" class:files={mode === 'files'}>
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
          <span class="glyph muted" aria-hidden="true">↰</span><span class="name muted">Up</span>
        </button>
      {/if}
      {#each listing.entries as e (e.name)}
        {#if e.folder}
          <button class="entry" onclick={() => browse(listing!.root, at(e))}>
            <span class="glyph folder"><Icon name="folder" size={16} /></span><span class="name">{e.name}</span>
            {#if e.modifiedUtc && mode === 'files'}<span class="muted meta mono">{when(e.modifiedUtc)}</span>{/if}
          </button>
        {:else if mode === 'open'}
          <button class="entry file" disabled={opening !== null} onclick={() => open(e)} title="Open in the Image tab">
            <span class="glyph"><Icon name="file" size={16} /></span>
            <span class="name">{e.name}</span>
            <span class="muted meta mono">{opening === e.name ? 'Opening…' : `${e.bytes != null ? size(e.bytes) : ''} ${e.modifiedUtc ? when(e.modifiedUtc) : ''}`}</span>
          </button>
        {:else}
          <div class="entry row">
            <span class="glyph"><Icon name={e.kind === 'image' ? 'image' : 'file'} size={16} /></span>
            <span class="name" title={e.name}>{e.name}</span>
            <span class="muted meta mono">{e.bytes != null ? size(e.bytes) : ''} {e.modifiedUtc ? when(e.modifiedUtc) : ''}</span>
            <span class="actions">
              {#if e.kind === 'fits'}
                <button class="secondary small" disabled={opening !== null} onclick={() => open(e)} title="Open in the Image tab: solve it, measure its stars">
                  {opening === e.name ? 'Opening…' : 'Open'}
                </button>
              {:else if e.kind === 'image'}
                <a class="button small" href={api.fileUrl(listing.root, at(e), true)} target="_blank" rel="noopener" title="Show it in a new browser tab">View</a>
              {/if}
              <a class="button small dl" href={api.fileUrl(listing.root, at(e))} download={e.name} title="Download to this device" aria-label="Download {e.name}">
                <Icon name="download" size={15} />
              </a>
            </span>
          </div>
        {/if}
      {:else}
        <p class="muted empty">{mode === 'open' ? 'No folders or FITS files here.' : 'Nothing here yet.'}</p>
      {/each}
    </div>
    {#if mode === 'files' && summary}<p class="muted summary">{summary}</p>{/if}
  {/if}

  {#if error}<p class="error">{error}</p>{:else if !listing}<p class="muted">Loading…</p>{/if}
</div>

<style>
  .browser {
    display: grid;
    grid-template-rows: auto auto minmax(0, 1fr) auto auto;
    gap: 10px;
    min-height: 0;
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
    align-items: center;
    gap: 8px;
    width: 100%;
    padding: 5px 6px;
    border-radius: 4px;
    font-size: 13px;
    cursor: pointer;
  }
  .entry.row {
    cursor: default;
  }
  .entry:hover,
  .entry:focus-visible {
    background: var(--input);
  }
  .entry[disabled] {
    cursor: wait;
  }
  .glyph {
    width: 16px;
    display: grid;
    place-items: center;
    color: var(--muted);
    flex: none;
  }
  .glyph.folder {
    color: var(--accent);
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
  .actions {
    display: flex;
    gap: 4px;
    flex: none;
  }
  .small {
    font-size: 12px;
    padding: 2px 8px;
  }
  /* Links that look like the page's buttons. */
  a.button {
    display: inline-flex;
    align-items: center;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    background: var(--button);
    color: var(--text);
    text-decoration: none;
  }
  a.button:hover {
    border-color: var(--accent);
  }
  a.dl {
    padding: 2px 6px;
  }
  .summary {
    margin: 0;
    font-size: 12px;
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
  /* A narrow screen: the size and date under the name, so the buttons keep their room. */
  @media (max-width: 600px) {
    .files .row {
      flex-wrap: wrap;
    }
    .files .row .meta {
      order: 3;
      flex-basis: 100%;
      padding-left: 24px;
    }
  }
</style>
