import {createRoot, type Root} from "react-dom/client"
import {flushSync} from "react-dom"
import {SearchSelect, type SearchSelectProps} from "./shared/ui/search-select"
export type {SelectOption} from "./shared/ui/search-select"
type SelectProps = SearchSelectProps
interface SelectHandle {setValue: (value:string)=>void; unmount:()=>void}
const openSelects = new Set<string>()
export function hasOpenCombobox(): boolean {return openSelects.size > 0}
export function mountCombobox(container:HTMLElement, initial:SelectProps):SelectHandle {
 const root:Root=createRoot(container)
 let props=initial
 const render=():void=>root.render(<SearchSelect {...props} onOpenChange={open=>{if(open)openSelects.add(props.id);else openSelects.delete(props.id)}} onChange={value=>{props={...props,value};render();props.onChange(value)}}/>)
 flushSync(render)
 return {setValue(value){if(props.value===value)return;props={...props,value};flushSync(render)},unmount(){openSelects.delete(props.id);root.unmount()}}
}
