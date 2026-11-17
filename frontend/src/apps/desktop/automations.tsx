import {Button} from '@/shared/ui/arc/button/button'
import {Input} from '@/shared/ui/arc/input/input'
import {
  mountAutomations,
  type AutomationButtonProps,
  type AutomationInputProps,
} from '../../features/automations/mount'

function Action({variant, disabled, onClick, children}: AutomationButtonProps) {
  return <Button variant={variant === 'outline' ? 'secondary' : variant ?? 'primary'}
    disabled={disabled} onClick={onClick}>{children}</Button>
}

function Search(props: AutomationInputProps) {
  return <div className="arc-automation-search"><Input {...props} label="搜索课程" type="search"/></div>
}

export function mountDesktopAutomations(container: HTMLElement, props: Parameters<typeof mountAutomations>[1]) {
  return mountAutomations(container, {...props, controls: {Action, Search}})
}
