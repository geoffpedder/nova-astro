<script lang="ts">
  import { api } from '../../lib/api'
  import { setStackView } from '../../lib/floats.svelte'
  import { liveStack, span } from '../../lib/liveStack.svelte'
  import { previewSrc } from '../../lib/preview.svelte'
  import { act, nova } from '../../lib/state.svelte'
  import GuideChart from '../GuideChart.svelte'
  import ZoomableImage from '../ZoomableImage.svelte'
  import Icon from './Icon.svelte'

  // The compact layout's image: the latest frame (or the live stack) edge to edge, with small floating boxes over it.
  // The boxes sit inside the area the bars leave free (the --in-* insets set by CompactApp).
  let { ontap, onguide }: { ontap: () => void; onguide: () => void } = $props()

  const image = $derived(nova.image)
  const stack = $derived(liveStack.current)
  const showingStack = $derived(liveStack.showing)
  const src = $derived(showingStack && stack ? api.liveStackUrl(stack, 2400) : image ? previewSrc(image) : null)

  let zoomed = $state(false)
  let magnification = $state(1)
  let loading = $state(false)
  $effect(() => {
    if (src) loading = true
  })

  // What's on screen: the sequence's target while one runs, else the opened file's name.
  const target = $derived(nova.status?.sequencer?.status?.target ?? image?.openedFile ?? null)
  const wheel = $derived(nova.status?.filterWheel)
  const filter = $derived(wheel?.connected ? (wheel.names[wheel.position - 1] ?? null) : null)
  const stars = $derived(image?.stars)

  const solver = $derived(nova.status?.plateSolve)
  const solved = $derived(solver?.last?.solved && solver.last.imageId === image?.id ? solver.last : null)

  const guider = $derived(nova.status?.guider)
  const guiding = $derived(guider?.state === 'Guiding' || guider?.settling)
  const unit = $derived(guider?.stats?.units === 'arcsec' ? '″' : ' px')
  const fmt = (v: number | null | undefined) => (v == null ? '—' : v.toFixed(2))
</script>

