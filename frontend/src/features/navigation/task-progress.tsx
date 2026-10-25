import {createRoot} from 'react-dom/client'
import {Progress, ProgressLabel, ProgressValue} from '@/shared/ui/progress'
export function mountTaskProgress(container:HTMLElement, completed:number|null, total:number|null, unit:string) {
 const root=createRoot(container)
 const known=total!==null&&Number.isFinite(total)&&total>0&&completed!==null&&Number.isFinite(completed)
 const max=known?total:100
 const value=known?Math.min(max,Math.max(0,completed)):null
 root.render(<Progress className="task-progress-component" value={value} max={max}>
   <ProgressLabel>{known?'任务进度':'处理中'}</ProgressLabel>
   {known&&<ProgressValue>{()=>`${value}/${max} ${unit}`}</ProgressValue>}
 </Progress>)
 return ()=>root.unmount()
}
