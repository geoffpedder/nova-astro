/**
 * The optional password: whether NOVA asks for one, and whether this browser is signed in. Until it is, the page
 * shows the sign-in screen and doesn't connect for live updates. Any 401 from the server (a password set or
 * changed from elsewhere, or the session ran out) brings the sign-in screen back.
 */
export const auth = $state({
  checked: false,
  passwordSet: false,
  signedIn: true,
})

export async function checkAuth(): Promise<void> {
  try {
    const response = await fetch('/api/auth')
    if (response.ok) {
      const status: { passwordSet: boolean; signedIn: boolean } = await response.json()
      auth.passwordSet = status.passwordSet
      auth.signedIn = status.signedIn
    }
  } catch {
    // the server is unreachable: keep what we knew; the hub's retries show it's offline
  }
  auth.checked = true
}

/** A request was refused for want of a password. */
export function signedOut(): void {
  auth.passwordSet = true
  auth.signedIn = false
}
