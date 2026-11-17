import {Component,type ReactNode} from 'react'
import {createRoot} from 'react-dom/client'
import {NativeBridge} from '@/shared/native/bridge'
import {WorkspaceApp} from '@/features/windows-workspace/app'
class WorkspaceErrorBoundary extends Component<{children:ReactNode},{failed:boolean}> {
 state={failed:false}
 static getDerivedStateFromError():{failed:boolean}{return {failed:true}}
 render():ReactNode {return this.state.failed?<main><h1>无法显示学习空间</h1><p>已有本地记录保留。</p><button onClick={()=>location.reload()}>重新打开</button></main>:this.props.children}
}
const bridge=new NativeBridge()
createRoot(document.getElementById('root')!).render(<WorkspaceErrorBoundary><WorkspaceApp bridge={bridge}/></WorkspaceErrorBoundary>)
