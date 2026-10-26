<script lang="ts">
  import { api } from '../lib/api'
  import { mainView } from '../lib/inbox.svelte'
  import { abortSequence, session } from '../lib/session.svelte'
  import { act } from '../lib/state.svelte'

  // The running sequence in a header: what it's doing, Pause or Resume, Abort, and the exposure's progress along the
  // bottom edge. Clicking it opens the sequence.
  const sequencer = $derived(session.sequencer)
  const seq = $derived(session.sequence)
  const progress = $derived(session.sequenceProgress)

  // The bar eases forward between progress messages, but jumps back to 0 when the next exposure starts instead of
  // sliding backwards.
  let back = $state(false)
  let last = 0
  $effect.pre(() => {
    const p = progress ?? 0
    back = p < last
    last = p
  })
</script>

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
        <button
          class="seq-control"
          disabled={seq.pauseRequested}
          title={seq.pauseRequested ? 'Pausing after this frame' : 'Pause after the current frame'}
          aria-label="Pause the sequence"
          onclick={() => act(api.sequencePause)}>⏸</button
        >
      {/if}
      <button class="seq-control abort" title="Abort the sequence" aria-label="Abort the sequence" onclick={abortSequence}>■</button>
    {/if}
    {#if progress != null}
      <span class="seq-progress" class:back style:width="{progress}%" aria-hidden="true"></span>
    {/if}
  </div>
{/if}

<style>
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
</style>
