<script lang="ts">
  import { nova } from './lib/state.svelte'
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
  import TargetsView from './components/TargetsView.svelte'
  import Phd2View from './components/Phd2View.svelte'
  import GuidingView from './components/GuidingView.svelte'
  import WebcamView from './components/WebcamView.svelte'
  import WebcamFloat from './components/WebcamFloat.svelte'
  import ImageFloat from './components/ImageFloat.svelte'
  import GuideFloat from './components/GuideFloat.svelte'
  import SequenceChip from './components/SequenceChip.svelte'
  import Icon, { type IconName } from './components/Icon.svelte'
  import NovaStar from './components/NovaStar.svelte'
  import { mainView } from './lib/inbox.svelte'
  import SetupGuide from './components/SetupGuide.svelte'
  import { layout, setPanels } from './lib/layout.svelte'
  import ActivityView from './components/ActivityView.svelte'
  import MiniPcView from './components/MiniPcView.svelte'
  import { reveal } from './lib/reveal.svelte'
  import { refreshSetup, setupGuide } from './lib/setup.svelte'
  import { screenMode, session, theme, toggleFullscreen, toggleNight } from './lib/session.svelte'

  // The desktop layout: equipment panels down the left, the main views on the right. What it shows and does is
  // shared with the compact layout (lib/session and the components); this file only arranges it.
  const sequencer = $derived(session.sequencer)

  // The pages of the main area, with the same icons as the compact layout. A dot: something there wants a look.
  type Page = { view: typeof mainView.view; label: string; icon: IconName; title?: string; dot?: boolean }
  const pages = $derived.by<Page[]>(() => {
    const guider = nova.status?.guider
    const list: Page[] = [
      { view: 'image', label: 'Image', icon: 'image' },
      { view: 'sequence', label: 'Plan', icon: 'plan', title: "The night's sequence", dot: !!sequencer?.running },
      { view: 'targets', label: 'Targets', icon: 'targets', title: 'Find and frame targets' },
      { view: 'polar', label: 'Polar alignment', icon: 'polar', dot: !!nova.status?.polarAlignment?.running },
    ]
    if (guider?.screenPort) list.push({ view: 'phd2', label: 'PHD2', icon: 'guider', title: "PHD2's own window" })
    if (guider?.backend === 'nova')
      list.push({ view: 'guiding', label: 'Guiding', icon: 'guider', title: "NOVA's guider: the guide frame, its stars and the graph", dot: guider.state === 'LostLock' })
    list.push(
      { view: 'webcam', label: 'Webcam', icon: 'webcam', title: 'The webcam on the mini PC: watch the rig from indoors' },
      { view: 'minipc', label: 'Mini PC', icon: 'minipc', title: "The mini PC: its health, a terminal, diagnostics, and the files in its image folders" },
      { view: 'activity', label: 'Activity', icon: 'log', title: 'Everything NOVA has done and reported', dot: session.unseenProblem },
    )
    return list
  })

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
</script>

