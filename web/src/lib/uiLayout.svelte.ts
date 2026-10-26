/**
 * Which of NOVA's two layouts this browser shows: the desktop one (panels beside the main view) or the compact one
 * (the image full screen, everything else floating over it, made for phones and tablets). "Automatic" picks compact
 * on touch screens, phones and iPads alike. Chosen under ⏻ and remembered per browser.
 */
export type LayoutChoice = 'auto' | 'desktop' | 'compact'

const Key = 'nova.layout'

function loadChoice(): LayoutChoice {
  try {
    const saved = localStorage.getItem(Key)
    if (saved === 'desktop' || saved === 'compact') return saved
  } catch {
    // storage unavailable: automatic
  }
  return 'auto'
}

// A touch screen as the main pointer: phones and tablets (a laptop with a touch screen still has a mouse or pad).
const touch = typeof matchMedia === 'function' ? matchMedia('(pointer: coarse)') : null

export const uiLayout = $state({ choice: loadChoice(), touch: touch?.matches ?? false })

touch?.addEventListener('change', (e) => (uiLayout.touch = e.matches))

/** Whether the compact layout is showing. */
export function isCompact(): boolean {
  return uiLayout.choice === 'compact' || (uiLayout.choice === 'auto' && uiLayout.touch)
}

export function setLayoutChoice(choice: LayoutChoice): void {
  uiLayout.choice = choice
  try {
    if (choice === 'auto') localStorage.removeItem(Key)
    else localStorage.setItem(Key, choice)
  } catch {
    // just for this visit
  }
}
