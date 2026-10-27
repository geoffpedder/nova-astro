<script lang="ts">
  import { untrack } from 'svelte'
  import { mainView } from '../../lib/inbox.svelte'
  import { setPanelHost } from '../../lib/panelHost'
  import { screenMode, session, standalone, theme, toggleFullscreen, toggleNight } from '../../lib/session.svelte'
  import { refreshSetup, setupGuide } from '../../lib/setup.svelte'
  import { nova } from '../../lib/state.svelte'
  import ActivityView from '../ActivityView.svelte'
  import MiniPcView from '../MiniPcView.svelte'
  import CameraPanel from '../CameraPanel.svelte'
  import EquipmentPanel from '../EquipmentPanel.svelte'
  import FilterWheelPanel from '../FilterWheelPanel.svelte'
  import FlatPanelPanel from '../FlatPanelPanel.svelte'
  import FocuserPanel from '../FocuserPanel.svelte'
  import GuiderPanel from '../GuiderPanel.svelte'
  import GuidingView from '../GuidingView.svelte'
  import MountPanel from '../MountPanel.svelte'
  import NetworkBadge from '../NetworkBadge.svelte'
  import Phd2View from '../Phd2View.svelte'
  import PolarView from '../PolarView.svelte'
  import PowerMenu from '../PowerMenu.svelte'
  import RotatorPanel from '../RotatorPanel.svelte'
  import SequenceChip from '../SequenceChip.svelte'
  import SequenceView from '../SequenceView.svelte'
  import SetupGuide from '../SetupGuide.svelte'
  import TargetsView from '../TargetsView.svelte'
  import WebcamView from '../WebcamView.svelte'
  import CompactImage from './CompactImage.svelte'
  import Icon, { type IconName } from '../Icon.svelte'
  import KindBadge from '../KindBadge.svelte'
  import type { PanelKind } from '../Panel.svelte'
  import ShootControls from './ShootControls.svelte'

  // The compact layout, for phones and tablets: the image (or the target planner) fills the screen, and everything
  // else floats over it and can be put away. It shows the same components as the desktop layout (the equipment
  // panels, the views); this file only arranges them.

  // ---------- Panels: the equipment panels, one at a time in a panel sliding out from behind the icons ----------
  type SheetId = 'equipment' | 'mount' | 'camera' | 'focuser' | 'filterwheel' | 'guider' | 'setup'
  const sheetTitles: Record<SheetId, string> = {
    equipment: 'Equipment',
    mount: 'Mount',
    camera: 'Camera',
    focuser: 'Focuser',
    filterwheel: 'Filter wheel',
    guider: 'Guider',
    setup: 'Getting started',
  }
  const sheetKinds: Partial<Record<SheetId, PanelKind>> = {
    mount: 'mount',
    camera: 'camera',
    focuser: 'focuser',
    filterwheel: 'filterwheel',
    guider: 'guider',
  }
  // Which panel each Panel id lives in (the rotator and flat panel share the equipment one).
  const sheetOf: Record<string, SheetId> = { rotator: 'equipment', flatpanel: 'equipment' }
  let sheet = $state<SheetId | null>(null)
  // Opened from the top bar (the profile chip, Setup): they drop down from it. The rest slide out from the icons.
  const dropIds: SheetId[] = ['equipment', 'setup']
  const dropOpen = $derived(sheet !== null && dropIds.includes(sheet))
  const sideOpen = $derived(sheet !== null && !dropIds.includes(sheet))
  // What each shows: the open panel, or the last one while it slides away.
  let side = $state<SheetId | null>(null)
  let drop = $state<SheetId | null>(null)
  $effect(() => {
    if (sideOpen) side = sheet
    if (dropOpen) drop = sheet
  })
  function openSheet(id: SheetId) {
    sheet = sheet === id ? null : id
  }
  // The setup guide (or anything else) pointing at a panel opens it.
  setPanelHost({ show: (id) => (sheet = sheetOf[id] ?? (id as SheetId)) })
  // The setup guide's "Choose drivers" and similar open their part inside the equipment panel by themselves.

  // ---------- Views: the image or the planner fill the screen; the others cover it until closed ----------
  const view = $derived(mainView.view)
  const covering = $derived(view !== 'image' && view !== 'targets')
  const viewTitles: Record<string, string> = {
    sequence: 'Plan',
    polar: 'Polar alignment',
    guiding: 'Guiding',
    phd2: 'PHD2',
    webcam: 'Webcam',
    minipc: 'Mini PC',
    activity: 'Activity',
  }
  function showView(next: typeof mainView.view) {
    sheet = null
    mainView.view = mainView.view === next && next !== 'image' ? 'image' : next
  }

  // ---------- Putting things away: the exposure controls, or everything but the shutter (the top bar stays) ----------
  function loadFlag(key: string): boolean {
    try {
      return localStorage.getItem(key) === '1'
    } catch {
      return false
    }
  }
  function saveFlag(key: string, on: boolean) {
    try {
      localStorage.setItem(key, on ? '1' : '0')
    } catch {
      // just for this visit
    }
  }
  let shootMin = $state(loadFlag('nova.compact.shootMin'))
  let bare = $state(false)
  $effect(() => saveFlag('nova.compact.shootMin', shootMin))
  function tapImage() {
    if (sheet) sheet = null
    else bare = !bare
  }

  // A hint, the first time: a tap on the image hides the controls.
  let hint = $state(false)
  $effect(() => {
    if (untrack(() => loadFlag('nova.compact.hinted'))) return
    saveFlag('nova.compact.hinted', true)
    const show = setTimeout(() => (hint = true), 800)
    const hide = setTimeout(() => (hint = false), 5000)
    return () => (clearTimeout(show), clearTimeout(hide))
  })

  // Full screen (or opened from the Home Screen): room for rounded corners, which phones don't report.
  const edge = $derived(screenMode.fullscreen || standalone)

  // ---------- What the bars show ----------
  const status = $derived(nova.status)
  const profileName = $derived(status?.equipmentProfiles?.active ?? 'Equipment')
  const equipmentDot = $derived(!session.serverConnected ? 'off' : status?.equipmentConnected ? 'ok' : 'off')
  const mount = $derived(status?.mount)
  const camera = $derived(status?.camera)
  const cooler = $derived(camera?.controls?.cooler)
  const guider = $derived(status?.guider)
  const guideUnit = $derived(guider?.stats?.units === 'arcsec' ? '″' : ' px')

  type RailItem = { id: string; label: string; icon: IconName; kind?: string; dot: 'ok' | 'warn' | 'off' | 'danger' | null; open: () => void; on: boolean }
  const dotOf = (connected: boolean | undefined, problem = false) => (problem ? 'danger' : connected ? 'ok' : 'off')
  const rail = $derived.by<RailItem[]>(() => {
    const items: RailItem[] = [
      { id: 'mount', label: 'Mount', icon: 'mount', kind: 'mount', dot: mount ? dotOf(mount.connected, mount.state === 'Error') : null, open: () => openSheet('mount'), on: sheet === 'mount' },
      { id: 'camera', label: 'Camera', icon: 'camera', kind: 'camera', dot: camera ? dotOf(camera.connected, camera.state === 'Error') : null, open: () => openSheet('camera'), on: sheet === 'camera' },
    ]
    if (session.hasFocuser)
      items.push({ id: 'focuser', label: 'Focus', icon: 'focuser', kind: 'focuser', dot: dotOf(status?.focuser?.connected), open: () => openSheet('focuser'), on: sheet === 'focuser' })
    if (session.hasFilterWheel)
      items.push({ id: 'filterwheel', label: 'Filter', icon: 'filterwheel', kind: 'filterwheel', dot: dotOf(status?.filterWheel?.connected), open: () => openSheet('filterwheel'), on: sheet === 'filterwheel' })
    if (session.showGuider)
      items.push({
        id: 'guider',
        label: 'Guide',
        icon: 'guider',
        kind: 'guider',
        dot: guider?.state === 'LostLock' ? 'danger' : guider?.state === 'Guiding' ? 'ok' : guider?.connected ? 'warn' : 'off',
        open: () => openSheet('guider'),
        on: sheet === 'guider',
      })
    items.push(
      { id: 'sequence', label: 'Plan', icon: 'plan', dot: session.sequencer?.running ? 'warn' : null, open: () => showView('sequence'), on: view === 'sequence' },
      { id: 'polar', label: 'Polar', icon: 'polar', dot: status?.polarAlignment?.running ? 'warn' : null, open: () => showView('polar'), on: view === 'polar' },
      { id: 'webcam', label: 'Webcam', icon: 'webcam', dot: null, open: () => showView('webcam'), on: view === 'webcam' },
      { id: 'minipc', label: 'Mini PC', icon: 'minipc', dot: null, open: () => showView('minipc'), on: view === 'minipc' },
      { id: 'activity', label: 'Activity', icon: 'log', dot: session.unseenProblem ? 'warn' : null, open: () => showView('activity'), on: view === 'activity' },
    )
    return items
  })

  function openGuide() {
    setupGuide.open = true
    refreshSetup()
    sheet = 'setup'
  }
  // While setup isn't done, the guide opens by itself once (put away, it stays in the top bar).
  let guideShown = false
  $effect(() => {
    if (session.showGuide && !guideShown && !setupGuide.open) {
      guideShown = true
      untrack(() => (sheet = sheet ?? 'setup'))
    }
  })
