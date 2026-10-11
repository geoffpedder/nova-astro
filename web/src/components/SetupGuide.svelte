<script lang="ts">
  // The first-run guide: the steps to a working setup, each ticked from what NOVA can see, with a button that opens
  // the place where it's done; and what the mini PC is missing (as nova doctor would say), with the fix.
  import { api } from '../lib/api'
  import { mainView } from '../lib/inbox.svelte'
  import { showPlace } from '../lib/reveal.svelte'
  import { refreshSetup, setupGuide, setupSteps, type SetupStepKey } from '../lib/setup.svelte'
  import { act, nova } from '../lib/state.svelte'
  import Panel from './Panel.svelte'

  const steps = $derived(setupSteps())
  const required = $derived(steps.filter((s) => !s.optional))
  const doneCount = $derived(required.filter((s) => s.done).length)
  const complete = $derived(required.length > 0 && doneCount === required.length)
  const problems = $derived(setupGuide.check?.problems ?? [])
  const status = $derived(nova.status)
  const cameraReady = $derived(!!status?.camera?.connected && status.equipmentConnected)

  // "Take a frame and solve it": expose, then solve once the new image arrives.
  let solveAfter = $state<number | null>(null)
  $effect(() => {
    const image = status?.latestImage
    if (solveAfter === null || !image || image.id === solveAfter) return
    solveAfter = null
    act(() => api.solve())
  })
  async function frameAndSolve() {
    const before = status?.latestImage?.id ?? -1
    if (await act(() => api.expose(5, false))) solveAfter = before
  }

  function go(key: SetupStepKey) {
    if (key === 'site') showPlace('mount', 'site')
    else if (key === 'drivers') showPlace('equipment', 'drivers')
    else if (key === 'connect') {
      if ((status?.devices.length ?? 0) > 0 && !status?.equipmentConnected) act(api.connect)
      else showPlace('equipment')
    } else if (key === 'optics') showPlace('camera', 'optics')
    else if (key === 'guiding') {
      if (status?.guider.screenPort) mainView.view = 'phd2'
      else showPlace('guider')
    }
  }
  const actionLabel: Record<SetupStepKey, string> = {
    site: 'Set the site',
    drivers: 'Choose drivers',
    connect: 'Connect',
    optics: 'Set the optics',
    solve: '',
    guiding: 'Open PHD2',
  }

  async function putAway() {
    setupGuide.open = false
    if (!complete && (await act(() => api.setSetupHidden(true)))) await refreshSetup()
  }

  let copied = $state<string | null>(null)
  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text)
      copied = text
      setTimeout(() => (copied = null), 1500)
    } catch {
      // clipboard unavailable (plain http to the mini PC): the command stays selectable
    }
  }
</script>

<Panel id="setup" title="Getting started">
  {#snippet badge()}
    <span class="badge {complete ? 'ok' : 'warn'}">{complete ? 'Done' : `${doneCount} of ${required.length}`}</span>
  {/snippet}

  <ol class="steps">
    {#each steps as step, i (step.key)}
      <li class:done={step.done}>
        <span class="mark" aria-hidden="true">{step.done ? '✓' : step.optional ? '○' : i + 1}</span>
        <div class="text">
          <div class="title">
            {step.title}{#if step.optional}<span class="muted optional">optional</span>{/if}
            <span class="sr-only">{step.done ? 'done' : 'to do'}</span>
          </div>
          <div class="detail muted">{step.detail}</div>
        </div>
        {#if !step.done}
          {#if step.key === 'solve'}
            <div class="actions">
              <button class="secondary" disabled={!cameraReady || solveAfter !== null || status?.plateSolve.solving}
                title={cameraReady ? 'A 5 second exposure, then a plate solve' : 'Connect a camera first'}
                onclick={frameAndSolve}>{solveAfter !== null ? 'Exposing…' : 'Take a frame and solve'}</button>
              {#if status?.latestImage}
                <button class="secondary" disabled={status.plateSolve.solving} onclick={() => act(() => api.solve())}>Solve the latest</button>
              {/if}
            </div>
          {:else if (step.key !== 'optics' || status?.camera) && (step.key !== 'guiding' || status?.guider.screenPort || status?.guider.connected)}
            <button class="secondary" onclick={() => go(step.key)}>{actionLabel[step.key]}</button>
          {/if}
        {/if}
      </li>
    {/each}
  </ol>

  {#if problems.length > 0}
    <h3>On the mini PC</h3>
    <ul class="problems">
      {#each problems as problem (problem.text)}
        <li>
          <span class={problem.optional ? 'note' : 'bad'} aria-hidden="true">{problem.optional ? '!' : '✗'}</span>
          <div class="text">
            <div>{problem.text}</div>
            {#if problem.fix}
              <button class="fix mono" title="Copy this command" onclick={() => copy(problem.fix!)}>
                {problem.fix}{#if copied === problem.fix}<span class="muted copied">copied</span>{/if}
              </button>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
    <p class="muted hint">Run the commands on the mini PC; <span class="mono">nova doctor</span> there checks the same things and more.</p>
  {/if}

  <div class="footer">
    {#if complete}
      <span class="muted">All set. This guide stays under Setup in the header.</span>
    {:else}
      <span class="muted">It goes away by itself once the steps are done.</span>
    {/if}
    <button class="secondary" onclick={putAway}>{complete ? 'Close' : 'Put away'}</button>
  </div>
</Panel>

<style>
  .steps,
  .problems {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .steps li,
  .problems li {
    display: flex;
    align-items: flex-start;
    gap: 10px;
  }
  .steps li > button,
  .actions {
    flex-shrink: 0;
  }
  .actions {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .mark {
    flex-shrink: 0;
    width: 20px;
    height: 20px;
    border-radius: 50%;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 11px;
    border: 1px solid var(--panel-border);
    color: var(--muted);
  }
  .done .mark {
    border-color: var(--ok);
    color: var(--ok);
  }
  .text {
    flex: 1;
    min-width: 0;
  }
  .title {
    font-weight: 600;
  }
  .done .title {
    font-weight: 400;
  }
  .optional {
    font-weight: 400;
    font-size: 11px;
    margin-left: 6px;
  }
  .detail {
    font-size: 12px;
    margin-top: 2px;
  }
  h3 {
    font-size: 12px;
    margin: 14px 0 8px;
  }
  .bad {
    color: var(--danger);
  }
  .note {
    color: var(--warn);
  }
  .bad,
  .note {
    width: 20px;
    text-align: center;
    flex-shrink: 0;
  }
  .fix {
    all: unset;
    cursor: copy;
    display: block;
    margin-top: 4px;
    font-size: 11px;
    padding: 4px 6px;
    border-radius: 3px;
    background: var(--bg);
    overflow-wrap: anywhere;
    user-select: all;
  }
  .fix:focus-visible {
    outline: 2px solid var(--accent);
  }
  .copied {
    margin-left: 8px;
  }
  .hint {
    font-size: 11px;
    margin: 8px 0 0;
  }
  .footer {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 14px;
    font-size: 12px;
  }
  .footer span {
    flex: 1;
  }
  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
  }
</style>
