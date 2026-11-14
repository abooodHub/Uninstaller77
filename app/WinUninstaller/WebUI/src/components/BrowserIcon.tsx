import { Globe2 } from 'lucide-react'
import chromeIcon from '../assets/browsers/chrome.png'
import edgeIcon from '../assets/browsers/edge.png'
import braveIcon from '../assets/browsers/brave.png'
import operaIcon from '../assets/browsers/opera.png?no-inline'
import vivaldiIcon from '../assets/browsers/vivaldi.png?no-inline'

// Opera Norway AS (brand.opera.com), Vivaldi Technologies (vivaldi.com/press, CC BY 4.0).
const browserIconFiles = new Map([
  ['google chrome', chromeIcon],
  ['microsoft edge', edgeIcon],
  ['brave', braveIcon],
  ['opera', operaIcon],
  ['vivaldi', vivaldiIcon],
])

export function BrowserIcon({ browser, className = '' }: { browser: string; className?: string }) {
  const key = browser.trim().toLowerCase()
  const file = browserIconFiles.get(key)
  const brandCls = key.replace(/\s+/g, '-')
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
