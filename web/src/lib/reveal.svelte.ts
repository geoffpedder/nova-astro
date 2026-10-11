/**
 * The setup guide's buttons point at places elsewhere on the page: a panel to open and scroll to, and optionally a
 * part of it to open (the site editor, Drivers…, Camera → Optics). Whoever owns the place opens it and clears its
 * half of the request.
 */
export type RevealPart = 'site' | 'drivers' | 'optics'

export const reveal = $state<{ panel: string | null; part: RevealPart | null }>({ panel: null, part: null })

export function showPlace(panel: string, part: RevealPart | null = null): void {
  reveal.panel = panel
  reveal.part = part
}
