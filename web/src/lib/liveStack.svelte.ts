import { api } from './api'
import { ask } from './confirm.svelte'
import { imageView, setStackView } from './floats.svelte'
import { act, nova } from './state.svelte'

/**
 * Live stacking as every layout shows it: the switch, the stack building up, and Save and Reset. Whether the stack or
 * the latest frame is on screen is imageView.stack (remembered per browser, followed by the pop-out).
 */
class LiveStackView {
  readonly status = $derived(nova.status?.liveStack ?? null)
  /** The stack last added to. */
  readonly current = $derived(this.status?.current ?? null)
  /** The stack is what's on screen (chosen, and it has a frame). */
  readonly showing = $derived(imageView.stack && !!this.current && this.current.frames > 0)
  /** What the last Save said. */
  note = $state<string | null>(null)
  saving = $state(false)

  async setEnabled(on: boolean): Promise<void> {
    // The stack is shown as soon as it has a frame.
    if ((await act(() => api.setLiveStack(on))) && on) setStackView(true)
  }

  async save(): Promise<void> {
    const stack = this.current
    if (!stack) return
    this.saving = true
    try {
      await act(async () => (this.note = `Saved ${await api.saveLiveStack(stack.key)}`))
    } finally {
      this.saving = false
    }
  }

  async reset(): Promise<void> {
    const stack = this.current
    if (!stack) return
    const ok = await ask(`Start ${stack.label} again?`, {
      message: `The ${stack.frames} frames stacked so far are dropped from the stack (the frames themselves stay saved). Save it first to keep it.`,
      confirm: 'Start again',
      danger: true,
    })
    if (ok && (await act(() => api.resetLiveStack(stack.key)))) this.note = null
  }
}

export const liveStack = new LiveStackView()

/** A length of time, e.g. "45 s", "12 min 30 s", "2 h 05 min". */
export function span(seconds: number): string {
  const s = Math.round(seconds)
  if (s >= 3600) return `${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`
  if (s >= 60) return `${Math.floor(s / 60)} min ${s % 60} s`
  return `${s} s`
}
