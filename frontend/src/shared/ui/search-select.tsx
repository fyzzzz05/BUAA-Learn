import {useEffect,useRef,useState} from "react"
import {Combobox, ComboboxContent, ComboboxEmpty, ComboboxInput, ComboboxItem, ComboboxList} from "./combobox"

export interface SelectOption { value: string; label: string; description?: string; search?: string }
export interface SearchSelectProps {
 id: string; name: string; label: string; placeholder: string; options: SelectOption[]; value: string; compact?: boolean; resetSearchOnClose?: boolean;
 onChange: (value: string) => void; onOpenChange?: (open: boolean) => void
}
export function SearchSelect({id,name,label,placeholder,options,value,compact,resetSearchOnClose=false,onChange,onOpenChange}: SearchSelectProps): React.JSX.Element {
 const anchor = useRef<HTMLDivElement>(null)
 const selected = options.find(item => item.value === value) ?? null
 const [open,setOpen]=useState(false)
 const [query,setQuery]=useState(selected?.label??"")
 useEffect(()=>{if(resetSearchOnClose&&!open)setQuery(selected?.label??"")},[resetSearchOnClose,open,selected?.label])
 return (
      <div ref={anchor} className={`buaa-combobox ${compact ? 'buaa-combobox-compact' : ''}`}>
        <Combobox<SelectOption>
          items={options}
          value={selected}
          name={name}
          autoHighlight
          itemToStringLabel={item => item.label}
          itemToStringValue={item => item.value}
          filter={(item, query) => `${item.label} ${item.description ?? ''} ${item.search ?? ''}`.toLowerCase().includes(query.trim().toLowerCase())}
          inputValue={resetSearchOnClose?query:undefined}
          onInputValueChange={resetSearchOnClose?setQuery:undefined}
          onOpenChange={next=>{setOpen(next);onOpenChange?.(next)}}
          onValueChange={item => onChange(item?.value ?? '')}
        >
          <ComboboxInput id={id} aria-label={label} placeholder={placeholder} />
          <ComboboxContent anchor={anchor} className="buaa-combobox-popup" aria-label={`${label}选项`}>
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
      </div>
 )
}
