<script lang="ts">
  import { api } from '../../lib/api'
  import { abortExposure, expose, exposure } from '../../lib/exposure.svelte'
  import { mainView } from '../../lib/inbox.svelte'
  import { liveStack } from '../../lib/liveStack.svelte'
  import { portal } from '../../lib/portal'
  import { nova } from '../../lib/state.svelte'
  import Icon from './Icon.svelte'

  // The compact layout's shutter and what goes with it: the mode, the exposure, Loop and Save (the same settings as the
  // Camera panel's, lib/exposure), and the last few frames.
  let {
    minimal,
    ontoggle,
    onopen,
  }: {
    /** Only the shutter (folded away, or everything hidden). */
    minimal: boolean
    ontoggle: () => void
    /** Open one of the layout's panels (camera, filterwheel). */
    onopen: (panel: string) => void
  } = $props()

  const camera = $derived(nova.status?.camera)
  const ready = $derived(camera?.connected ?? false)
  const busy = $derived(!!nova.exposure)
  const e = $derived(nova.exposure)
  const fraction = $derived(e ? (e.state === 'Downloading' || e.totalSeconds <= 0 ? 1 : Math.min(1, e.elapsedSeconds / e.totalSeconds)) : 0)
  const Circumference = 2 * Math.PI * 39
  const wheel = $derived(nova.status?.filterWheel)
  const filter = $derived(wheel?.connected ? (wheel.names[wheel.position - 1] ?? `${wheel.position}`) : null)
  const gain = $derived(camera?.controls?.gain?.value ?? null)
  const history = $derived((nova.status?.imageHistory?.images ?? []).slice(0, 3))

  // The mode: just frames, frames stacked as they come, or the night's plan. Live stacking is the only one that
  // changes how frames are taken; Autorun opens the plan.
  type Mode = 'preview' | 'live' | 'autorun'
  const mode = $derived<Mode>(liveStack.status?.enabled ? 'live' : nova.status?.sequencer?.running ? 'autorun' : 'preview')
  const modes: { value: Mode; label: string; note: string }[] = [
    { value: 'preview', label: 'Preview', note: 'Single frames, or a loop' },
    { value: 'live', label: 'Live stack', note: 'Frames stacked as they come' },
    { value: 'autorun', label: 'Autorun', note: "Run the night's plan" },
  ]
  let menu = $state<'mode' | 'exposure' | null>(null)
  let menuAt = $state({ left: 0, top: 0 })
  function openMenu(which: 'mode' | 'exposure', event: MouseEvent) {
    if (menu === which) return (menu = null)
    menu = which
    const anchor = (event.currentTarget as HTMLElement).closest('.shoot') as HTMLElement
    const r = anchor.getBoundingClientRect()
    const phone = innerWidth <= 720
    // On a phone it opens above the pill; on a tablet, to the left of the column.
    menuAt = phone ? { left: Math.max(8, r.right - 260), top: r.top - 8 } : { left: r.left - 268, top: (event.currentTarget as HTMLElement).getBoundingClientRect().top }
  }
  async function chooseMode(next: Mode) {
    menu = null
    if (next === 'autorun') {
      mainView.view = 'sequence'
      return
    }
    if ((next === 'live') !== !!liveStack.status?.enabled) await liveStack.setEnabled(next === 'live')
  }

  const presets = [1, 2, 5, 10, 30, 60, 120, 180, 300]
  function closeOnOutside(event: MouseEvent) {
    if (menu && !(event.target as Element).closest('.shoot-menu, .menu-anchor')) menu = null
  }
</script>

<svelte:window onclick={closeOnOutside} />

