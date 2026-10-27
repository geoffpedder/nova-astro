<script lang="ts">
  import { portal } from '../lib/portal'
  import { api } from '../lib/api'
  import { act, nova, stopHub } from '../lib/state.svelte'
  import { auth, checkAuth } from '../lib/auth.svelte'
  import { ask as confirmAsk } from '../lib/confirm.svelte'
  import { download } from '../lib/download'
  import PasswordDialog from './PasswordDialog.svelte'
  import NotificationsDialog from './NotificationsDialog.svelte'
  import { isCompact, setLayoutChoice, uiLayout, type LayoutChoice } from '../lib/uiLayout.svelte'

  // ⏻ in the header: restart or shut down the mini PC, after a confirmation that says what will stop.
  const power = $derived(nova.status?.power)
  const sequencer = $derived(nova.status?.sequencer)
  const mount = $derived(nova.status?.mount)
  const cooler = $derived(nova.status?.camera?.controls?.cooler)

  let open = $state(false)
  let asking = $state<'restart' | 'shutdown' | null>(null)
  let park = $state(true)
  let coolerOff = $state(true)
  let busy = $state(false)
  let done = $state<'restart' | 'shutdown' | null>(null)
  let passwordOpen = $state(false)
  let notificationsOpen = $state(false)

  async function signOut() {
    open = false
    await act(api.signOut)
    await stopHub()
    await checkAuth()
  }

  // What would be cut short, so the confirmation can say so.
  const running = $derived(
    [
      sequencer?.running ? `the sequence “${sequencer.status?.sequenceName}” (aborted)` : null,
      nova.status?.autofocus?.running ? 'autofocus' : null,
      nova.status?.polarAlignment?.running ? 'polar alignment' : null,
      nova.status?.exposure ? 'the current exposure' : null,
    ].filter(Boolean) as string[],
  )
  const canPark = $derived(!!mount?.connected && mount.state !== 'Parked')
  const cooling = $derived(!!cooler?.on)

  function ask(kind: 'restart' | 'shutdown') {
    open = false
    park = true
    coolerOff = true
    asking = kind
  }

  async function confirm() {
    if (!asking) return
    busy = true
    const kind = asking
    const options = { park: park && canPark, coolerOff: coolerOff && cooling }
    const ok = await act(() => (kind === 'restart' ? api.systemRestart(options) : api.systemShutdown(options)))
    busy = false
    asking = null
    if (ok) done = kind
  }

  // Backing up all of NOVA's settings, and restoring a backup (NOVA restarts to swap them in).
  let backupNote = $state<string | null>(null)
  let restoreInput = $state<HTMLInputElement | null>(null)
  async function backUp() {
    open = false
    try {
      await download('/api/backup', 'nova-backup.zip')
    } catch (e) {
      backupNote = `Couldn't make the backup: ${e instanceof Error ? e.message : e}`
    }
  }
  async function restore(event: Event) {
    const input = event.currentTarget as HTMLInputElement
    const file = input.files?.[0]
    input.value = ''
    if (!file) return
    const sure = await confirmAsk(`Restore NOVA's settings from ${file.name}?`, {
      message:
        "This replaces all of NOVA's settings with the backup's: sites, optics, equipment profiles, sequences, the guider and its darks, notifications, the password, the drivers and the hotspot. NOVA restarts to finish, stopping anything it's doing.",
      confirm: 'Restore',
      danger: true,
    })
    if (!sure) return
    try {
      const response = await fetch('/api/backup/restore', { method: 'POST', headers: { 'Content-Type': 'application/zip' }, body: file })
      const result = await response.json().catch(() => null)
      if (!response.ok) throw new Error(result?.detail ?? `${response.status} ${response.statusText}`)
      if (result?.restarting) {
        backupNote = "Restoring: NOVA is restarting with the backup's settings. This page reloads in a moment."
        setTimeout(() => location.reload(), 15_000)
      } else {
        backupNote = 'The backup is ready: restart NOVA to finish restoring it.'
      }
    } catch (e) {
      backupNote = `Couldn't restore: ${e instanceof Error ? e.message : e}`
    }
  }

  // Which layout this browser shows: automatic (compact on phones and tablets), or one of them always.
  const layouts: { value: LayoutChoice; label: string; title: string }[] = [
    { value: 'auto', label: 'Automatic', title: 'Compact on phones and tablets, desktop otherwise' },
    { value: 'desktop', label: 'Desktop', title: 'Panels beside the main view' },
    { value: 'compact', label: 'Compact', title: 'The image full screen, everything else floating over it: made for touch' },
  ]
  function chooseLayout(choice: LayoutChoice) {
    open = false
    setLayoutChoice(choice)
  }

  function closeOnOutsideClick(event: MouseEvent) {
    if (open && !(event.target as Element).closest('.power-wrap')) open = false
  }
