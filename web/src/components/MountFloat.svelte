<script lang="ts">
  import { mountFloat } from '../lib/floats.svelte'
  import { nova } from '../lib/state.svelte'
  import FloatWindow from './FloatWindow.svelte'
  import MountModel from './MountModel.svelte'

  // The mount's 3D view popped out: a small window over every tab, to watch a slew or a meridian flip from anywhere.
  const layout = mountFloat.layout
  const state = $derived(nova.status?.mount?.connected ? nova.status.mount.state : null)
</script>

{#if layout.popped}
  <FloatWindow title={state ? `Mount, ${state.toLowerCase()}` : 'Mount'} {layout} save={mountFloat.save}>
    <MountModel />
  </FloatWindow>
{/if}
