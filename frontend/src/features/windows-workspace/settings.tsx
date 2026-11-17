import {useEffect,useState,useRef} from 'react'
import type {NativeBridge} from '@/shared/native/bridge'
interface ModelView {base_url:string;model:string;ready:boolean;key_state:'none'|'saved'|'missing'|'unavailable';has_credential_reference:boolean;cleanup_pending:boolean}
const labels={none:'未保存密钥',saved:'已保存密钥',missing:'已保存的密钥不可用，请替换或移除。',unavailable:'无法访问系统凭据管理器，请检查权限。'}
export function ModelSettings({bridge,session}:{bridge:NativeBridge;session:string}):React.JSX.Element {
 const [url,setUrl]=useState('');const [model,setModel]=useState('');const [key,setKey]=useState('');const [view,setView]=useState<ModelView>();const [keyView,setKeyView]=useState<ModelView>();const [error,setError]=useState('');const [message,setMessage]=useState('');const [loading,setLoading]=useState(true);const [saving,setSaving]=useState(false)
 const probe=useRef(0)
 useEffect(()=>setKey(''),[session])
 const apply=(value:ModelView):void=>{setView(value);setKeyView(value);setUrl(value.base_url);setModel(value.model);setKey('')}
 useEffect(()=>{let active=true;void bridge.call<ModelView>('model_settings').then(value=>{if(active)apply(value)}).catch((e:Error)=>{if(active)setError(e.message)}).finally(()=>{if(active)setLoading(false)});return()=>{active=false}},[bridge])
 useEffect(()=>{if(!url||saving)return;let active=true;const version=++probe.current;const timer=setTimeout(()=>{void bridge.call<ModelView>('model_key_status',{base_url:url}).then(value=>{if(active&&version===probe.current)setKeyView(value)}).catch(()=>{if(active&&version===probe.current)setKeyView(undefined)})},300);return()=>{active=false;clearTimeout(timer)}},[bridge,url,saving])
 const save=async(action:'keep'|'replace'|'remove'):Promise<void>=>{
  if(saving)return;probe.current++;setError('');setMessage('')
  if(action==='replace'&&(!/^[\x21-\x7e]+$/.test(key)||key.length>2560)){setError('密钥不可包含空白或控制字符，且最多为 2560 字节。');return}
  setSaving(true)
  try{const value=await bridge.call<ModelView>('save_model_settings',{base_url:url,model,key_action:action,...(action==='replace'?{key}:{})});apply(value);setMessage('已保存。')}
  catch(e){setError(e instanceof Error?e.message:'保存失败。')}
  finally{setSaving(false)}
 }
 const removeKey=async():Promise<void>=>{if(saving)return;probe.current++;setSaving(true);setError('');setMessage('');try{const value=await bridge.call<ModelView>('remove_model_key',{base_url:url});setKeyView(value);setKey('');setView(current=>current?{...current,cleanup_pending:value.cleanup_pending}:current);setMessage(value.cleanup_pending?'':'密钥已移除。')}catch(e){setError(e instanceof Error?e.message:'移除失败。')}finally{setSaving(false)}}
 const cleanup=async():Promise<void>=>{setSaving(true);setError('');try{const value=await bridge.call<ModelView>('cleanup_model_keys');setView(value);setMessage(value.cleanup_pending?'':'旧密钥已移除。')}catch(e){setError(e instanceof Error?e.message:'移除失败。')}finally{setSaving(false)}}
 return <><h1>模型与设置</h1>{error&&<p role="alert" className="error">{error}</p>}{message&&<p role="status">{message}</p>}{loading?<p role="status">正在读取…</p>:<form aria-busy={saving} className="model-settings" onSubmit={event=>{event.preventDefault();void save(key?'replace':'keep')}}><fieldset disabled={saving}><label htmlFor="model-url">服务地址</label><input id="model-url" type="url" autoComplete="off" spellCheck={false} placeholder="https://api.example.com/v1" value={url} required maxLength={2048} onChange={event=>{setUrl(event.target.value);setKey('');setKeyView(undefined);setMessage('')}}/><label htmlFor="model-name">模型名称</label><input id="model-name" autoComplete="off" spellCheck={false} value={model} required maxLength={256} onChange={event=>setModel(event.target.value)}/><div className="key-heading"><label htmlFor="model-key">API Key</label>{keyView&&<span>{labels[keyView.key_state]}</span>}</div><input id="model-key" type="password" autoComplete="new-password" spellCheck={false} value={key} maxLength={2560} placeholder={keyView?.has_credential_reference?'留空保留该服务的密钥':'可留空'} onChange={event=>setKey(event.target.value)}/><div className="settings-actions"><button type="submit">{saving?'处理中…':'保存'}</button>{keyView?.has_credential_reference&&<button type="button" onClick={()=>void removeKey()}>移除已保存密钥</button>}</div></fieldset>{view?.cleanup_pending&&<div className="cleanup-pending"><p>仍有旧密钥待移除，请检查系统凭据权限后重试。</p><button type="button" disabled={saving} onClick={()=>void cleanup()}>重试移除</button></div>}</form>}</>
}
