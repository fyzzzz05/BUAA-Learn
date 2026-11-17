export interface HostMessage {v:number;id?:string;ok?:boolean;owner?:string|null;result?:unknown;error?:string;event?:string}
interface WebView {postMessage(message:unknown):void;addEventListener(name:'message',listener:(event:{data:unknown})=>void):void}
declare global {interface Window {chrome?:{webview?:WebView}}}
const errors:Record<string,string>={
 automation_model:'请先配置模型，再开启自动笔记。',
 qa_scope:'课程或课次不属于当前问答范围。',
 qa_no_material:'请先获取课堂识别文本，再提问。',
 qa_configuration:'请先在模型与设置中配置可用的模型。',
 qa_busy:'已有问答正在运行，请稍后重试。',
 qa_authentication:'模型鉴权失败，请检查密钥。',
 qa_quota:'模型限流或额度不足，请稍后重试。',
 qa_timeout:'模型请求超时，请重试。',
 qa_network:'无法连接模型，请检查网络。',
 qa_model:'模型未返回可用回答，请重试。',
stale_account:'账号已切换，请重新打开页面。',invalid_request:'操作参数无效，请重新打开页面。',operation_failed:'操作未完成，已有记录保留，请重试。',cancelled:'操作已取消。',credential_unavailable:'无法访问 Windows 凭据管理器，请检查权限后重试。',model_configuration:'请检查模型名称和服务地址：远程服务使用 HTTPS，本地服务可使用 localhost HTTP，地址不能包含用户名、密码、查询参数或片段。'}
export class NativeBridge {
 private pending=new Map<string,{resolve:(value:unknown)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>;owner:string|null;method:string}>()
 private listeners=new Set<()=>void>()
 private serial=0
 owner:string|null=null
 constructor(){window.chrome?.webview?.addEventListener('message',event=>{
  if(typeof event.data!=='object'||!event.data)return
  const message=event.data as HostMessage
  if(message.v!==1)return
  if(message.event==='workspace_changed'){this.listeners.forEach(listener=>listener());return}
  if(message.event==='changed'){if(message.owner===this.owner)this.listeners.forEach(listener=>listener());return}
  if(typeof message.id!=='string')return
  const request=this.pending.get(message.id);if(!request)return
  clearTimeout(request.timer);this.pending.delete(message.id)
  if(request.method!=='workspace'&&request.method!=='select_account'&&request.method!=='connect'&&(request.owner!==this.owner||message.owner!==request.owner)){request.reject(new Error(errors.stale_account));return}
  if(!message.ok){request.reject(new Error(errors[message.error??'']??errors.operation_failed));return}
  if(request.method==='select_account')this.owner=message.owner??null
  request.resolve(message.result)
 })}
 subscribe=(listener:()=>void):(()=>void)=>{this.listeners.add(listener);return()=>this.listeners.delete(listener)}
 call<T>(method:string,args:Record<string,unknown>={}):Promise<T>{
  const view=window.chrome?.webview
  if(!view)return Promise.reject(new Error('本地宿主未连接，请从 Windows 应用打开。'))
  const id=`r${++this.serial}`
  return new Promise<T>((resolve,reject)=>{
   const timer=setTimeout(()=>{this.pending.delete(id);reject(new Error('等待宿主响应超时，请重试。'))},method==='ask_course'?190000:method==='save_pdf_as'||method==='save_markdown_as'?300000:method==='slide'?45000:15000)
   this.pending.set(id,{resolve:value=>resolve(value as T),reject,timer,owner:this.owner,method})
   try{view.postMessage({v:1,id,method,owner:this.owner,args})}catch{clearTimeout(timer);this.pending.delete(id);reject(new Error(errors.operation_failed))}
  })
 }
}
