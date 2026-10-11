<script module lang="ts">
  /** Equipment kinds with their own colour (see --kind-* in app.css). */
  export type PanelKind = 'mount' | 'camera' | 'focuser' | 'filterwheel' | 'rotator' | 'guider' | 'flatpanel'
</script>

<script lang="ts">
  import { tick, untrack, type Snippet } from 'svelte'
  import { reveal } from '../lib/reveal.svelte'

  let {
    id,
    title,
    kind,
    badge,
    children,
  }: {
    /** Key for remembering collapsed state. */
    id: string
    title: string
    kind?: PanelKind
    /** Shown in the header even when collapsed, e.g. a status badge. */
    badge?: Snippet
    children: Snippet
  } = $props()

  const storageKey = $derived(`nova.panel.${id}.collapsed`)
  // A panel's id never changes, so reading the saved state once is enough.
  let collapsed = $state(untrack(() => load()))

  function load(): boolean {
    try {
      return localStorage.getItem(storageKey) === '1'
    } catch {
      return false
    }
  }

  function toggle() {
    setCollapsed(!collapsed)
  }

  function setCollapsed(value: boolean) {
    collapsed = value
    try {
      localStorage.setItem(storageKey, collapsed ? '1' : '0')
    } catch {
      // storage unavailable; state just won't persist
    }
  }

  // The setup guide pointing here: open, scroll into view and flash.
  let section = $state<HTMLElement>()
  let flash = $state(false)
  $effect(() => {
    if (reveal.panel !== id) return
    reveal.panel = null
    untrack(() => setCollapsed(false))
    tick().then(() => {
      section?.scrollIntoView({ behavior: 'smooth', block: 'start' })
      flash = true
      setTimeout(() => (flash = false), 1600)
    })
  })
</script>

<section bind:this={section} class="panel" class:flash class:tinted={kind} style:--kind={kind ? `var(--kind-${kind})` : undefined} class:collapsed>
  <h2>
    <button class="toggle" aria-expanded={!collapsed} onclick={toggle}>
      <span class="chevron" aria-hidden="true">▾</span>
      {title}
    </button>
    {@render badge?.()}
  </h2>
  {#if !collapsed}
    <div class="body">{@render children()}</div>
  {/if}
</section>

<style>
  .tinted {
    border-left: 3px solid var(--kind);
    background: color-mix(in srgb, var(--panel) 90%, var(--kind) 10%);
  }
  h2 {
    margin: 0;
  }
  .body {
    margin-top: 10px;
  }
  .toggle {
    all: unset;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    flex: 1;
    color: inherit;
  }
  .toggle:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
    border-radius: 4px;
  }
  .tinted .toggle {
    color: color-mix(in srgb, var(--kind) 70%, var(--text) 30%);
  }
  .chevron {
    display: inline-block;
    transition: transform 0.15s;
    font-size: 10px;
  }
  .collapsed .chevron {
    transform: rotate(-90deg);
  }
  .flash {
    animation: flash 1.6s ease-out;
  }
  @keyframes flash {
    from {
      box-shadow: 0 0 0 2px var(--accent);
    }
    to {
      box-shadow: 0 0 0 2px transparent;
    }
  }
</style>
