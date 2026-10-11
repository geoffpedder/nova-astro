// NOVA's own confirmation dialog (styled like the rest of the page, red in night mode) instead of the
// browser's confirm(). One at a time: ConfirmDialog in App shows whatever is asked here.

interface Request {
  title: string
  message: string | null
  confirm: string
  danger: boolean
  resolve: (ok: boolean) => void
}

export const confirmation = $state<{ request: Request | null }>({ request: null })

/** Asks a yes/no question; resolves true if the user confirms. */
export function ask(title: string, options: { message?: string; confirm?: string; danger?: boolean } = {}): Promise<boolean> {
  confirmation.request?.resolve(false) // a newer question replaces an unanswered one
  return new Promise((resolve) => {
    confirmation.request = {
      title,
      message: options.message ?? null,
      confirm: options.confirm ?? 'OK',
      danger: options.danger ?? false,
      resolve,
    }
  })
}

export function answer(ok: boolean): void {
  const request = confirmation.request
  confirmation.request = null
  request?.resolve(ok)
}
