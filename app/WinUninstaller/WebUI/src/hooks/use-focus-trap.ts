import { useCallback, useLayoutEffect, useRef } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent } from 'react'

export function useFocusTrap<T extends HTMLElement>(active: boolean) {
  const ref = useRef<T>(null)
  useLayoutEffect(() => {
    if (!active || !ref.current) return
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const first = ref.current.querySelector<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])')
    ;(first || ref.current).focus()
    return () => { if (previous?.isConnected) previous.focus() }
  }, [active])
  const onKeyDown = useCallback((event: ReactKeyboardEvent<T>) => {
    if (!active || event.key !== 'Tab' || !ref.current) return
    const focusable = [...ref.current.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])')]
      .filter(item => item.getAttribute('aria-hidden') !== 'true')
    if (!focusable.length) { event.preventDefault(); ref.current.focus(); return }
    const first = focusable[0], last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
  }, [active])
  return [ref, onKeyDown] as const
}
