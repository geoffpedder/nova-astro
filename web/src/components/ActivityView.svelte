<script lang="ts">
  import { nova } from '../lib/state.svelte'

  // The Activity tab: the whole activity log: newest at the bottom, kept in view unless you've scrolled up to read older entries.
  let list: HTMLElement | undefined = $state()
  let problemsOnly = $state(false)
  let following = true

  const entries = $derived(problemsOnly ? nova.log.filter((e) => e.level !== 'info') : nova.log)

  function onscroll() {
    if (list) following = list.scrollHeight - list.scrollTop - list.clientHeight < 40
  }

  $effect(() => {
    void entries.length
    if (following) list?.scrollTo({ top: list.scrollHeight })
  })

  const time = (iso: string) => new Date(iso).toLocaleTimeString()
</script>

<div class="panel activity">
  <div class="head">
    <h2>Activity</h2>
    <label class="check"><input type="checkbox" bind:checked={problemsOnly} /> Warnings and errors only</label>
  </div>
  <ol bind:this={list} class="mono" {onscroll}>
    {#each entries as entry, i (i)}
      <li class={entry.level}><span class="muted">{time(entry.timeUtc)}</span> {entry.message}</li>
    {:else}
      <li class="muted">{problemsOnly ? 'No warnings or errors.' : 'Nothing yet.'}</li>
    {/each}
  </ol>
</div>

<style>
  .activity {
    display: flex;
    flex-direction: column;
    min-height: 0;
  }
  .head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 12px;
  }
  .check {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
  }
  ol {
    list-style: none;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    flex: 1;
    font-size: 12px;
  }
  li {
    padding: 1px 0;
    overflow-wrap: anywhere;
  }
  li.warn {
    color: var(--warn);
  }
  li.error {
    color: var(--danger);
  }
</style>