<div class="stage-image">
  {#if src}
    <ZoomableImage
      {src}
      alt={showingStack ? 'The live stack' : 'The latest frame'}
      bind:zoomed
      bind:magnification
      {ontap}
      onloaded={() => (loading = false)}
      onfailed={() => (loading = false)}
    />
  {:else}
    <button class="empty" onclick={ontap}>No image yet: take an exposure with the shutter.</button>
  {/if}
</div>

<div class="huds">
  {#if image || showingStack}
    <!-- Zoomed in, the navigator takes its place. -->
    <div class="hud info" class:hidden={zoomed}>
      {#if showingStack && stack}
        <div class="title">{stack.label}</div>
        <div class="facts mono">
          <span><span class="k">Frames</span>{stack.frames}</span>
          <span><span class="k">Total</span>{span(stack.integrationSeconds)}</span>
          {#if stack.rejected}<span class="warn" title={stack.lastRejected ?? ''}><span class="k">Left out</span>{stack.rejected}</span>{/if}
        </div>
      {:else if image}
        {#if target}<div class="title">{target}</div>{/if}
        <div class="facts mono">
          <span><span class="k">Exp</span>{image.durationSeconds} s</span>
          {#if filter}<span><span class="k">Filter</span>{filter}</span>{/if}
          {#if stars?.medianHfr != null}<span><span class="k">HFR</span>{stars.medianHfr.toFixed(2)}</span>{/if}
          <span><span class="k">Stars</span>{stars?.count ?? 0}</span>
        </div>
        {#if solved}<div class="solved mono">{solved.raText} {solved.decText}, {solved.rotation?.toFixed(1)}°</div>{/if}
      {/if}
    </div>
  {/if}

  <div class="hud tools">
    <button class:on={zoomed} disabled={!src} onclick={() => (zoomed = !zoomed)} title={zoomed ? 'Fit the whole image' : 'One image pixel per screen pixel'} aria-label="Zoom">
      <Icon name={zoomed ? 'shrink' : 'expand'} size={20} />
    </button>
    {#if stack && stack.frames > 0}
      <button class:on={showingStack} onclick={() => setStackView(!showingStack)} title={showingStack ? 'Show the latest frame' : `Show the live stack: ${stack.label}`} aria-label="Frame or stack">
        <Icon name="stack" size={20} />
      </button>
    {/if}
    <button
      disabled={!image || showingStack || !solver?.available || solver?.solving}
      onclick={() => act(() => api.solve(image?.id))}
      title={solver?.available ? `Plate solve this frame with ${solver.name}` : (solver?.problem ?? 'No plate solver')}
      aria-label="Plate solve"
    >
      {#if solver?.solving}<span class="spin"></span>{:else}<Icon name="solve" size={20} />{/if}
    </button>
  </div>

  {#if showingStack && stack}
    <div class="hud stack-actions">
      <button disabled={liveStack.saving} onclick={() => liveStack.save()}>{liveStack.saving ? 'Saving…' : 'Save stack'}</button>
      <button onclick={() => liveStack.reset()}>Reset</button>
    </div>
  {/if}

  {#if guider && (guiding || guider.state === 'LostLock')}
    <button class="hud guide" onclick={onguide} title="Guiding">
      <span class="head">
        <span>{guider.state === 'LostLock' ? 'Star lost' : guider.settling ? 'Settling' : 'Guiding'}</span>
        <span class="mono">Total <b>{fmt(guider.stats?.rmsTotal)}{unit}</b></span>
      </span>
      <span class="chart"><GuideChart history={guider.history ?? []} {unit} steps={120} fill /></span>
    </button>
  {/if}

  {#if loading && src}<div class="loading" role="status">Loading…</div>{/if}
</div>

<style>
  .stage-image {
    position: absolute;
    inset: 0;
    display: flex;
    background: #000;
    /* The zoom navigator, inside the area the bars leave free (where the info box is). */
    --nav-left: calc(var(--in-l) + 12px);
    --nav-top: calc(var(--in-t) + 12px);
  }
  .info.hidden {
    display: none;
  }
  .stage-image :global(> *) {
    flex: 1;
  }
  .empty {
    all: unset;
    flex: 1;
    display: grid;
    place-items: center;
    color: var(--muted);
    font-size: 14px;
    padding: 24px;
    text-align: center;
  }
  .huds {
    position: absolute;
    top: var(--in-t);
    left: var(--in-l);
    right: var(--in-r);
    bottom: var(--in-b);
    pointer-events: none;
    transition:
      top 0.25s ease,
      left 0.25s ease,
      right 0.25s ease,
      bottom 0.25s ease,
      opacity 0.25s ease;
  }
  .huds > :global(*) {
    pointer-events: auto;
  }
  .hud {
    position: absolute;
    background: var(--glass);
    backdrop-filter: blur(10px);
    -webkit-backdrop-filter: blur(10px);
    border: 1px solid var(--glass-line);
    border-radius: 8px;
    font-size: 12px;
  }
  .info {
    left: 12px;
    top: 12px;
    max-width: calc(100% - 90px);
    padding: 8px 12px;
    display: grid;
    gap: 2px;
  }
  .title {
    font-size: 15px;
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .facts {
    display: flex;
    flex-wrap: wrap;
    gap: 2px 14px;
  }
  .k {
    font-family: var(--font);
    color: var(--muted);
    font-size: 11px;
    margin-right: 5px;
  }
  .warn {
    color: var(--warn);
  }
  .solved {
    color: var(--ok);
    font-size: 11px;
  }
  .tools {
    right: 12px;
    top: 12px;
    display: flex;
    flex-direction: column;
    padding: 3px;
    gap: 2px;
  }
  .tools button {
    all: unset;
    width: 40px;
    height: 40px;
    display: grid;
    place-items: center;
    border-radius: 6px;
    color: var(--muted);
    cursor: pointer;
  }
  .tools button.on {
    color: var(--text);
    background: rgb(255 255 255 / 0.1);
  }
  .tools button:disabled {
    opacity: 0.35;
    cursor: default;
  }
  .spin {
    width: 16px;
    height: 16px;
    border-radius: 50%;
    border: 2px solid var(--muted);
    border-top-color: var(--accent);
    animation: spin 0.8s linear infinite;
  }
  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }
  .stack-actions {
    left: 50%;
    top: 12px;
    transform: translateX(-50%);
    display: flex;
    gap: 4px;
    padding: 3px;
  }
  .stack-actions button {
    all: unset;
    padding: 6px 12px;
    border-radius: 6px;
    cursor: pointer;
    font-size: 12px;
  }
  .stack-actions button:hover {
    background: rgb(255 255 255 / 0.08);
  }
  .guide {
    all: unset;
    position: absolute;
    left: 12px;
    bottom: 12px;
    width: min(300px, calc(100% - var(--shoot-pill, 0px) - 36px));
    padding: 8px 10px 6px;
    display: grid;
    gap: 4px;
    cursor: pointer;
    background: var(--glass);
    backdrop-filter: blur(10px);
    -webkit-backdrop-filter: blur(10px);
    border: 1px solid var(--glass-line);
    border-radius: 8px;
    font-size: 11px;
    color: var(--muted);
    box-sizing: border-box;
  }
  .guide .head {
    display: flex;
    justify-content: space-between;
  }
  .guide b {
    color: var(--text);
    font-weight: 500;
  }
  .guide .chart {
    height: 48px;
    display: block;
  }
  .loading {
    position: absolute;
    left: 50%;
    top: 50%;
    transform: translate(-50%, -50%);
    padding: 6px 12px;
    border-radius: 999px;
    background: var(--glass);
    font-size: 13px;
    pointer-events: none;
    animation: appear 0.15s ease-out 0.4s both;
  }
  @keyframes appear {
    from {
      opacity: 0;
    }
  }
  @media (max-width: 720px) {
    .tools button {
      width: 36px;
      height: 36px;
    }
  }
</style>