</script>

<svelte:window onclick={closeOnOutsideClick} />

<div class="power-wrap">
  <button class="secondary power" title="Restart or shut down the mini PC, notifications and the password" aria-haspopup="menu" aria-expanded={open} onclick={() => (open = !open)}
    >⏻</button
  >
  {#if open}
    <div class="menu panel" role="menu">
      <button role="menuitem" class="item" disabled={!power?.available} title={power?.problem ?? ''} onclick={() => ask('restart')}
        >Restart the mini PC…</button
      >
      <button role="menuitem" class="item" disabled={!power?.available} title={power?.problem ?? ''} onclick={() => ask('shutdown')}
        >Shut down the mini PC…</button
      >
      {#if power && !power.available}<p class="muted hint">{power.problem}</p>{/if}
      <hr />
      <button role="menuitem" class="item" onclick={() => ((open = false), (notificationsOpen = true))}
        title="Messages to your phone when a sequence finishes or needs you">Notifications…</button
      >
      <button role="menuitem" class="item" onclick={() => ((open = false), (passwordOpen = true))}
        title="Ask for a password before anyone can use NOVA">{auth.passwordSet ? 'Change the password…' : 'Set a password…'}</button
      >
      {#if auth.passwordSet}
        <button role="menuitem" class="item" onclick={signOut}>Sign out of this browser</button>
      {/if}
      <hr />
      <button role="menuitem" class="item" onclick={backUp}
        title="All of NOVA's settings in one file, to keep or to move to another mini PC. It holds your password, notification token, hotspot password and sites: keep it private"
        >Back up settings</button
      >
      <button role="menuitem" class="item" onclick={() => ((open = false), restoreInput?.click())}
        title="Put back the settings from a backup (this mini PC's or another's); NOVA restarts to finish">Restore settings…</button
      >
      <hr />
      <div class="menu-title muted">Layout on this browser{uiLayout.choice === 'auto' ? `: ${isCompact() ? 'compact' : 'desktop'}` : ''}</div>
      {#each layouts as l (l.value)}
        <button role="menuitemradio" aria-checked={uiLayout.choice === l.value} class="item layout" title={l.title} onclick={() => chooseLayout(l.value)}>
          <span class="tick">{uiLayout.choice === l.value ? '●' : ''}</span>{l.label}
        </button>
      {/each}
    </div>
  {/if}
</div>

<input type="file" accept=".zip,application/zip" hidden bind:this={restoreInput} onchange={restore} />
{#if backupNote}
  <div use:portal class="backdrop" role="presentation" onclick={() => (backupNote = null)}></div>
  <div use:portal class="panel dialog" role="dialog" aria-modal="true" aria-label="Backup">
    <p>{backupNote}</p>
    <div class="actions"><button class="secondary" onclick={() => (backupNote = null)}>Close</button></div>
  </div>
{/if}

{#if passwordOpen}<PasswordDialog onclose={() => (passwordOpen = false)} />{/if}
{#if notificationsOpen}<NotificationsDialog onclose={() => (notificationsOpen = false)} />{/if}

{#if asking}
  <div use:portal class="backdrop" role="presentation" onclick={() => !busy && (asking = null)}></div>
  <div use:portal class="dialog panel" role="dialog" aria-modal="true" aria-labelledby="power-title">
    <h3 id="power-title">{asking === 'restart' ? 'Restart the mini PC?' : 'Shut down the mini PC?'}</h3>
    {#if running.length}
      <p class="warn">This stops {running.join(', ')}.</p>
    {/if}
    {#if canPark}
      <label class="check"><input type="checkbox" bind:checked={park} disabled={busy} /> Park the mount first</label>
    {/if}
    {#if cooling}
      <label class="check"><input type="checkbox" bind:checked={coolerOff} disabled={busy} /> Turn the camera's cooler off first</label>
    {/if}
    <p class="muted small">
      {asking === 'restart'
        ? 'NOVA comes back by itself after the restart; this page reconnects when it does.'
        : 'To start it again you need to switch the mini PC on at the box (or with wake-on-LAN).'}
    </p>
    <div class="actions">
      <button class="secondary" disabled={busy} onclick={() => (asking = null)}>Cancel</button>
      <button class="danger" disabled={busy} onclick={confirm}>
        {busy ? (park && canPark ? 'Parking…' : 'Working…') : asking === 'restart' ? 'Restart' : 'Shut down'}
      </button>
    </div>
  </div>
{/if}

{#if done}
  <div use:portal class="backdrop solid" role="presentation"></div>
  <div use:portal class="dialog panel" role="alertdialog" aria-live="assertive">
    <h3>{done === 'restart' ? 'Restarting…' : 'Shutting down…'}</h3>
    <p>
      {done === 'restart'
        ? 'The mini PC is restarting. This page reconnects by itself in a minute or two.'
        : "The mini PC is shutting down. Wait about 30 seconds (until it's quiet) before switching off its power."}
    </p>
    <div class="actions"><button class="secondary" onclick={() => (done = null)}>Close</button></div>
  </div>
{/if}

<style>
  .power-wrap {
    position: relative;
  }
  .power {
    padding: 4px 10px;
  }
  .menu {
    position: absolute;
    right: 0;
    top: calc(100% + 4px);
    z-index: 20;
    min-width: 220px;
    padding: 6px;
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.4);
  }
  .menu hr {
    border: 0;
    border-top: 1px solid var(--panel-border);
    margin: 6px 0;
  }
  .item {
    all: unset;
    box-sizing: border-box;
    display: block;
    width: 100%;
    padding: 6px 8px;
    border-radius: 4px;
    font-size: 13px;
    cursor: pointer;
  }
  .item:hover:not(:disabled),
  .item:focus-visible {
    background: var(--input);
  }
  .item:disabled {
    opacity: 0.45;
    cursor: default;
  }
  .hint {
    font-size: 11px;
    margin: 6px 8px 2px;
  }
  .menu-title {
    font-size: 12px;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    padding: 4px 8px;
  }
  .item.layout {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .tick {
    width: 10px;
    font-size: 9px;
    color: var(--accent);
  }
  /* Opened from the compact layout's bars, the menu can be taller than the space: it scrolls. */
  .menu {
    max-height: calc(100dvh - 80px);
    overflow-y: auto;
  }
  .backdrop {
    position: fixed;
    inset: 0;
    background: rgb(0 0 0 / 0.5);
    z-index: 40;
  }
  .backdrop.solid {
    background: rgb(0 0 0 / 0.8);
  }
  .dialog {
    position: fixed;
    z-index: 41;
    left: 50%;
    top: 30%;
    transform: translate(-50%, -30%);
    width: min(420px, calc(100vw - 32px));
    display: grid;
    gap: 10px;
    padding: 16px;
  }
  h3 {
    margin: 0;
  }
  .warn {
    color: var(--warn);
    margin: 0;
  }
  p {
    margin: 0;
  }
  .small {
    font-size: 12px;
  }
  label.check {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 13px;
  }
  .actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
  }
</style>