</script>

<div
  class="compact"
  class:shoot-min={shootMin}
  class:bare
  class:edge
  class:planning={view === 'targets'}
  class:covering
>
  <header class="top">
    <span class="brand">NOVA</span>
    <button class="profile" onclick={() => openSheet('equipment')} class:on={sheet === 'equipment'} title="Equipment: profiles, devices and drivers">
      <span class="dot {equipmentDot}"></span><span class="name">{profileName}</span><Icon name="down" size={15} />
    </button>
    <div class="tabs" role="tablist" aria-label="View">
      <button role="tab" aria-selected={view === 'image'} class:on={view === 'image'} onclick={() => showView('image')}><Icon name="image" size={16} />Image</button>
      <button role="tab" aria-selected={view === 'targets'} class:on={view === 'targets'} onclick={() => showView('targets')}><Icon name="targets" size={16} />Targets</button>
    </div>
    <div class="seq wide"><SequenceChip /></div>
    <!-- How the mini PC is connected; tap for the Network window (Wi-Fi, the hotspot). On a phone it's in Mini PC. -->
    <div class="net wide"><NetworkBadge /></div>
    <div class="chips wide">
      {#if nova.hub !== 'connected'}
        <span class="badge warn">NOVA {nova.hub}…</span>
      {:else if !session.serverConnected}
        <span class="badge warn" title="Retrying every few seconds">driver server offline</span>
      {/if}
      {#if status?.site && !status.site.sunProtection}<span class="badge danger" title="Sun protection is switched off (Mount panel)">☀ Sun protection off</span>{/if}
      {#if mount?.connected}<span class="chip"><span class="k">Mount</span>{mount.state}</span>{/if}
      {#if guider?.state === 'Guiding' && guider.stats}<span class="chip"><span class="k">Guide</span>{guider.stats.rmsTotal.toFixed(2)}{guideUnit}</span>{/if}
      {#if cooler?.temperature != null}<span class="chip"><span class="k">Sensor</span>{cooler.temperature.toFixed(1)} °C</span>{/if}
    </div>
    <span class="spacer"></span>
    {#if setupGuide.check && status && session.setupLeft + session.setupProblems > 0}
      <button class="setup" onclick={openGuide} title="The setup checklist">Setup: {session.setupLeft + session.setupProblems}</button>
    {/if}
    {#if screenMode.canFullscreen}
      <button class="icon-btn" onclick={toggleFullscreen} title={screenMode.fullscreen ? 'Leave full screen' : 'Full screen'} aria-label="Full screen">
        <Icon name={screenMode.fullscreen ? 'shrink' : 'expand'} size={22} />
      </button>
    {/if}
    <button class="icon-btn" onclick={toggleNight} title={theme.night ? 'Day colours' : 'Red night-vision mode'} aria-label="Night mode">
      <Icon name={theme.night ? 'sun' : 'moon'} size={22} />
    </button>
    <div class="power"><PowerMenu /></div>
  </header>

  <nav class="rail" aria-label="Equipment and views">
    {#each rail as item (item.id)}
      <button class:on={item.on} style:--kind={item.kind ? `var(--kind-${item.kind})` : undefined} onclick={item.open} title={item.label}>
        {#if item.dot}<span class="state {item.dot}"></span>{/if}
        <Icon name={item.icon} size={22} />
        <span class="label">{item.label}</span>
      </button>
    {/each}
  </nav>

  <main class="stage">
    <CompactImage ontap={tapImage} onguide={() => (guider?.backend === 'nova' ? showView('guiding') : openSheet('guider'))} />
    <div class="view-box targets" class:hidden={view !== 'targets'}><TargetsView /></div>
    {#if covering}
      <section class="view-box covered" aria-label={viewTitles[view]}>
        <header class="view-head">
          <h2>{viewTitles[view]}</h2>
          <button class="icon-btn" onclick={() => showView('image')} aria-label="Close"><Icon name="close" size={22} /></button>
        </header>
        <div class="view-body">
          {#if view === 'polar'}<PolarView />{/if}
          {#if view === 'guiding'}<GuidingView />{/if}
          {#if view === 'webcam'}<WebcamView active={true} />{/if}
        </div>
      </section>
    {/if}
    <!-- Kept running out of sight, as the desktop does: their state and timers carry on. -->
    <div class="view-box covered kept" class:hidden={view !== 'sequence'}><div class="view-body"><SequenceView /></div></div>
    <div class="view-box covered kept minipc" class:hidden={view !== 'minipc'}>
      <header class="view-head phone-only">
        <h2>Mini PC</h2>
        <div class="net"><NetworkBadge /></div>
        <PowerMenu />
        <button class="icon-btn" onclick={() => showView('image')} aria-label="Close"><Icon name="close" size={22} /></button>
      </header>
      <div class="view-body"><MiniPcView active={view === 'minipc'} /></div>
    </div>
    <div class="view-box covered kept" class:hidden={view !== 'activity'}><div class="view-body"><ActivityView /></div></div>
    <div class="view-box covered kept" class:hidden={view !== 'phd2'}><div class="view-body"><Phd2View active={view === 'phd2'} /></div></div>
    <div class="seq-float"><SequenceChip /></div>
    {#if hint}<div class="hint" role="status">Tap the image to hide the controls, double-tap to zoom</div>{/if}
  </main>

  {#if view === 'image'}
    <ShootControls minimal={shootMin || bare} ontoggle={() => ((shootMin = !shootMin), (bare = false))} onopen={(p) => openSheet(p as SheetId)} />
  {/if}

  <!-- The panels slide out from behind the icons: this frame starts at their edge and clips anything beyond it. -->
  <div class="sheet-frame">
    <button class="scrim" class:on={sheet !== null} onclick={() => (sheet = null)} aria-label="Close the panel" tabindex="-1"></button>
    <div class="sheet side" class:on={sideOpen} style:--kind={side && sheetKinds[side] ? `var(--kind-${sheetKinds[side]})` : 'var(--accent)'} role="dialog" aria-label={side ? sheetTitles[side] : undefined}>
      <div class="grab"></div>
      <header class="sheet-head">
        {#if side && sheetKinds[side]}<KindBadge kind={sheetKinds[side]!} size={24} />{/if}
        <h2>{side ? sheetTitles[side] : ''}</h2>
        {#if side === 'guider' && guider?.backend === 'nova'}
          <button class="link" onclick={() => showView('guiding')}>Guide frame and graph</button>
        {:else if side === 'guider' && guider?.screenPort}
          <button class="link" onclick={() => showView('phd2')}>PHD2's window</button>
        {/if}
        <button class="icon-btn" onclick={() => (sheet = null)} aria-label="Close"><Icon name="close" size={20} /></button>
      </header>
      <div class="sheet-body">
        <!-- Every panel stays mounted (its effects keep running); only the open one shows. -->
        <div class:hidden={side !== 'mount'}><MountPanel /></div>
        <div class:hidden={side !== 'camera'}><CameraPanel /></div>
        {#if session.hasFocuser}<div class:hidden={side !== 'focuser'}><FocuserPanel /></div>{/if}
        {#if session.hasFilterWheel}<div class:hidden={side !== 'filterwheel'}><FilterWheelPanel /></div>{/if}
        {#if session.showGuider}<div class:hidden={side !== 'guider'}><GuiderPanel /></div>{/if}
      </div>
    </div>
    <div class="sheet drop" class:on={dropOpen} role="dialog" aria-label={drop ? sheetTitles[drop] : undefined}>
      <header class="sheet-head">
        <h2>{drop ? sheetTitles[drop] : ''}</h2>
        <button class="icon-btn" onclick={() => (sheet = null)} aria-label="Close"><Icon name="close" size={20} /></button>
      </header>
      <div class="sheet-body">
        <div class:hidden={drop !== 'setup'}>{#if session.showGuide || drop === 'setup'}<SetupGuide />{/if}</div>
        <div class:hidden={drop !== 'equipment'}>
          <EquipmentPanel />
          {#if session.hasRotator}<div class="more"><h3>Rotator</h3><RotatorPanel /></div>{/if}
          {#if session.hasFlatPanel}<div class="more"><h3>Flat panel</h3><FlatPanelPanel /></div>{/if}
        </div>
      </div>
      <div class="grab drop-grab"></div>
    </div>
  </div>
</div>

<style>
  .compact {
    /* Sizes of the bars, and the screen's unsafe edges (notch, cutout, home bar). */
    --top: 48px;
    --rail: 68px;
    --shoot: 112px;
    --bar: 0px;
    --safe-t: env(safe-area-inset-top);
    --safe-b: env(safe-area-inset-bottom);
    --safe-l: env(safe-area-inset-left);
    --safe-r: env(safe-area-inset-right);
    --corner: 0px;
    --glass: rgb(20 20 20 / 0.72);
    --glass-line: rgb(255 255 255 / 0.07);
    /* What the bars cover, so the boxes over the image keep clear of them. */
    --in-t: calc(var(--top) + var(--safe-t));
    --in-l: var(--rail);
    --in-r: var(--shoot);
    --in-b: var(--safe-b);

    position: fixed;
    inset: 0;
    padding-left: var(--safe-l);
    padding-right: var(--safe-r);
    background: #000;
    display: grid;
    grid-template-columns: var(--rail) minmax(0, 1fr) var(--shoot);
    grid-template-rows: calc(var(--top) + var(--safe-t)) minmax(0, 1fr);
    grid-template-areas: 'top top top' 'rail stage shoot';
    overflow: hidden;
    color: var(--text);
  }
  :global(:root[data-theme='night']) .compact {
    --glass: rgb(8 0 0 / 0.82);
    --glass-line: var(--panel-border);
  }
  /* Full screen, or from the Home Screen: room for rounded corners, which Android doesn't report. */
  .compact.edge {
    --safe-t: max(env(safe-area-inset-top), 6px);
    --safe-b: max(env(safe-area-inset-bottom), 6px);
    --corner: 18px;
  }
  .compact.shoot-min,
  .compact.bare,
  .compact.planning,
  .compact.covering {
    --in-r: 0px;
  }
  .compact.bare {
    --in-l: 0px;
  }

  /* ---------- Top bar ---------- */
  .top {
    grid-area: top;
    z-index: 3;
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
    padding: var(--safe-t) calc(8px + var(--corner)) 0 var(--corner);
    background: var(--glass);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border-bottom: 1px solid var(--glass-line);
    transition:
      transform 0.25s ease,
      opacity 0.25s ease;
  }
  .brand {
    width: var(--rail);
    flex: none;
    text-align: center;
    font: 600 13px var(--font-mono);
    letter-spacing: 0.3em;
    color: var(--accent);
  }
  .compact.edge .brand {
    width: auto;
  }
  .profile {
    all: unset;
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 5px 6px 5px 10px;
    border-radius: 999px;
    background: rgb(0 0 0 / 0.35);
    font-size: 12px;
    min-width: 0;
    cursor: pointer;
    flex: 0 1 auto;
  }
  .profile.on {
    box-shadow: inset 0 0 0 1px var(--accent);
  }
  .profile .name {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 160px;
  }
  .dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    flex: none;
    background: #555;
  }
  .dot.ok {
    background: var(--ok);
  }
  .tabs {
    display: flex;
    gap: 2px;
    padding: 3px;
    border-radius: 6px;
    background: rgb(0 0 0 / 0.35);
    flex: none;
  }
  .tabs button {
    all: unset;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    min-width: 64px;
    padding: 6px 12px;
    text-align: center;
    border-radius: 4px;
    font-size: 13px;
    color: var(--muted);
    cursor: pointer;
  }
  .tabs button.on {
    background: var(--button);
    color: var(--text);
    box-shadow: inset 0 -2px 0 var(--accent);
  }
  .tabs button.on :global(.icon) {
    color: var(--accent);
  }
  .seq {
    min-width: 0;
    flex: 0 1 auto;
    display: flex;
  }
  .net {
    display: flex;
    min-width: 0;
    flex: 0 1 auto;
  }
  .view-head .net {
    margin-right: 6px;
  }
  .chips {
    display: flex;
    gap: 14px;
    min-width: 0;
    overflow: hidden;
    align-items: center;
  }
  .chip {
    font: 12px var(--font-mono);
    white-space: nowrap;
  }
  .chip .k {
    font: 11px var(--font);
    color: var(--muted);
    margin-right: 6px;
  }
  .spacer {
    flex: 1;
  }
  .setup {
    all: unset;
    padding: 5px 10px;
    border-radius: 999px;
    border: 1px solid var(--warn);
    color: var(--warn);
    font-size: 12px;
    cursor: pointer;
    white-space: nowrap;
  }
  .icon-btn {
    all: unset;
    width: 36px;
    height: 36px;
    display: grid;
    place-items: center;
    border-radius: 6px;
    color: var(--muted);
    cursor: pointer;
    flex: none;
  }
  .icon-btn:hover {
    color: var(--text);
    background: rgb(255 255 255 / 0.06);
  }
  /* The ⏻ menu's button, made to match the icons. */
  .power :global(.power) {
    background: none;
    border: none;
    color: var(--muted);
    font-size: 18px;
    padding: 4px 8px;
  }

  /* ---------- Icon rail ---------- */
  .rail {
    grid-area: rail;
    z-index: 2;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
    padding: 8px 0 calc(8px + var(--safe-b) + var(--corner));
    background: var(--glass);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border-right: 1px solid var(--glass-line);
    overflow-y: auto;
    transition:
      transform 0.25s ease,
      opacity 0.25s ease;
  }
  /* Everything but the shutter: the boxes over the image go too. */
  .compact.bare :global(.huds) {
    opacity: 0;
    pointer-events: none;
  }
  .compact.bare :global(.shoot .toggle) {
    display: none;
  }
  .compact.bare .rail {
    transform: translateX(-100%);
    opacity: 0;
    pointer-events: none;
  }
  .rail button {
    all: unset;
    position: relative;
    width: 56px;
    padding: 8px 0 6px;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 3px;
    border-radius: 8px;
    color: var(--muted);
    font-size: 10px;
    cursor: pointer;
    flex: none;
  }
  .rail button:hover,
  .rail button.on {
    color: var(--text);
    background: rgb(255 255 255 / 0.07);
  }
  .rail button.on::before {
    content: '';
    position: absolute;
    left: -6px;
    top: 10px;
    bottom: 10px;
    width: 3px;
    border-radius: 2px;
    background: var(--kind, var(--accent));
  }
  .state {
    position: absolute;
    top: 6px;
    right: 10px;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: #555;
    box-shadow: 0 0 0 2px rgb(0 0 0 / 0.6);
  }
  .state.ok {
    background: var(--ok);
  }
  .state.warn {
    background: var(--warn);
  }
  .state.danger {
    background: var(--danger);
  }

  /* ---------- The stage: the image edge to edge, under the bars ---------- */
  .stage {
    grid-area: 1 / 1 / -1 / -1;
    position: relative;
    z-index: 0;
    min-width: 0;
    min-height: 0;
  }
  .view-box {
    position: absolute;
    top: var(--in-t);
    left: var(--in-l);
    right: var(--in-r);
    bottom: var(--in-b);
    display: grid;
    min-height: 0;
    background: var(--bg);
    transition:
      top 0.25s ease,
      left 0.25s ease,
      bottom 0.25s ease;
  }
  .view-box.targets {
    padding: 8px;
  }
  .hidden {
    display: none !important;
  }
  .covered {
    grid-template-rows: auto minmax(0, 1fr);
    z-index: 1;
  }
  .covered.kept {
    grid-template-rows: minmax(0, 1fr);
  }
  .view-head {
    display: flex;
    align-items: center;
    padding: 6px 8px 6px 14px;
  }
  .view-head h2 {
    flex: 1;
    margin: 0;
    font-size: 15px;
    font-weight: 600;
  }
  .view-body {
    min-height: 0;
    display: grid;
    padding: 0 8px 8px;
    overflow: auto;
  }
  .covered.kept .view-body {
    padding-top: 8px;
  }
  .phone-only {
    display: none;
  }
  .seq-float {
    display: none;
  }
  .hint {
    position: absolute;
    z-index: 5;
    left: 50%;
    bottom: calc(var(--in-b) + 110px);
    transform: translateX(-50%);
    padding: 8px 14px;
    border-radius: 999px;
    background: var(--glass);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    font-size: 13px;
    white-space: nowrap;
    pointer-events: none;
  }

  /* ---------- Panels ---------- */
  .sheet-frame {
    position: fixed;
    z-index: 10;
    top: calc(var(--top) + var(--safe-t));
    bottom: 0;
    left: calc(var(--rail) + var(--safe-l));
    right: 0;
    overflow: hidden;
    pointer-events: none;
  }
  .scrim {
    all: unset;
    position: absolute;
    inset: 0;
    background: rgb(0 0 0 / 0.3);
    opacity: 0;
    transition: opacity 0.2s;
  }
  .scrim.on {
    opacity: 1;
    pointer-events: auto;
  }
  .sheet {
    position: absolute;
    top: 0;
    bottom: 0;
    left: 0;
    width: min(420px, 100%);
    display: flex;
    flex-direction: column;
    background: var(--panel);
    border-right: 1px solid var(--panel-border);
    box-shadow: 12px 0 32px rgb(0 0 0 / 0.45);
    transform: translateX(calc(-100% - 40px));
    visibility: hidden;
    transition:
      transform 0.22s ease,
      visibility 0s 0.22s;
    pointer-events: auto;
  }
  /* A panel for a kind of equipment carries a light tint of its colour, as the desktop's boxes do. */
  .sheet.side {
    background: color-mix(in srgb, var(--panel) 92%, var(--kind) 8%);
  }
  .sheet.on {
    transform: none;
    visibility: visible;
    transition: transform 0.22s ease;
  }
  .grab {
    display: none;
  }
  /* Dropping down from the top bar, under the profile chip. */
  .sheet.drop {
    bottom: auto;
    max-height: calc(100% - 24px);
    width: min(460px, 100%);
    border-right: 1px solid var(--panel-border);
    border-bottom: 1px solid var(--panel-border);
    border-radius: 0 0 12px 12px;
    box-shadow: 0 12px 32px rgb(0 0 0 / 0.45);
    transform: translateY(-100vh);
  }
  .sheet.drop.on {
    transform: none;
  }
  .sheet-head {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 8px 8px 14px;
    border-bottom: 1px solid var(--panel-border);
  }
  .sheet-head h2 {
    flex: 1;
    margin: 0;
    font-size: 15px;
    font-weight: 600;
  }
  .link {
    all: unset;
    color: var(--accent);
    font-size: 12px;
    cursor: pointer;
  }
  .sheet-body {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 12px 14px calc(16px + var(--safe-b));
  }
  .more {
    margin-top: 18px;
  }
  .more h3 {
    margin: 0 0 8px;
    font: 500 11px var(--font);
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--muted);
  }

  /* ---------- Tablets in portrait ---------- */
  @media (max-width: 1000px) {
    .chips {
      display: none;
    }
  }

  /* ---------- Phones: icons along the bottom, the shutter a floating pill, panels from below ---------- */
  @media (max-width: 720px) {
    .compact {
      --top: 46px;
      --rail: 0px;
      --shoot: 0px;
      --bar: 54px;
      --in-l: 0px;
      --in-r: 0px;
      --in-b: calc(var(--bar) + var(--safe-b));
      grid-template-columns: minmax(0, 1fr);
      grid-template-rows: calc(var(--top) + var(--safe-t)) minmax(0, 1fr) calc(var(--bar) + var(--safe-b));
      grid-template-areas: 'top' 'stage' 'rail';
    }
    .compact.bare {
      --in-b: var(--safe-b);
    }
    .top {
      gap: 6px;
    }
    .brand {
      width: auto;
      padding-left: 10px;
      letter-spacing: 0.12em;
      font-size: 12px;
    }
    .profile .name {
      max-width: 86px;
    }
    .tabs button {
      min-width: 0;
      padding: 5px 10px;
      font-size: 12px;
    }
    /* No room for the icons on a phone. */
    .tabs button :global(.icon) {
      display: none;
    }
    .wide,
    .power {
      display: none;
    }
    .phone-only {
      display: flex;
    }
    .covered.kept.minipc {
      grid-template-rows: auto minmax(0, 1fr);
    }
    .seq-float {
      display: block;
      position: absolute;
      z-index: 2;
      top: calc(var(--in-t) + 8px);
      left: 50%;
      transform: translateX(-50%);
      max-width: calc(100% - 120px);
      background: var(--glass);
      border-radius: var(--radius);
    }
    .compact:not(.covering):not(.planning) .seq-float {
      top: calc(var(--in-t) + 64px);
    }
    .setup {
      padding: 4px 8px;
    }
    .rail {
      grid-area: rail;
      flex-direction: row;
      overflow: hidden;
      gap: 0;
      padding: 3px max(2px, calc(var(--safe-b) * 0.4), var(--corner)) calc(3px + var(--safe-b));
      border-right: none;
      border-top: 1px solid var(--glass-line);
    }
    .compact.bare .rail {
      transform: translateY(100%);
    }
    .rail button {
      flex: 1 1 0;
      min-width: 0;
      width: auto;
      padding: 7px 0 5px;
      gap: 2px;
      font-size: 9px;
      border-radius: 6px;
    }
    .rail button.on::before {
      left: 8px;
      right: 8px;
      top: -3px;
      bottom: auto;
      width: auto;
      height: 3px;
    }
    .state {
      top: 4px;
      right: calc(50% - 15px);
      width: 6px;
      height: 6px;
    }
    .sheet-frame {
      left: var(--safe-l);
      right: var(--safe-r);
      bottom: calc(var(--bar) + var(--safe-b));
    }
    .compact.bare .sheet-frame {
      bottom: var(--safe-b);
    }
    .sheet.side {
      top: auto;
      left: 0;
      right: 0;
      bottom: 0;
      width: 100%;
      max-height: calc(100% - 40px);
      border-right: none;
      border-radius: 14px 14px 0 0;
      transform: translateY(100vh);
      box-shadow: 0 -12px 32px rgb(0 0 0 / 0.5);
    }
    .sheet.side.on {
      transform: none;
    }
    .sheet.drop {
      left: 0;
      right: 0;
      width: 100%;
      border-right: none;
      border-radius: 0 0 14px 14px;
    }
    .grab {
      display: block;
      width: 40px;
      height: 4px;
      border-radius: 2px;
      background: var(--cut-line);
      margin: 8px auto 0;
    }
    .grab.drop-grab {
      margin: 0 auto 8px;
    }
    .sheet-body {
      padding-bottom: 16px;
    }
    .hint {
      white-space: normal;
      width: max-content;
      max-width: calc(100% - 32px);
      text-align: center;
    }
  }

  /* A phone held sideways: icons without labels, a slimmer shutter column. */
  @media (min-width: 721px) and (max-height: 500px) {
    .compact {
      --rail: 52px;
      --shoot: 92px;
      --top: 40px;
    }
    .rail {
      gap: 0;
      padding-top: 4px;
    }
    .rail button {
      width: 44px;
      padding: 6px 0;
      gap: 0;
    }
    .rail .label {
      display: none;
    }
    .state {
      top: 4px;
      right: 6px;
    }
  }
</style>
