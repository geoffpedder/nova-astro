import { api } from './api'
import type { PreviewMode } from './types'

/**
 * How frames are shown, the same in every viewer (the Image tab, its pop-out, the compact layout): debayered or
 * not, stretched or linear, and how big a preview to fetch. Remembered per browser.
 */
export const previewModes: { value: PreviewMode; label: string }[] = [
  { value: 'auto', label: 'Colour (auto)' },
  { value: 'mono', label: 'Mono (raw)' },
  { value: 'RGGB', label: 'Colour: RGGB' },
  { value: 'BGGR', label: 'Colour: BGGR' },
  { value: 'GRBG', label: 'Colour: GRBG' },
  { value: 'GBRG', label: 'Colour: GBRG' },
]

/** Preview resolution: the longest side in pixels, 0 = full sensor resolution. */
export const previewSizes = [
  { value: 800, label: '800 px', note: 'fastest' },
  { value: 1200, label: '1200 px', note: '' },
  { value: 1600, label: '1600 px', note: 'default' },
  { value: 2400, label: '2400 px', note: '' },
  { value: 0, label: 'Full resolution', note: 'slow over Wi-Fi' },
]

function load(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function save(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // just for this visit
  }
}

function loadMode(): PreviewMode {
  const saved = load('nova.previewMode')
  return previewModes.some((m) => m.value === saved) ? (saved as PreviewMode) : 'auto'
}

function loadSize(): number {
  const saved = Number(load('nova.previewSize') ?? 1600)
  return previewSizes.some((s) => s.value === saved) ? saved : 1600
}

export const preview = $state({
  /** "auto" debayers whenever the camera reports a Bayer pattern. */
  mode: loadMode(),
  stretch: load('nova.previewStretch') !== '0',
  size: loadSize(),
})

export function setPreviewMode(mode: PreviewMode): void {
  preview.mode = mode
  save('nova.previewMode', mode)
}

export function setPreviewStretch(stretch: boolean): void {
  preview.stretch = stretch
  save('nova.previewStretch', stretch ? '1' : '0')
}

export function setPreviewSize(size: number): void {
  preview.size = size
  save('nova.previewSize', String(size))
}

/** A frame's preview address with this browser's choices; size overrides the chosen resolution (a small pop-out). */
export function previewSrc(image: { id: number; capturedUtc: string }, size = preview.size): string {
  return api.previewUrl(image, preview.mode, preview.stretch, size)
}
