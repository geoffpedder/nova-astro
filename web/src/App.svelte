<script lang="ts">
  import ConfirmDialog from './components/ConfirmDialog.svelte'
  import { onMount } from 'svelte'
  import { api } from './lib/api'
  import { act, nova, startHub, stopHub } from './lib/state.svelte'
  import type { DeviceKind } from './lib/types'
  import EquipmentPanel from './components/EquipmentPanel.svelte'
  import MountPanel from './components/MountPanel.svelte'
  import PolarView from './components/PolarView.svelte'
  import CameraPanel from './components/CameraPanel.svelte'
  import FocuserPanel from './components/FocuserPanel.svelte'
  import FilterWheelPanel from './components/FilterWheelPanel.svelte'
  import RotatorPanel from './components/RotatorPanel.svelte'
  import FlatPanelPanel from './components/FlatPanelPanel.svelte'
  import GuiderPanel from './components/GuiderPanel.svelte'
  import ImageViewer from './components/ImageViewer.svelte'
  import SequenceView from './components/SequenceView.svelte'
  import PowerMenu from './components/PowerMenu.svelte'
  import NetworkBadge from './components/NetworkBadge.svelte'
  import LogPanel from './components/LogPanel.svelte'
  import DriverSettings from './components/DriverSettings.svelte'
  import TargetsView from './components/TargetsView.svelte'
  import Phd2View from './components/Phd2View.svelte'
  import GuidingView from './components/GuidingView.svelte'
  import WebcamView from './components/WebcamView.svelte'
  import WebcamFloat from './components/WebcamFloat.svelte'
  import ImageFloat from './components/ImageFloat.svelte'
  import RotationPrompt from './components/RotationPrompt.svelte'
  import FlatsPrompt from './components/FlatsPrompt.svelte'
  import { mainView } from './lib/inbox.svelte'
  import { ask } from './lib/confirm.svelte'
  import SetupGuide from './components/SetupGuide.svelte'
  import { layout, setPanels } from './lib/layout.svelte'
  import ActivityView from './components/ActivityView.svelte'
  import { reveal } from './lib/reveal.svelte'
  import SignIn from './components/SignIn.svelte'
  import { auth, checkAuth } from './lib/auth.svelte'
  import { refreshSetup, setupGuide, setupRemaining } from './lib/setup.svelte'

  let night = $state(false)

  // Full screen: the browser's own, so the page fills a tablet or laptop at the scope (Esc also leaves it). Safari
  // on iPad still uses the webkit names; iPhones can't do it at all, so the button only shows where it works.
  type WebkitDocument = Document & { webkitFullscreenEnabled?: boolean; webkitFullscreenElement?: Element | null; webkitExitFullscreen?: () => Promise<void> }
  type WebkitElement = HTMLElement & { webkitRequestFullscreen?: () => Promise<void> }
  const doc = document as WebkitDocument
  const canFullscreen = !!(doc.fullscreenEnabled || doc.webkitFullscreenEnabled)
  let fullscreen = $state(false)
  const syncFullscreen = () => (fullscreen = !!(doc.fullscreenElement ?? doc.webkitFullscreenElement))
  async function toggleFullscreen() {
    try {
      if (fullscreen) await (doc.exitFullscreen ? doc.exitFullscreen() : doc.webkitExitFullscreen?.())
      else {
        const root = document.documentElement as WebkitElement
        await (root.requestFullscreen ? root.requestFullscreen() : root.webkitRequestFullscreen?.())
      }
    } catch {
      // refused (e.g. not from a click); nothing to undo
    }
    syncFullscreen()
  }

  // Main area: the image, the sequence or targets & framing. All stay mounted so switching keeps their state.
  const sequencer = $derived(nova.status?.sequencer)
  const seq = $derived(sequencer?.status ?? null)

  // The current exposure's progress as a thin bar along the bottom of the sequence box (0–100). It eases forward
  // between progress messages, but jumps back to 0 when the next exposure starts instead of sliding backwards.
  const seqProgress = $derived.by(() => {
    const e = nova.exposure
    if (!sequencer?.running || !e) return null
    if (e.state === 'Downloading' || e.totalSeconds <= 0) return 100
    return Math.min(100, (100 * e.elapsedSeconds) / e.totalSeconds)
  })
  let progressBack = $state(false)
  let lastProgress = 0
  $effect.pre(() => {
    const p = seqProgress ?? 0
    progressBack = p < lastProgress
    lastProgress = p
  })

  onMount(() => {
    try {
      night = localStorage.getItem('nova.night') === '1'
    } catch {
      // storage unavailable (private mode); default to day theme
    }
    // Live updates start once this browser may use NOVA (see the effect below).
    checkAuth()
    document.addEventListener('fullscreenchange', syncFullscreen)
    document.addEventListener('webkitfullscreenchange', syncFullscreen)
    // The setup guide's check of the mini PC (drivers, ASTAP, PHD2): often while it's showing, now and then otherwise.
    let ticks = 0
    const timer = setInterval(() => {
      if (auth.signedIn && (showGuide || ++ticks % 6 === 0)) refreshSetup()
    }, 10_000)
    return () => {
      clearInterval(timer)
      document.removeEventListener('fullscreenchange', syncFullscreen)
      document.removeEventListener('webkitfullscreenchange', syncFullscreen)
    }
  })

  // Signed in (or no password): connect for live updates and check the setup.
  $effect(() => {
    if (!auth.checked) return
    if (!auth.signedIn) {
      stopHub()
      return
    }
    startHub()
    refreshSetup()
  })

  const setupLeft = $derived(setupRemaining())
  const setupProblems = $derived((setupGuide.check?.problems ?? []).filter((p) => !p.optional).length)
  // Shows by itself until setup is done (unless put away), or when opened from the header.
  const showGuide = $derived(
    setupGuide.open || (setupGuide.check != null && nova.status != null && setupLeft > 0 && !setupGuide.check.hidden),
  )
  // Finishing the last step while the guide is showing keeps it open, so its "All set" is seen.
  let guideWasAuto = false
  $effect(() => {
    if (showGuide && !setupGuide.open && setupLeft > 0) guideWasAuto = true
    else if (guideWasAuto && setupLeft === 0) {
      guideWasAuto = false
      setupGuide.open = true
    }
  })
  async function abortSequence() {
    if (await ask('Abort the sequence?', { message: 'The current exposure is cancelled.', confirm: 'Abort', danger: true }))
      act(api.sequenceAbort)
  }

  // A warning or error since the Activity tab was last looked at: a dot on the tab.
  let seenProblem = $state('')
  const lastProblem = $derived([...nova.log].reverse().find((e) => e.level !== 'info')?.timeUtc ?? '')
  $effect(() => {
    if (mainView.view === 'activity') seenProblem = lastProblem
  })
  const unseenProblem = $derived(lastProblem !== '' && lastProblem > seenProblem && mainView.view !== 'activity')

  // Something points at a panel (the setup guide's buttons): the panel column has to be open to show it.
  $effect(() => {
    if (reveal.panel && !layout.panels) setPanels(true)
  })

  function openGuide() {
    setPanels(true)
    setupGuide.open = true
    refreshSetup()
    document.querySelector('aside')?.scrollTo({ top: 0, behavior: 'smooth' })
  }

  $effect(() => {
    document.documentElement.dataset.theme = night ? 'night' : ''
    try {
      localStorage.setItem('nova.night', night ? '1' : '0')
    } catch {
      // ignore
    }
  })

  // An error notice clears itself after 20 seconds (a new error restarts the wait).
  $effect(() => {
    const message = nova.error
    if (!message) return
    const timer = setTimeout(() => {
      if (nova.error === message) nova.error = null
    }, 20_000)
    return () => clearTimeout(timer)
  })

  // Optional equipment only gets a panel when a device of that kind exists (connected or not),
  // so the sidebar shows what you actually have. Panels appear by themselves if one turns up later.
  const has = (kind: DeviceKind) => (nova.status?.devices ?? []).some((d) => d.kinds.includes(kind))
  const hasFilterWheel = $derived(has('FilterWheel'))
  const hasFocuser = $derived(has('Focuser'))
  const hasRotator = $derived(has('Rotator'))
  const hasFlatPanel = $derived(has('FlatPanel'))
  // PHD2 isn't a device: show its panel when it's reachable, or when the user disconnected it (to reconnect).
  const showGuider = $derived(nova.status?.guider ? nova.status.guider.connected || !nova.status.guider.autoConnect : false)
  const serverConnected = $derived(nova.status?.serverConnected ?? false)
