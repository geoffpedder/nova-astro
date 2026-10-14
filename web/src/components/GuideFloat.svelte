<script lang="ts">
  import { guideFloat } from '../lib/floats.svelte'
  import { mainView } from '../lib/inbox.svelte'
  import { nova } from '../lib/state.svelte'
  import FloatWindow from './FloatWindow.svelte'
  import GuideChart from './GuideChart.svelte'

  // The guiding graph popped out, with the RMS: over every tab, whichever guider is in use.
  const layout = guideFloat.layout
  const guider = $derived(nova.status?.guider)
  const stats = $derived(guider?.stats)
  const unit = $derived(stats?.units === 'arcsec' ? '″' : ' px')
  const fmt = (v: number | null | undefined) => (v == null ? '—' : v.toFixed(2))
  const phase = $derived(guider?.settling ? 'Settling' : (guider?.state ?? 'Stopped'))
</script>

{#if layout.popped}
  <FloatWindow
    title={phase === 'Guiding' ? 'Guiding' : `Guiding, ${phase.toLowerCase()}`}
    {layout}
    save={guideFloat.save}
    expandTitle="Open the Guiding tab"
    onexpand={() => (mainView.view = nova.status?.guider?.backend === 'nova' ? 'guiding' : 'phd2')}
  >
    <div class="body">
      <div class="rms mono">
        <span class="ra">RA {fmt(stats?.rmsRa)}{unit}</span>
        <span class="dec">Dec {fmt(stats?.rmsDec)}{unit}</span>
        <span>Total {fmt(stats?.rmsTotal)}{unit}</span>
      </div>
      <div class="chart"><GuideChart history={guider?.history ?? []} {unit} fill /></div>
    </div>
  </FloatWindow>
{/if}

<style>
  .body {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 6px 8px 8px;
    aspect-ratio: 2.4;
  }
  .rms {
    display: flex;
    gap: 12px;
    font-size: 12px;
  }
  .chart {
    flex: 1;
    min-height: 0;
  }
  .ra {
    color: #60a5fa;
  }
  .dec {
    color: #f87171;
  }
  :global(:root[data-theme='night']) .ra {
    color: #a01010;
  }
  :global(:root[data-theme='night']) .dec {
    color: #ff3030;
  }
</style>
