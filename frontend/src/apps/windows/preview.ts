// Browser QA only. This adapter is never included in the Windows package.
const listeners=new Set<(event:{data:unknown})=>void>()
window.chrome={webview:{addEventListener:(_name,listener)=>{listeners.add(listener)},postMessage(message){void fetch('/bridge',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(message)}).then(response=>response.json()).then(data=>listeners.forEach(listener=>listener({data})))}}}
// Native WebView2 receives host push events. The QA-only HTTP adapter polls.
setInterval(()=>listeners.forEach(listener=>listener({data:{v:1,event:'workspace_changed'}})),1000)
await import('./main')

export {}
