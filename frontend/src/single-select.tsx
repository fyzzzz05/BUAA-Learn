import { createRef } from "react"
import { createRoot, type Root } from "react-dom/client"
import { flushSync } from "react-dom"
import {
  Combobox, ComboboxContent, ComboboxEmpty, ComboboxInput,
  ComboboxItem, ComboboxList,
} from "./shared/ui/combobox"

export interface SelectOption {
  value: string
  label: string
  description?: string
  search?: string
}
interface SelectProps {
  id: string
  name: string
  label: string
  placeholder: string
  options: SelectOption[]
  value: string
  compact?: boolean
  onChange: (value: string) => void
}
interface SelectHandle {
  setValue: (value: string) => void
  unmount: () => void
}
const openSelects = new Set<string>()

export function hasOpenCombobox(): boolean {
  return openSelects.size > 0
}

export function mountCombobox(container: HTMLElement, initial: SelectProps): SelectHandle {
  const root: Root = createRoot(container)
  const anchor = createRef<HTMLDivElement>()
  let props = initial
  const render = (): void => {
    const selected = props.options.find(item => item.value === props.value) ?? null
    root.render(
      <div ref={anchor} className={`buaa-combobox ${props.compact ? 'buaa-combobox-compact' : ''}`}>
        <Combobox<SelectOption>
          items={props.options}
          value={selected}
          name={props.name}
          autoHighlight
          itemToStringLabel={item => item.label}
          itemToStringValue={item => item.value}
          filter={(item, query) => `${item.label} ${item.description ?? ''} ${item.search ?? ''}`.toLowerCase().includes(query.trim().toLowerCase())}
          onOpenChange={open => {if(open)openSelects.add(props.id);else openSelects.delete(props.id)}}
          onValueChange={item => {
            const value = item?.value ?? ''
            props = {...props, value}
            render()
            props.onChange(value)
          }}
        >
          <ComboboxInput id={props.id} aria-label={props.label} placeholder={props.placeholder} />
          <ComboboxContent anchor={anchor} className="buaa-combobox-popup" aria-label={`${props.label}选项`}>
            <ComboboxEmpty>没有找到匹配的选项</ComboboxEmpty>
            <ComboboxList>
              {(item: SelectOption) => (
                <ComboboxItem key={item.value} value={item}>
                  <span className="buaa-combobox-option"><strong>{item.label}</strong>
                    {item.description && <small>{item.description}</small>}
                  </span>
                </ComboboxItem>
              )}
            </ComboboxList>
          </ComboboxContent>
        </Combobox>
      </div>,
    )
  }
  // The host app immediately queries form controls after a route render.
  flushSync(render)
  return {
    setValue(value) {if(props.value === value)return;props = {...props, value};flushSync(render)},
    unmount() {openSelects.delete(props.id);root.unmount()},
  }
}
