import { useState } from 'react'
import { Check, CircleAlert, Package } from 'lucide-react'

interface AppIconProps {
  app: { name: string; icon?: string | null }
  small?: boolean
  variant?: 'list' | 'details' | 'progress' | 'small' | 'default'
  selected?: boolean
  isProcessing?: boolean
  operationState?: string
  sharedTransition?: boolean
  className?: string
  fallbackLabel?: string
}

export function AppIcon({
  app,
  small = false,
  variant = 'default',
  selected = false,
  isProcessing = false,
  operationState,
  sharedTransition = false,
  className = '',
  fallbackLabel = ''
}: AppIconProps) {
  const [failedSource, setFailedSource] = useState<string | null>(null)
  const hasIcon = Boolean(app.icon && app.icon !== failedSource)
  const effectiveVariant = small ? 'small' : variant
  const verifiedIcon = operationState === 'Succeeded' || operationState === 'Completed'
  const stateCls = operationState ? `state-${operationState.toLowerCase()}` : ''

  return (
    <span
      className={`app-icon animated-icon-container variant-${effectiveVariant} ${selected ? 'is-selected' : ''} ${isProcessing ? 'is-processing' : ''} ${sharedTransition ? 'shared-icon-transition' : ''} ${stateCls} ${className}`}
      data-variant={effectiveVariant}
      data-selected={selected ? 'true' : 'false'}
    >
      {(effectiveVariant === 'progress' || isProcessing) && (
        <svg className="icon-processing-ring" viewBox="0 0 48 48" aria-hidden="true">
          <circle className="processing-track-bg" cx="24" cy="24" r="21" fill="none" strokeWidth="2.5" />
          <circle
            className={`processing-track-fg ${isProcessing ? 'spinning' : ''} ${stateCls}`}
            cx="24"
            cy="24"
            r="21"
            fill="none"
            strokeWidth="2.5"
            strokeDasharray="131.95"
            strokeDashoffset={isProcessing ? '40' : '0'}
          />
        </svg>
      )}
      <span className="animated-icon-inner" data-icon-source={hasIcon ? 'application' : 'unavailable'}>
        {hasIcon ? (
          <img className="app-icon-img" src={app.icon!} alt="" loading={effectiveVariant === 'progress' || effectiveVariant === 'details' ? 'eager' : 'lazy'} onError={() => setFailedSource(app.icon || null)} />
        ) : <span className="app-icon-fallback" title={fallbackLabel || undefined} role={fallbackLabel ? 'img' : undefined} aria-label={fallbackLabel || undefined}><Package aria-hidden="true" strokeWidth={1.5} /></span>}
      </span>
      {effectiveVariant === 'progress' && !isProcessing && operationState && (
        <span className={`icon-state-badge ${verifiedIcon ? 'success' : operationState === 'Failed' ? 'failure' : 'neutral'}`}>
          {verifiedIcon ? <Check size={11} strokeWidth={3} /> : <CircleAlert size={11} strokeWidth={3} />}
        </span>
      )}
    </span>
  )
}
