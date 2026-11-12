import { api } from './api'
import { act, nova } from './state.svelte'

/**
 * Taking a frame by hand, from the Camera panel or the Image tab's toolbar: one set of settings for both, so the
 * toolbar's Expose takes exactly what the Camera panel shows.
 */
export const exposure = $state({ duration: 5, save: true, loop: false })

/**
 * lastImageId: the image that was latest when the last exposure started (the next one starts when a newer one arrives).
 * active: this browser started the frames being taken (Expose, the shutter), and they haven't been stopped: only then
 * does a new frame start another, so a sequence's frames, or another device's, never do.
 */
export const looping = { lastImageId: undefined as number | undefined, active: false }

/** Live stacking is on (the Image tab's switch, the compact layout's mode): it takes frame after frame by itself. */
export const liveStacking = () => !!nova.status?.liveStack?.enabled

/** Whether a frame is followed by another: Loop is on, or live stacking (a stack of one frame isn't one). */
export const repeats = () => exposure.loop || liveStacking()

export function expose(): void {
  looping.lastImageId = nova.image?.id
  looping.active = true
  act(() => api.expose(exposure.duration, exposure.save))
}

export function abortExposure(): void {
  exposure.loop = false
  looping.active = false
  act(api.abortExposure)
}
