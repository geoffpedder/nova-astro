/**
 * The page's layout on this browser: whether the panel column on the left is open. Folding it gives the main view
 * (image, targets, PHD2…) the room on a tablet. Remembered per browser.
 */
function load(key: string): boolean {
  try {
    return localStorage.getItem(key) !== '0'
  } catch {
    return true
  }
}

export const layout = $state({ panels: load('nova.layout.panels') })

export function setPanels(open: boolean): void {
  layout.panels = open
  try {
    localStorage.setItem('nova.layout.panels', open ? '1' : '0')
  } catch {
    // storage unavailable: just for this visit
  }
}
