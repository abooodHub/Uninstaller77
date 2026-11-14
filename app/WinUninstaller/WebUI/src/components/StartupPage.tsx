import { Fragment, useId, useState } from 'react'
import { ChevronDown, CircleAlert, Database, ExternalLink, FolderOpen, LoaderCircle, Power, RefreshCw, Search, UserRound, UsersRound } from 'lucide-react'
import type { StartupPageState } from '../host-contract'
import type { HostAction } from '../host-operations'
import type { words } from '../ui-strings'
import { AppIcon } from './AppIcon'

type Scope = 'All' | StartupPageState['items'][number]['scope']
type Selection = { snapshotId: string | null | undefined; indices: ReadonlySet<number> }

export function StartupPage({ page, lang, run }: { page: StartupPageState; lang: typeof words.en; run: HostAction }) {
  const [search, setSearch] = useState('')
  const [scope, setScope] = useState<Scope>('All')
  const [expanded, setExpanded] = useState<string | null>(null)
  const [selection, setSelection] = useState<Selection>({ snapshotId: undefined, indices: new Set() })
  const [requesting, setRequesting] = useState(false)
  const detailsId = useId()
  const query = search.trim().toLocaleLowerCase()
  const startup = lang.startup
  const entries = page.items.map((item, index) => ({ ...item, index, sourceLabel: item.source === 'Run' ? startup.runKey : startup.folder,
    scopeLabel: item.scope === 'User' ? startup.user : startup.machine }))
    .filter(item => scope === 'All' || item.scope === scope)
    .filter(item => [item.name, item.sourceLabel, item.scopeLabel, item.location, item.executable ?? '']
      .some(field => field.toLocaleLowerCase().includes(query)))
  const incomplete = page.failedSources > 0
  const busy = requesting || page.loading
  const canBatch = Boolean(page.snapshotId) && !page.failed
  const canChange = (entry: StartupPageState['items'][number]) => entry.scope === 'User' && Boolean(entry.changeToken) &&
    (entry.status === 'Enabled' || entry.status === 'Disabled')
  const eligible = canBatch ? entries.filter(canChange) : []
  const selectionIsCurrent = selection.snapshotId === page.snapshotId
  const chosen = selectionIsCurrent ? eligible.filter(entry => selection.indices.has(entry.index)) : []
  const allSelected = eligible.length > 0 && chosen.length === eligible.length
  const enabledShown = eligible.filter(entry => entry.status === 'Enabled')
  const enabledSelected = chosen.filter(entry => entry.status === 'Enabled')
  const disabledSelected = chosen.filter(entry => entry.status === 'Disabled')
  const setSelected = (items: typeof entries) => setSelection({ snapshotId: page.snapshotId, indices: new Set(items.map(item => item.index)) })

  async function changeBatch(items: typeof entries, enabled: boolean) {
    if (busy || !canBatch || items.length === 0) return
    setRequesting(true)
    try {
      if (await run('setStartupBatchEnabled', { snapshotId: page.snapshotId, indices: items.map(item => item.index), enabled })) setSelected([])
    } finally { setRequesting(false) }
  }

  return (
    <>
      <header className="page-header startup-heading">
        <div>
          <div className="workspace-heading">
            <h1>{startup.title}</h1>
          </div>
          <p className="subtitle">{startup.intro}</p>
        </div>
        <button className="button primary" onClick={() => void run('openStartupSettings')}><ExternalLink size={15} />{startup.settings}</button>
      </header>
      <section className="apps-surface startup-surface" aria-label={startup.title}>
        <div className="catalog-tabs startup-scopes" role="group" aria-label={startup.scope}>
          {([['All', startup.all], ['User', startup.user], ['Machine', startup.machine]] as const).map(([value, label]) => (
            <button key={value} type="button" className={`catalog-tab${scope === value ? ' active' : ''}`} aria-pressed={scope === value}
              onClick={() => { if (scope !== value) setSelected([]); setScope(value); setExpanded(null) }}>{label}</button>
          ))}
        </div>
        <div className="toolbar startup-toolbar">
          <label className="searchbox">
            <Search size={16} />
            <input type="search" value={search} onChange={event => { setSelected([]); setSearch(event.target.value) }} aria-label={startup.search} placeholder={startup.search} />
          </label>
          <button className="button quiet" disabled={busy} onClick={() => { setSelected([]); void run('scanStartupApps') }}>
            <RefreshCw size={15} className={page.loading ? 'spin' : ''} />{lang.refresh}
          </button>
          {page.scanned && <span className="workspace-count" role="status"><bdi>{entries.length}</bdi> {lang.totalShown}</span>}
        </div>
        {(page.failed || incomplete) && <div className="startup-notice" role="alert"><CircleAlert size={16} />{page.failed ? startup.failed : startup.partial}</div>}
        {page.batchResult && !page.loading && <div className="startup-notice" role="status">
          <span>{(page.batchResult.enabled ? startup.batchEnabled : startup.batchDisabled).replace('{count}', String(page.batchResult.changed))}
            {page.batchResult.failed.length > 0 && ` · ${startup.batchFailed.replace('{count}', String(page.batchResult.failed.length))}`}</span>
          {page.batchResult.failed.length > 0 && <details><summary>{lang.details}</summary><ul>
            {page.batchResult.failed.map((name, index) => <li key={index}><bdi>{name}</bdi></li>)}
          </ul></details>}
        </div>}
        {entries.length > 0 ? (
          <div className="startup-list" tabIndex={0} aria-label={startup.title}>
            <table className="startup-table">
              <thead><tr><th scope="col"><div className="startup-entry"><input type="checkbox" checked={allSelected}
                ref={input => { if (input) input.indeterminate = chosen.length > 0 && !allSelected }}
                disabled={busy || eligible.length === 0} aria-label={startup.selectShown} title={startup.selectShown}
                onChange={event => setSelected(event.target.checked ? eligible : [])} />{lang.application}</div></th><th scope="col">{startup.source}</th><th scope="col">{startup.scope}</th>
                <th scope="col">{startup.status}</th><th scope="col" aria-label={lang.details} /></tr></thead>
              <tbody>{entries.map(entry => {
                const open = expanded === entry.id
                const status = entry.status === 'Enabled' ? startup.enabled : entry.status === 'Disabled' ? startup.disabled : startup.unknown
                const action = entry.status === 'Enabled' ? startup.disable : startup.enable
                const checked = canBatch && canChange(entry) && selectionIsCurrent && selection.indices.has(entry.index)
                return <Fragment key={entry.id}>
                  <tr className={`startup-row${checked ? ' is-selected' : ''}`} data-open={open}>
                    <td><div className="startup-entry"><input type="checkbox" checked={checked} disabled={busy || !canBatch || !canChange(entry)}
                      aria-label={`${lang.selectItem}: ${entry.name} · ${entry.scopeLabel}`}
                      onChange={event => setSelected(event.target.checked ? [...chosen, entry] : chosen.filter(item => item.index !== entry.index))} />
                      <AppIcon app={entry} variant="list" />
                      <strong dir="auto" title={entry.name}><bdi>{entry.name}</bdi></strong></div></td>
                    <td><span className="startup-source">{entry.source === 'Run' ? <Database size={14} /> : <FolderOpen size={14} />}{entry.sourceLabel}</span></td>
                    <td><span className="startup-scope">{entry.scope === 'User' ? <UserRound size={14} /> : <UsersRound size={14} />}{entry.scopeLabel}</span></td>
                    <td><div className="startup-status"><span className={entry.status === 'Enabled' ? 'startup-enabled' : undefined}>{status}</span>
                      {canChange(entry) && <button type="button" className="button quiet" disabled={busy || page.failed} aria-label={`${action}: ${entry.name}`}
                        onClick={() => void run('setStartupEnabled', { id: entry.id, token: entry.changeToken, enabled: entry.status === 'Disabled' })}>{action}</button>}</div></td>
                    <td><button type="button" className="startup-disclosure" aria-label={`${lang.details}: ${entry.name}`} title={lang.details}
                      aria-expanded={open} aria-controls={open ? detailsId : undefined} onClick={() => setExpanded(open ? null : entry.id)}><ChevronDown size={17} /></button></td>
                  </tr>
                  {open && <tr className="startup-details" id={detailsId}><td colSpan={5}>
                    <dl><dt>{startup.location}</dt><dd><bdi dir="ltr">{entry.location}</bdi></dd>
                      {entry.executable && <><dt>{startup.executable}</dt><dd><bdi dir="ltr">{entry.executable}</bdi></dd></>}
                    </dl>
                    {!entry.changeToken && <button className="button quiet" onClick={() => void run('openStartupSettings')}><ExternalLink size={13} />{startup.settings}</button>}
                  </td></tr>}
                </Fragment>
              })}</tbody>
            </table>
          </div>
        ) : page.loading || (!page.scanned && !page.failed) ? (
          <div className="empty-state" role="status"><LoaderCircle className="spin" /><strong>{lang.refreshing}</strong></div>
        ) : (page.scanned && (query || (!page.failed && !incomplete))) && (
          <div className="empty-state"><div className="empty-icon">{query ? <Search /> : <Power />}</div>
            <strong>{query ? startup.noMatches : scope === 'All' ? startup.empty : startup.emptyScope}</strong></div>
        )}
        {eligible.length > 0 && <footer className="workspace-selection startup-actions">
          <span role="status">{busy ? lang.refreshing : chosen.length > 0 ? <>{lang.selectedCount} <bdi>{chosen.length}</bdi></> : startup.bulkScope}</span>
          {chosen.length > 0 ? <>
            <button type="button" className="button quiet" disabled={busy} onClick={() => setSelected([])}>{lang.residualDeselectAll}</button>
            <button type="button" className="button quiet" disabled={busy || disabledSelected.length === 0} onClick={() => void changeBatch(disabledSelected, true)}>
              {startup.enableSelected} <bdi>({disabledSelected.length})</bdi></button>
            <button type="button" className="button primary" disabled={busy || enabledSelected.length === 0} onClick={() => void changeBatch(enabledSelected, false)}>
              {startup.disableSelected} <bdi>({enabledSelected.length})</bdi></button>
          </> : <button type="button" className="button quiet" disabled={busy || enabledShown.length === 0} onClick={() => void changeBatch(enabledShown, false)}>
            {startup.disableAllShown} <bdi>({enabledShown.length})</bdi></button>}
        </footer>}
      </section>
    </>
  )
}
