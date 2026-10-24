<script lang="ts">
  import { api } from '../lib/api'
  import { download } from '../lib/download'
  import type { SystemStats, TerminalStatus } from '../lib/types'

  // The mini PC at a glance (CPU, memory, temperature, disks), read every few seconds while the Activity tab shows.
  let { active }: { active: boolean } = $props()
  let stats = $state<SystemStats | null>(null)
  let failed = $state(false)

  $effect(() => {
    if (!active) return
    let stop = false
    const read = async () => {
      try {
        stats = await api.systemStats()
        failed = false
      } catch {
        failed = true
      }
    }
    read()
    const timer = setInterval(() => !stop && read(), 4000)
    return () => {
      stop = true
      clearInterval(timer)
    }
  })

  const gb = (bytes: number) => (bytes >= 100 * 1024 ** 3 ? (bytes / 1024 ** 3).toFixed(0) : (bytes / 1024 ** 3).toFixed(1))
  const mb = (bytes: number) => `${Math.round(bytes / 1024 ** 2)} MB`
  const level = (percent: number) => (percent >= 90 ? 'danger' : percent >= 75 ? 'warn' : 'ok')
  function uptime(seconds: number) {
    const d = Math.floor(seconds / 86400)
    const h = Math.floor((seconds % 86400) / 3600)
    const m = Math.floor((seconds % 3600) / 60)
    return d > 0 ? `${d} d ${h} h` : h > 0 ? `${h} h ${m} min` : `${m} min`
  }
  // A terminal on the mini PC (ttyd through NOVA), below the stats when opened. Only behind NOVA's password.
  let terminal = $state<TerminalStatus | null>(null)
  let terminalOpen = $state(load('nova.terminalOpen') === '1')
  let terminalTall = $state(false)
  let terminalHeight = $state(Number(load('nova.terminalHeight')) || 280)
  $effect(() => {
    if (!active) return
    api.terminal().then((t) => (terminal = t)).catch(() => (terminal = null))
  })
  function load(key: string): string | null {
    try {
      return localStorage.getItem(key)
    } catch {
      return null
    }
  }
  function keep(key: string, value: string) {
    try {
      localStorage.setItem(key, value)
    } catch {
      // just for this visit
    }
  }
  // Everything needed to look into a problem, in one zip to download (or send on): logs, nova doctor, the settings.
  let preparing = $state(false)
  let diagnosticsError = $state<string | null>(null)
  async function saveDiagnostics() {
    preparing = true
    diagnosticsError = null
    try {
      await download('/api/diagnostics', 'nova-diagnostics.zip')
    } catch (e) {
      diagnosticsError = `Couldn't prepare the diagnostics: ${e instanceof Error ? e.message : e}`
    } finally {
      preparing = false
    }
  }

  function toggleTerminal() {
    terminalOpen = !terminalOpen
    keep('nova.terminalOpen', terminalOpen ? '1' : '0')
  }
  // Its height, dragged at the box's bottom edge: remembered per browser.
  function resized(node: HTMLElement) {
    const observer = new ResizeObserver(() => {
      if (!terminalTall && node.offsetHeight > 80) {
        terminalHeight = node.offsetHeight
        keep('nova.terminalHeight', String(terminalHeight))
      }
    })
    observer.observe(node)
    return { destroy: () => observer.disconnect() }
  }
  const memPercent = $derived(stats?.memoryUsedBytes != null && stats.memoryTotalBytes ? (100 * stats.memoryUsedBytes) / stats.memoryTotalBytes : null)
</script>

