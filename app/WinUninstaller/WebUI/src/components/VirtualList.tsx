import { useEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, ReactNode } from 'react'
import { markFirstAppearance, virtualRange } from '../presentation'

type VirtualIdentity = { id?: string | number; name?: string }

export function VirtualList<T>({ items, render, rowHeight, className = '' }: { items: T[]; render: (item: T, index: number) => ReactNode; rowHeight: number; className?: string }) {
  const host = useRef<HTMLDivElement>(null)
  const entered = useMemo(() => new Set<string>(), [])
  const [viewport, setViewport] = useState({ top: 0, height: 640 })
  useEffect(() => {
    if (!host.current) return
    const observer = new ResizeObserver(entries => setViewport(value => ({ ...value, height: entries[0].contentRect.height })))
    observer.observe(host.current)
    return () => observer.disconnect()
  }, [])
  const { start, end, totalHeight } = virtualRange(items.length, viewport.top, viewport.height, rowHeight)
  return (
    <div className={`virtual-scroll ${className}`} ref={host} onScroll={event => { const top = event.currentTarget.scrollTop; setViewport(value => ({ ...value, top })) }}>
      <div style={{ height: totalHeight, position: 'relative' }}>
        {items.slice(start, end).map((item, offset) => {
          const index = start + offset
          const value = item as VirtualIdentity | [VirtualIdentity]
          const stableId = Array.isArray(value) ? value[0]?.id : value.id ?? value.name
          const key = String(stableId ?? index)
          return (
            <div key={key} className="virtual-row" style={{ height: rowHeight, transform: `translateY(${index * rowHeight}px)` }}>
              <div ref={node => { if (node && markFirstAppearance(key, entered)) node.classList.add('virtual-item-enter') }} style={{ '--stagger': Math.min(offset, 8) } as CSSProperties}>
                {render(item, index)}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
