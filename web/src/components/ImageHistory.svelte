<script lang="ts">
  // The last few images down the right of the viewer: click one to look at it, pin one to keep it, or pick two to
  // compare side by side or blinking.
  import { api } from '../lib/api'
  import { act } from '../lib/state.svelte'
  import type { CompareMode, HistoryImage } from '../lib/types'

  let {
    images,
    size,
    latestId,
    viewing,
    mode,
    pair,
    onview,
    onmode,
    onpair,
    oncollapse,
  }: {
    /** Newest first. */
    images: HistoryImage[]
    /** How many are kept. */
    size: number
    latestId: number | null
    /** The image on screen (single mode), or null when following the latest. */
    viewing: number | null
    mode: CompareMode
    /** The two being compared (A, B). */
    pair: [number | null, number | null]
    onview: (id: number | null) => void
    onmode: (mode: CompareMode) => void
    onpair: (pair: [number | null, number | null]) => void
    /** Hide the column, to give the image the room. */
    oncollapse: () => void
  } = $props()

  const time = (utc: string) => new Date(utc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  const shownId = $derived(viewing ?? latestId)
  // A newer frame arrived while looking at an older one.
  const behind = $derived(viewing != null && latestId != null && viewing !== latestId)

  function click(id: number) {
    if (mode === 'single') onview(id === latestId ? null : id)
    else if (id !== pair[0]) onpair([pair[0], id])
  }
</script>

<aside class="history" aria-label="Image history">
  <div class="head">
    <span class="title">History</span>
    <select
      value={size}
      onchange={(e) => act(() => api.historySize(Number(e.currentTarget.value)))}
      title="How many images to keep (autofocus and polar alignment frames aren't kept)"
      aria-label="Images kept"
    >
      {#each Array.from({ length: 18 }, (_, i) => i + 3) as n (n)}<option value={n}>{n}</option>{/each}
    </select>
    <button class="collapse" onclick={oncollapse} title="Hide the history (the image gets the room)" aria-label="Hide the history">⟩</button>
  </div>
  <div class="modes" role="group" aria-label="Compare">
    <button class:on={mode === 'single'} onclick={() => onmode('single')} title="One image at a time">One</button>
    <button class:on={mode === 'side'} onclick={() => onmode('side')} disabled={images.length < 2} title="Two images side by side, zoomed and moved together"
      >Side</button
    >
    <button class:on={mode === 'blink'} onclick={() => onmode('blink')} disabled={images.length < 2} title="Switch between two images in the same place"
      >Blink</button
    >
  </div>
  {#if mode !== 'single'}
    <p class="hint muted">Click an image to compare it (B); <b>A</b> sets the other.</p>
  {/if}

  <div class="list">
    {#each images as h (h.info.id)}
      {@const id = h.info.id}
      {@const s = h.info.stars}
      <div
        class="card"
        class:shown={mode === 'single' && id === shownId}
        class:a={mode !== 'single' && id === pair[0]}
        class:b={mode !== 'single' && id === pair[1]}
      >
        <button class="pick" onclick={() => click(id)} title={mode === 'single' ? 'Look at this image' : 'Compare this image (B)'}>
          <img class="frame" src={api.thumbnailUrl(h.info)} alt="Image {id}" loading="lazy" />
          <span class="meta mono">
            <span class="line"><span>{time(h.info.capturedUtc)}</span><span>{h.info.durationSeconds} s</span></span>
            <span class="line">{#if s.medianHfr != null}<span>HFR {s.medianHfr.toFixed(2)}</span><span>{s.count} stars</span>{:else}<span>no stars</span>{/if}</span>
            {#if h.solve?.solved}<span class="ok" title="Plate solved">✓</span>{/if}
          </span>
        </button>
        <div class="badges">
          {#if id === latestId}<span class="badge" class:new={behind}>{behind ? 'new' : 'latest'}</span>{/if}
          {#if h.info.openedFile}<span class="badge" title={h.info.openedFile}>file</span>{/if}
          {#if mode !== 'single'}
            {#if id === pair[0]}<span class="badge pair-a">A</span>{:else if id === pair[1]}<span class="badge pair-b">B</span>{:else}
              <button class="tiny" onclick={() => onpair([id, pair[1] === id ? pair[0] : pair[1]])} title="Compare from this image (A)">A</button>
            {/if}
          {/if}
          <span class="spacer"></span>
          <button
            class="tiny pin"
            class:on={h.pinned}
            aria-pressed={h.pinned}
            title={h.pinned ? 'Pinned: newer images won\'t push it out. Click to unpin' : 'Pin: keep it however many newer images arrive'}
            onclick={() => act(() => api.pinImage(id, !h.pinned))}><span class="emoji">📌</span></button
          >
        </div>
      </div>
    {:else}
      <p class="muted hint">Images appear here as they're taken or opened.</p>
    {/each}
  </div>
</aside>

<style>
  .history {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-height: 0;
    font-size: 12px;
  }
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
  }
  .title {
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--muted);
  }
  .collapse {
    background: none;
    border: none;
    color: var(--muted);
    padding: 0 4px;
    font-size: 14px;
    cursor: pointer;
  }
  .head select {
    padding: 1px 4px;
    font-size: 12px;
  }
  .modes {
    display: flex;
  }
  .modes button {
    flex: 1;
    padding: 2px 4px;
    font-size: 12px;
    border-radius: 0;
    background: var(--input);
    color: var(--muted);
    border: 1px solid var(--panel-border);
  }
  .modes button:first-child {
    border-radius: var(--radius) 0 0 var(--radius);
  }
  .modes button:last-child {
    border-radius: 0 var(--radius) var(--radius) 0;
  }
  .modes button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  .hint {
    margin: 0;
    font-size: 11px;
  }
  .list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    overflow-y: auto;
    min-height: 0;
    flex: 1;
  }
  .card {
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    padding: 4px;
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .card.shown {
    border-color: var(--accent);
  }
  .card.a {
    border-color: var(--ok);
  }
  .card.b {
    border-color: var(--warn);
  }
  .pick {
    all: unset;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .pick img {
    width: 100%;
    aspect-ratio: 4 / 3;
    object-fit: contain;
    background: #000;
    border-radius: 4px;
    display: block;
  }
  .meta {
    display: flex;
    flex-direction: column;
    font-size: 11px;
    line-height: 1.35;
  }
  .meta .line {
    display: flex;
    gap: 10px;
  }
  .ok {
    color: var(--ok);
  }
  .badges {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  .spacer {
    flex: 1;
  }
  .badge {
    font-size: 10px;
    padding: 0 5px;
    border-radius: 999px;
    border: 1px solid var(--panel-border);
    color: var(--muted);
  }
  .badge.new {
    color: var(--accent);
    border-color: var(--accent);
  }
  .badge.pair-a {
    color: var(--ok);
    border-color: var(--ok);
  }
  .badge.pair-b {
    color: var(--warn);
    border-color: var(--warn);
  }
  .tiny {
    padding: 0 5px;
    font-size: 10px;
    line-height: 16px;
    background: none;
    border: 1px solid var(--panel-border);
    color: var(--muted);
    border-radius: 4px;
  }
  .pin {
    filter: grayscale(1);
    opacity: 0.5;
  }
  .pin.on {
    filter: none;
    opacity: 1;
  }
</style>