<div class="panel machine">
  <div class="head">
    <h2>Mini PC</h2>
    {#if stats}<span class="muted small">{stats.host}{stats.uptimeSeconds != null ? `, up ${uptime(stats.uptimeSeconds)}` : ''}</span>{/if}
    <span class="gap"></span>
    {#if terminalOpen && terminal?.available}
      <button class="secondary small-button" onclick={() => (terminalTall = !terminalTall)} aria-pressed={terminalTall}
        title={terminalTall ? 'Back to its own size' : 'Taller'}>{terminalTall ? '⤡' : '⤢'}</button>
    {/if}
    <button class="secondary small-button" disabled={preparing} onclick={saveDiagnostics}
      title="One zip to look into a problem, or to send with a bug report: the logs (NOVA's, the drivers', the USB messages), nova doctor, the system and NOVA's settings. Passwords, tokens and your sites' coordinates are left out."
      >{preparing ? 'Preparing…' : 'Diagnostics'}</button
    >
    <button class="secondary small-button" class:on={terminalOpen} aria-pressed={terminalOpen} onclick={toggleTerminal}
      title="A terminal on the mini PC: run commands (nova doctor, journalctl…) as NOVA's user">Terminal</button>
  </div>
  {#if diagnosticsError}<p class="small bad">{diagnosticsError}</p>{/if}
  {#if terminalOpen}
    {#if terminal?.available}
      <div class="terminal" class:tall={terminalTall} style:height={terminalTall ? '70vh' : `${terminalHeight}px`} use:resized>
        <iframe src="/terminal/" title="Terminal on the mini PC"></iframe>
      </div>
    {:else}
      <p class="muted small">{terminal?.problem ?? 'Checking the terminal…'}</p>
    {/if}
  {/if}
  {#if stats}
    <div class="rows mono">
      <span class="label">CPU</span>
      {#if stats.cpuPercent != null}
        <span class="bar"><span class={level(stats.cpuPercent)} style:width="{stats.cpuPercent}%"></span></span>
        <span>{stats.cpuPercent.toFixed(0)}% of {stats.cores} cores{stats.load != null ? `, load ${stats.load.toFixed(2)}` : ''}{stats.cpuTemperature != null ? `, ${stats.cpuTemperature.toFixed(0)} °C` : ''}</span>
      {:else}
        <span class="bar"></span><span class="muted">{stats.cores} cores (usage shown on the mini PC's Linux)</span>
      {/if}

      <span class="label">Memory</span>
      {#if memPercent != null}
        <span class="bar"><span class={level(memPercent)} style:width="{memPercent}%"></span></span>
        <span>{gb(stats.memoryUsedBytes!)} of {gb(stats.memoryTotalBytes!)} GB used ({memPercent.toFixed(0)}%), NOVA {mb(stats.novaMemoryBytes)}</span>
      {:else}
        <span class="bar"></span><span>{stats.memoryTotalBytes ? `${gb(stats.memoryTotalBytes)} GB, ` : ''}NOVA {mb(stats.novaMemoryBytes)}</span>
      {/if}

      {#each stats.disks as disk (disk.label)}
        {@const used = disk.totalBytes > 0 ? (100 * (disk.totalBytes - disk.freeBytes)) / disk.totalBytes : 0}
        <span class="label" title={disk.path}>{disk.label}</span>
        <span class="bar"><span class={level(used)} style:width="{used}%"></span></span>
        <span title={disk.path}>{gb(disk.freeBytes)} GB free of {gb(disk.totalBytes)} GB</span>
      {/each}
    </div>
  {:else}
    <p class="muted small">{failed ? "Couldn't read the mini PC's stats." : 'Reading…'}</p>
  {/if}
</div>

<style>
  .machine {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .head {
    display: flex;
    align-items: baseline;
    gap: 12px;
  }
  .gap {
    flex: 1;
  }
  .small-button {
    font-size: 12px;
    padding: 2px 10px;
  }
  .bad {
    color: var(--danger);
    margin: 4px 0 0;
  }
  .small-button.on {
    color: var(--accent);
    border-color: var(--accent);
  }
  /* The terminal: its height dragged at the bottom edge. */
  .terminal {
    resize: vertical;
    overflow: hidden;
    min-height: 120px;
    max-height: 85vh;
    border: 1px solid var(--panel-border);
    border-radius: var(--radius);
    background: #141414;
  }
  .terminal.tall {
    resize: none;
  }
  .terminal iframe {
    display: block;
    width: 100%;
    height: 100%;
    border: 0;
  }
  :global(:root[data-theme='night']) .terminal iframe {
    filter: grayscale(1) sepia(1) saturate(6) hue-rotate(-50deg) brightness(0.8);
  }
  .small {
    font-size: 12px;
    margin: 0;
  }
  .rows {
    display: grid;
    grid-template-columns: auto minmax(60px, 160px) 1fr;
    gap: 4px 10px;
    align-items: center;
    font-size: 12px;
  }
  .label {
    color: var(--muted);
  }
  .bar {
    height: 6px;
    background: var(--input);
    border-radius: 3px;
    overflow: hidden;
  }
  .bar > span {
    display: block;
    height: 100%;
    transition: width 0.6s ease;
  }
  .bar .ok {
    background: var(--ok);
  }
  .bar .warn {
    background: var(--warn);
  }
  .bar .danger {
    background: var(--danger);
  }
  @media (max-width: 600px) {
    .rows {
      grid-template-columns: auto 1fr;
    }
    .rows .bar {
      display: none;
    }
  }
</style>
