<script lang="ts">
  import { onDestroy } from 'svelte'
  import { api } from '../lib/api'
  import { act, mountLock, nova } from '../lib/state.svelte'
  import type { MotionDirection } from '../lib/types'

  // Hold a button to move; release to stop. While held, the browser repeats "still moving" so the server's
  // dead-man's switch keeps going; if this page stops (closed tab, lost Wi-Fi) the mount stops within ~1 s.
  const mount = $derived(nova.status?.mount)
  // Not while parked, and not while an exposure or a sequence would be spoiled.
  const locked = $derived(mountLock())
  const ready = $derived((mount?.connected ?? false) && mount?.state !== 'Parked' && !locked)
  const rates = $derived(mount?.slewRates ?? [])
  const moving = $derived(new Set(mount?.moving ?? []))

  let held: MotionDirection | null = $state(null)
  let repeat: ReturnType<typeof setInterval> | null = null

  async function press(direction: MotionDirection, event: PointerEvent) {
    if (!ready) return
    ;(event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId)
    release()
    held = direction
    if (!(await act(() => api.mountMotion(direction, true)))) {
      held = null
      return
    }
    repeat = setInterval(() => {
      if (held) api.mountMotion(held, true).catch(() => release())
    }, 400)
  }

  function release() {
    if (repeat) clearInterval(repeat)
    repeat = null
    if (held) {
      const direction = held
      held = null
      api.mountMotion(direction, false).catch(() => {})
    }
  }

  const stopAll = () => {
    release()
    act(() => api.mountMotion(null, false))
  }

  onDestroy(release)

  // Arrow keys while a direction button has focus.
  const keys: Record<string, MotionDirection> = { ArrowUp: 'North', ArrowDown: 'South', ArrowLeft: 'East', ArrowRight: 'West' }
  function keydown(e: KeyboardEvent) {
    const direction = keys[e.key]
    if (!direction || e.repeat) return
    e.preventDefault()
    press(direction, new PointerEvent('pointerdown'))
  }
  function keyup(e: KeyboardEvent) {
    if (keys[e.key]) release()
  }
</script>

<svelte:window onblur={release} />

<div class="manual">
  <div class="pad" role="group" aria-label="Move the mount">
    {#each [['', 'North', ''], ['East', 'stop', 'West'], ['', 'South', '']] as row, r (r)}
      {#each row as cell, c (c)}
        {#if cell === 'stop'}
          <button class="stop" title="Stop all motion" onclick={stopAll} disabled={!mount?.connected}>■</button>
        {:else if cell}
          {@const d = cell as MotionDirection}
          <button
            class="dir"
            class:active={held === d || moving.has(d)}
            disabled={!ready}
            title={locked ?? `Hold to move ${d.toLowerCase()} (arrow keys work too)`}
            onpointerdown={(e) => press(d, e)}
            onpointerup={release}
            onpointercancel={release}
            onlostpointercapture={release}
            onkeydown={keydown}
            onkeyup={keyup}
            oncontextmenu={(e) => e.preventDefault()}>{d[0]}</button
          >
        {:else}
          <span></span>
        {/if}
      {/each}
    {/each}
  </div>
  <label class="rate">
    Speed
    <select
      value={mount?.slewRate ?? ''}
      disabled={!mount?.connected || rates.length === 0}
      onchange={(e) => act(() => api.mountRate(e.currentTarget.value))}
    >
      {#each rates as r (r.name)}<option value={r.name}>{r.label}</option>{/each}
    </select>
  </label>
  {#if mount?.state === 'Parked'}<p class="muted note">Unpark to move by hand.</p>
  {:else if locked && mount?.connected}<p class="note lock">🔒 {locked}</p>{/if}
</div>

<style>
  .manual {
    display: flex;
    align-items: center;
    gap: 14px;
    margin: 4px 0 10px;
  }
  .pad {
    display: grid;
    grid-template-columns: repeat(3, 36px);
    grid-template-rows: repeat(3, 32px);
    gap: 4px;
    outline: none;
  }
  .dir,
  .stop {
    padding: 0;
    font-weight: 600;
    touch-action: none;
    user-select: none;
  }
  .dir.active {
    background: var(--warn);
    color: var(--accent-text);
  }
  .stop {
    background: var(--danger);
    color: #fff;
    font-size: 11px;
  }
  .rate {
    display: grid;
    gap: 2px;
    font-size: 12px;
    color: var(--muted);
  }
  .rate select {
    color: var(--text);
  }
  .note {
    font-size: 11px;
    margin: 0;
  }
  .lock {
    color: var(--warn);
  }
</style>
