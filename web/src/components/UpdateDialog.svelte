<script lang="ts">
  // A newer release's notes and how to update, opened from the top bar's "Update" chip or the Mini PC box. NOVA only
  // tells: the update itself is "nova update" on the mini PC, when it suits (not in the middle of a night).
  import { onMount } from 'svelte'
  import { portal } from '../lib/portal'
  import { dismissUpdate, updates, watchUpdates } from '../lib/updates.svelte'

  onMount(watchUpdates)

  const status = $derived(updates.status)
  const latest = $derived(status?.latest ?? null)

  // The notes are the changelog's section: headings, bullets (with wrapped lines), **bold** and `code`. Drawn as
  // text, never as HTML.
  type Span = { text: string; bold?: boolean; code?: boolean }
  type Block = { kind: 'heading' | 'item' | 'text'; spans: Span[] }
  function spans(text: string): Span[] {
    const out: Span[] = []
    for (const part of text.split(/(\*\*[^*]+\*\*|`[^`]+`)/)) {
      if (!part) continue
      if (part.startsWith('**') && part.endsWith('**') && part.length > 4) out.push({ text: part.slice(2, -2), bold: true })
      else if (part.startsWith('`') && part.endsWith('`') && part.length > 2) out.push({ text: part.slice(1, -1), code: true })
      else out.push({ text: part.replace(/\*([^*\s][^*]*)\*/g, '$1').replace(/\[([^\]]+)\]\([^)]*\)/g, '$1') })
    }
    return out
  }
  const blocks = $derived.by(() => {
    const out: (Block & { raw: string })[] = []
    for (const line of (latest?.notes ?? '').replace(/\r/g, '').split('\n')) {
      const text = line.trim()
      const last = out.at(-1)
      if (!text) {
        if (last) out.push({ kind: 'text', raw: '', spans: [] })
      } else if (/^#{1,6}\s/.test(text)) out.push({ kind: 'heading', raw: text.replace(/^#+\s*/, ''), spans: [] })
      else if (/^[-*]\s/.test(text)) out.push({ kind: 'item', raw: text.slice(2), spans: [] })
      else if (last && last.raw && last.kind !== 'heading') last.raw += ' ' + text // a wrapped line
      else out.push({ kind: 'text', raw: text, spans: [] })
    }
    return out.filter((b) => b.raw).map((b) => ({ kind: b.kind, spans: spans(b.raw) }))
  })

  const published = $derived(
    latest?.publishedUtc ? new Date(latest.publishedUtc).toLocaleDateString([], { day: 'numeric', month: 'short', year: 'numeric' }) : null,
  )
  let copied = $state(false)
  async function copy() {
    try {
      await navigator.clipboard.writeText('nova update')
      copied = true
      setTimeout(() => (copied = false), 2000)
    } catch {
      // no clipboard here (not https): it's short enough to type
    }
  }
  const close = () => (updates.open = false)
</script>

<svelte:window onkeydown={(e) => updates.open && e.key === 'Escape' && close()} />

{#if updates.open && status && latest}
  <div use:portal class="backdrop" role="presentation" onclick={close}></div>
  <div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="update-title">
    <h3 id="update-title">{status.available ? `NOVA ${latest.version} is out` : `NOVA ${latest.version}`}</h3>
    <p class="muted">
      {#if status.devBuild}This NOVA was built from source.{:else}This mini PC has {status.installed}.{/if}
      {#if published}Released {published}.{/if}
    </p>

    <div class="notes">
      {#each blocks as block, i (i)}
        {#if block.kind === 'heading'}
          <h4>{#each block.spans as s, j (j)}{s.text}{/each}</h4>
        {:else}
          <p class:item={block.kind === 'item'}>
            {#each block.spans as s, j (j)}{#if s.bold}<b>{s.text}</b>{:else if s.code}<code>{s.text}</code>{:else}{s.text}{/if}{/each}
          </p>
        {/if}
      {:else}
        <p class="muted">No notes with this release.</p>
      {/each}
    </div>

    {#if status.available}
      <div class="how">
        <span>To update, run this on the mini PC (its terminal is in the Mini PC tab), when nothing is running:</span>
        <span class="command"><code>nova update</code><button class="secondary small" onclick={copy}>{copied ? 'Copied' : 'Copy'}</button></span>
        <span class="muted">It takes a minute or two, and NOVA restarts. Your settings are kept.</span>
      </div>
    {/if}

    <div class="actions">
      <a class="muted" href={latest.url} target="_blank" rel="noopener noreferrer">On GitHub</a>
      <span class="spacer"></span>
      {#if status.available}
        <button class="secondary" onclick={dismissUpdate} title="Hide the top bar's Update button until the next release">Not now</button>
      {/if}
      <button onclick={close}>Close</button>
    </div>
  </div>
{/if}

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
    top: 50%;
    transform: translate(-50%, -50%);
    width: min(620px, calc(100vw - 32px));
    max-height: calc(100vh - 48px);
    max-height: calc(100dvh - 48px);
    display: flex;
    flex-direction: column;
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
  .notes {
    flex: 1;
    min-height: 80px;
    overflow-y: auto;
    display: grid;
    gap: 6px;
    align-content: start;
    padding: 10px 12px;
    background: var(--input);
    border: 1px solid var(--panel-border);
  }
  .notes h4 {
    margin: 6px 0 0;
    font-family: var(--font-mono);
    font-size: 12px;
    font-weight: 500;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--accent);
  }
  .notes h4:first-child {
    margin-top: 0;
  }
  .notes p {
    line-height: 1.45;
  }
  .notes p.item {
    padding-left: 14px;
    position: relative;
  }
  .notes p.item::before {
    content: '';
    position: absolute;
    left: 3px;
    top: 0.62em;
    width: 4px;
    height: 4px;
    background: var(--muted);
  }
  code {
    font-family: var(--font-mono);
    font-size: 12px;
  }
  .how {
    display: grid;
    gap: 6px;
    font-size: 13px;
  }
  .command {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .command code {
    flex: 1;
    padding: 6px 10px;
    font-size: 14px;
    background: var(--input);
    border: 1px solid var(--panel-border);
    user-select: all;
  }
  .small {
    font-size: 12px;
    padding: 4px 10px;
  }
  .actions {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .actions a {
    font-size: 12px;
  }
  .spacer {
    flex: 1;
  }
</style>