<!-- The header at its own height, the main area exactly the rest of the window: the page itself never scrolls. -->
<div class="shell">
<header>
  <div class="brand"><NovaStar size={17} />NOVA</div>
  {#if nova.status?.version}<span class="version mono muted" title="NOVA version">{nova.status.version === '0.0.0-dev' ? 'dev build' : `v${nova.status.version}`}</span>{/if}
  {#if nova.hub !== 'connected'}
    <span class="badge warn">NOVA {nova.hub}…</span>
  {:else if !session.serverConnected}
    <span class="badge warn" title="Retrying every few seconds">driver server offline</span>
  {:else}
    <span class="badge ok">live</span>
  {/if}
  <NetworkBadge />
  {#if nova.status?.site && !nova.status.site.sunProtection}
    <span class="badge danger" title="Sun protection is switched off (Mount panel): turn it back on before using the scope outside">☀ Sun protection off</span>
  {/if}

  <SequenceChip />

  <div class="spacer"></div>

  {#if !session.showGuide && setupGuide.check && nova.status}
    <button class="secondary setup-link" class:warn={session.setupLeft + session.setupProblems > 0} onclick={openGuide}
      title="The setup checklist, and what the mini PC is missing">
      {session.setupLeft + session.setupProblems > 0 ? `Setup: ${session.setupLeft + session.setupProblems} to do` : 'Setup ✓'}
    </button>
  {/if}

  {#if screenMode.canFullscreen}
    <button class="secondary" onclick={toggleFullscreen} aria-pressed={screenMode.fullscreen}
      title={screenMode.fullscreen ? 'Leave full screen (Esc)' : 'Fill the screen with NOVA (the browser hides its bars)'}>
      {screenMode.fullscreen ? '⤡ Exit full screen' : '⤢ Full screen'}
    </button>
  {/if}
  <button class="secondary" onclick={toggleNight} title="Red night-vision mode">
    {theme.night ? '☀ Day' : '☾ Night'}
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
    {#if session.showGuide}<SetupGuide />{/if}
    <EquipmentPanel />
    <MountPanel />
    <CameraPanel />
    {#if session.hasFilterWheel}<FilterWheelPanel />{/if}
    {#if session.hasFocuser}<FocuserPanel />{/if}
    {#if session.hasRotator}<RotatorPanel />{/if}
    {#if session.hasFlatPanel}<FlatPanelPanel />{/if}
    {#if session.showGuider}<GuiderPanel />{/if}
  </aside>
  <section class="content">
    <nav class="tabs" aria-label="Main view">
      {#each pages as page (page.view)}
        <button class:active={mainView.view === page.view} aria-current={mainView.view === page.view ? 'page' : undefined} title={page.title} onclick={() => (mainView.view = page.view)}>
          <Icon name={page.icon} size={17} />
          <span>{page.label}</span>
          {#if page.dot}<span class="dot"></span>{/if}
        </button>
      {/each}
    </nav>
    <div class="view" class:hidden={mainView.view !== 'image'}><ImageViewer /></div>
    <div class="view" class:hidden={mainView.view !== 'sequence'}><SequenceView /></div>
    <div class="view" class:hidden={mainView.view !== 'targets'}><TargetsView /></div>
    {#if mainView.view === 'polar'}<div class="view"><PolarView /></div>{/if}
    <div class="view" class:hidden={mainView.view !== 'phd2'}><Phd2View active={mainView.view === 'phd2'} /></div>
    {#if mainView.view === 'guiding'}<div class="view"><GuidingView /></div>{/if}
    {#if mainView.view === 'webcam'}<div class="view"><WebcamView active={true} /></div>{/if}
    <div class="view" class:hidden={mainView.view !== 'minipc'}><MiniPcView active={mainView.view === 'minipc'} /></div>
    <div class="view" class:hidden={mainView.view !== 'activity'}><ActivityView /></div>
    <LogPanel />
  </section>
</main>
</div>
<WebcamFloat />
<ImageFloat />
<GuideFloat />

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
    display: flex;
    align-items: center;
    gap: 8px;
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
    overflow-x: hidden; /* a panel's cut corner reaches 1 px past its edge */
    /* Room for the scrollbar kept whether it's there or not, so folding panels away doesn't widen the column. */
    scrollbar-gutter: stable;
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
  /* The main area's pages: a bar of tabs, the open one lit and underlined. */
  .tabs {
    display: flex;
    gap: 2px;
    padding: 3px;
    border-radius: 6px;
    background: var(--input);
    border: 1px solid var(--panel-border);
    overflow-x: auto;
    scrollbar-width: none;
  }
  .tabs button {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    flex: none;
    background: none;
    border: none;
    color: var(--muted);
    padding: 6px 14px;
    border-radius: 4px;
    white-space: nowrap;
  }
  .tabs button:hover {
    color: var(--text);
    background: rgb(255 255 255 / 0.04);
  }
  .tabs button.active {
    color: var(--text);
    background: var(--button);
    box-shadow: inset 0 -2px 0 var(--accent);
  }
  .tabs button.active :global(.icon) {
    color: var(--accent);
  }
  .dot {
    display: inline-block;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--warn);
    margin-left: -2px;
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
