import {useState, useEffect, type CSSProperties} from 'react'
import {createRoot} from 'react-dom/client'
import {createPortal} from 'react-dom'
import {Home, Library, NotebookPen, Timer, Clock3, Settings2, CircleUserRound, BookOpen} from 'lucide-react'
import {SidebarProvider, Sidebar, SidebarHeader, SidebarContent, SidebarFooter, SidebarGroup, SidebarGroupLabel, SidebarMenu, SidebarMenuItem, SidebarMenuButton, SidebarMenuBadge, SidebarTrigger, useSidebar} from '@/shared/ui/sidebar'
import {TooltipProvider} from '@/shared/ui/tooltip'
type Props = {page:string; name:string; count:number; recent:{id:string;name:string}[]}
const links = [{id:'home',href:'#/',label:'开始学习',icon:Home},{id:'courses',href:'#/courses',label:'本学期课程',icon:Library},{id:'tasks',href:'#/tasks',label:'作业与事项',icon:NotebookPen},{id:'automations',href:'#/automations',label:'课程自动化',icon:Timer}]
function Navigation({data,trigger}:{data:Props;trigger:HTMLElement}) {
 const {state,isMobile,openMobile,setOpenMobile} = useSidebar()
 useEffect(()=>{const close=()=>setOpenMobile(false);window.addEventListener('hashchange',close);return()=>window.removeEventListener('hashchange',close)},[setOpenMobile])
 const item=(link:typeof links[number])=><SidebarMenuItem key={link.id}><SidebarMenuButton render={<a href={link.href} aria-current={data.page===link.id?'page':undefined} onClick={()=>setOpenMobile(false)}/>} isActive={data.page===link.id} tooltip={link.label}><link.icon/><span>{link.label}</span></SidebarMenuButton>{link.id==='tasks'&&data.count>0&&<SidebarMenuBadge>{data.count}</SidebarMenuBadge>}</SidebarMenuItem>
 return <><Sidebar collapsible="icon"><SidebarHeader><SidebarMenu><SidebarMenuItem><SidebarMenuButton size="lg" render={<a href="#/" aria-label="BUAA Learn 开始学习" onClick={()=>setOpenMobile(false)}/> } tooltip="BUAA Learn"><BookOpen/><span className="nav-brand"><span className="nav-brand-university">BUAA</span> <span className="nav-brand-learn">Learn</span></span></SidebarMenuButton></SidebarMenuItem></SidebarMenu></SidebarHeader><SidebarContent><SidebarGroup><SidebarMenu>{links.map(item)}</SidebarMenu></SidebarGroup>{(state==='expanded'||isMobile)&&data.recent.length>0&&<SidebarGroup><SidebarGroupLabel>最近学习</SidebarGroupLabel><SidebarMenu>{data.recent.map(c=><SidebarMenuItem key={c.id}><SidebarMenuButton render={<a href={`#/course/${encodeURIComponent(c.id)}`} onClick={()=>setOpenMobile(false)}/>}><span>{c.name}</span></SidebarMenuButton></SidebarMenuItem>)}</SidebarMenu></SidebarGroup>}</SidebarContent><SidebarFooter><SidebarMenu>{[{id:'jobs',href:'#/jobs',label:'任务状态',icon:Clock3},{id:'settings',href:'#/settings',label:'模型与设置',icon:Settings2}].map(item)}<SidebarMenuItem><SidebarMenuButton size="lg" tooltip={data.name} render={<div/>}><CircleUserRound/><span>{data.name}</span></SidebarMenuButton></SidebarMenuItem></SidebarMenu></SidebarFooter></Sidebar>{createPortal(<SidebarTrigger aria-label={isMobile?(openMobile?'关闭导航':'打开导航'):(state==='expanded'?'收起侧栏':'展开侧栏')} title="切换侧栏（⌘/Ctrl+B）"/>,trigger)}</>
}
export function mountNavigation(container:HTMLElement,trigger:HTMLElement) {
 const root=createRoot(container)
 function App({data}:{data:Props}){
  const [open,setOpen]=useState(()=>localStorage.getItem('buaa-sidebar-collapsed')!=='true')
  return <TooltipProvider><SidebarProvider open={open} onOpenChange={value=>{setOpen(value);localStorage.setItem('buaa-sidebar-collapsed',String(!value))}} style={{'--sidebar-width':'240px','--sidebar-width-icon':'56px'} as CSSProperties}><Navigation data={data} trigger={trigger}/></SidebarProvider></TooltipProvider>
 }
 return {update(data:Props){root.render(<App data={data}/> )}}
}

export {mountTaskProgress} from './task-progress'