</script>

{#if auth.checked && !auth.signedIn}
  <SignIn />
{:else}
<!-- The header at its own height, the main area exactly the rest of the window: the page itself never scrolls. -->
<div class="shell">
<header>
  <div class="brand">NOVA</div>
  {#if nova.status?.version}<span class="version mono muted" title="NOVA version">{nova.status.version === '0.0.0-dev' ? 'dev build' : `v${nova.status.version}`}</span>{/if}
  <span class="muted">{nova.status?.backend ?? '…'}</span>
  {#if nova.hub !== 'connected'}
    <span class="badge warn">NOVA {nova.hub}…</span>
  {:else if !serverConnected}
    <span class="badge warn" title="Retrying every few seconds">driver server offline</span>
  {:else}
    <span class="badge ok">live</span>
  {/if}
  <NetworkBadge />
  {#if nova.status?.site && !nova.status.site.sunProtection}
    <span class="badge danger" title="Sun protection is switched off (Mount panel): turn it back on before using the scope outside">☀ Sun protection off</span>
  {/if}

  {#if seq && (sequencer?.running || seq.state === 'Failed')}
    <div class="seq-chip {seq.state.toLowerCase()}">
      <button class="seq-text" title="{seq.step}: {seq.message}" onclick={() => (mainView.view = 'sequence')}>
        {seq.state === 'Paused' ? '⏸' : seq.state === 'Failed' ? '✗' : '▶'}
        {seq.target ?? seq.sequenceName}, frame {seq.framesDone} of {seq.framesTotal}: {seq.message}
      </button>
      {#if sequencer?.running}
        {#if seq.state === 'Paused'}
          <button class="seq-control" title="Resume the sequence" aria-label="Resume the sequence" onclick={() => act(api.sequenceResume)}>▶</button>
        {:else}
          <button class="seq-control" disabled={seq.pauseRequested}
            title={seq.pauseRequested ? 'Pausing after this frame' : 'Pause after the current frame'} aria-label="Pause the sequence"
            onclick={() => act(api.sequencePause)}>⏸</button
          >
        {/if}
        <button class="seq-control abort" title="Abort the sequence" aria-label="Abort the sequence" onclick={abortSequence}>■</button>
      {/if}
      {#if seqProgress != null}
        <span class="seq-progress" class:back={progressBack} style:width="{seqProgress}%" aria-hidden="true"></span>
      {/if}
    </div>
  {/if}

  <div class="spacer"></div>

  {#if !showGuide && setupGuide.check && nova.status}
    <button class="secondary setup-link" class:warn={setupLeft + setupProblems > 0} onclick={openGuide}
      title="The setup checklist, and what the mini PC is missing">
      {setupLeft + setupProblems > 0 ? `Setup: ${setupLeft + setupProblems} to do` : 'Setup ✓'}
    </button>
  {/if}

  {#if canFullscreen}
    <button class="secondary" onclick={toggleFullscreen} aria-pressed={fullscreen}
      title={fullscreen ? 'Leave full screen (Esc)' : 'Fill the screen with NOVA (the browser hides its bars)'}>
      {fullscreen ? '⤡ Exit full screen' : '⤢ Full screen'}
    </button>
  {/if}
  <button class="secondary" onclick={() => (night = !night)} title="Red night-vision mode">
    {night ? '☀ Day' : '☾ Night'}
  </button>
  <PowerMenu />
</header>

<main class:panels-folded={!layout.panels}>
  {#if !layout.panels}
    <button class="panels-strip" onclick={() => setPanels(true)} title="Show the equipment panels" aria-label="Show the equipment panels">
      <span aria-hidden="true">▸</span><span class="strip-label">Equipment</span>
    </button>
  {/if}
  <aside class:hidden={!layout.panels}>
    <button class="fold-panels" onclick={() => setPanels(false)} title="Fold the panels away, for more room">◂ Hide panels</button>
    {#if showGuide}<SetupGuide />{/if}
    <EquipmentPanel />
    <MountPanel />
    <CameraPanel />
    {#if hasFilterWheel}<FilterWheelPanel />{/if}
    {#if hasFocuser}<FocuserPanel />{/if}
    {#if hasRotator}<RotatorPanel />{/if}
    {#if hasFlatPanel}<FlatPanelPanel />{/if}
    {#if showGuider}<GuiderPanel />{/if}
  </aside>
  <section class="content">
    <nav class="tabs" aria-label="Main view">
      <button class:active={mainView.view === 'image'} aria-pressed={mainView.view === 'image'} onclick={() => (mainView.view = 'image')}>Image</button>
      <button class:active={mainView.view === 'sequence'} aria-pressed={mainView.view === 'sequence'} onclick={() => (mainView.view = 'sequence')}>
        Sequence{#if sequencer?.running}<span class="dot"></span>{/if}
      </button>
      <button class:active={mainView.view === 'targets'} aria-pressed={mainView.view === 'targets'} onclick={() => (mainView.view = 'targets')}
        >Targets</button
      >
      <button class:active={mainView.view === 'polar'} aria-pressed={mainView.view === 'polar'} onclick={() => (mainView.view = 'polar')}
        >Polar alignment{#if nova.status?.polarAlignment?.running}<span class="dot"></span>{/if}</button
      >
      {#if nova.status?.guider?.screenPort}
        <button class:active={mainView.view === 'phd2'} aria-pressed={mainView.view === 'phd2'} onclick={() => (mainView.view = 'phd2')}
          title="PHD2's own window">PHD2</button
        >
      {/if}
      {#if nova.status?.guider?.backend === 'nova'}
        <button class:active={mainView.view === 'guiding'} aria-pressed={mainView.view === 'guiding'} onclick={() => (mainView.view = 'guiding')}
          title="NOVA's guider: the guide frame, its stars and the graph">Guiding{#if nova.status.guider.state === 'LostLock'}<span class="dot"></span>{/if}</button
        >
      {/if}
      <button class:active={mainView.view === 'webcam'} aria-pressed={mainView.view === 'webcam'} onclick={() => (mainView.view = 'webcam')}
        title="The webcam on the mini PC: watch the rig from indoors">Webcam</button
      >
      <button class:active={mainView.view === 'activity'} aria-pressed={mainView.view === 'activity'} onclick={() => (mainView.view = 'activity')}
        title="Everything NOVA has done and reported">Activity{#if unseenProblem}<span class="dot"></span>{/if}</button
      >
    </nav>
    <div class="view" class:hidden={mainView.view !== 'image'}><ImageViewer /></div>
    <div class="view" class:hidden={mainView.view !== 'sequence'}><SequenceView /></div>
    <div class="view" class:hidden={mainView.view !== 'targets'}><TargetsView /></div>
    {#if mainView.view === 'polar'}<div class="view"><PolarView /></div>{/if}
    <div class="view" class:hidden={mainView.view !== 'phd2'}><Phd2View active={mainView.view === 'phd2'} /></div>
    {#if mainView.view === 'guiding'}<div class="view"><GuidingView /></div>{/if}
    {#if mainView.view === 'webcam'}<div class="view"><WebcamView active={true} /></div>{/if}
    <div class="view" class:hidden={mainView.view !== 'activity'}><ActivityView active={mainView.view === 'activity'} /></div>
    <LogPanel />
  </section>
</main>
</div>
<WebcamFloat />
<ImageFloat />

{#if nova.settingsDevice}
  <DriverSettings device={nova.settingsDevice} onclose={() => (nova.settingsDevice = null)} />
{/if}
<ConfirmDialog />
<RotationPrompt />
<FlatsPrompt />

<!-- Errors in full (wrapped, not cut off), under the header; they clear themselves after a while. -->
{#if nova.error}
  <div class="error-toast" role="alert">
    <span class="icon" aria-hidden="true">⚠</span>
    <span class="message">{nova.error}</span>
    <button class="close" title="Dismiss" aria-label="Dismiss" onclick={() => (nova.error = null)}>✕</button>
  </div>
{/if}
{/if}

<style>
  header {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    background: var(--header);
    border-bottom: 1px solid var(--panel-border);
    /* One line: the sequence status shrinks (its text cut short) rather than pushing the buttons onto a second row. */
    flex-wrap: nowrap;
  }
  header > :global(*) {
    flex-shrink: 0;
  }
  .brand {
    font-family: var(--font-mono);
    font-weight: 600;
    letter-spacing: 0.35em;
    color: #7fb6de;
  }
  :global(:root[data-theme='night']) .brand {
    color: var(--accent);
  }
  .version {
    font-size: 11px;
    margin-left: -6px;
  }
  .setup-link.warn {
    border-color: var(--warn);
    color: var(--warn);
  }
  .spacer {
    flex: 1;
  }
  .error-toast {
    position: fixed;
    top: 58px;
    right: 16px;
    z-index: 50;
    width: min(520px, calc(100vw - 32px));
    display: flex;
    align-items: flex-start;
    gap: 8px;
    padding: 10px 12px;
    background: var(--panel);
    color: var(--danger);
    border: 1px solid var(--danger);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
    font-size: 13px;
    line-height: 1.4;
  }
  .error-toast .message {
    flex: 1;
    white-space: normal;
    overflow-wrap: anywhere;
  }
  .error-toast .close {
    background: none;
    border: none;
    color: var(--danger);
    padding: 0 2px;
    cursor: pointer;
  }
  .shell {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr);
    height: 100vh;
    height: 100dvh;
    overflow: hidden;
  }
  /* The shell fills the window exactly, so the page has nothing to scroll; without this a stray pixel of width
     brings a scrollbar, which takes height, which brings the other scrollbar, and the wheel moves the whole page. */
  @media (min-width: 801px) {
    :global(html),
    :global(body) {
      overflow: hidden;
    }
  }
  main {
    display: grid;
    grid-template-columns: minmax(300px, 360px) minmax(0, 1fr);
    gap: 12px;
    padding: 12px 16px;
    min-height: 0;
  }
  aside {
    display: flex;
    flex-direction: column;
    gap: 12px;
    overflow-y: auto;
  }
  /* Folded: the panels stay mounted (their state and timers carry on), just out of sight. */
  aside.hidden {
    display: none;
  }
  main.panels-folded {
    grid-template-columns: 28px minmax(0, 1fr);
  }
  .panels-strip {
    all: unset;
    box-sizing: border-box;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 10px;
    padding: 10px 0;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    background: var(--panel);
    color: var(--muted);
    font-size: 12px;
  }
  .panels-strip:hover,
  .panels-strip:focus-visible {
    color: var(--text);
    border-color: var(--accent);
  }
  .strip-label {
    writing-mode: vertical-rl;
    transform: rotate(180deg);
    letter-spacing: 0.12em;
    text-transform: uppercase;
  }
  .fold-panels {
    all: unset;
    cursor: pointer;
    align-self: flex-end;
    font-size: 12px;
    color: var(--muted);
    margin-bottom: -6px;
  }
  .fold-panels:hover,
  .fold-panels:focus-visible {
    color: var(--text);
  }
  .content {
    display: grid;
    grid-template-rows: auto minmax(0, 1fr) auto;
    gap: 8px;
    min-height: 0;
  }
  .view {
    display: grid;
    min-height: 0;
    grid-row: 2;
    grid-column: 1;
  }
  .view.hidden {
    display: none;
  }
  .tabs {
    display: flex;
    gap: 4px;
  }
  .tabs button {
    background: none;
    border: 1px solid transparent;
    color: var(--muted);
    padding: 4px 14px;
    border-radius: var(--radius);
  }
  .tabs button.active {
    color: var(--text);
    border-color: var(--panel-border);
    background: var(--panel);
  }
  .dot {
    display: inline-block;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--warn);
    margin-left: 6px;
    vertical-align: middle;
  }
  .seq-chip {
    position: relative;
    overflow: hidden;
    display: inline-flex;
    align-items: center;
    border: 1px solid var(--warn);
    border-radius: var(--radius);
    flex: 0 1 auto !important;
    min-width: 110px;
  }
  .seq-text {
    all: unset;
    cursor: pointer;
    color: var(--text);
    font-size: 12px;
    padding: 3px 10px;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .seq-text:focus-visible,
  .seq-control:focus-visible {
    outline: 2px solid var(--accent);
  }
  .seq-control {
    all: unset;
    cursor: pointer;
    flex-shrink: 0;
    font-size: 11px;
    line-height: 1;
    padding: 4px 7px;
    border-left: 1px solid var(--panel-border);
    color: var(--text);
  }
  .seq-control:hover:not(:disabled) {
    background: var(--button);
  }
  .seq-control:disabled {
    opacity: 0.4;
    cursor: default;
  }
  .seq-control.abort {
    color: var(--danger);
  }
  /* The current exposure's progress, 2 px along the box's lower edge. */
  .seq-progress {
    position: absolute;
    left: 0;
    bottom: 0;
    height: 2px;
    background: var(--warn);
    transition: width 1s linear;
    pointer-events: none;
  }
  .seq-progress.back {
    transition: none;
  }
  .seq-chip.paused {
    border-color: var(--muted);
  }
  .seq-chip.failed {
    border-color: var(--danger);
  }
  @media (max-width: 800px) {
    header {
      flex-wrap: wrap;
    }
    /* Narrow screens stack the panels and the main view, and the page scrolls. */
    .shell {
      display: block;
      height: auto;
      overflow: visible;
    }
    main {
      grid-template-columns: 1fr;
      height: auto;
    }
    .content {
      grid-template-rows: auto 70vh auto;
    }
    main.panels-folded {
      grid-template-columns: 1fr;
    }
    .panels-strip {
      flex-direction: row;
      justify-content: center;
      padding: 8px;
    }
    .strip-label {
      writing-mode: horizontal-tb;
      transform: none;
    }
  }
</style>
