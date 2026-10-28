import { createRoot } from 'react-dom/client'
import { Preview } from './unregistered-preview'
const language = new URLSearchParams(location.search).get('lang') === 'en' ? 'en' : 'ar'
document.documentElement.lang = language
document.documentElement.dir = language === 'ar' ? 'rtl' : 'ltr'
const root = createRoot(document.getElementById('root')!)
root.render(<Preview />)
if (import.meta.hot) import.meta.hot.dispose(() => root.unmount())
