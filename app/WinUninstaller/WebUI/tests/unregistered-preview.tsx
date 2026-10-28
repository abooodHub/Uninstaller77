// Explicit development fixture. Not reachable from the production index or WebView bridge.
import { useState } from 'react'
import { UnregisteredDialog } from '../src/App'
import { words } from '../src/ui-strings'
const params = new URLSearchParams(location.search)
const language = params.get('lang') === 'en' ? 'en' : 'ar'
export function Preview() {
  const [review, setReview] = useState({ open: true, state: params.get('state') || 'Review', busy: ['Scanning', 'Restoring', 'Deleting'].includes(params.get('state') || ''),
    planId: '0123456789abcdef0123456789abcdef', app: {name:'Owned deployment fixture',icon:null}, root:'C:\\Owned Fixture\\Apps\\desktop\\release\\win-unpacked',
    target:'C:\\Owned Fixture\\Apps\\desktop\\release\\win-unpacked\\Owned.exe', kind:'ElectronBundle', count:230, bytes:345678901,
    error:params.get('state') === 'Unsupported' ? 'UnregisteredApp:MixedData' : '',
    deletedFiles:params.get('state') === 'Deleted' ? 230 : params.get('state') === 'DeleteIncomplete' ? 1 : 0,
    remainingFiles:params.get('state') === 'DeleteIncomplete' ? 229 : 0, unverifiedMissingFiles:0,
    canRetryDelete:params.get('state') === 'DeleteIncomplete', pendingPath:'C:\\Owned Fixture\\PendingRemoval\\owned',
    fromBackup:params.get('backup')==='true', pendingFolder:params.get('backup')==='true'?'Quarantine':'PendingRemoval',
    files:Array.from({length:200},(_,i)=>({path:i===0 ? 'Owned.exe' : 'resources\\owned-file-'+i+'.bin',bytes:1024*(i+1)})) })
  const [actions,setActions]=useState<string[]>([])
  return <div className="reduce-motion"><small style={{position:'fixed',bottom:2,left:8,fontSize:10,color:'#9aa7b6',zIndex:10000}}>ISOLATED UI FIXTURE · NO FILE OPERATIONS <output data-testid="fixture-actions">{actions.join(',')}</output></small>
    <UnregisteredDialog review={review} lang={words[language]} run={async (op,payload)=>{setActions(old=>[...old,op]); if(op==='confirmUnregistered'||op==='retryUnregisteredDelete'){if(payload?.permanent!==true||payload?.acknowledged!==true) return false; setReview(old=>({...old,state:'Deleted',deletedFiles:230,remainingFiles:0,canRetryDelete:false}));} return true}} />
  </div>
}
