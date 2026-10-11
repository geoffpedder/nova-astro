<script lang="ts">
  // Turning the camera by hand to the planned framing angle: shown over every tab, since you may be at the scope
  // with a phone. Measuring → "turn it 12° clockwise" → Check again, until it's within a degree.
  import { api } from '../lib/api'
  import { act, nova } from '../lib/state.svelte'

  const r = $derived(nova.status?.plateSolve?.rotation ?? null)
  let dismissed = $state(false)
  let wasRunning = false
  $effect(() => {
    const running = r?.running ?? false
    if (running && !wasRunning) dismissed = false // a new run shows again
    wasRunning = running
  })
  const visible = $derived(!!r && (r.running || !dismissed))

  // The dial, as seen from behind the camera: the plan straight up, the camera where it is now, so the arrow turns
  // the way the words say.
  const turn = $derived(r?.turnDegrees != null ? Math.abs(r.turnDegrees) : 0)
  const now = $derived(r?.direction === 'clockwise' ? -turn : turn)
  const arc = $derived.by(() => {
    const radius = 44
    const point = (deg: number) => {
      const t = ((deg - 90) * Math.PI) / 180
      return `${(60 + radius * Math.cos(t)).toFixed(1)} ${(60 + radius * Math.sin(t)).toFixed(1)}`
    }
    return `M ${point(now)} A ${radius} ${radius} 0 0 ${now > 0 ? 0 : 1} ${point(0)}`
  })
</script>

{#if visible && r}
  <div class="rotation" role="status" aria-live="polite">
    <header>
      <strong>Camera rotation{#if r.rotator} with {r.rotator}{/if}</strong>
      <span class="muted small">Plan {r.plannedAngle.toFixed(1)}°{#if r.cameraAngle != null}, camera {r.cameraAngle.toFixed(1)}°{/if}</span>
      <span class="spacer"></span>
      {#if !r.running}
        <button class="close" title="Dismiss" aria-label="Dismiss" onclick={() => (dismissed = true)}>✕</button>
      {/if}
    </header>

    {#if r.waiting && r.turnDegrees != null}
      <div class="ask">
        <svg viewBox="0 0 120 120" width="112" height="112" aria-hidden="true">
          <circle cx="60" cy="60" r="56" class="ring" />
          <rect x="38" y="44" width="44" height="32" class="planned" />
          <line x1="60" y1="44" x2="60" y2="30" class="planned" />
          <g transform="rotate({now} 60 60)">
            <rect x="38" y="44" width="44" height="32" class="camera" />
            <line x1="60" y1="44" x2="60" y2="30" class="camera" />
          </g>
          {#if turn >= 1}<path d={arc} class="arrow" marker-end="url(#rotation-head)" />{/if}
          <defs>
            <marker id="rotation-head" viewBox="0 0 10 10" refX="5" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse">
              <path d="M0 0 L10 5 L0 10 z" class="head" />
            </marker>
          </defs>
        </svg>
        <div class="words">
          <p class="big">Turn the camera {turn.toFixed(1)}° {r.direction}</p>
          <p class="muted small">
            as seen from behind the camera, looking up the telescope{#if r.upsideDown}; that frames the plan upside down (the same sky){/if}.
            {#if r.fromSequence}The sequence waits up to 20 minutes, then carries on as it is.{/if}
          </p>
        </div>
      </div>
      <div class="buttons">
        <button onclick={() => act(() => api.rotationTurned())}>Turned it: check again</button>
        <button class="secondary" onclick={() => act(() => api.rotationKeep())}>Keep it as it is</button>
        {#if !r.fromSequence}<button class="secondary" onclick={() => act(() => api.rotationCancel())}>Cancel</button>{/if}
      </div>
    {:else}
      <p class="status">
        {#if r.running}<span class="spinner" aria-hidden="true"></span>{/if}
        {r.message}
      </p>
      {#if r.running && !r.fromSequence}
        <div class="buttons"><button class="secondary" onclick={() => act(() => api.rotationCancel())}>Cancel</button></div>
      {/if}
    {/if}
  </div>
{/if}

<style>
  .rotation {
    position: fixed;
    left: 50%;
    bottom: 24px;
    transform: translateX(-50%);
    z-index: 45;
    width: min(460px, calc(100vw - 32px));
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 12px 14px;
    background: var(--panel);
    border: 1px solid var(--accent);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 0.45);
  }
  header {
    display: flex;
    align-items: baseline;
    gap: 8px;
  }
  .spacer {
    flex: 1;
  }
  .small {
    font-size: 12px;
  }
  .close {
    background: none;
    border: none;
    color: var(--muted);
    padding: 0 2px;
    cursor: pointer;
  }
  .ask {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .words p {
    margin: 0;
  }
  .big {
    font-size: 20px;
    font-weight: 700;
    color: var(--accent);
    margin-bottom: 4px !important;
  }
  .buttons {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }
  .status {
    margin: 0;
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ring {
    fill: none;
    stroke: var(--panel-border);
    stroke-width: 1.5;
  }
  .planned {
    fill: none;
    stroke: var(--accent);
    stroke-width: 2;
    stroke-dasharray: 4 3;
  }
  .camera {
    fill: none;
    stroke: var(--text);
    stroke-width: 2.5;
  }
  .arrow {
    fill: none;
    stroke: var(--accent);
    stroke-width: 2.5;
  }
  .head {
    fill: var(--accent);
  }
  .spinner {
    width: 12px;
    height: 12px;
    border: 2px solid var(--panel-border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.9s linear infinite;
    flex: none;
  }
  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }
  @media (max-width: 480px) {
    .ask svg {
      width: 84px;
      height: 84px;
    }
    .big {
      font-size: 17px;
    }
  }
</style>
