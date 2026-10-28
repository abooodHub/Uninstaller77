// Dev/test entry only: never linked by the production index.html or copied to wwwroot.
import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import { ProgressDialog } from '../src/App'
import { words } from '../src/ui-strings'
import ownedIcon from '../src/assets/u77.png'

const params = new URLSearchParams(location.search)
const language = params.get('lang') === 'ar' ? 'ar' : 'en'
const mode = params.get('mode') || 'running'
const active = { id:'owned-missing',name:'Owned application — no icon',icon:null }
const owned = { id:'owned-logo',name:'Owned icon fixture',icon:ownedIcon }
const broken = { id:'owned-broken',name:'Owned corrupt image fixture',icon:'data:image/png;base64,invalid' }
const base = {
  isRunning:mode === 'running' || mode === 'review', needsReview:mode === 'review' || mode === 'recovered',
  total:4, processed:1, successful:1, failed:0, skipped:0, remaining:2, elapsed:'00:01:23',
  state:'Running',operationState:'AwaitingCompletion',app:active,
  status:language === 'ar' ? 'تعذر تأكيد انتهاء أداة الإزالة. الطابور متوقف للمراجعة؛ لن يبدأ البرنامج التالي.' : 'Completion is uncertain. The queue is paused for review; the next application will not start.',
  errorDetails:language === 'ar' ? 'دليل اصطناعي للاختبار فقط؛ لا يثبت رمز الخروج اكتمال الإزالة.' : 'Synthetic test evidence only. An exit code does not establish verified removal.',
  failureCode:'NeedsUserAttention',exitCode:0,
  queueItems:[{operationId:'1',app:owned,state:'Completed'},{operationId:'2',app:active,state:'AwaitingCompletion'},{operationId:'3',app:broken,state:'Idle'},{operationId:'4',app:{id:'owned-last',name:'Owned remaining fixture',icon:null},state:'Idle'}],
  results:[{app:owned,state:'Succeeded'}], logs:['Explicit isolated fixture. No bridge requests or uninstalls.'],
  review:{recovered:mode === 'recovered',canAcknowledge:mode === 'recovered',registrationState:params.get('registration') || 'NotChecked',queueId:'owned-fixture-review',reason:'Synthetic evidence only.',items:[{operationId:'owned-operation',name:'Owned fixture',lastState:'NeedsUserAttention',evidence:'Synthetic evidence preserved'}]}
}
if (mode === 'review' || mode === 'recovered') { base.operationState='NeedsUserAttention'; base.queueItems[1].state='NeedsUserAttention' }
if (!['review','recovered'].includes(mode)) base.errorDetails=''
if (mode === 'partial') {
  Object.assign(base,{state:'PartialFailure',isRunning:false,processed:2,successful:1,failed:1,skipped:2,remaining:0,operationState:'Failed',results:[{app:owned,state:'Succeeded'},{app:active,state:'Failed'},{app:broken,state:'Skipped'},{app:{id:'last',name:'Owned last fixture',icon:null},state:'Skipped'}]})
  base.queueItems[1].state='Failed';base.queueItems[2].state='Cancelled';base.queueItems[3].state='Cancelled'
}
if (params.get('rows') === '181') {
  base.total=181;base.remaining=179
  base.queueItems.push(...Array.from({length:177},(_,i)=>({operationId:'extra-'+i,app:{id:'extra-'+i,name:'Owned queue fixture '+(i+5),icon:null},state:'Idle'})))
}
export function Preview() {
  const [actions,setActions]=useState<string[]>([])
  const [previewOp,setPreviewOp]=useState(base)
  return <div className={params.get('motion') === 'reduce' ? 'reduce-motion' : ''}><aside style={{position:'fixed',insetInlineStart:8,bottom:2,zIndex:100,color:'#bbc4d0',fontSize:10}} data-testid="fixture-banner">ISOLATED UI TEST · NO UNINSTALLS <output data-testid="fixture-actions">{actions.join(',')}</output></aside>
    <ProgressDialog lang={words[language]} op={previewOp} run={async op=>{
      setActions(old=>[...old,op])
      if(op === 'reviewQueue' && previewOp.isRunning) setPreviewOp(old=>({...old,isRunning:false,review:{...old.review,recovered:true,canAcknowledge:true}}))
      return true
    }} /></div>
}
document.documentElement.lang=language
document.documentElement.dir=language === 'ar' ? 'rtl' : 'ltr'
const previewRoot = createRoot(document.getElementById('root')!)
previewRoot.render(<Preview />)
if (import.meta.hot) import.meta.hot.dispose(() => previewRoot.unmount())
