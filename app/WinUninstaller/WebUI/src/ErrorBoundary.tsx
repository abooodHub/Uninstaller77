import { Component } from 'react'
import type { ErrorInfo, ReactNode } from 'react'

type Props = { children: ReactNode }
type BoundaryState = { failed: boolean }

export default class ErrorBoundary extends Component<Props, BoundaryState> {
  state: BoundaryState = { failed: false }

  static getDerivedStateFromError(): BoundaryState {
    return { failed: true }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Uninstaller 77 interface render failed', error, info.componentStack)
  }

  render() {
    if (!this.state.failed) return this.props.children
    const arabic = typeof document !== 'undefined' && document.documentElement.lang === 'ar'
    return <main className="fatal-ui-error" role="alert" dir={arabic ? 'rtl' : 'ltr'}>
      <h1>{arabic ? 'تعذر عرض واجهة Uninstaller 77' : 'Uninstaller 77 could not display its interface'}</h1>
      <p>{arabic ? 'لم تبدأ أي عملية إزالة من هذه الشاشة. أعد تحميل الواجهة، ثم راجع السجل المحلي إذا استمر الخطأ.' : 'No uninstall was started from this screen. Reload the interface, then review the local log if the problem continues.'}</p>
      <button type="button" className="button primary" onClick={() => window.location.reload()}>
        {arabic ? 'إعادة تحميل الواجهة' : 'Reload interface'}
      </button>
    </main>
  }
}
