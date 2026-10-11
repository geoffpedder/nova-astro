<script lang="ts">
  import { mainView } from '../lib/inbox.svelte'
  import { nova } from '../lib/state.svelte'

  // The activity log's latest entry, on one line under the main view (in its warning colour, so nothing goes
  // unseen). The whole log is the Activity tab: click the line to open it.
  const latest = $derived(nova.log.length > 0 ? nova.log[nova.log.length - 1] : null)
  const time = (iso: string) => new Date(iso).toLocaleTimeString()
</script>

<button class="panel log" onclick={() => (mainView.view = 'activity')} title="Open the whole activity log">
  <span class="label">Activity</span>
  {#if latest}
    <span class="latest mono {latest.level}">{time(latest.timeUtc)} {latest.message}</span>
  {:else}
    <span class="latest muted">Nothing yet</span>
  {/if}
</button>

<style>
  .log {
    all: unset;
    box-sizing: border-box;
    display: flex;
    align-items: center;
    gap: 12px;
    min-width: 0;
    cursor: pointer;
    font-size: 12px;
  }
  .log:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }
  .label {
    flex: none;
    font-family: var(--font-mono);
    font-size: 10.5px;
    font-weight: 500;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    color: var(--muted);
  }
  .log:hover .label {
    color: var(--text);
  }
  .latest {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .latest.warn {
    color: var(--warn);
  }
  .latest.error {
    color: var(--danger);
  }
</style>
