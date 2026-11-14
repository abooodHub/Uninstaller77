import { useState } from 'react'
import { FileText, FolderOpen, Square } from 'lucide-react'
import type { InstallationReport, State } from '../host-contract'
import type { HostAction } from '../host-operations'
import { installationWords } from '../installation-strings'

export function InstallationPage({ page, language, run }: { page: State['installationPage']; language: State['language']; run: HostAction }) {
  const lang = installationWords[language]
  const [registryScope, setRegistryScope] = useState('')
  const active = page?.active
  return <>
    <header className="page-header workspace-page-header"><h1>{lang.title}</h1></header>
    <section className="apps-surface installation-surface" aria-label={lang.title}>
      <p>{lang.instructions}</p>
      <div className="toolbar catalog-toolbar">
        <label className="searchbox"><FileText size={15}/><input aria-label={lang.registry} dir="ltr" placeholder="Software\Vendor\Product"
          title={lang.registry} value={registryScope} maxLength={192} disabled={page?.busy} onChange={event => setRegistryScope(event.target.value)}/></label>
        {page?.busy ? <>
          <button className="button primary" disabled={page.finishing || page.cancelling || active?.state !== 'Monitoring'}
            onClick={() => void run('finishInstallationRecording', { id: active?.id })}><Square size={14}/>{lang.finish}</button>
          <button className="button quiet" disabled={page.finishing || page.cancelling}
            onClick={() => void run('cancelInstallationRecording')}>{lang.cancel}</button>
        </>
          : <button className="button primary" onClick={() => void run('startInstallationRecording', { registryScope })}><FolderOpen size={15}/>{lang.start}</button>}
      </div>
      <p>{lang.association}</p>
      <div className="windows-app-groups" aria-label={lang.logs}>
        {page?.busy && !active && <p role="status">{page.cancelling ? lang.states.Cancelling : lang.states.Capturing}</p>}
        {active && <InstallationLog report={active} lang={lang} open
          pendingState={page?.cancelling ? 'Cancelling' : page?.finishing ? 'Finishing' : undefined}
          onExport={page?.busy ? undefined : () => void run('exportInstallationRecording', { id: active.id })}/>}
        {page?.reports.map(report => <InstallationLog key={report.id} report={report} lang={lang}
          onExport={page.busy ? undefined : () => void run('exportInstallationRecording', { id: report.id })}/>)}
        {!active && !page?.busy && !page?.reports.length && <div className="empty-state">{lang.empty}</div>}
      </div>
    </section>
  </>
}

function InstallationLog({ report, lang, open, pendingState, onExport }: {
  report: InstallationReport; lang: typeof installationWords.en | typeof installationWords.ar; open?: boolean
  pendingState?: 'Cancelling' | 'Finishing'; onExport?: () => void
}) {
  const state = pendingState ?? report.state as keyof typeof lang.states
  return <details className="installation-log" open={open || undefined}>
    <summary><span><bdi>{report.program || report.root}</bdi></span><span>{lang.states[state] || state}</span></summary>
    <div className="installation-log-body">
      <p dir="ltr">{report.root}{report.registryScope && <> · {report.registryScope}</>}</p>
      <p role={open ? 'status' : undefined}>{lang.files} <bdi>{report.baselineFiles}</bdi> · {lang.keys} <bdi>{report.baselineKeys}</bdi> · {lang.changes} <bdi>{report.count}</bdi>
        {Boolean(report.linkedCount) && <> · {lang.linked} <bdi>{report.linkedCount}</bdi></>}
      </p>
      {onExport && <button className="button quiet" onClick={onExport}>{lang.export}</button>}
      {report.issues.map(issue => <p className="simple-result-reason" key={issue} dir="auto">{issue}</p>)}
      {report.changes.length > 0 && <>
        {report.count > report.changes.length && <p>{lang.latest} <bdi>{report.changes.length}/{report.count}</bdi></p>}
        <ul>{report.changes.map((item, index) => <li key={`${item.kind}:${item.path}:${item.at}:${index}`}>
          <bdi dir="ltr">{item.path}{item.view ? ` [${item.view}-bit]` : ''}</bdi>
          <small>{item.phase === 'During' ? lang.live : lang.comparison} · {item.kind === 'File' || item.kind === 'FileEvent' ? lang.file : lang.registryKey} · {lang.actions[item.change as keyof typeof lang.actions] || item.change} · {item.association === 'RegistrationLinked' ? lang.linked : lang.observed}</small>
        </li>)}</ul>
      </>}
    </div>
  </details>
}