<aside class="shoot" class:minimal aria-label="Exposure">
  <button class="toggle" onclick={ontoggle} title={minimal ? 'More exposure controls' : 'Just the shutter'} aria-label={minimal ? 'More exposure controls' : 'Fewer exposure controls'}>
    <Icon name={minimal ? 'left' : 'right'} size={18} />
  </button>
  <button class="param mode menu-anchor" onclick={(ev) => openMenu('mode', ev)} aria-haspopup="menu">
    <span class="k">Mode</span><span class="v">{modes.find((m) => m.value === mode)?.label}</span>
  </button>
  <button class="param exp menu-anchor" disabled={busy} onclick={(ev) => openMenu('exposure', ev)} aria-haspopup="menu">
    <span class="k">Exposure</span><span class="v mono">{exposure.duration} s</span>
  </button>
  <button class="param extra" onclick={() => onopen('camera')}><span class="k">Gain</span><span class="v mono">{gain ?? '—'}</span></button>
  {#if wheel}
    <button class="param extra" onclick={() => onopen('filterwheel')}><span class="k">Filter</span><span class="v mono">{filter ?? '—'}</span></button>
  {/if}

  <button
    class="shutter"
    class:busy
    disabled={!ready && !busy}
    onclick={() => (busy ? abortExposure() : expose())}
    aria-label={busy ? 'Stop the exposure' : 'Expose'}
    title={!ready ? 'Connect a camera first' : busy ? 'Stop the exposure (and Loop)' : `Take a ${exposure.duration} s frame`}
  >
    <svg class="ring" viewBox="0 0 84 84" aria-hidden="true">
      <circle class="bg" cx="42" cy="42" r="39" />
      <circle class="fg" cx="42" cy="42" r="39" stroke-dasharray={Circumference} stroke-dashoffset={Circumference * (1 - fraction)} />
    </svg>
    <span class="core"></span>
  </button>
  <div class="time mono">
    {#if e}{e.state === 'Downloading' ? 'Downloading' : `${e.elapsedSeconds.toFixed(0)} / ${e.totalSeconds} s`}{/if}
  </div>
  <div class="toggles extra">
    <button class:on={exposure.loop} aria-pressed={exposure.loop} onclick={() => (exposure.loop = !exposure.loop)} title="Start the next frame as soon as one arrives">Loop</button>
    <button class:on={exposure.save} aria-pressed={exposure.save} onclick={() => (exposure.save = !exposure.save)} title="Save each frame as FITS">Save</button>
  </div>
  <div class="spacer extra"></div>
  <div class="thumbs extra">
    {#each history as h (h.info.id)}
      <img src={api.thumbnailUrl(h.info)} alt="Frame from {new Date(h.info.capturedUtc).toLocaleTimeString()}" />
    {/each}
  </div>
</aside>

{#if menu === 'mode'}
  <div use:portal class="shoot-menu" role="menu" style:left="{menuAt.left}px" style:top="{menuAt.top}px" class:above={innerWidth <= 720}>
    {#each modes as m (m.value)}
      <button role="menuitemradio" aria-checked={mode === m.value} class:on={mode === m.value} onclick={() => chooseMode(m.value)}>
        <b>{m.label}</b><small>{m.note}</small>
      </button>
    {/each}
  </div>
{:else if menu === 'exposure'}
  <div use:portal class="shoot-menu exposure" role="menu" style:left="{menuAt.left}px" style:top="{menuAt.top}px" class:above={innerWidth <= 720}>
    <div class="presets">
      {#each presets as p (p)}
        <button class:on={exposure.duration === p} onclick={() => ((exposure.duration = p), (menu = null))}>{p} s</button>
      {/each}
    </div>
    <label class="custom">Seconds <input type="number" min="0" max="3600" step="0.5" bind:value={exposure.duration} /></label>
  </div>
{/if}

<style>
  .shoot {
    grid-area: shoot;
    position: relative;
    z-index: 2;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 8px;
    padding: 8px 8px calc(12px + var(--safe-b) + var(--corner));
    background: var(--glass);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border-left: 1px solid var(--glass-line);
    overflow: hidden auto;
    transition: background 0.25s;
  }
  .toggle {
    all: unset;
    align-self: flex-end;
    width: 28px;
    height: 24px;
    display: grid;
    place-items: center;
    border-radius: 6px;
    color: var(--muted);
    cursor: pointer;
    flex: none;
  }
  .param {
    all: unset;
    box-sizing: border-box;
    width: 100%;
    padding: 6px 4px;
    border-radius: 6px;
    background: rgb(0 0 0 / 0.3);
    text-align: center;
    cursor: pointer;
    flex: none;
  }
  .param:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .param .k {
    display: block;
    font-size: 10px;
    color: var(--muted);
    letter-spacing: 0.06em;
    text-transform: uppercase;
  }
  .param .v {
    font-size: 14px;
    font-weight: 500;
  }
  .shutter {
    all: unset;
    position: relative;
    width: 80px;
    height: 80px;
    margin: 4px 0 0;
    border-radius: 50%;
    display: grid;
    place-items: center;
    cursor: pointer;
    flex: none;
  }
  .shutter:disabled {
    opacity: 0.4;
    cursor: default;
  }
  .ring {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    transform: rotate(-90deg);
  }
  .ring circle {
    fill: none;
    stroke-width: 4;
  }
  .ring .bg {
    stroke: rgb(255 255 255 / 0.12);
  }
  .ring .fg {
    stroke: var(--accent);
    stroke-linecap: round;
    transition: stroke-dashoffset 1s linear;
  }
  .core {
    width: 60px;
    height: 60px;
    border-radius: 50%;
    background: var(--text);
    transition:
      border-radius 0.2s,
      width 0.2s,
      height 0.2s,
      background 0.2s;
  }
  .shutter.busy .core {
    width: 28px;
    height: 28px;
    border-radius: 5px;
    background: var(--danger);
  }
  .time {
    font-size: 12px;
    min-height: 1.4em;
    text-align: center;
  }
  .toggles {
    display: flex;
    gap: 4px;
    width: 100%;
  }
  .toggles button {
    all: unset;
    flex: 1;
    padding: 6px 0;
    border-radius: 6px;
    background: rgb(0 0 0 / 0.3);
    font-size: 11px;
    color: var(--muted);
    text-align: center;
    cursor: pointer;
  }
  .toggles button.on {
    color: var(--accent-text);
    background: var(--accent);
  }
  .spacer {
    flex: 1;
  }
  .thumbs {
    display: grid;
    gap: 6px;
    width: 100%;
  }
  .thumbs img {
    width: 100%;
    aspect-ratio: 3 / 2;
    object-fit: cover;
    border-radius: 4px;
    display: block;
    background: #000;
  }
  :global(:root[data-theme='night']) .thumbs img {
    filter: grayscale(1) sepia(1) saturate(6) hue-rotate(-50deg) brightness(0.55);
  }

  /* Folded away: just the shutter (and how far through it is), floating over the image. */
  .shoot.minimal {
    background: transparent;
    backdrop-filter: none;
    -webkit-backdrop-filter: none;
    border-color: transparent;
    justify-content: center;
  }
  .shoot.minimal > :not(.shutter):not(.time):not(.toggle) {
    display: none;
  }
  .shoot.minimal .toggle {
    position: absolute;
    top: 8px;
    right: 8px;
    background: var(--glass);
    color: var(--text);
    border: 1px solid var(--glass-line);
  }

  .shoot-menu {
    position: fixed;
    z-index: 30;
    width: 260px;
    padding: 6px;
    display: grid;
    gap: 2px;
    border-radius: 10px;
    background: var(--panel);
    border: 1px solid var(--cut-line);
    box-shadow: 0 12px 32px rgb(0 0 0 / 0.5);
  }
  .shoot-menu.above {
    transform: translateY(-100%);
  }
  .shoot-menu > button {
    all: unset;
    display: grid;
    gap: 1px;
    padding: 8px 10px;
    border-radius: 6px;
    cursor: pointer;
  }
  .shoot-menu > button:hover,
  .shoot-menu > button.on {
    background: var(--button);
  }
  .shoot-menu > button.on {
    box-shadow: inset 3px 0 0 var(--accent);
  }
  .shoot-menu small {
    color: var(--muted);
    font-size: 11px;
  }
  .presets {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 4px;
  }
  .presets button {
    all: unset;
    padding: 8px 0;
    text-align: center;
    border-radius: 6px;
    background: var(--input);
    font: 13px var(--font-mono);
    cursor: pointer;
    border: 1px solid transparent;
  }
  .presets button.on {
    border-color: var(--accent);
  }
  .custom {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 6px 4px 2px;
    font-size: 12px;
    color: var(--muted);
  }
  .custom input {
    width: 90px;
  }

  /* A phone: a pill floating over the image, mode and exposure stacked beside the shutter. */
  @media (max-width: 720px) {
    .shoot {
      position: absolute;
      grid-area: stage;
      align-self: end;
      justify-self: end;
      margin: 0 10px 10px 0;
      padding: 6px;
      display: grid;
      grid-template-columns: auto auto auto;
      grid-template-areas: 'toggle mode shutter' 'toggle exp shutter';
      column-gap: 6px;
      row-gap: 2px;
      align-items: center;
      border: 1px solid var(--glass-line);
      border-radius: 40px;
      overflow: visible;
    }
    .extra,
    .time {
      display: none;
    }
    .toggle {
      grid-area: toggle;
      align-self: center;
      width: 22px;
    }
    .mode {
      grid-area: mode;
    }
    .exp {
      grid-area: exp;
    }
    .shutter {
      grid-area: shutter;
      width: 64px;
      height: 64px;
      margin: 0;
    }
    .core {
      width: 48px;
      height: 48px;
    }
    .shutter.busy .core {
      width: 22px;
      height: 22px;
    }
    .param {
      width: auto;
      min-width: 92px;
      padding: 3px 10px;
      border-radius: 999px;
      display: flex;
      gap: 6px;
      align-items: baseline;
      justify-content: space-between;
    }
    .param .k {
      display: inline;
      font-size: 9px;
    }
    .param .v {
      font-size: 13px;
    }
    .param.extra {
      display: none;
    }
    .shoot.minimal {
      grid-template-areas: 'toggle shutter';
      grid-template-columns: auto auto;
      background: var(--glass);
      backdrop-filter: blur(12px);
      -webkit-backdrop-filter: blur(12px);
      border-color: var(--glass-line);
    }
    .shoot.minimal .toggle {
      position: static;
      background: none;
      border: none;
    }
  }
  /* A phone held sideways: only the essentials down the side. */
  @media (min-width: 721px) and (max-height: 500px) {
    .extra {
      display: none;
    }
    .shutter {
      width: 66px;
      height: 66px;
    }
    .core {
      width: 50px;
      height: 50px;
    }
  }
</style>
