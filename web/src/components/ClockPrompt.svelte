<script lang="ts">
  // The mini PC's clock against this device's, over every tab: with no internet (a dark site, the hotspot) nothing
  // corrects the mini PC's, and the sky NOVA works out, and the time it gives the mount, come from it. A clock that's
  // set from the internet is trusted: then it's this device that's out, and nothing is shown.
  import { onMount } from 'svelte'
  import { api } from '../lib/api'
  import { act } from '../lib/state.svelte'
  import type { ClockStatus } from '../lib/types'

  const Tolerance = 60_000 // ms: less than this isn't worth a prompt
  const Every = 10 * 60_000
  const DismissedKey = 'nova-clock-dismissed'

  let status = $state<ClockStatus | null>(null)
  /** How far the mini PC's clock is ahead of this device's (ms; negative = behind). */
  let ahead = $state(0)
  let dismissed = $state(false)
  let setting = $state(false)

  const visible = $derived(!!status && status.synchronized !== true && Math.abs(ahead) > Tolerance && !dismissed)

  async function check() {
    try {
      const sent = Date.now()
      const s = await api.clock()
      const back = Date.now()
      if (back - sent > 5000) return // too slow an answer to compare clocks by
      status = s
      ahead = Date.parse(s.utc) - (sent + back) / 2
    } catch {
      // offline or signed out: the page says so elsewhere
    }
  }

  onMount(() => {
    try {
      dismissed = sessionStorage.getItem(DismissedKey) === '1'
    } catch {
      // no storage (a private window): ask again next time
    }
    check()
    const timer = setInterval(check, Every)
    const onVisible = () => document.visibilityState === 'visible' && check()
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      clearInterval(timer)
      document.removeEventListener('visibilitychange', onVisible)
    }
  })

  function dismiss() {
    dismissed = true
    try {
      sessionStorage.setItem(DismissedKey, '1')
    } catch {
      // no storage
    }
  }

  async function set() {
    setting = true
    await act(() => api.setClock(new Date().toISOString()))
    await check()
    setting = false
  }

  function describe(ms: number) {
    const s = Math.round(Math.abs(ms) / 1000)
    const text =
      s >= 2 * 86400 ? `${Math.round(s / 86400)} days`
      : s >= 3600 ? `${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`
      : `${Math.floor(s / 60)} min ${s % 60} s`
    return `${text} ${ms > 0 ? 'ahead of' : 'behind'}`
  }
  const clock = (ms: number) => new Date(ms).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
</script>

{#if visible && status}
  <div class="clock" role="alert">
    <header>
      <strong>The mini PC's clock is wrong</strong>
      <span class="spacer"></span>
      <button class="close" title="Not now" aria-label="Not now" onclick={dismiss}>✕</button>
    </header>
    <p>
      It's {describe(ahead)} this device's: it says {clock(Date.now() + ahead)}, this device {clock(Date.now())}.
    </p>
    <p class="muted small">
      With no internet nothing corrects it, and targets' positions, twilight times and the mount's GoTos all depend on it.
    </p>
    {#if status.canSet}
      <div class="buttons">
        <button disabled={setting} onclick={set}>{setting ? 'Setting…' : 'Set it from this device'}</button>
        <button class="secondary" disabled={setting} onclick={dismiss}>Not now</button>
      </div>
    {:else if status.problem}
      <p class="muted small">{status.problem}.</p>
    {/if}
  </div>
{/if}

<style>
  .clock {
    position: fixed;
    left: 50%;
    bottom: 24px;
    transform: translateX(-50%);
    z-index: 45;
    width: min(480px, calc(100vw - 32px));
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 12px 14px;
    background: var(--panel);
    border: 1px solid var(--warn);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
  }
  header {
    display: flex;
    align-items: baseline;
    gap: 8px;
    color: var(--warn);
  }
  .spacer {
    flex: 1;
  }
  .close {
    background: none;
    border: none;
    color: var(--muted);
    padding: 0 2px;
    cursor: pointer;
  }
  p {
    margin: 0;
  }
  .small {
    font-size: 12px;
  }
  .buttons {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
</style>
