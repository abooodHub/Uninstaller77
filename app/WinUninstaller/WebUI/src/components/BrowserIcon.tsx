import { Globe2 } from 'lucide-react'
import chromeIcon from '../assets/browsers/chrome.png'
import edgeIcon from '../assets/browsers/edge.png'
import braveIcon from '../assets/browsers/brave.png'

const browserIconFiles: Record<string, string> = {
  'Google Chrome': chromeIcon,
  'Microsoft Edge': edgeIcon,
  Brave: braveIcon
}

export function BrowserIcon({ browser, className = '' }: { browser: string; className?: string }) {
  const file = browserIconFiles[browser]
  const brandCls = browser.toLowerCase().replace(/\s+/g, '-')
  return (
    <span className={`browser-icon-wrap brand-${brandCls} ${className}`}>
      {file ? (
        <img className="browser-icon" src={file} width="20" height="20" alt={browser} />
      ) : (
        <Globe2 className="browser-icon browser-icon-fallback" size={20} aria-hidden="true" />
      )}
    </span>
  )
}
