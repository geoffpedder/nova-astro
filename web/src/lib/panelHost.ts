import { getContext, setContext } from 'svelte'

/**
 * A layout that shows the equipment panels in its own containers (the compact layout's slide-out panels) instead of
 * as collapsible boxes in a column. A panel inside one drops its own header and box, and asks the host to show it
 * when something points at it (the setup guide). Without a host, panels are the desktop's boxes.
 */
export interface PanelHost {
  /** Bring the panel with this id into view. */
  show(id: string): void
}

const key = Symbol('panel-host')

export function setPanelHost(host: PanelHost): void {
  setContext(key, host)
}

export function getPanelHost(): PanelHost | undefined {
  return getContext<PanelHost | undefined>(key)
}
