import {createPortal, flushSync} from 'react-dom'
import {createRoot} from 'react-dom/client'
import {BookOpen, Clock3, Download, LogIn, LogOut, Play, RefreshCw, X} from 'lucide-react'
import {Alert, type AlertTone} from '@/shared/ui/arc/alert/alert'
import {Badge, type BadgeTone} from '@/shared/ui/arc/badge/badge'
import {Button} from '@/shared/ui/arc/button/button'
import {EmptyState} from '@/shared/ui/arc/empty-state/empty-state'
import {Input} from '@/shared/ui/arc/input/input'
import {Tabs, TabsList, TabsTrigger} from '@/shared/ui/arc/tabs/tabs'
import './arc-controls.css'

const icons = {
  login: LogIn, logout: LogOut, sync: RefreshCw, 'recordings-sync': RefreshCw,
  'course-sync': RefreshCw, 'schedule-sync': RefreshCw, analyze: Play, materials: Download,
  'close-reader': X, 'open-reader': BookOpen,
}

function widget(slot: HTMLElement) {
  const {arc, label = '', description = '', tone = 'neutral', command: action, id, value, name, placeholder,
    type, disabled, variant, size} = slot.dataset
  switch (arc) {
    case 'button': {
      const Icon = icons[action as keyof typeof icons]
      return <Button type={type === 'submit' ? 'submit' : 'button'} disabled={disabled === 'true'}
        name={name} formNoValidate={slot.dataset.skipValidation === 'true'}
        variant={variant === 'primary' || variant === 'ghost' ? variant : 'secondary'}
        size={size === 'sm' ? 'sm' : 'md'} data-action={action} data-id={id}
        aria-label={slot.dataset.iconOnly === 'true' ? label : undefined}>
        {Icon && <Icon aria-hidden="true"/>}{slot.dataset.iconOnly !== 'true' && label}
      </Button>
    }
    case 'input':
      return <Input id={id} name={name} label={label} description={description || undefined}
        type={type} defaultValue={value} placeholder={placeholder}
        autoComplete={type === 'password' ? 'off' : undefined} required={slot.dataset.required === 'true'}/>
    case 'badge':
      return <Badge tone={tone as BadgeTone} size="sm">{label}</Badge>
    case 'alert':
      return <Alert title={label} tone={tone as AlertTone}>{description || undefined}</Alert>
    case 'empty':
      return <EmptyState title={label} description={description} icon={slot.dataset.icon === 'clock' ? <Clock3/> : <BookOpen/>}/>
    default:
      return null
  }
}

/** Share one React root across the host-rendered desktop pages and the reader dialog. */
export function createArcControls() {
  const root = createRoot(document.createElement('div'), {identifierPrefix:'desktop-arc-'})
  const regions = new Map<HTMLElement, React.ReactPortal[]>()
  function commit() {
    flushSync(() => root.render([...regions.values()].flat()))
  }
  return {
    replace(region: HTMLElement, html: string) {
      if (regions.delete(region)) commit()
      region.innerHTML = html
      const slots = [...region.querySelectorAll<HTMLElement>('[data-arc]')]
      if (!slots.length) return
      regions.set(region, slots.map((slot, index) => createPortal(widget(slot), slot, `${region.id}:${index}`)))
      commit()
    },
    readerTabs(region: HTMLElement, tabs: [string, string][], selected: string, onSelect: (value: string) => void) {
      const list = <Tabs value={selected} onValueChange={onSelect} activationMode="manual">
        <TabsList aria-label="课堂材料">
          {tabs.map(([value,label]) => <TabsTrigger key={value} id={`reader-tab-${value}`} value={value}
            aria-controls="reader-body">{label}</TabsTrigger>)}
        </TabsList>
      </Tabs>
      regions.set(region, [createPortal(list, region, 'reader-tabs')])
      commit()
    },
  }
}
