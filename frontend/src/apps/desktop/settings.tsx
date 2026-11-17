import {useEffect, useState} from 'react'
import {createRoot} from 'react-dom/client'
import {Download, FileText, FolderOpen, RefreshCw} from 'lucide-react'
import {Alert} from '@/shared/ui/arc/alert/alert'
import {Badge} from '@/shared/ui/arc/badge/badge'
import {Button} from '@/shared/ui/arc/button/button'
import './settings.css'

interface UpdateState {
  version: string
  supported: boolean
  manualInstall: boolean
  status: 'idle' | 'checking' | 'current' | 'available' | 'downloading' | 'ready' | 'installing'
  update: {version: string; notes: string; size: number} | null
  error: string
  percent: number
}

interface DesktopBridge {
  settings(): Promise<UpdateState>
  checkUpdate(): Promise<UpdateState>
  downloadUpdate(): Promise<UpdateState>
  cancelUpdate(): Promise<void>
  installUpdate(): Promise<UpdateState>
  openData(): Promise<boolean>
  openLog(): Promise<boolean>
  onUpdate(callback: (value: UpdateState) => void): () => void
}

declare global {
  interface Window {buaaDesktop?: DesktopBridge}
}

function DesktopSettings({desktop}: {desktop: DesktopBridge}) {
  const [state, setState] = useState<UpdateState | null>(null)
  const [updateError, setUpdateError] = useState('')
  const [diagnosticError, setDiagnosticError] = useState('')
  const [opening, setOpening] = useState<'data' | 'log' | null>(null)

  useEffect(() => {
    let active = true
    const unsubscribe = desktop.onUpdate(setState)
    void desktop.settings().then(value => {
      if (active) setState(current => current ?? value)
    }).catch(() => {
      if (active) setUpdateError('无法读取应用版本。')
    })
    return () => {active = false; unsubscribe()}
  }, [desktop])

  async function updateAction(action: 'checkUpdate' | 'downloadUpdate' | 'cancelUpdate' | 'installUpdate') {
    setUpdateError('')
    try {await desktop[action]()}
    catch {setUpdateError('更新操作未完成，请重试。')}
  }

  async function openLocal(which: 'data' | 'log') {
    setDiagnosticError('')
    setOpening(which)
    try {
      const opened = await (which === 'data' ? desktop.openData() : desktop.openLog())
      if (!opened) throw new Error('open failed')
    } catch {
      setDiagnosticError(which === 'data' ? '无法打开数据目录。' : '无法打开诊断日志。')
    } finally {setOpening(null)}
  }

  const status = state?.status
  const busy = status === 'checking' || status === 'downloading' || status === 'installing'
  const error = updateError || state?.error
  return <>
    <section className="panel arc-settings desktop-settings-section" aria-labelledby="updates-title">
      <div className="panel-head">
        <h2 id="updates-title">应用更新</h2>
        {state && <Badge size="sm">当前版本 {state.version}</Badge>}
      </div>
      <div className="panel-body desktop-settings-body">
        <div className="actions">
          <Button variant="secondary" loading={status === 'checking'}
            disabled={!state?.supported || busy} onClick={() => void updateAction('checkUpdate')}>
            <RefreshCw aria-hidden="true"/>检查更新
          </Button>
          <span role="status" className="desktop-update-status">
            {status === 'checking' ? '正在检查更新…' : status === 'current' ? '已是当前平台的最新版本'
              : state && !state.supported ? '此构建不支持应用内更新' : ''}
          </span>
        </div>
        {state?.update && status !== 'checking' && <div className="desktop-update-release">
          <h3>版本 {state.update.version}</h3>
          {state.update.notes && <div className="desktop-update-notes" aria-label="更新内容" tabIndex={0}>{state.update.notes}</div>}
          {status === 'downloading' && <>
            <div className="desktop-update-progress">
              <progress max={100} value={state.percent} aria-label="安装包下载进度"/>
              <span>{state.percent}%</span>
            </div>
            <Button variant="ghost" onClick={() => void updateAction('cancelUpdate')}>取消下载</Button>
          </>}
          {status === 'available' && <Button onClick={() => void updateAction('downloadUpdate')}>
            <Download aria-hidden="true"/>下载安装包
          </Button>}
          {(status === 'ready' || status === 'installing') && <>
            {state.manualInstall && <p className="desktop-update-status">打开 DMG 后，将应用拖到「应用程序」完成更新。</p>}
            <Button loading={status === 'installing'} disabled={status === 'installing'}
              onClick={() => void updateAction('installUpdate')}>
              {state.manualInstall ? '退出并打开安装包' : '退出并安装'}
            </Button>
          </>}
        </div>}
        {error && <Alert tone="danger" title={error}/>}
      </div>
    </section>
    <section className="panel arc-settings desktop-settings-section" aria-labelledby="diagnostics-title">
      <div className="panel-head"><h2 id="diagnostics-title">本地数据与诊断</h2></div>
      <div className="panel-body desktop-settings-body">
        <div className="actions">
          <Button variant="secondary" disabled={opening !== null} onClick={() => void openLocal('data')}>
            <FolderOpen aria-hidden="true"/>打开数据目录
          </Button>
          <Button variant="secondary" disabled={opening !== null} onClick={() => void openLocal('log')}>
            <FileText aria-hidden="true"/>打开诊断日志
          </Button>
        </div>
        {diagnosticError && <Alert tone="danger" title={diagnosticError}/>}
      </div>
    </section>
  </>
}

export function mountDesktopSettings(container: HTMLElement) {
  const desktop = window.buaaDesktop
  if (!desktop) return
  const root = createRoot(container)
  root.render(<DesktopSettings desktop={desktop}/>)
  return {unmount: () => root.unmount()}
}
